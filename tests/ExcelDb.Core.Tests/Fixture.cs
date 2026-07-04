using System.Collections.Generic;
using System.Threading.Tasks;
using ExcelDb;
using ExcelDb.Database;
using ExcelDb.Loading;
using ExcelDb.Protocol;
using ExcelDb.Schema;
using Game.Configs;

namespace ExcelDb.Core.Tests;

/// <summary>Counts loads/unloads and resolves every key to "res:{scheme}:{id}".</summary>
public sealed class FakeExternalLoader : IExternalLoader
{
    public string Scheme { get; }
    public readonly Dictionary<ExternalKey, int> Loads = new();
    public readonly Dictionary<ExternalKey, int> Unloads = new();

    public FakeExternalLoader(string scheme = "unity") => Scheme = scheme;

    public ValueTask<object?> LoadAsync(ExternalKey key)
    {
        Loads.TryGetValue(key, out var n);
        Loads[key] = n + 1;
        return new ValueTask<object?>($"res:{key}");
    }

    public void Unload(ExternalKey key, object resource)
    {
        Unloads.TryGetValue(key, out var n);
        Unloads[key] = n + 1;
    }
}

public static class Fixture
{
    public const int CharacterTable = 1;
    public const int SkillTable = 2;
    public const int BuffTable = 3;
    public const int BulletTable = 4;
    public const int LevelTable = 5;

    public static SchemaRegistry NewRegistry() => SchemaRegistry.FromFiles(ConfigsReflection.Descriptor);

    static ExternalRef Ext(string id) => new() { Scheme = "unity", Id = id };
    static RowRef Ref(int table, int id) => new() { Table = table, Id = id };

    /// <summary>
    /// Standard content: hero -> fireball -> burn(payload) / icenova(chain),
    /// icenova -> fireball (cycle). Externals: hero icon, burn vfx.
    /// </summary>
    public static InMemoryTableLoader StandardLoader()
    {
        var characters = new TableContent(new List<TableRecord>
        {
            new(1, new CharacterConfig
            {
                Key = "hero", Name = "Hero", Hp = 100,
                Icon = Ext("icon_hero"),
                Skills = { Ref(SkillTable, 1) },
            }),
            new(2, new CharacterConfig { Key = "villain", Name = "Villain", Hp = 200 }),
        });

        var skills = new TableContent(new List<TableRecord>
        {
            new(1, new SkillConfig
            {
                Key = "fireball", Cost = 10,
                Payload = Ref(BuffTable, 1),
                Chains = { Ref(SkillTable, 2) },
            }),
            new(2, new SkillConfig
            {
                Key = "icenova", Cost = 20,
                Chains = { Ref(SkillTable, 1) },   // cycle: fireball <-> icenova
            }),
        });

        var buffs = new TableContent(new List<TableRecord>
        {
            new(1, new BuffConfig { Key = "burn", Duration = 3, Vfx = Ext("vfx_burn") }),
        });

        var bullets = new TableContent(new List<TableRecord>
        {
            new(1, new BulletConfig { Key = "pellet", Speed = 42 }),
        });

        var levels = new TableContent(new List<TableRecord>
        {
            new(1, new LevelConfig { Stage = 1, Difficulty = "easy", HpScale = 100 }),
            new(2, new LevelConfig { Stage = 1, Difficulty = "hard", HpScale = 250, Bosses = { Ref(CharacterTable, 2) } }),
        });

        return new InMemoryTableLoader()
            .Add(new TableId(CharacterTable), characters)
            .Add(new TableId(SkillTable), skills)
            .Add(new TableId(BuffTable), buffs)
            .Add(new TableId(BulletTable), bullets)
            .Add(new TableId(LevelTable), levels);
    }

    public static ConfigDatabase Open(
        out InMemoryTableLoader loader,
        out FakeExternalLoader unity,
        UnsupportedPolicy policy = UnsupportedPolicy.Throw)
    {
        var tables = StandardLoader();
        var external = new FakeExternalLoader();
        loader = tables;
        unity = external;
        return ConfigDatabase.Open(
            NewRegistry(),
            packs => packs.Mount("base", tables).MountExternal(external),
            new DatabaseOptions { UnsupportedOperation = policy });
    }

    public static RowId Hero => new(CharacterTable, 1);
    public static RowId Villain => new(CharacterTable, 2);
    public static RowId Fireball => new(SkillTable, 1);
    public static RowId Icenova => new(SkillTable, 2);
    public static RowId Burn => new(BuffTable, 1);
    public static RowId Pellet => new(BulletTable, 1);
}
