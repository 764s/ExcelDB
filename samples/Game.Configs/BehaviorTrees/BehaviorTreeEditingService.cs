using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExcelDb;
using ExcelDb.Database;
using ExcelDb.Protocol;

namespace Game.Configs.BehaviorTrees;

public static class BehaviorTreeTables
{
    public const int BehaviorTree = 6;
    public const int BehaviorNode = 7;

    public static readonly TableId BehaviorTreeTable = new(BehaviorTree);
    public static readonly TableId BehaviorNodeTable = new(BehaviorNode);
}

public enum BehaviorTreeIssueSeverity
{
    Warning = 0,
    Error = 1,
}

public sealed class BehaviorTreeValidationIssue
{
    public BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity severity, RowId? row, string message)
    {
        Severity = severity;
        Row = row;
        Message = message;
    }

    public BehaviorTreeIssueSeverity Severity { get; }
    public RowId? Row { get; }
    public string Message { get; }
    public bool IsError => Severity == BehaviorTreeIssueSeverity.Error;

    public override string ToString()
    {
        var prefix = Severity == BehaviorTreeIssueSeverity.Error ? "[Error] " : "[Warning] ";
        return Row.HasValue
            ? prefix + Row.Value + ": " + Message
            : prefix + Message;
    }
}

public enum BehaviorNodeDeleteMode
{
    DetachOnly,
    DeleteSubtree,
}

public sealed class BehaviorActionDescriptor
{
    public BehaviorActionDescriptor(string kind, string name, string summary = "", bool isDeprecated = false)
    {
        Kind = kind;
        Name = name;
        Summary = summary;
        IsDeprecated = isDeprecated;
    }

    public string Kind { get; }
    public string Name { get; }
    public string Summary { get; }
    public bool IsDeprecated { get; }
}

public interface IBehaviorActionCatalog
{
    IReadOnlyList<string> NodeKinds { get; }
    IReadOnlyList<BehaviorActionDescriptor> ActionsForKind(string kind);
    bool IsKnownKind(string kind);
    bool IsKnownAction(string kind, string action);
}

public sealed class StaticBehaviorActionCatalog : IBehaviorActionCatalog
{
    static readonly string[] Kinds = { "Selector", "Sequence", "Condition", "Action", "Decorator" };
    readonly Dictionary<string, List<BehaviorActionDescriptor>> _actions;

    public StaticBehaviorActionCatalog(IEnumerable<BehaviorActionDescriptor>? actions = null)
    {
        _actions = new Dictionary<string, List<BehaviorActionDescriptor>>(StringComparer.Ordinal);
        foreach (var kind in Kinds)
            _actions[kind] = new List<BehaviorActionDescriptor>();

        foreach (var action in actions ?? DefaultActions())
        {
            if (!_actions.TryGetValue(action.Kind, out var list))
            {
                list = new List<BehaviorActionDescriptor>();
                _actions[action.Kind] = list;
            }
            list.Add(action);
        }
    }

    public IReadOnlyList<string> NodeKinds => Kinds;

    public IReadOnlyList<BehaviorActionDescriptor> ActionsForKind(string kind) =>
        _actions.TryGetValue(kind, out var actions)
            ? actions.OrderBy(action => action.Name, StringComparer.Ordinal).ToArray()
            : Array.Empty<BehaviorActionDescriptor>();

    public bool IsKnownKind(string kind) => Kinds.Contains(kind, StringComparer.Ordinal);

    public bool IsKnownAction(string kind, string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return false;

        return _actions.TryGetValue(kind, out var actions) &&
               actions.Any(candidate => string.Equals(candidate.Name, action, StringComparison.Ordinal));
    }

    static IEnumerable<BehaviorActionDescriptor> DefaultActions()
    {
        yield return new BehaviorActionDescriptor("Condition", "HasPatrolRoute");
        yield return new BehaviorActionDescriptor("Condition", "CanSeeEnemy");
        yield return new BehaviorActionDescriptor("Action", "MoveToWaypoint");
        yield return new BehaviorActionDescriptor("Action", "AttackTarget");
        yield return new BehaviorActionDescriptor("Action", "NewAction");
    }
}

public sealed class BehaviorTreeEditingService
{
    readonly ConfigDatabase _db;
    readonly IBehaviorActionCatalog _actions;

