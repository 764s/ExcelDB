using System.Collections.ObjectModel;
using System.Globalization;
using ExcelDb;
using ExcelDb.Database;
using ExcelDb.Excel;
using ExcelDb.Loading;
using ExcelDb.Protocol;
using ExcelDb.Schema;
using Game.Configs;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace SkillEditor.Avalonia.ViewModels;

public sealed class EditorDocument : NotifyObject
{
    readonly SchemaRegistry _registry = SchemaRegistry.FromFiles(ConfigsReflection.Descriptor);
    ConfigDatabase? _db;

    string _workbookPath = Path.GetFullPath("skills.xlsx");
    string _status = "Create or open an Excel workbook.";
    TableViewModel? _selectedTable;

    public EditorDocument()
    {
        LoadSchemaTables();
        SelectedTable = Tables.FirstOrDefault();
    }

    public ObservableCollection<TableViewModel> Tables { get; } = new();
    public ObservableCollection<RowViewModel> Rows { get; } = new();
    public ObservableCollection<string> Issues { get; } = new();

    public string WorkbookPath
    {
        get => _workbookPath;
        set => SetProperty(ref _workbookPath, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public TableViewModel? SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (!SetProperty(ref _selectedTable, value))
                return;

            RefreshRows();
            OnPropertyChanged(nameof(SelectedSchema));
        }
    }

    public TableSchema? SelectedSchema => SelectedTable?.Schema;
    public bool HasWorkbook => _db != null;
    public bool CanUndo => _db?.CanUndo ?? false;
    public bool CanRedo => _db?.CanRedo ?? false;

    public void CreateSampleWorkbook()
    {
        if (File.Exists(WorkbookPath))
            throw new IOException("'" + WorkbookPath + "' already exists.");

        var loader = new ExcelTableLoader(WorkbookPath, _registry, new ExcelTableLoaderOptions { CreateIfMissing = true });
        foreach (var table in SampleWorkbookData.Create())
            loader.Write(table.Key, table.Value);

        OpenWorkbook();
        Status = "Created sample workbook.";
    }

    public void OpenWorkbook()
    {
        var loader = new ExcelTableLoader(WorkbookPath, _registry);
        _db = ConfigDatabase.Open(_registry, packs => packs.Mount("excel", loader));

        LoadSchemaTables();
        SelectedTable = Tables.FirstOrDefault();
        Issues.Clear();
        RefreshTables();
        RefreshCapabilities();
        Status = "Opened " + WorkbookPath;
    }

    public void Save()
    {
        RequireDatabase().SaveAssets();
        RefreshTables();
        RefreshCapabilities();
        Status = "Saved " + WorkbookPath;
    }

    public void Undo()
    {
        if (RequireDatabase().Undo())
        {
            RefreshRows();
            RefreshTables();
            RefreshCapabilities();
            Status = "Undo complete.";
        }
    }

    public void Redo()
    {
        if (RequireDatabase().Redo())
        {
            RefreshRows();
            RefreshTables();
            RefreshCapabilities();
            Status = "Redo complete.";
        }
    }

    public void Validate()
    {
        var report = RequireDatabase().Validate();
        Issues.Clear();
        foreach (var issue in report.Issues)
            Issues.Add(issue.ToString());

        Status = report.IsClean
            ? "Validation clean."
            : "Validation found " + report.Issues.Count.ToString(CultureInfo.InvariantCulture) + " issue(s).";
    }

    public void ReportError(Exception ex)
    {
        Status = ex.Message;
    }

    public void AddRow()
    {
        var table = RequireSelectedTable();
        var db = RequireDatabase();

        using (var tx = db.BeginEdit("Create " + table.Name))
        {
            var (id, draft) = tx.Create(table.Schema.Id);
            ApplyKeyDefaults(table.Schema, draft, id.Id);
            tx.Commit();
        }

        RefreshRows();
        RefreshTables();
        RefreshCapabilities();
        Status = "Created row in " + table.Name + ".";
    }

