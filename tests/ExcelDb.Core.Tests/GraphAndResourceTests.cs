using System.Linq;
using ExcelDb;
using Game.Configs;
using Xunit;

namespace ExcelDb.Core.Tests;

public class DependencyGraphTests
{
    [Fact]
    public void Recursive_TraversesNestedAndTerminatesOnCycle()
    {
        var db = Fixture.Open(out _, out _);
        var deps = db.GetDependencies(Fixture.Hero, recursive: true);

        // hero -> fireball -> burn(payload) & icenova(chain); icenova -> fireball closes the cycle.
        Assert.Equal(
            new[] { Fixture.Fireball, Fixture.Icenova, Fixture.Burn }.OrderBy(r => r.ToString()),
            deps.Rows.OrderBy(r => r.ToString()));

        Assert.Equal(
            new[] { "unity:icon_hero", "unity:vfx_burn" },
            deps.Externals.Select(e => e.ToString()).OrderBy(s => s));
    }

    [Fact]
    public void NonRecursive_StopsAtDirectReferences()
    {
        var db = Fixture.Open(out _, out _);
        var deps = db.GetDependencies(Fixture.Hero, recursive: false);
        Assert.Equal(new[] { Fixture.Fireball }, deps.Rows);
        Assert.Equal(new[] { "unity:icon_hero" }, deps.Externals.Select(e => e.ToString()));
    }

    [Fact]
    public void SelfCycle_TerminatesAndListsBothSides()
    {
        var db = Fixture.Open(out _, out _);
        var deps = db.GetDependencies(Fixture.Fireball, recursive: true);
        Assert.Contains(Fixture.Icenova, deps.Rows);
        Assert.DoesNotContain(Fixture.Fireball, deps.Rows);   // root excluded
    }

    [Fact]
    public void InboundReferences_ListReferrers()
    {
        var db = Fixture.Open(out _, out _);
        Assert.Equal(new[] { Fixture.Fireball }, db.GetInboundReferences(Fixture.Burn));
        Assert.Equal(new[] { Fixture.Hero }, db.GetInboundReferences(Fixture.Fireball).Where(r => r.Table.Number == Fixture.CharacterTable));
    }

    [Fact]
    public void EditingRefs_UpdatesGraphIncrementally()
    {
        var db = Fixture.Open(out _, out _);
        using (var tx = db.BeginEdit("retarget payload"))
        {
            var skill = tx.GetMutable<SkillConfig>(Fixture.Fireball);
            skill.Payload.Table = Fixture.BulletTable;
            skill.Payload.Id = 1;
            tx.Commit();
        }

        Assert.Empty(db.GetInboundReferences(Fixture.Burn));
        Assert.Equal(new[] { Fixture.Fireball }, db.GetInboundReferences(Fixture.Pellet));

        db.Undo();
        Assert.Equal(new[] { Fixture.Fireball }, db.GetInboundReferences(Fixture.Burn));
    }
}

public class ResourceScopeTests
{
    [Fact]
    public async System.Threading.Tasks.Task PreloadRow_AcquiresNestedExternals()
    {
        var db = Fixture.Open(out _, out var unity);
        using var scope = db.CreateScope("battle").PreloadRow(Fixture.Hero, recursive: true);
        await scope.WaitAll();

        Assert.Equal(2, scope.Count);   // icon_hero + vfx_burn
        Assert.Equal("res:unity:icon_hero", scope.Get<string>(new ExternalKey("unity", "icon_hero")));
        Assert.Equal(1, unity.Loads[new ExternalKey("unity", "vfx_burn")]);
    }

    [Fact]
    public void SharedAsset_UnloadedOnlyWhenLastScopeCloses()
    {
        var db = Fixture.Open(out _, out var unity);
        var icon = new ExternalKey("unity", "icon_hero");

        var a = db.CreateScope("a");
        var b = db.CreateScope("b");
        a.Acquire(icon);
        b.Acquire(icon);

        Assert.Equal(1, unity.Loads[icon]);   // in-flight dedupe: one load
        a.Dispose();
        Assert.False(unity.Unloads.ContainsKey(icon));
        b.Dispose();
        Assert.Equal(1, unity.Unloads[icon]);
    }

    [Fact]
    public void SameKeyTwiceInOneScope_CountsOnce()
    {
        var db = Fixture.Open(out _, out var unity);
        var icon = new ExternalKey("unity", "icon_hero");

        var scope = db.CreateScope("s");
        scope.Acquire(icon);
        scope.Acquire(icon);
        Assert.Equal(1, scope.Count);

        scope.Dispose();
        Assert.Equal(1, unity.Unloads[icon]);
    }

    [Fact]
    public void MissingScheme_SilentPolicy_YieldsNullResource()
    {
        var db = Fixture.Open(out _, out _, UnsupportedPolicy.Silent);
        using var scope = db.CreateScope("s");
        var task = scope.Acquire(new ExternalKey("fmod", "event:/boom"));
        Assert.True(task.IsCompleted);
        Assert.Null(scope.Get<object>(new ExternalKey("fmod", "event:/boom")));
        Assert.True(db.Info.IgnoredOperations.ContainsKey("LoadExternal"));
    }

    [Fact]
    public void MissingScheme_ThrowPolicy_Throws()
    {
        var db = Fixture.Open(out _, out _, UnsupportedPolicy.Throw);
        using var scope = db.CreateScope("s");
        Assert.Throws<ExcelDb.UnsupportedOperationException>(
            () => { _ = scope.Acquire(new ExternalKey("fmod", "event:/boom")); });
    }
}
