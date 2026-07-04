using System.Collections.Generic;
using System.Linq;
using ExcelDb;
using ExcelDb.Database;
using ExcelDb.Loading;
using ExcelDb.Protocol;
using Game.Configs;
using Xunit;

namespace ExcelDb.Core.Tests;

public class ClipboardTests
{
    [Fact]
    public void PasteSubtree_RemapsInternalRefs_SuffixesKeys()
    {
        var db = Fixture.Open(out _, out _);
        var clip = db.Copy(new[] { Fixture.Hero }, CopyDepth.Subtree);
        Assert.Equal(4, clip.Count);   // hero + fireball + icenova + burn (cycle-safe)

        var pasted = db.Paste(clip);
        Assert.Equal(4, pasted.Count);

        var newHero = db.LoadAsset<CharacterConfig>(pasted[0]);
        Assert.Equal("hero_copy", newHero.Key);

        // Internal refs remapped onto the pasted set.
        var newFireball = db.LoadAsset<SkillConfig>(new RowId(newHero.Skills[0].Table, newHero.Skills[0].Id));
        Assert.Equal("fireball_copy", newFireball.Key);
        Assert.NotEqual(Fixture.Fireball.Id, newHero.Skills[0].Id);

        var newBurn = db.LoadAsset<BuffConfig>(new RowId(newFireball.Payload.Table, newFireball.Payload.Id));
        Assert.Equal("burn_copy", newBurn.Key);

        // The copied cycle is remapped too: new icenova chains back to new fireball.
        var newIcenova = db.LoadAsset<SkillConfig>(new RowId(newFireball.Chains[0].Table, newFireball.Chains[0].Id));
        Assert.Equal(newHero.Skills[0].Id, newIcenova.Chains[0].Id);

        // One undo unit reverts the whole paste.
        db.Undo();
        Assert.False(db.TryLoadAsset(pasted[0], out _));
    }

    [Fact]
    public void ShallowDuplicate_KeepsOutwardRefs()
    {
        var db = Fixture.Open(out _, out _);
        var copy = db.Duplicate(Fixture.Hero);

        var duplicated = db.LoadAsset<CharacterConfig>(copy);
        Assert.Equal("hero_copy", duplicated.Key);
        Assert.Equal(Fixture.Fireball.Id, duplicated.Skills[0].Id);   // outward ref untouched
        Assert.Equal(100, duplicated.Hp);
    }

    [Fact]
    public void Paste_AcrossDatabaseInstances()
    {
        var source = Fixture.Open(out _, out _);
        var target = Fixture.Open(out _, out _);

        var clip = source.Copy(new[] { Fixture.Villain });
        var pasted = target.Paste(clip);

        var row = target.LoadAsset<CharacterConfig>(pasted[0]);
        Assert.Equal("villain_copy", row.Key);   // "villain" already exists in target
        Assert.Equal(200, row.Hp);
    }

    [Fact]
    public void ValidationGate_IsCleanOnFixture()
    {
        var db = Fixture.Open(out _, out _);
        Assert.True(db.Validate().IsClean);
    }
}

public class ValidationTests
{
    [Fact]
    public void DanglingReference_Reported()
    {
        var db = Fixture.Open(out _, out _);
        using (var tx = db.BeginEdit("delete burn"))
        {
            tx.Delete(Fixture.Burn);
            tx.Commit();
        }

        var report = db.Validate();
        Assert.False(report.IsClean);
        Assert.Contains(report.Issues, i => i.Message.Contains("dangling") && i.Message.Contains("BuffConfig#1"));
    }

    [Fact]
    public void GroupConstraintViolation_Reported()
    {
        var db = Fixture.Open(out _, out _);
        using (var tx = db.BeginEdit("bad payload"))
        {
            var skill = tx.GetMutable<SkillConfig>(Fixture.Fireball);
            skill.Payload.Table = Fixture.CharacterTable;   // CharacterConfig does not implement EffectPayload
            skill.Payload.Id = 1;
            tx.Commit();
        }

        var report = db.Validate();
        Assert.Contains(report.Issues, i =>
            i.Severity == Validation.IssueSeverity.Error &&
            i.Message.Contains("EffectPayload") &&
            i.Message.Contains("CharacterConfig"));
    }

