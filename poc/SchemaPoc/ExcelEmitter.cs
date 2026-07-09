using System.Globalization;

namespace SchemaPoc;

/// <summary>SchemaDesc → workbook 结构(三行表头 + 数据区 + 隐藏伴随列 + 元数据/键清单 sheet)。</summary>
public static class ExcelEmitter
{
    public sealed record ColumnPlan(string Path, string Display, string TypeDisplay, string? EnumList, string? RefTable, bool IsKey);

    public static List<XlsxSheet> Emit(SchemaDesc schema, SampleData samples)
    {
        var sheets = new List<XlsxSheet>();
        var keysByTable = samples.Keys();
        var keysSheetCol = new Dictionary<string, int>();
        var i = 1;
        foreach (var t in schema.Tables.OrderBy(t => t.Id)) keysSheetCol[t.Name] = i++;

        foreach (var table in schema.Tables.OrderBy(t => t.Id))
        {
            var columns = PlanColumns(schema, table.Fields, "", "");
            sheets.Add(BuildDataSheet(table.SheetName, columns, samples.Rows(table.Name), keysSheetCol));

            foreach (var f in table.Fields.Where(f => f.Shape is ValueShape.ChildTable or ValueShape.Weighted))
            {
                var childCols = new List<ColumnPlan> { new("__parent", "__parent", "parent key", null, table.Name, false) };
                childCols.AddRange(PlanColumns(schema, f.Children, "", ""));
                sheets.Add(BuildDataSheet($"{table.SheetName}.{f.Name}", childCols, samples.Rows($"{table.Name}.{f.Name}"), keysSheetCol));
            }
        }

        // __exceldb_keys:引用下拉的数据源
        var keys = new XlsxSheet { Name = "__exceldb_keys", Hidden = true };
        foreach (var (tableName, col) in keysSheetCol)
        {
            keys.Cell(1, col, tableName);
            var list = keysByTable.TryGetValue(tableName, out var k) ? k : new List<string>();
            for (var r = 0; r < list.Count; r++) keys.Cell(r + 2, col, list[r]);
        }
        sheets.Add(keys);

        // __exceldb:metadata
        var meta = new XlsxSheet { Name = "__exceldb", Hidden = true };
        var row = 1;
        meta.Cell(row, 1, "[workbook]");
        meta.Cell(row, 2, $"guid={Guid.NewGuid():N}");
        meta.Cell(row, 3, $"schema_hash={schema.SchemaHash:x16}");
        meta.Cell(row, 4, "format=1");
        meta.Cell(row++, 5, $"saved_utc={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
        meta.Cell(row++, 1, "[tables]");
        foreach (var t in schema.Tables.OrderBy(t => t.Id))
        {
            meta.Cell(row, 1, t.Name); meta.Cell(row, 2, t.SheetName); meta.Cell(row, 3, 4); meta.Cell(row++, 4, $"id={t.Id}");
            foreach (var f in t.Fields.Where(f => f.Shape is ValueShape.ChildTable or ValueShape.Weighted))
            { meta.Cell(row, 1, $"{t.Name}.{f.Name}"); meta.Cell(row, 2, $"{t.SheetName}.{f.Name}"); meta.Cell(row, 3, 4); meta.Cell(row++, 4, $"parent={t.Id}:{f.Id}"); }
        }
        meta.Cell(row++, 1, "[fields]");
        foreach (var t in schema.Tables.OrderBy(t => t.Id))
        {
            var cols = PlanColumns(schema, t.Fields, "", "");
            for (var c = 0; c < cols.Count; c++)
            { meta.Cell(row, 1, t.Name); meta.Cell(row, 2, cols[c].Path); meta.Cell(row, 3, c + 1); meta.Cell(row++, 4, 1); }
        }
        sheets.Add(meta);
        return sheets;
    }

    static XlsxSheet BuildDataSheet(string sheetName, List<ColumnPlan> columns, List<Dictionary<string, object?>> rows, Dictionary<string, int> keysSheetCol)
    {
        var s = new XlsxSheet { Name = sheetName, FreezeTopRows = 3 };
        var guidCol = columns.Count + 1;
        var revCol = columns.Count + 2;
        s.HiddenColumns.Add(guidCol);
        s.HiddenColumns.Add(revCol);

        for (var c = 0; c < columns.Count; c++)
        {
            var col = columns[c];
            s.Cell(1, c + 1, col.Display + (col.IsKey ? " *" : ""));
            s.Cell(2, c + 1, col.Path);
            s.Cell(3, c + 1, col.TypeDisplay);
            if (col.EnumList != null) s.Validations.Add((c + 1, 4, col.EnumList));
            else if (col.RefTable != null && keysSheetCol.TryGetValue(col.RefTable, out var kc))
                s.Validations.Add((c + 1, 4, $"__exceldb_keys!${XlsxWriter.ColName(kc)}$2:${XlsxWriter.ColName(kc)}$1000"));
        }
        s.Cell(1, guidCol, "__guid"); s.Cell(2, guidCol, "__guid"); s.Cell(3, guidCol, "system");
        s.Cell(1, revCol, "__rev"); s.Cell(2, revCol, "__rev"); s.Cell(3, revCol, "system");

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns.Count; c++)
                if (rows[r].TryGetValue(columns[c].Path, out var v)) s.Cell(r + 4, c + 1, v);
            s.Cell(r + 4, guidCol, Guid.NewGuid().ToString("N"));
            s.Cell(r + 4, revCol, 1);
        }
        return s;
    }

    public static List<ColumnPlan> PlanColumns(SchemaDesc schema, List<FieldDesc> fields, string pathPrefix, string displayPrefix)
    {
        var cols = new List<ColumnPlan>();
        foreach (var f in fields)
        {
            var path = pathPrefix.Length == 0 ? f.Name : $"{pathPrefix}.{f.Name}";
            var display = f.DisplayName;
            switch (f.Shape)
            {
                case ValueShape.StructExpanded:
                    cols.AddRange(PlanColumns(schema, f.Children, path, display));
                    break;
                case ValueShape.ChildTable or ValueShape.Weighted:
                    break; // 子表落独立 sheet
                case ValueShape.Union:
                {
                    var tokens = f.Children.Select(c => VariantToken(f.Name, c.Name)).ToList();
                    cols.Add(new(path, display, $"union({string.Join("|", tokens)})", string.Join(",", tokens), null, false));
                    foreach (var variant in f.Children)
                    {
                        var token = VariantToken(f.Name, variant.Name);
                        cols.AddRange(PlanColumns(schema, variant.Children, $"{path}.{token}", $"{display}.{token}"));
                    }
                    break;
                }
                default:
                    cols.Add(new(path, display, TypeDisplay(schema, f), EnumList(schema, f), f.Shape == ValueShape.InternalRef ? f.RefTable : null, f.KeyOrder > 0));
                    break;
            }
        }
        return cols;
    }

    static string VariantToken(string oneofName, string fieldName) =>
        fieldName.StartsWith(oneofName + "_", StringComparison.Ordinal) ? fieldName[(oneofName.Length + 1)..] : fieldName;

    static string? EnumList(SchemaDesc schema, FieldDesc f)
    {
        if (f.Shape != ValueShape.Enum) return null;
        var e = schema.Enums.FirstOrDefault(e => e.FullName == f.TypeName);
        return e == null ? null : string.Join(",", e.Values.Where(v => v.Number != 0 || e.Values.Count == 1).Select(v => v.Name));
    }

    static string TypeDisplay(SchemaDesc schema, FieldDesc f)
    {
        var range = f.Min.HasValue || f.Max.HasValue
            ? $" [{f.Min?.ToString(CultureInfo.InvariantCulture) ?? ""}..{f.Max?.ToString(CultureInfo.InvariantCulture) ?? ""}]"
            : "";
        return f.Shape switch
        {
            ValueShape.Scalar => f.TypeName + range + (f.KeyOrder > 0 ? " key" : ""),
            ValueShape.Enum => $"enum {Short(f.TypeName)}",
            ValueShape.ScalarList => $"list<{Short(f.TypeName)}> {f.Format}",
            ValueShape.StructSingleCell => $"struct {Short(f.TypeName)} {f.Format}",
            ValueShape.StructListSingleCell => $"list<{Short(f.TypeName)}> {f.Format}",
            ValueShape.Map => $"map<{f.MapKeyType},{f.MapValueType}> {f.Format}",
            ValueShape.Expression => $"expr({f.ExprResult.Replace("Expr", "").ToLowerInvariant()})",
            ValueShape.Curve => "curve 点列",
            ValueShape.InternalRef => $"ref {f.RefTable}{f.RefGroup}",
            ValueShape.UnityRef => "unity asset(路径 @guid)",
            ValueShape.LocalizedRef => "loc key",
            _ => f.Shape.ToString(),
        };
    }

    static string Short(string full) => full.Contains('.') ? full[(full.LastIndexOf('.') + 1)..] : full;
}