    public BehaviorTreeEditingService(ConfigDatabase db, IBehaviorActionCatalog? actions = null)
    {
        _db = db;
        _actions = actions ?? new StaticBehaviorActionCatalog();
    }

    public IBehaviorActionCatalog ActionCatalog => _actions;

    public RowId CreateTree(string key, string title)
    {
        using var tx = _db.BeginEdit("Create behavior tree");
        var (id, draft) = tx.Create<BehaviorTreeConfig>();
        draft.Key = UniqueTreeKey(string.IsNullOrWhiteSpace(key) ? "behavior_tree_" + id.Id : key.Trim());
        draft.Title = string.IsNullOrWhiteSpace(title) ? draft.Key : title.Trim();
        tx.Commit();
        return id;
    }

    public void RenameTree(RowId treeId, string title)
    {
        RequireTree(treeId);
        using var tx = _db.BeginEdit("Rename behavior tree");
        tx.GetMutable<BehaviorTreeConfig>(treeId).Title = title.Trim();
        tx.Commit();
    }

    public RowId DuplicateTree(RowId sourceTreeId, string key, string title)
    {
        var source = RequireTree(sourceTreeId);
        using var tx = _db.BeginEdit("Duplicate behavior tree");
        var (newTreeId, treeDraft) = tx.Create<BehaviorTreeConfig>();
        treeDraft.Key = UniqueTreeKey(string.IsNullOrWhiteSpace(key) ? source.Key + "_copy" : key.Trim());
        treeDraft.Title = string.IsNullOrWhiteSpace(title) ? source.Title + " Copy" : title.Trim();

        var usedKeys = ExistingNodeKeys();
        var root = ToRowId(source.Root);
        if (IsNodeRef(root))
        {
            var duplicatedRoot = DuplicateExistingSubtree(tx, newTreeId, root, usedKeys, new Dictionary<RowId, RowId>());
            treeDraft.Root = ToRef(duplicatedRoot);
        }

        tx.Commit();
        return newTreeId;
    }

    public void DeleteTree(RowId treeId)
    {
        RequireTree(treeId);
        using var tx = _db.BeginEdit("Delete behavior tree");
        foreach (var nodeId in OwnedNodes(treeId))
            tx.Delete(nodeId);
        tx.Delete(treeId);
        tx.Commit();
    }

    public RowId CreateRoot(RowId treeId, string kind)
    {
        var tree = RequireTree(treeId);
        if (IsNodeRef(ToRowId(tree.Root)))
            throw new InvalidOperationException("The selected behavior tree already has a root node.");

        EnsureKnownKind(kind);
        using var tx = _db.BeginEdit("Create behavior tree root");
        var (id, draft) = CreateNode(tx, treeId, kind, 420, 80, kind.ToLowerInvariant() + "_root");
        draft.DisplayName = "Root " + kind;
        tx.GetMutable<BehaviorTreeConfig>(treeId).Root = ToRef(id);
        tx.Commit();
        return id;
    }

    public RowId AddChild(RowId treeId, RowId parentId, string kind)
    {
        EnsureKnownKind(kind);
        var parent = RequireOwnedNode(treeId, parentId);
        EnsureCanHaveChild(parent, parent.Children.Count + 1);

        using var tx = _db.BeginEdit("Add behavior child");
        var (id, draft) = CreateNode(tx, treeId, kind, parent.X + 220, parent.Y + 140, kind.ToLowerInvariant());
        tx.GetMutable<BehaviorNodeConfig>(parentId).Children.Add(ToRef(id));
        tx.Commit();
        return id;
    }

    public void RemoveChild(RowId treeId, RowId parentId, RowId childId)
    {
        RequireOwnedNode(treeId, parentId);
        RequireOwnedNode(treeId, childId);

        using var tx = _db.BeginEdit("Remove behavior child");
        var parent = tx.GetMutable<BehaviorNodeConfig>(parentId);
        RemoveChildRef(parent, childId);
        tx.Commit();
    }