    public void DeleteRow(RowViewModel? row)
    {
        if (row == null)
            return;

        var db = RequireDatabase();
        using (var tx = db.BeginEdit("Delete " + row.RowId))
        {
            tx.Delete(row.RowId);
            tx.Commit();
        }

        RefreshRows();
        RefreshTables();
        RefreshCapabilities();
        Status = "Deleted row #" + row.IdNumber.ToString(CultureInfo.InvariantCulture) + ".";
    }

    public bool TrySetCell(RowViewModel row, string fieldName, string value)
    {
        var table = RequireSelectedTable();
        var db = RequireDatabase();
        var field = ExcelFieldCodec.FindField(table.Schema.Descriptor, fieldName);
        if (field == null)
        {
            Status = "Unknown field '" + fieldName + "'.";
            return false;
        }

        try
        {
            using (var tx = db.BeginEdit("Edit " + table.Name + "." + field.Name))
            {
                var draft = tx.GetMutable(row.RowId);
                ExcelFieldCodec.SetField(draft, field, value, _registry);
                tx.Commit();
            }

            row.Reload(db.LoadAsset(row.RowId));
            RefreshTables();
            RefreshCapabilities();
            Status = "Edited " + table.Name + "#" + row.IdNumber.ToString(CultureInfo.InvariantCulture) + "." + field.Name + ".";
            return true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return false;
        }
    }

    void RefreshRows()
    {
        Rows.Clear();
        if (_db == null || SelectedTable == null)
            return;

        var fields = SelectedTable.Schema.Descriptor.Fields.InDeclarationOrder().ToArray();
        foreach (var id in _db.FindAssets(SelectedTable.Schema.Id))
            Rows.Add(new RowViewModel(this, id, fields, _db.LoadAsset(id)));
    }

    void RefreshTables()
    {
        if (_db == null)
        {
            foreach (var table in Tables)
                table.IsDirty = false;
            return;
        }

        var dirty = new HashSet<string>(_db.Info.DirtyTables, StringComparer.Ordinal);
        foreach (var table in Tables)
            table.IsDirty = dirty.Contains(table.Name);
    }

    void LoadSchemaTables()
    {
        Tables.Clear();
        foreach (var schema in _registry.Tables.OrderBy(table => table.Id.Number))
            Tables.Add(new TableViewModel(schema));
    }

    void RefreshCapabilities()
    {
        OnPropertyChanged(nameof(HasWorkbook));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    ConfigDatabase RequireDatabase() =>
        _db ?? throw new InvalidOperationException("Open or create a workbook first.");

    TableViewModel RequireSelectedTable() =>
        SelectedTable ?? throw new InvalidOperationException("Select a table first.");

    static void ApplyKeyDefaults(TableSchema schema, IMessage draft, int rowId)
    {
        foreach (var field in schema.KeyFields)
        {
            switch (field.FieldType)
            {
                case FieldType.String:
                    field.Accessor.SetValue(draft, schema.Name.ToLowerInvariant() + "_" + rowId.ToString(CultureInfo.InvariantCulture));
                    break;
                case FieldType.Int32:
                case FieldType.SInt32:
                case FieldType.SFixed32:
                    field.Accessor.SetValue(draft, rowId);
                    break;
                case FieldType.UInt32:
                case FieldType.Fixed32:
                    field.Accessor.SetValue(draft, (uint)rowId);
                    break;
                case FieldType.Int64:
                case FieldType.SInt64:
                case FieldType.SFixed64:
                    field.Accessor.SetValue(draft, (long)rowId);
                    break;
                case FieldType.UInt64:
                case FieldType.Fixed64:
                    field.Accessor.SetValue(draft, (ulong)rowId);
                    break;
            }
        }
    }

    static Dictionary<TableId, TableContent> SeedTables()
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
}
