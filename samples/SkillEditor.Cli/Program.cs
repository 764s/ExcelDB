using System.Globalization;
using ExcelDb;
using ExcelDb.Database;
using ExcelDb.Excel;
using ExcelDb.Loading;
using ExcelDb.Protocol;
using ExcelDb.Schema;
using Game.Configs;
using Game.Configs.BehaviorTrees;

var registry = SchemaRegistry.FromFiles(ConfigsReflection.Descriptor);

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0])
    {
        case "init" when args.Length == 2:
            InitWorkbook(args[1]);
            return 0;
        case "list" when args.Length == 3:
            ListRows(args[1], args[2]);
            return 0;
        case "set" when args.Length >= 6:
            SetField(args[1], args[2], args[3], args[4], string.Join(' ', args.Skip(5)));
            return 0;
        case "validate" when args.Length == 2:
            Validate(args[1]);
            return 0;
        case "migrate" when args.Length == 2:
            Migrate(args[1]);
            return 0;
        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

void InitWorkbook(string path)
{
    if (File.Exists(path))
        throw new IOException($"'{path}' already exists.");

    var loader = new ExcelTableLoader(path, registry, new ExcelTableLoaderOptions { CreateIfMissing = true });
    foreach (var table in SeedTables())
        loader.Write(table.Key, table.Value);

    Console.WriteLine($"Created {path}");
}

void ListRows(string path, string tableName)
{
    var db = Open(path);
    var schema = registry.Get(tableName);

    foreach (var id in db.FindAssets(schema.Id))
    {
        var row = db.LoadAsset(id);
        var key = schema.KeyFields.Count == 0
            ? string.Empty
            : string.Join("|", schema.KeyFields.Select(field => field.Accessor.GetValue(row)));
        Console.WriteLine($"{id.Id}\t{key}\t{db.Describe(id)}");
    }
}

void SetField(string path, string tableName, string selector, string fieldName, string value)
{
    var db = Open(path);
    var schema = registry.Get(tableName);
    var id = ResolveRow(db, schema, selector);
    var field = ExcelFieldCodec.FindField(schema.Descriptor, fieldName)
        ?? throw new ArgumentException($"'{tableName}' has no field '{fieldName}'.");

    using (var tx = db.BeginEdit($"Set {tableName}.{field.Name}"))
    {
        var draft = tx.GetMutable(id);
        ExcelFieldCodec.SetField(draft, field, value, registry);
        tx.Commit();
    }

    db.SaveAssets();
    Console.WriteLine($"Updated {db.Describe(id)}.{field.Name} = {value}");
}

void Validate(string path)
{
    var db = Open(path);
    var report = db.Validate();
    var behaviorIssues = new BehaviorTreeEditingService(db).ValidateAll();
    if (report.IsClean && behaviorIssues.All(issue => !issue.IsError))
    {
        Console.WriteLine("Clean");
        return;
    }

    Console.Write(report.ToString());
    foreach (var issue in behaviorIssues)
        Console.WriteLine(issue.ToString());
}

void Migrate(string path)
{
    var db = Open(path);
    var changed = new BehaviorTreeEditingService(db).MigrateWorkbook();
    if (changed)
        db.SaveAssets();
    Console.WriteLine(changed ? $"Migrated {path}" : $"No behavior-tree migration needed for {path}");
}

ConfigDatabase Open(string path)
{
    var loader = new ExcelTableLoader(path, registry);
    return ConfigDatabase.Open(registry, packs => packs.Mount("excel", loader));
}

RowId ResolveRow(ConfigDatabase db, TableSchema schema, string selector)
{
    if (selector.StartsWith("#", StringComparison.Ordinal))
        return new RowId(schema.Id, int.Parse(selector.Substring(1), CultureInfo.InvariantCulture));
    if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        return new RowId(schema.Id, id);
    if (schema.KeyFields.Count != 1)
        throw new ArgumentException($"'{schema.Name}' has {schema.KeyFields.Count} key fields; use #id for this command.");
    return db.Find(schema.Name, selector);
}

Dictionary<TableId, TableContent> SeedTables()
{
    static ExternalRef Ext(string id) => new() { Scheme = "unity", Id = id };
    static RowRef Ref(int table, int id) => new() { Table = table, Id = id };

    return new Dictionary<TableId, TableContent>
    {
        [new TableId(1)] = new TableContent(new List<TableRecord>
        {
            new(1, new CharacterConfig
            {
                Key = "hero",
                Name = "Hero",
                Hp = 100,
                Icon = Ext("icon_hero"),
                Skills = { Ref(2, 1) },
            }),
            new(2, new CharacterConfig { Key = "villain", Name = "Villain", Hp = 200 }),
        }),
        [new TableId(2)] = new TableContent(new List<TableRecord>
        {
            new(1, new SkillConfig
            {
                Key = "fireball",
                Cost = 10,
                Payload = Ref(3, 1),
                Chains = { Ref(2, 2) },
            }),
            new(2, new SkillConfig
            {
                Key = "icenova",
                Cost = 20,
                Chains = { Ref(2, 1) },
            }),
        }),
        [new TableId(3)] = new TableContent(new List<TableRecord>
        {
            new(1, new BuffConfig { Key = "burn", Duration = 3, Vfx = Ext("vfx_burn") }),
        }),
        [new TableId(4)] = new TableContent(new List<TableRecord>
        {
            new(1, new BulletConfig { Key = "pellet", Speed = 42 }),
        }),
        [new TableId(5)] = new TableContent(new List<TableRecord>
        {
            new(1, new LevelConfig { Stage = 1, Difficulty = "easy", HpScale = 100 }),
            new(2, new LevelConfig { Stage = 1, Difficulty = "hard", HpScale = 250, Bosses = { Ref(1, 2) } }),
        }),
        [new TableId(6)] = new TableContent(new List<TableRecord>
        {
            new(1, new BehaviorTreeConfig
            {
                Key = "guard_ai",
                Title = "Guard AI",
                Root = Ref(7, 1),
            }),
        }),
        [new TableId(7)] = new TableContent(new List<TableRecord>
        {
            new(1, new BehaviorNodeConfig
            {
                Key = "root",
                DisplayName = "Root Selector",
                Kind = "Selector",
                X = 420,
                Y = 60,
                OwnerTree = Ref(6, 1),
                Children = { Ref(7, 2), Ref(7, 5) },
            }),
            new(2, new BehaviorNodeConfig
            {
                Key = "patrol_sequence",
                DisplayName = "Patrol",
                Kind = "Sequence",
                X = 210,
                Y = 210,
                OwnerTree = Ref(6, 1),
                Children = { Ref(7, 3), Ref(7, 4) },
            }),
            new(3, new BehaviorNodeConfig
            {
                Key = "has_patrol_route",
                DisplayName = "Has Route",
                Kind = "Condition",
                Action = "HasPatrolRoute",
                X = 90,
                Y = 360,
                OwnerTree = Ref(6, 1),
            }),
            new(4, new BehaviorNodeConfig
            {
                Key = "move_to_waypoint",
                DisplayName = "Move To Waypoint",
                Kind = "Action",
                Action = "MoveToWaypoint",
                X = 330,
                Y = 360,
                OwnerTree = Ref(6, 1),
            }),
            new(5, new BehaviorNodeConfig
            {
                Key = "attack_sequence",
                DisplayName = "Attack",
                Kind = "Sequence",
                X = 650,
                Y = 210,
                OwnerTree = Ref(6, 1),
                Children = { Ref(7, 6), Ref(7, 7) },
            }),
            new(6, new BehaviorNodeConfig
            {
                Key = "can_see_enemy",
                DisplayName = "Can See Enemy",
                Kind = "Condition",
                Action = "CanSeeEnemy",
                X = 540,
                Y = 360,
                OwnerTree = Ref(6, 1),
            }),
            new(7, new BehaviorNodeConfig
            {
                Key = "attack_target",
                DisplayName = "Attack Target",
                Kind = "Action",
                Action = "AttackTarget",
                X = 780,
                Y = 360,
                OwnerTree = Ref(6, 1),
            }),
        }),
    };
}

void PrintUsage()
{
    Console.WriteLine("SkillEditor.Cli init <workbook.xlsx>");
    Console.WriteLine("SkillEditor.Cli list <workbook.xlsx> <TableName>");
    Console.WriteLine("SkillEditor.Cli set <workbook.xlsx> <TableName> <key|#id> <field> <value>");
    Console.WriteLine("SkillEditor.Cli validate <workbook.xlsx>");
    Console.WriteLine("SkillEditor.Cli migrate <workbook.xlsx>");
}
