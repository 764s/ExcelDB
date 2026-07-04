using System.Linq;
using ExcelDb;
using ExcelDb.Protocol;
using Game.Configs;
using Game.Configs.BehaviorTrees;
using Xunit;

namespace ExcelDb.Core.Tests;

public class BehaviorTreeEditingServiceTests
{
    [Fact]
    public void CreateRoot_ForEmptyTree_SetsTreeRootAndOwner()
    {
        var db = Fixture.Open(out _, out _);
        var service = new BehaviorTreeEditingService(db);

        var treeId = service.CreateTree("empty_tree", "Empty Tree");
        var rootId = service.CreateRoot(treeId, "Selector");

        var tree = db.LoadAsset<BehaviorTreeConfig>(treeId);
        var root = db.LoadAsset<BehaviorNodeConfig>(rootId);
        Assert.Equal(rootId.Id, tree.Root.Id);
        Assert.Equal(Fixture.BehaviorNodeTable, tree.Root.Table);
        Assert.Equal(treeId.Id, root.OwnerTree.Id);
        Assert.Empty(service.ValidateTree(treeId).Where(issue => issue.IsError));
    }

    [Fact]
    public void AddChild_ReturnsCreatedNodeAndPersistsChildOrder()
    {
        var db = Fixture.Open(out _, out _);
        var service = new BehaviorTreeEditingService(db);

        var first = service.AddChild(Fixture.GuardTree, Fixture.GuardRoot, "Action");
        var second = service.AddChild(Fixture.GuardTree, Fixture.GuardRoot, "Action");
        service.MoveChild(Fixture.GuardTree, Fixture.GuardRoot, second, -1);

        var root = db.LoadAsset<BehaviorNodeConfig>(Fixture.GuardRoot);
        Assert.Contains(root.Children, child => child.Id == first.Id);
        Assert.True(root.Children.IndexOf(root.Children.Single(child => child.Id == second.Id)) <
                    root.Children.IndexOf(root.Children.Single(child => child.Id == first.Id)));
    }

    [Fact]
    public void Reparent_PreventsCycles()
    {
        var db = Fixture.Open(out _, out _);
        var service = new BehaviorTreeEditingService(db);

        Assert.Throws<InvalidOperationException>(() =>
            service.ReparentNode(Fixture.GuardTree, new RowId(Fixture.BehaviorNodeTable, 2), new RowId(Fixture.BehaviorNodeTable, 3)));
    }

    [Fact]
    public void Reparent_PreventsCrossTreeNodes()
    {
        var db = Fixture.Open(out _, out _);
        var service = new BehaviorTreeEditingService(db);

        var otherTree = service.CreateTree("other_tree", "Other Tree");
        var otherRoot = service.CreateRoot(otherTree, "Selector");

        Assert.Throws<InvalidOperationException>(() =>
            service.ReparentNode(Fixture.GuardTree, otherRoot, Fixture.GuardRoot));
    }

    [Fact]
    public void DeleteSubtree_DoesNotTouchOtherTree()
    {
        var db = Fixture.Open(out _, out _);
        var service = new BehaviorTreeEditingService(db);
        var otherTree = service.DuplicateTree(Fixture.GuardTree, "guard_ai_copy", "Guard AI Copy");
        var otherRoot = db.LoadAsset<BehaviorTreeConfig>(otherTree).Root.Id;

        service.DeleteNode(Fixture.GuardTree, new RowId(Fixture.BehaviorNodeTable, 2), BehaviorNodeDeleteMode.DeleteSubtree);

        Assert.False(db.TryLoadAsset(new RowId(Fixture.BehaviorNodeTable, 2), out _));
        Assert.False(db.TryLoadAsset(new RowId(Fixture.BehaviorNodeTable, 3), out _));
        Assert.True(db.TryLoadAsset(new RowId(Fixture.BehaviorNodeTable, otherRoot), out _));
        Assert.Empty(service.ValidateTree(otherTree).Where(issue => issue.IsError));
    }

    [Fact]
    public void DuplicateSubtree_CreatesOwnedSiblingWithoutSharingRows()
    {
        var db = Fixture.Open(out _, out _);
        var service = new BehaviorTreeEditingService(db);

        var duplicate = service.DuplicateSubtree(Fixture.GuardTree, new RowId(Fixture.BehaviorNodeTable, 2));
        var root = db.LoadAsset<BehaviorNodeConfig>(Fixture.GuardRoot);
        var duplicateNode = db.LoadAsset<BehaviorNodeConfig>(duplicate);

        Assert.Contains(root.Children, child => child.Id == duplicate.Id);
        Assert.Equal(Fixture.GuardTree.Id, duplicateNode.OwnerTree.Id);
        Assert.NotEqual(2, duplicate.Id);
        Assert.All(duplicateNode.Children, child => Assert.True(child.Id > 7));
    }

    [Fact]
    public void MigrateWorkbook_BackfillsMissingOwners()
    {
        var db = Fixture.Open(out _, out _);
        using (var tx = db.BeginEdit("clear owners"))
        {
            foreach (var nodeId in db.FindAssets<BehaviorNodeConfig>())
                tx.GetMutable<BehaviorNodeConfig>(nodeId).OwnerTree = new RowRef();
            tx.Commit();
        }

        var service = new BehaviorTreeEditingService(db);
        Assert.True(service.MigrateWorkbook());

        foreach (var nodeId in db.FindAssets<BehaviorNodeConfig>())
        {
            var node = db.LoadAsset<BehaviorNodeConfig>(nodeId);
            Assert.Equal(Fixture.BehaviorTreeTable, node.OwnerTree.Table);
            Assert.Equal(Fixture.GuardTree.Id, node.OwnerTree.Id);
        }
        Assert.Empty(service.ValidateAll().Where(issue => issue.IsError));
    }
}
