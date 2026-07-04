using System.Linq;
using ExcelDb;
using ExcelDb.Database;
using Game.Configs;
using Xunit;

namespace ExcelDb.Core.Tests;

public class ReadTests
{
    [Fact]
    public void LoadAsset_ReturnsSharedResidentInstance()
    {
        var db = Fixture.Open(out _, out _);
        var a = db.LoadAsset<CharacterConfig>(Fixture.Hero);
        var b = db.LoadAsset<CharacterConfig>(Fixture.Hero);
        Assert.Same(a, b);
        Assert.Equal("Hero", a.Name);
        Assert.Equal(100, a.Hp);
    }

    [Fact]
    public void Find_BySingleAndMultiKey()
    {
        var db = Fixture.Open(out _, out _);
        Assert.Equal(Fixture.Hero, db.Find<CharacterConfig>("hero"));
        Assert.Equal(new RowId(Fixture.LevelTable, 2), db.Find<LevelConfig>(1, "hard"));
        Assert.False(db.TryFind<LevelConfig>(out _, 9, "hard"));
    }

    [Fact]
    public void FindAssets_EnumeratesInIdOrder()
    {
        var db = Fixture.Open(out _, out _);
        Assert.Equal(new[] { 1, 2 }, db.FindAssets<CharacterConfig>().Select(r => r.Id));
    }

    [Fact]
    public void Describe_UsesTableNameAndKey()
    {
        var db = Fixture.Open(out _, out _);
        Assert.Equal("CharacterConfig#1 (hero)", db.Describe(Fixture.Hero));
        Assert.Equal("LevelConfig#2 (1|hard)", db.Describe(new RowId(Fixture.LevelTable, 2)));
    }

    [Fact]
    public void LoadAsset_MissingRow_Throws()
    {
        var db = Fixture.Open(out _, out _);
        Assert.Throws<RowNotFoundException>(() => db.LoadAsset(new RowId(Fixture.CharacterTable, 99)));
    }
}

public class EditTests
{
    [Fact]
    public void Commit_UpdatesResidentInPlace_HoldersSeeNewValue()
    {
        var db = Fixture.Open(out _, out _);
        var held = db.LoadAsset<CharacterConfig>(Fixture.Hero);

        using (var tx = db.BeginEdit("hp up"))
        {
            tx.GetMutable<CharacterConfig>(Fixture.Hero).Hp = 150;
            tx.Commit();
        }

        Assert.Equal(150, held.Hp);
        Assert.Same(held, db.LoadAsset<CharacterConfig>(Fixture.Hero));
    }

    [Fact]
    public void MutatingDraft_WithoutCommit_ChangesNothing()
    {
        var db = Fixture.Open(out _, out _);
        using (var tx = db.BeginEdit("abandoned"))
            tx.GetMutable<CharacterConfig>(Fixture.Hero).Hp = 999;

        Assert.Equal(100, db.LoadAsset<CharacterConfig>(Fixture.Hero).Hp);
        Assert.False(db.CanUndo);
    }

    [Fact]
    public void UnchangedDraft_ProducesNoUndoEntry()
    {
        var db = Fixture.Open(out _, out _);
        using (var tx = db.BeginEdit("no-op"))
        {
            tx.GetMutable<CharacterConfig>(Fixture.Hero);
            tx.Commit();
        }
        Assert.False(db.CanUndo);
    }

    [Fact]
    public void UndoRedo_GlobalStackAcrossTables()
    {
        var db = Fixture.Open(out _, out _);
        var hero = db.LoadAsset<CharacterConfig>(Fixture.Hero);
        var fireball = db.LoadAsset<SkillConfig>(Fixture.Fireball);

        using (var tx = db.BeginEdit("hero hp"))
        {
            tx.GetMutable<CharacterConfig>(Fixture.Hero).Hp = 150;
            tx.Commit();
        }
        using (var tx = db.BeginEdit("skill cost"))
        {
            tx.GetMutable<SkillConfig>(Fixture.Fireball).Cost = 99;
            tx.Commit();
        }

        Assert.True(db.Undo());          // skill cost back
        Assert.Equal(10, fireball.Cost);
        Assert.Equal(150, hero.Hp);

        Assert.True(db.Undo());          // hero hp back
        Assert.Equal(100, hero.Hp);

        Assert.True(db.Redo());
        Assert.Equal(150, hero.Hp);
        Assert.True(db.Redo());
        Assert.Equal(99, fireball.Cost);
        Assert.False(db.Redo());
    }