    [Fact]
    public void FixedTableViolation_Reported()
    {
        var db = Fixture.Open(out _, out _);
        using (var tx = db.BeginEdit("bad skill ref"))
        {
            var hero = tx.GetMutable<CharacterConfig>(Fixture.Hero);
            hero.Skills[0].Table = Fixture.BuffTable;   // skills must reference SkillConfig
            tx.Commit();
        }

        var report = db.Validate();
        Assert.Contains(report.Issues, i =>
            i.Severity == Validation.IssueSeverity.Error &&
            i.Message.Contains("must reference 'SkillConfig'"));
    }
}

public class HotReloadTests
{
    [Fact]
    public void Reload_MergesInPlace_HoldersSeeNewValues()
    {
        var db = Fixture.Open(out var loader, out _);
        var heldHero = db.LoadAsset<CharacterConfig>(Fixture.Hero);

        TableId? reloaded = null;
        db.OnTableReloaded += t => reloaded = t;

        loader.Replace(new TableId(Fixture.CharacterTable), new TableContent(new List<TableRecord>
        {
            new(1, new CharacterConfig { Key = "hero", Name = "Hero", Hp = 999 }),   // villain removed
            new(7, new CharacterConfig { Key = "newcomer", Hp = 1 }),                // added
        }));

        Assert.Equal(Fixture.CharacterTable, reloaded!.Value.Number);
        Assert.Equal(999, heldHero.Hp);                                   // same instance, new data
        Assert.Same(heldHero, db.LoadAsset<CharacterConfig>(Fixture.Hero));
        Assert.False(db.TryLoadAsset(Fixture.Villain, out _));            // deleted row detached
        Assert.Equal(1, db.LoadAsset<CharacterConfig>(new RowId(Fixture.CharacterTable, 7)).Hp);

        // High-water mark respects reloaded ids.
        using var tx = db.BeginEdit("create");
        var (id, _) = tx.Create<CharacterConfig>();
        Assert.Equal(8, id.Id);
    }

    [Fact]
    public void Reload_PrunesUndoEntriesTouchingTable_KeepsOthers()
    {
        var db = Fixture.Open(out var loader, out _);

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
        Assert.Equal(2, db.Info.UndoCount);

        loader.Replace(new TableId(Fixture.CharacterTable), new TableContent(new List<TableRecord>
        {
            new(1, new CharacterConfig { Key = "hero", Hp = 500 }),
        }));

        Assert.Equal(1, db.Info.UndoCount);
        Assert.True(db.Undo());   // the surviving skill entry
        Assert.Equal(10, db.LoadAsset<SkillConfig>(Fixture.Fireball).Cost);
        Assert.False(db.CanUndo);
        Assert.Equal(500, db.LoadAsset<CharacterConfig>(Fixture.Hero).Hp);   // untouched by undo
    }

    [Fact]
    public void ReloadFailure_KeepsOldData_RaisesEvent()
    {
        var db = Fixture.Open(out var loader, out _);
        System.Exception? failure = null;
        db.OnReloadFailed += (_, e) => failure = e;

        loader.Replace(new TableId(Fixture.CharacterTable), new TableContent(new List<TableRecord>
        {
            new(1, new CharacterConfig { Key = "dup", Hp = 1 }),
            new(1, new CharacterConfig { Key = "dup2", Hp = 2 }),   // duplicate id -> reject
        }));

        Assert.NotNull(failure);
        Assert.Equal(100, db.LoadAsset<CharacterConfig>(Fixture.Hero).Hp);   // untouched
        Assert.Equal("hero", db.LoadAsset<CharacterConfig>(Fixture.Hero).Key);
    }
}