    public void MoveChild(RowId treeId, RowId parentId, RowId childId, int direction)
    {
        RequireOwnedNode(treeId, parentId);
        RequireOwnedNode(treeId, childId);
        if (direction == 0)
            return;

        using var tx = _db.BeginEdit("Move behavior child order");
        var parent = tx.GetMutable<BehaviorNodeConfig>(parentId);
        var index = IndexOfChild(parent, childId);
        if (index < 0)
            throw new InvalidOperationException("The node is not a child of the selected parent.");

        var next = Math.Max(0, Math.Min(parent.Children.Count - 1, index + direction));
        if (next == index)
            return;

        var item = parent.Children[index].Clone();
        parent.Children.RemoveAt(index);
        parent.Children.Insert(next, item);
        tx.Commit();
    }

    public void ReparentNode(RowId treeId, RowId nodeId, RowId newParentId)
    {
        RequireTree(treeId);
        RequireOwnedNode(treeId, nodeId);
        var newParent = RequireOwnedNode(treeId, newParentId);
        EnsureCanHaveChild(newParent, newParent.Children.Count + 1);

        if (IsRoot(treeId, nodeId))
            throw new InvalidOperationException("Root nodes cannot be reparented. Replace the root or delete the tree instead.");
        if (nodeId == newParentId || CollectSubtree(nodeId).Contains(newParentId))
            throw new InvalidOperationException("Reparenting would create a cycle.");

        using var tx = _db.BeginEdit("Reparent behavior node");
        foreach (var parentId in ParentsOf(treeId, nodeId))
            RemoveChildRef(tx.GetMutable<BehaviorNodeConfig>(parentId), nodeId);
        tx.GetMutable<BehaviorNodeConfig>(newParentId).Children.Add(ToRef(nodeId));
        tx.Commit();
    }

    public void DeleteNode(RowId treeId, RowId nodeId, BehaviorNodeDeleteMode mode)
    {
        RequireTree(treeId);
        RequireOwnedNode(treeId, nodeId);
        if (IsRoot(treeId, nodeId))
            throw new InvalidOperationException("Root nodes cannot be deleted directly. Delete the whole tree or create a replacement root.");

        if (mode == BehaviorNodeDeleteMode.DetachOnly)
        {
            using var detach = _db.BeginEdit("Detach behavior node");
            foreach (var parentId in ParentsOf(treeId, nodeId))
                RemoveChildRef(detach.GetMutable<BehaviorNodeConfig>(parentId), nodeId);
            detach.Commit();
            return;
        }

        var subtree = CollectSubtree(nodeId);
        var outsideInbound = ParentsOfSubtree(treeId, subtree)
            .Where(parent => !subtree.Contains(parent))
            .ToArray();
        if (outsideInbound.Any(parent => !ParentsOf(treeId, nodeId).Contains(parent)))
            throw new InvalidOperationException("The subtree is referenced by another branch. Detach or reparent that reference first.");

        using var tx = _db.BeginEdit("Delete behavior subtree");
        foreach (var parentId in ParentsOf(treeId, nodeId))
            RemoveChildRef(tx.GetMutable<BehaviorNodeConfig>(parentId), nodeId);
        foreach (var id in subtree.OrderByDescending(id => id.Id))
            tx.Delete(id);
        tx.Commit();
    }

    public RowId DuplicateSubtree(RowId treeId, RowId nodeId)
    {
        RequireTree(treeId);
        RequireOwnedNode(treeId, nodeId);
        if (IsRoot(treeId, nodeId))
            throw new InvalidOperationException("Duplicate the whole tree to copy the root subtree.");

        var parentId = ParentsOf(treeId, nodeId).FirstOrDefault();
        if (!IsNodeRef(parentId))
            throw new InvalidOperationException("The selected node has no parent in this tree.");

        using var tx = _db.BeginEdit("Duplicate behavior subtree");
        var duplicate = DuplicateExistingSubtree(tx, treeId, nodeId, ExistingNodeKeys(), new Dictionary<RowId, RowId>());
        var parent = tx.GetMutable<BehaviorNodeConfig>(parentId);
        var index = IndexOfChild(parent, nodeId);
        if (index < 0 || index == parent.Children.Count - 1)
            parent.Children.Add(ToRef(duplicate));
        else
            parent.Children.Insert(index + 1, ToRef(duplicate));
        tx.Commit();
        return duplicate;
    }