    [Fact]
    public void CreateAndDelete_WithUndo()
    {
        var db = Fixture.Open(out _, out _);

        RowId created;
        using (var tx = db.BeginEdit("new character"))
        {
            var (id, draft) = tx.Create<CharacterConfig>();
            draft.Key = "npc";
            draft.Hp = 5;
            created = id;
            tx.Commit();
        }
        Assert.Equal(3, created.Id);   // ids continue after high-water mark
        Assert.Equal(5, db.LoadAsset<CharacterConfig>(created).Hp);
        Assert.Equal(created, db.Find<CharacterConfig>("npc"));

        using (var tx = db.BeginEdit("delete npc"))
        {
            tx.Delete(created);
            tx.Commit();
        }
        Assert.False(db.TryLoadAsset(created, out _));

        Assert.True(db.Undo());   // undo delete -> row back
        Assert.Equal(5, db.LoadAsset<CharacterConfig>(created).Hp);

        Assert.True(db.Undo());   // undo create -> row gone
        Assert.False(db.TryLoadAsset(created, out _));
        Assert.False(db.TryFind<CharacterConfig>(out _, "npc"));
    }

    [Fact]
    public void KeyConflict_ThrowsAndRollsBack()
    {
        var db = Fixture.Open(out _, out _);
        using var tx = db.BeginEdit("rename");
        tx.GetMutable<CharacterConfig>(Fixture.Villain).Key = "hero";

        Assert.Throws<DataValidationException>(tx.Commit);
        Assert.Equal("villain", db.LoadAsset<CharacterConfig>(Fixture.Villain).Key);
        Assert.False(db.CanUndo);
    }

    [Fact]
    public void KeyRename_ReindexesLookup()
    {
        var db = Fixture.Open(out _, out _);
        using (var tx = db.BeginEdit("rename"))
        {
            tx.GetMutable<CharacterConfig>(Fixture.Hero).Key = "hero_v2";
            tx.Commit();
        }
        Assert.Equal(Fixture.Hero, db.Find<CharacterConfig>("hero_v2"));
        Assert.False(db.TryFind<CharacterConfig>(out _, "hero"));

        db.Undo();
        Assert.Equal(Fixture.Hero, db.Find<CharacterConfig>("hero"));
    }

    [Fact]
    public void SaveAssets_WritesOnlyDirtyTables()
    {
        var db = Fixture.Open(out var loader, out _);
        using (var tx = db.BeginEdit("hp"))
        {
            tx.GetMutable<CharacterConfig>(Fixture.Hero).Hp = 123;
            tx.Commit();
        }

        db.SaveAssets();
        var write = Assert.Single(loader.Writes);
        Assert.Equal(Fixture.CharacterTable, write.Table.Number);
        var savedHero = (CharacterConfig)write.Content.Rows.Single(r => r.Id == 1).Row;
        Assert.Equal(123, savedHero.Hp);

        db.SaveAssets();   // nothing dirty anymore
        Assert.Single(loader.Writes);
    }
}

public class PolicyTests
{
    [Fact]
    public void Throw_FailsFastOnReadOnlyEdit()
    {
        var db = Fixture.Open(out var loader, out _, UnsupportedPolicy.Throw);
        loader.IsWritable = false;
        Assert.Throws<ExcelDb.UnsupportedOperationException>(() => db.BeginEdit("x"));
    }

    [Fact]
    public void Silent_EditIsFullyUsableNoOp_AndCounted()
    {
        var db = Fixture.Open(out var loader, out _, UnsupportedPolicy.Silent);
        loader.IsWritable = false;

        string? ignored = null;
        db.OnOperationIgnored += op => ignored = op;

        using (var tx = db.BeginEdit("hp up"))
        {
            var draft = tx.GetMutable<CharacterConfig>(Fixture.Hero);
            draft.Hp = 999;               // draft is fully usable
            Assert.Equal(999, draft.Hp);
            tx.Commit();                  // silently dropped
        }

        Assert.Equal(100, db.LoadAsset<CharacterConfig>(Fixture.Hero).Hp);
        Assert.Equal("Edit", ignored);
        Assert.True(db.Info.IgnoredOperations["BeginEdit"] >= 1);
        Assert.True(db.Info.IgnoredOperations["Edit"] >= 1);
        Assert.False(db.CanUndo);
    }

    [Fact]
    public void Silent_SaveAssetsKeepsTableDirty()
    {
        var db = Fixture.Open(out var loader, out _, UnsupportedPolicy.Silent);
        using (var tx = db.BeginEdit("hp"))
        {
            tx.GetMutable<CharacterConfig>(Fixture.Hero).Hp = 111;
            tx.Commit();
        }

        loader.IsWritable = false;
        db.SaveAssets();
        Assert.Empty(loader.Writes);
        Assert.Contains("CharacterConfig", db.Info.DirtyTables);

        loader.IsWritable = true;
        db.SaveAssets();
        Assert.Single(loader.Writes);
        Assert.Empty(db.Info.DirtyTables);
    }
}