/// <summary>演示数据(fireball 全家桶),验证 cell 文法。</summary>
public sealed class SampleData
{
    readonly Dictionary<string, List<Dictionary<string, object?>>> _rows = new();

    public List<Dictionary<string, object?>> Rows(string table) => _rows.TryGetValue(table, out var r) ? r : new();

    public Dictionary<string, List<string>> Keys() => new()
    {
        ["SkillConfig"] = new() { "fireball", "frostbolt" },
        ["ItemConfig"] = new() { "sword", "potion" },
    };

    public static SampleData Default()
    {
        var s = new SampleData();
        s._rows["SkillConfig"] = new()
        {
            new()
            {
                ["common.id"] = "fireball", ["common.display_name"] = "skill.fireball.name",
                ["common.tags"] = "boss;fire", ["common.enabled"] = "TRUE",
                ["damage"] = 120, ["damage_type"] = "FIRE",
                ["cost.mp"] = 30, ["cost.hp"] = 0,
                ["tick_times"] = "0.5;1;1.5",
                ["next_rank"] = "frostbolt",
                ["icon"] = "Assets/Icons/fire.png @1f2a3b4c5d6e7f8090a1b2c3d4e5f601",
                ["damage_by_level"] = "(1,10) (10,55)",
                ["crit_formula"] = "base_damage * (1 + crit)",
                ["resist_decay"] = "fire=0.5, frost=0.1",
                ["rewards"] = "sword&2#potion&5",
                ["effect"] = "dot", ["effect.dot.amount_per_tick"] = 10, ["effect.dot.duration"] = 3.5,
            },
            new()
            {
                ["common.id"] = "frostbolt", ["common.display_name"] = "skill.frostbolt.name",
                ["common.tags"] = "control", ["common.enabled"] = "TRUE",
                ["damage"] = 60, ["damage_type"] = "FROST",
                ["cost.mp"] = 20, ["cost.hp"] = 0,
                ["effect"] = "damage", ["effect.damage.amount"] = 60,
            },
        };
        s._rows["SkillConfig.levels"] = new()
        {
            new() { ["__parent"] = "fireball", ["level"] = 1, ["required_exp"] = 0 },
            new() { ["__parent"] = "fireball", ["level"] = 2, ["required_exp"] = 100 },
            new() { ["__parent"] = "frostbolt", ["level"] = 1, ["required_exp"] = 0 },
        };
        s._rows["SkillConfig.drops"] = new()
        {
            new() { ["__parent"] = "fireball", ["item"] = "sword", ["count"] = 1, ["weight"] = 10, ["condition"] = "player_level > 5" },
            new() { ["__parent"] = "fireball", ["item"] = "potion", ["count"] = 3, ["weight"] = 90, ["condition"] = "" },
        };
        s._rows["ItemConfig"] = new()
        {
            new() { ["common.id"] = "sword", ["common.display_name"] = "item.sword.name", ["common.tags"] = "weapon", ["common.enabled"] = "TRUE", ["max_stack"] = 1 },
            new() { ["common.id"] = "potion", ["common.display_name"] = "item.potion.name", ["common.tags"] = "consumable", ["common.enabled"] = "TRUE", ["max_stack"] = 99 },
        };
        return s;
    }
}