    public void SetNodeField(RowId treeId, RowId nodeId, string fieldName, string value)
    {
        var node = RequireOwnedNode(treeId, nodeId);
        using var tx = _db.BeginEdit("Edit behavior node");
        var draft = tx.GetMutable<BehaviorNodeConfig>(nodeId);
        switch (fieldName)
        {
            case nameof(BehaviorNodeConfig.DisplayName):
                draft.DisplayName = value.Trim();
                break;
            case nameof(BehaviorNodeConfig.Kind):
                EnsureKnownKind(value);
                EnsureCanChangeKind(node, value.Trim());
                draft.Kind = value.Trim();
                break;
            case nameof(BehaviorNodeConfig.Action):
                EnsureActionAllowed(node.Kind, value.Trim());
                draft.Action = value.Trim();
                break;
            default:
                throw new ArgumentException("Unsupported behavior node field '" + fieldName + "'.", nameof(fieldName));
        }
        tx.Commit();
    }

    public void MoveNode(RowId treeId, RowId nodeId, int x, int y)
    {
        RequireOwnedNode(treeId, nodeId);
        using var tx = _db.BeginEdit("Move behavior node");
        var draft = tx.GetMutable<BehaviorNodeConfig>(nodeId);
        draft.X = x;
        draft.Y = y;
        tx.Commit();
    }

    public RowId? GetParent(RowId treeId, RowId nodeId)
    {
        var parent = ParentsOf(treeId, nodeId).FirstOrDefault();
        return IsNodeRef(parent) ? parent : null;
    }

    public IReadOnlyList<RowId> GetParentCandidates(RowId treeId, RowId nodeId)
    {
        RequireOwnedNode(treeId, nodeId);
        var excluded = CollectSubtree(nodeId);
        excluded.Add(nodeId);
        return OwnedNodes(treeId)
            .Where(id => !excluded.Contains(id))
            .Where(id => !IsRoot(treeId, nodeId) || id == nodeId)
            .ToArray();
    }

