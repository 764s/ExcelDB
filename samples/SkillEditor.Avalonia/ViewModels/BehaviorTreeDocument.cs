using System.Collections.ObjectModel;
using System.Globalization;
using ExcelDb;
using ExcelDb.Database;
using ExcelDb.Excel;
using ExcelDb.Protocol;
using ExcelDb.Schema;
using Game.Configs;
using Game.Configs.BehaviorTrees;

namespace SkillEditor.Avalonia.ViewModels;

public sealed class BehaviorTreeDocument : NotifyObject
{
    readonly SchemaRegistry _registry = SchemaRegistry.FromFiles(ConfigsReflection.Descriptor);
    readonly IBehaviorActionCatalog _actionCatalog = new StaticBehaviorActionCatalog();
    ConfigDatabase? _db;
    BehaviorTreeEditingService? _service;

    string _workbookPath = Path.GetFullPath("behavior-trees.xlsx");
    string? _openedWorkbookPath;
    string _status = "Create or open a workbook, then edit the behavior tree graph.";
    BehaviorTreeAssetViewModel? _selectedTree;
    BehaviorNodeViewModel? _selectedNode;

    public ObservableCollection<BehaviorTreeAssetViewModel> Trees { get; } = new();
    public ObservableCollection<BehaviorNodeViewModel> Nodes { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();

    public string WorkbookPath
    {
        get => _workbookPath;
        set => SetProperty(ref _workbookPath, value);
    }

    public string? OpenedWorkbookPath
    {
        get => _openedWorkbookPath;
        private set => SetProperty(ref _openedWorkbookPath, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public BehaviorTreeAssetViewModel? SelectedTree
    {
        get => _selectedTree;
        set
        {
            if (!SetProperty(ref _selectedTree, value))
                return;

            RefreshNodes();
        }
    }

    public BehaviorNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_selectedNode == value)
                return;

            if (_selectedNode != null)
                _selectedNode.IsSelected = false;

            _selectedNode = value;
            if (_selectedNode != null)
                _selectedNode.IsSelected = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedNodeTitle));
            OnPropertyChanged(nameof(SelectedNodeKind));
            OnPropertyChanged(nameof(SelectedNodeAction));
            OnPropertyChanged(nameof(SelectedNodeKey));
        }
    }

    public string SelectedNodeTitle => SelectedNode?.Title ?? string.Empty;
    public string SelectedNodeKind => SelectedNode?.Kind ?? string.Empty;
    public string SelectedNodeAction => SelectedNode?.Action ?? string.Empty;
    public string SelectedNodeKey => SelectedNode?.Key ?? string.Empty;
    public IReadOnlyList<string> NodeKinds => _actionCatalog.NodeKinds;
    public bool HasWorkbook => _db != null;
    public bool CanUndo => _db?.CanUndo ?? false;
    public bool CanRedo => _db?.CanRedo ?? false;
    public bool HasDirtyTables => _db?.Info.DirtyTables.Count > 0;

    public IReadOnlyList<string> ActionOptionsFor(string kind) =>
        _actionCatalog.ActionsForKind(kind).Select(action => action.Name).ToArray();

    public void CreateSampleWorkbook()
    {
        if (File.Exists(WorkbookPath))
            throw new IOException("'" + WorkbookPath + "' already exists.");

        var loader = new ExcelTableLoader(WorkbookPath, _registry, new ExcelTableLoaderOptions { CreateIfMissing = true });
        foreach (var table in SampleWorkbookData.Create())
            loader.Write(table.Key, table.Value);

        OpenWorkbook();
        Status = "Created sample behavior tree workbook.";
    }

    public void OpenWorkbook()
    {
        var fullPath = Path.GetFullPath(WorkbookPath);
        var loader = new ExcelTableLoader(fullPath, _registry);
        _db = ConfigDatabase.Open(_registry, packs => packs.Mount("excel", loader));
        _service = new BehaviorTreeEditingService(_db, _actionCatalog);
        OpenedWorkbookPath = fullPath;
        WorkbookPath = fullPath;

        var migrated = _service.MigrateWorkbook();
        RefreshTrees();
        Issues.Clear();
        RefreshCapabilities();
        Status = migrated
            ? "Opened and migrated " + fullPath + ". Save to persist ownership metadata."
            : "Opened " + fullPath;
    }

    public void Save()
    {
        var db = RequireDatabase();
        if (!ValidateForSave())
            return;

        db.SaveAssets();
        RefreshCapabilities();
        Status = "Saved " + (OpenedWorkbookPath ?? WorkbookPath);
    }

    public void Undo()
    {
        if (RequireDatabase().Undo())
        {
            RefreshTrees(SelectedTree?.Id, SelectedNode?.Id);
            RefreshCapabilities();
            Status = "Undo complete.";
        }
    }

    public void Redo()
    {
        if (RequireDatabase().Redo())
        {
            RefreshTrees(SelectedTree?.Id, SelectedNode?.Id);
            RefreshCapabilities();
            Status = "Redo complete.";
        }
    }

    public void Validate()
    {
        FillIssues();
        Status = Issues.Count == 0
            ? "Validation clean."
            : "Validation found " + Issues.Count.ToString(CultureInfo.InvariantCulture) + " issue(s).";
    }

    public void ReportError(Exception ex)
    {
        Status = ex.Message;
    }

    public void CreateTree()
    {
        var id = RequireService().CreateTree("behavior_tree", "Behavior Tree");
        RefreshTrees(id, null);
        RefreshCapabilities();
        Status = "Created behavior tree.";
    }

    public void RenameSelectedTree(string title)
    {
        var tree = RequireSelectedTree();
        RequireService().RenameTree(tree.Id, title);
        RefreshTrees(tree.Id, SelectedNode?.Id);
        RefreshCapabilities();
        Status = "Renamed behavior tree.";
    }

    public void DuplicateSelectedTree()
    {
        var tree = RequireSelectedTree();
        var id = RequireService().DuplicateTree(tree.Id, tree.Key + "_copy", tree.Title + " Copy");
        RefreshTrees(id, null);
        RefreshCapabilities();
        Status = "Duplicated behavior tree.";
    }

    public void DeleteSelectedTree()
    {
        var tree = RequireSelectedTree();
        RequireService().DeleteTree(tree.Id);
        RefreshTrees();
        RefreshCapabilities();
        Status = "Deleted behavior tree.";
    }

    public IEnumerable<(BehaviorNodeViewModel Parent, BehaviorNodeViewModel Child)> Edges()
    {
        var byId = Nodes.ToDictionary(node => node.Id);
        foreach (var node in Nodes)
        foreach (var childId in node.Children)
            if (byId.TryGetValue(childId, out var child))
                yield return (node, child);
    }

    public void AddNode(string kind)
    {
        var tree = RequireSelectedTree();
        var service = RequireService();
        RowId id;
        if (SelectedNode == null)
            id = service.CreateRoot(tree.Id, kind);
        else
            id = service.AddChild(tree.Id, SelectedNode.Id, kind);

        RefreshNodes(id);
        RefreshCapabilities();
        Status = "Created " + kind + " node.";
    }

    public void DetachSelectedNode()
    {
        var tree = RequireSelectedTree();
        var node = RequireSelectedNode();
        RequireService().DeleteNode(tree.Id, node.Id, BehaviorNodeDeleteMode.DetachOnly);
        RefreshNodes();
        RefreshCapabilities();
        Status = "Detached node. Validation will require reparenting or deleting it.";
    }

    public void DeleteSelectedSubtree()
    {
        var tree = RequireSelectedTree();
        var node = RequireSelectedNode();
        RequireService().DeleteNode(tree.Id, node.Id, BehaviorNodeDeleteMode.DeleteSubtree);
        RefreshNodes();
        RefreshCapabilities();
        Status = "Deleted subtree.";
    }

    public void DuplicateSelectedSubtree()
    {
        var tree = RequireSelectedTree();
        var node = RequireSelectedNode();
        var duplicate = RequireService().DuplicateSubtree(tree.Id, node.Id);
        RefreshNodes(duplicate);
        RefreshCapabilities();
        Status = "Duplicated subtree.";
    }

    public void MoveSelectedNodeInOrder(int direction)
    {
        var tree = RequireSelectedTree();
        var node = RequireSelectedNode();
        var parent = RequireService().GetParent(tree.Id, node.Id);
        if (!parent.HasValue || parent.Value.Id == 0)
            return;

        RequireService().MoveChild(tree.Id, parent.Value, node.Id, direction);
        RefreshNodes(node.Id);
        RefreshCapabilities();
        Status = "Moved child order.";
    }

    public void ReparentSelectedNode(RowId parentId)
    {
        var tree = RequireSelectedTree();
        var node = RequireSelectedNode();
        RequireService().ReparentNode(tree.Id, node.Id, parentId);
        RefreshNodes(node.Id);
        RefreshCapabilities();
        Status = "Reparented node.";
    }

    public void CommitNodePosition(RowId nodeId, double x, double y)
    {
        var tree = RequireSelectedTree();
        RequireService().MoveNode(tree.Id, nodeId, (int)Math.Round(x), (int)Math.Round(y));
        RefreshNodes(nodeId);
        RefreshCapabilities();
        Status = "Moved node.";
    }

    public void SetNodeTitle(RowId nodeId, string value) =>
        SetNodeField(nodeId, nameof(BehaviorNodeConfig.DisplayName), value);

    public void SetNodeKind(RowId nodeId, string value) =>
        SetNodeField(nodeId, nameof(BehaviorNodeConfig.Kind), value);

    public void SetNodeAction(RowId nodeId, string value) =>
        SetNodeField(nodeId, nameof(BehaviorNodeConfig.Action), value);

    public IReadOnlyList<BehaviorNodeViewModel> ParentCandidates(RowId nodeId)
    {
        if (SelectedTree == null || _service == null)
            return Array.Empty<BehaviorNodeViewModel>();

        var ids = new HashSet<RowId>(_service.GetParentCandidates(SelectedTree.Id, nodeId));
        return Nodes.Where(node => ids.Contains(node.Id)).ToArray();
    }

    public RowId? ParentOf(RowId nodeId) =>
        SelectedTree == null || _service == null ? null : _service.GetParent(SelectedTree.Id, nodeId);

    void SetNodeField(RowId nodeId, string fieldName, string value)
    {
        var tree = RequireSelectedTree();
        RequireService().SetNodeField(tree.Id, nodeId, fieldName, value);
        RefreshNodes(nodeId);
        RefreshCapabilities();
        Status = "Edited node.";
    }

    bool ValidateForSave()
    {
        FillIssues();
        var hasError = Issues.Any(issue => issue.StartsWith("[Error]", StringComparison.Ordinal));
        if (hasError)
        {
            Status = "Save blocked: fix validation errors first.";
            return false;
        }
        return true;
    }

    void FillIssues()
    {
        var db = RequireDatabase();
        var service = RequireService();
        var report = db.Validate();
        Issues.Clear();
        foreach (var issue in report.Issues)
            Issues.Add(issue.ToString());
        foreach (var issue in service.ValidateAll())
            Issues.Add(issue.ToString());
    }

    void RefreshTrees(RowId? preferredTree = null, RowId? preferredNode = null)
    {
        Trees.Clear();
        if (_db == null)
            return;

        foreach (var id in _db.FindAssets<BehaviorTreeConfig>())
        {
            var tree = _db.LoadAsset<BehaviorTreeConfig>(id);
            Trees.Add(new BehaviorTreeAssetViewModel(id, tree.Key, tree.Title));
        }

        SelectedTree = preferredTree.HasValue
            ? Trees.FirstOrDefault(tree => tree.Id == preferredTree.Value) ?? Trees.FirstOrDefault()
            : Trees.FirstOrDefault();
        RefreshNodes(preferredNode);
    }

    void RefreshNodes(RowId? preferredNode = null)
    {
        var selectedId = preferredNode ?? SelectedNode?.Id;
        Nodes.Clear();

        if (_db == null || SelectedTree == null)
        {
            SelectedNode = null;
            return;
        }

        var tree = _db.LoadAsset<BehaviorTreeConfig>(SelectedTree.Id);
        var root = tree.Root == null ? default : new RowId(tree.Root.Table, tree.Root.Id);
        var visited = new HashSet<RowId>();
        AddNodeRecursive(root, visited);

        SelectedNode = selectedId.HasValue
            ? Nodes.FirstOrDefault(node => node.Id == selectedId.Value) ?? Nodes.FirstOrDefault()
            : Nodes.FirstOrDefault();
    }

    void AddNodeRecursive(RowId id, HashSet<RowId> visited)
    {
        if (!visited.Add(id) || !_db!.TryLoadAsset<BehaviorNodeConfig>(id, out var node))
            return;

        Nodes.Add(new BehaviorNodeViewModel(
            id,
            node.Key,
            node.DisplayName,
            node.Kind,
            node.Action,
            node.X,
            node.Y,
            node.Children.Select(child => new RowId(child.Table, child.Id)).ToArray()));

        foreach (var child in node.Children)
            AddNodeRecursive(new RowId(child.Table, child.Id), visited);
    }

    ConfigDatabase RequireDatabase() =>
        _db ?? throw new InvalidOperationException("Open or create a workbook first.");

    BehaviorTreeEditingService RequireService() =>
        _service ?? throw new InvalidOperationException("Open or create a workbook first.");

    BehaviorTreeAssetViewModel RequireSelectedTree() =>
        SelectedTree ?? throw new InvalidOperationException("Select a behavior tree first.");

    BehaviorNodeViewModel RequireSelectedNode() =>
        SelectedNode ?? throw new InvalidOperationException("Select a behavior node first.");

    void RefreshCapabilities()
    {
        OnPropertyChanged(nameof(HasWorkbook));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasDirtyTables));
    }
}