    public IReadOnlyList<BehaviorTreeValidationIssue> ValidateTree(RowId treeId)
    {
        var issues = new List<BehaviorTreeValidationIssue>();
        if (!_db.TryLoadAsset<BehaviorTreeConfig>(treeId, out var tree))
        {
            issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, treeId, "Behavior tree is missing."));
            return issues;
        }

        var root = ToRowId(tree.Root);
        if (!IsNodeRef(root))
        {
            issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, treeId, "Behavior tree has no root node."));
            return issues;
        }

        var visited = new HashSet<RowId>();
        var visiting = new HashSet<RowId>();
        var parentCounts = new Dictionary<RowId, int>();

        void Visit(RowId id)
        {
            if (!IsNodeRef(id))
                return;
            if (!visiting.Add(id))
            {
                issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, id, "Behavior tree contains a cycle."));
                return;
            }
            if (!visited.Add(id))
            {
                visiting.Remove(id);
                return;
            }
            if (!_db.TryLoadAsset<BehaviorNodeConfig>(id, out var node))
            {
                issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, id, "Referenced behavior node is missing."));
                visiting.Remove(id);
                return;
            }
            if (!SameRef(node.OwnerTree, treeId))
            {
                issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, id, "Node is not owned by this behavior tree."));
                visiting.Remove(id);
                return;
            }

            ValidateNodeShape(id, node, issues);
            foreach (var childRef in node.Children)
            {
                var childId = ToRowId(childRef);
                if (childRef.Table != BehaviorTreeTables.BehaviorNode)
                {
                    issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, id, "Child references must point to BehaviorNodeConfig."));
                    continue;
                }

                parentCounts.TryGetValue(childId, out var count);
                parentCounts[childId] = count + 1;
                Visit(childId);
            }
            visiting.Remove(id);
        }

        Visit(root);

        foreach (var pair in parentCounts.Where(pair => pair.Value > 1))
            issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, pair.Key, "Node has multiple parents in one behavior tree."));

        foreach (var nodeId in OwnedNodes(treeId))
            if (!visited.Contains(nodeId))
                issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, nodeId, "Node is owned by this tree but is unreachable from the root."));

        return issues;
    }

    public IReadOnlyList<BehaviorTreeValidationIssue> ValidateAll()
    {
        var issues = new List<BehaviorTreeValidationIssue>();
        var treeIds = new HashSet<RowId>(_db.FindAssets<BehaviorTreeConfig>());
        foreach (var treeId in treeIds)
            issues.AddRange(ValidateTree(treeId));

        foreach (var nodeId in _db.FindAssets<BehaviorNodeConfig>())
        {
            var node = _db.LoadAsset<BehaviorNodeConfig>(nodeId);
            var owner = ToRowId(node.OwnerTree);
            if (!IsTreeRef(owner))
            {
                issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, nodeId, "Node has no owner tree."));
            }
            else if (!treeIds.Contains(owner))
            {
                issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, nodeId, "Node owner tree is missing."));
            }
        }

        return issues;
    }

    public bool MigrateWorkbook()
    {
        var changed = false;
        var claimed = new Dictionary<RowId, RowId>();
        using var tx = _db.BeginEdit("Migrate behavior tree ownership");

        foreach (var treeId in _db.FindAssets<BehaviorTreeConfig>())
        {
            var tree = _db.LoadAsset<BehaviorTreeConfig>(treeId);
            var root = ToRowId(tree.Root);
            if (!IsNodeRef(root))
                continue;

            var effectiveRoot = MigrateNode(tx, treeId, root, claimed, new HashSet<RowId>(), ref changed);
            if (effectiveRoot != root)
            {
                tx.GetMutable<BehaviorTreeConfig>(treeId).Root = ToRef(effectiveRoot);
                changed = true;
            }
        }

        tx.Commit();
        return changed;
    }

    BehaviorTreeConfig RequireTree(RowId treeId) =>
        treeId.Table == BehaviorTreeTables.BehaviorTreeTable && _db.TryLoadAsset<BehaviorTreeConfig>(treeId, out var tree)
            ? tree
            : throw new InvalidOperationException("Behavior tree " + treeId + " does not exist.");

    BehaviorNodeConfig RequireOwnedNode(RowId treeId, RowId nodeId)
    {
        if (!_db.TryLoadAsset<BehaviorNodeConfig>(nodeId, out var node))
            throw new InvalidOperationException("Behavior node " + nodeId + " does not exist.");
        if (!SameRef(node.OwnerTree, treeId))
            throw new InvalidOperationException("Behavior node " + nodeId + " is not owned by tree " + treeId + ".");
        return node;
    }

    (RowId Id, BehaviorNodeConfig Draft) CreateNode(
        ExcelDb.Editing.IEditTransaction tx,
        RowId treeId,
        string kind,
        int x,
        int y,
        string keyBase)
    {
        var (id, draft) = tx.Create<BehaviorNodeConfig>();
        draft.Key = UniqueNodeKey(keyBase + "_" + id.Id.ToString(CultureInfo.InvariantCulture), ExistingNodeKeys());
        draft.DisplayName = kind;
        draft.Kind = kind;
        draft.Action = kind == "Action" ? FirstActionName("Action") : kind == "Condition" ? FirstActionName("Condition") : string.Empty;
        draft.X = x;
        draft.Y = y;
        draft.OwnerTree = ToRef(treeId);
        return (id, draft);
    }

    RowId DuplicateExistingSubtree(
        ExcelDb.Editing.IEditTransaction tx,
        RowId treeId,
        RowId sourceId,
        HashSet<string> usedKeys,
        Dictionary<RowId, RowId> copied)
    {
        if (copied.TryGetValue(sourceId, out var existing))
            return existing;
        var source = _db.LoadAsset<BehaviorNodeConfig>(sourceId);
        var (copyId, copy) = tx.Create<BehaviorNodeConfig>();
        copied[sourceId] = copyId;

        copy.Key = UniqueNodeKey(source.Key + "_copy_" + copyId.Id.ToString(CultureInfo.InvariantCulture), usedKeys);
        copy.DisplayName = source.DisplayName;
        copy.Kind = source.Kind;
        copy.Action = source.Action;
        copy.X = source.X + 40;
        copy.Y = source.Y + 40;
        copy.OwnerTree = ToRef(treeId);
        foreach (var childRef in source.Children)
        {
            var childId = ToRowId(childRef);
            copy.Children.Add(IsNodeRef(childId) && _db.TryLoadAsset<BehaviorNodeConfig>(childId, out _)
                ? ToRef(DuplicateExistingSubtree(tx, treeId, childId, usedKeys, copied))
                : childRef.Clone());
        }
        return copyId;
    }

    RowId MigrateNode(
        ExcelDb.Editing.IEditTransaction tx,
        RowId treeId,
        RowId nodeId,
        Dictionary<RowId, RowId> claimed,
        HashSet<RowId> visiting,
        ref bool changed)
    {
        if (!IsNodeRef(nodeId) || !_db.TryLoadAsset<BehaviorNodeConfig>(nodeId, out var node))
            return nodeId;
        if (claimed.TryGetValue(nodeId, out var claimedTree) && claimedTree != treeId)
        {
            changed = true;
            return DuplicateExistingSubtree(tx, treeId, nodeId, ExistingNodeKeys(), new Dictionary<RowId, RowId>());
        }

        var owner = ToRowId(node.OwnerTree);
        if (IsTreeRef(owner) && owner != treeId)
        {
            claimed[nodeId] = owner;
            changed = true;
            return DuplicateExistingSubtree(tx, treeId, nodeId, ExistingNodeKeys(), new Dictionary<RowId, RowId>());
        }

        claimed[nodeId] = treeId;
        if (!SameRef(node.OwnerTree, treeId))
        {
            tx.GetMutable<BehaviorNodeConfig>(nodeId).OwnerTree = ToRef(treeId);
            changed = true;
        }

        if (!visiting.Add(nodeId))
            return nodeId;

        for (var i = 0; i < node.Children.Count; i++)
        {
            var childId = ToRowId(node.Children[i]);
            var effectiveChild = MigrateNode(tx, treeId, childId, claimed, visiting, ref changed);
            if (effectiveChild != childId)
            {
                tx.GetMutable<BehaviorNodeConfig>(nodeId).Children[i] = ToRef(effectiveChild);
                changed = true;
            }
        }
        visiting.Remove(nodeId);
        return nodeId;
    }

    HashSet<string> ExistingNodeKeys() =>
        new(_db.FindAssets<BehaviorNodeConfig>()
            .Select(id => _db.LoadAsset<BehaviorNodeConfig>(id).Key)
            .Where(key => !string.IsNullOrWhiteSpace(key)), StringComparer.Ordinal);

    string UniqueTreeKey(string desired)
    {
        var keys = new HashSet<string>(_db.FindAssets<BehaviorTreeConfig>()
            .Select(id => _db.LoadAsset<BehaviorTreeConfig>(id).Key), StringComparer.Ordinal);
        return UniqueKey(desired, keys);
    }

    static string UniqueNodeKey(string desired, HashSet<string> usedKeys) => UniqueKey(desired, usedKeys);

    static string UniqueKey(string desired, HashSet<string> usedKeys)
    {
        var baseKey = SanitizeKey(desired);
        var candidate = baseKey;
        var suffix = 2;
        while (!usedKeys.Add(candidate))
            candidate = baseKey + "_" + suffix++.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    static string SanitizeKey(string text)
    {
        var chars = text.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray();
        var key = new string(chars).Trim('_').ToLowerInvariant();
        return key.Length == 0 ? "behavior" : key;
    }

    IReadOnlyList<RowId> OwnedNodes(RowId treeId) =>
        _db.FindAssets<BehaviorNodeConfig>()
            .Where(id => SameRef(_db.LoadAsset<BehaviorNodeConfig>(id).OwnerTree, treeId))
            .ToArray();

    HashSet<RowId> CollectSubtree(RowId root)
    {
        var result = new HashSet<RowId>();
        void Visit(RowId id)
        {
            if (!result.Add(id) || !_db.TryLoadAsset<BehaviorNodeConfig>(id, out var node))
                return;
            foreach (var child in node.Children)
                Visit(ToRowId(child));
        }
        Visit(root);
        return result;
    }

    IReadOnlyList<RowId> ParentsOf(RowId treeId, RowId childId) =>
        OwnedNodes(treeId)
            .Where(id => _db.LoadAsset<BehaviorNodeConfig>(id).Children.Any(child => ToRowId(child) == childId))
            .ToArray();

    IReadOnlyList<RowId> ParentsOfSubtree(RowId treeId, HashSet<RowId> subtree) =>
        OwnedNodes(treeId)
            .Where(id => _db.LoadAsset<BehaviorNodeConfig>(id).Children.Any(child => subtree.Contains(ToRowId(child))))
            .ToArray();

    bool IsRoot(RowId treeId, RowId nodeId)
    {
        var tree = RequireTree(treeId);
        return ToRowId(tree.Root) == nodeId;
    }

    void ValidateNodeShape(RowId id, BehaviorNodeConfig node, List<BehaviorTreeValidationIssue> issues)
    {
        if (!_actions.IsKnownKind(node.Kind))
            issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, id, "Unknown node kind '" + node.Kind + "'."));

        switch (node.Kind)
        {
            case "Action":
            case "Condition":
                if (node.Children.Count > 0)
                    issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, id, node.Kind + " nodes cannot have children."));
                if (string.IsNullOrWhiteSpace(node.Action))
                    issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, id, node.Kind + " node must select an action."));
                else if (!_actions.IsKnownAction(node.Kind, node.Action))
                    issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, id, "Unknown " + node.Kind + " action '" + node.Action + "'."));
                break;
            case "Decorator":
                if (node.Children.Count > 1)
                    issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Error, id, "Decorator nodes can have at most one child."));
                if (node.Children.Count == 0)
                    issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Warning, id, "Decorator node has no child."));
                break;
            case "Selector":
            case "Sequence":
                if (node.Children.Count == 0)
                    issues.Add(new BehaviorTreeValidationIssue(BehaviorTreeIssueSeverity.Warning, id, node.Kind + " node has no children."));
                break;
        }
    }

    void EnsureKnownKind(string kind)
    {
        if (!_actions.IsKnownKind(kind.Trim()))
            throw new InvalidOperationException("Unknown behavior node kind '" + kind + "'.");
    }

    void EnsureActionAllowed(string kind, string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return;
        if ((kind == "Action" || kind == "Condition") && !_actions.IsKnownAction(kind, action))
            throw new InvalidOperationException("Unknown " + kind + " action '" + action + "'.");
    }

    static void EnsureCanHaveChild(BehaviorNodeConfig node, int childCount)
    {
        if ((node.Kind == "Action" || node.Kind == "Condition") && childCount > 0)
            throw new InvalidOperationException(node.Kind + " nodes cannot have children.");
        if (node.Kind == "Decorator" && childCount > 1)
            throw new InvalidOperationException("Decorator nodes can have only one child.");
    }

    static void EnsureCanChangeKind(BehaviorNodeConfig node, string newKind)
    {
        if ((newKind == "Action" || newKind == "Condition") && node.Children.Count > 0)
            throw new InvalidOperationException("Remove children before changing this node to " + newKind + ".");
        if (newKind == "Decorator" && node.Children.Count > 1)
            throw new InvalidOperationException("Decorator nodes can have only one child.");
    }

    string FirstActionName(string kind) =>
        _actions.ActionsForKind(kind).FirstOrDefault(action => !action.IsDeprecated)?.Name ?? string.Empty;

    static void RemoveChildRef(BehaviorNodeConfig parent, RowId childId)
    {
        for (var i = parent.Children.Count - 1; i >= 0; i--)
            if (ToRowId(parent.Children[i]) == childId)
                parent.Children.RemoveAt(i);
    }

    static int IndexOfChild(BehaviorNodeConfig parent, RowId childId)
    {
        for (var i = 0; i < parent.Children.Count; i++)
            if (ToRowId(parent.Children[i]) == childId)
                return i;
        return -1;
    }

    static RowId ToRowId(RowRef? reference) =>
        reference == null ? default : new RowId(reference.Table, reference.Id);

    static RowRef ToRef(RowId id) => new() { Table = id.Table.Number, Id = id.Id };

    static bool SameRef(RowRef? reference, RowId id) =>
        reference != null && reference.Table == id.Table.Number && reference.Id == id.Id;

    static bool IsTreeRef(RowId id) => id.Table.Number == BehaviorTreeTables.BehaviorTree && id.Id > 0;

    static bool IsNodeRef(RowId id) => id.Table.Number == BehaviorTreeTables.BehaviorNode && id.Id > 0;
}
