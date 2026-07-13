using System.Diagnostics;
using System.Text;
using ExcelDb.Protocol;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace SchemaPoc;

/// <summary>proto → descriptor set → SchemaDesc(模块 1 §5 管线的 PoC)。</summary>
public static class SchemaCompiler
{
    static readonly string[] DefaultExportTargets = ["client", "server"];
    static readonly HashSet<string> KnownExportTargets = new(DefaultExportTargets, StringComparer.Ordinal);

    // ---------- protoc ----------

    public static string LocateProtoc()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "grpc.tools");
        if (!Directory.Exists(root)) throw new InvalidOperationException($"grpc.tools 包不存在:{root}");
        var candidates = Directory.GetDirectories(root)
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(d, "tools", "windows_x64", "protoc.exe"))
            .Where(File.Exists).ToList();
        if (candidates.Count == 0) throw new InvalidOperationException("未找到 protoc.exe(grpc.tools/*/tools/windows_x64)");
        return candidates[0];
    }

    public static byte[] CompileDescriptorSet(string protosRoot, string protoRelPath)
    {
        var protoc = LocateProtoc();
        var wellKnown = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(protoc))!)!, "build", "native", "include");
        var outFile = Path.Combine(Path.GetTempPath(), $"schemapoc_{Guid.NewGuid():N}.pb");
        var args = $"--proto_path=\"{protosRoot}\"" +
                   (Directory.Exists(wellKnown) ? $" --proto_path=\"{wellKnown}\"" : "") +
                   $" --descriptor_set_out=\"{outFile}\" --include_imports {protoRelPath.Replace('\\', '/')}";
        var psi = new ProcessStartInfo(protoc, args) { RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"protoc 失败({p.ExitCode}):\n{err}");
        var bytes = File.ReadAllBytes(outFile);
        File.Delete(outFile);
        return bytes;
    }

    // ---------- descriptor set → SchemaDesc ----------

    sealed class TypeIndex
    {
        public readonly Dictionary<string, DescriptorProto> Messages = new();
        public readonly Dictionary<string, EnumDescriptorProto> Enums = new();
        public readonly Dictionary<string, string> EnumPackage = new();
    }

    public static SchemaDesc Compile(byte[] descriptorSetBytes)
    {
        var registry = new ExtensionRegistry
        {
            OptionsExtensions.Table, OptionsExtensions.Field, OptionsExtensions.EnumValue, OptionsExtensions.Defaults,
        };
        var set = FileDescriptorSet.Parser.WithExtensionRegistry(registry).ParseFrom(descriptorSetBytes);

        var index = new TypeIndex();
        SchemaDefaults? fileDefaults = null;
        foreach (var file in set.File)
        {
            foreach (var m in file.MessageType) IndexMessage(index, file.Package, m, file.Package);
            foreach (var e in file.EnumType) { index.Enums[$"{file.Package}.{e.Name}"] = e; index.EnumPackage[$"{file.Package}.{e.Name}"] = file.Package; }
            if (file.Package == "game" && file.Options != null)
                fileDefaults = file.Options.GetExtension(OptionsExtensions.Defaults);
        }

        var schema = new SchemaDesc();

        // 匿名嵌入形状(无 table option):生成 C# 类的输入
        foreach (var (fullName, msg) in index.Messages.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!fullName.StartsWith("game.") || msg.Options?.MapEntry == true) continue;
            if (msg.Options?.GetExtension(OptionsExtensions.Table) != null) continue;
            var shape = new TableDesc { Name = msg.Name, FullName = fullName, DisplayName = msg.Name, IsAsset = false };
            BuildFields(schema, index, fileDefaults, msg, fullName, DefaultExportTargets, shape.Fields);
            schema.Shapes.Add(shape);
        }

        foreach (var (fullName, msg) in index.Messages.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (fullName.StartsWith("exceldb.") || msg.Options?.MapEntry == true) continue;
            var opts = msg.Options?.GetExtension(OptionsExtensions.Table);
            if (opts == null) continue;

            var table = new TableDesc
            {
                Id = opts.Id,
                Name = msg.Name,
                FullName = fullName,
                DisplayName = opts.DisplayName.Length > 0 ? opts.DisplayName : msg.Name,
                SheetName = opts.SheetName.Length > 0 ? opts.SheetName : msg.Name,
                IsAsset = opts.Kind == TableKind.Asset,
                Retired = opts.Retired,
                Implements = opts.Implements.ToArray(),
                Validators = opts.Validators.ToArray(),
                ExportTargets = opts.Retired ? [] : ResolveTableExportTargets(schema, opts, msg.Name),
            };
            if (opts.Kind == TableKind.Unspecified)
                schema.Lints.Add($"[blocker] XDB015 {msg.Name}: table option 的 kind 未显式指定");
            if (table.IsAsset && opts.Id == 0)
                schema.Lints.Add($"[blocker] XDB001 {msg.Name}: ASSET 表缺 id");

            BuildFields(schema, index, fileDefaults, msg, fullName, table.ExportTargets, table.Fields);
            CollectKeys(table.Fields, "", table.KeyFields, schema, table);
            if (table.IsAsset && !table.Retired && table.KeyFields.Count == 0)
                schema.Lints.Add($"[blocker] XDB002 {msg.Name}: ASSET 表无 key 字段");
            schema.Tables.Add(table);
        }
        if (schema.Tables.GroupBy(t => t.Id).Any(g => g.Count() > 1))
            schema.Lints.Add("[blocker] XDB001 表 id 重复");

        ValidateReferenceTargets(schema);

        foreach (var (full, e) in index.Enums.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!full.StartsWith("game.")) continue;
            var ed = new EnumDesc { Name = e.Name, FullName = full };
            foreach (var v in e.Value)
                ed.Values.Add((v.Number, v.Name, v.Options?.GetExtension(OptionsExtensions.EnumValue)?.DisplayName ?? ""));
            if (e.Value.All(v => v.Number != 0))
                schema.Lints.Add($"[blocker] XDB013 {e.Name}: enum 缺 0 值");
            schema.Enums.Add(ed);
        }

        schema.SchemaHash = ComputeHash(schema);
        return schema;
    }

    static void IndexMessage(TypeIndex index, string pkg, DescriptorProto msg, string scope)
    {
        var full = $"{scope}.{msg.Name}";
        index.Messages[full] = msg;
        foreach (var n in msg.NestedType)
        {
            if (n.Options?.MapEntry == true) index.Messages[$"{full}.{n.Name}"] = n;
            else IndexMessage(index, pkg, n, full);
        }
        foreach (var e in msg.EnumType) index.Enums[$"{full}.{e.Name}"] = e;
    }

    static void BuildFields(
        SchemaDesc schema,
        TypeIndex index,
        SchemaDefaults? defaults,
        DescriptorProto msg,
        string msgFullName,
        string[] inheritedExportTargets,
        List<FieldDesc> output)
    {
        // oneof 分组
        var unionFields = new Dictionary<int, FieldDesc>();
        foreach (var f in msg.Field)
        {
            FieldDesc target;
            if (f.HasOneofIndex && !f.Proto3Optional)
            {
                if (!unionFields.TryGetValue(f.OneofIndex, out var union))
                {
                    union = new FieldDesc
                    {
                        Id = 0,
                        Name = msg.OneofDecl[f.OneofIndex].Name,
                        UnionName = msg.OneofDecl[f.OneofIndex].Name,
                        Shape = ValueShape.Union,
                        DisplayName = msg.OneofDecl[f.OneofIndex].Name,
                        ExportTargets = inheritedExportTargets.ToArray(),
                    };
                    unionFields[f.OneofIndex] = union;
                    output.Add(union);
                }
                target = BuildField(schema, index, defaults, f, msgFullName, inheritedExportTargets);
                union.Children.Add(target);
                continue;
            }
            target = BuildField(schema, index, defaults, f, msgFullName, inheritedExportTargets);
            output.Add(target);
        }
    }

    static FieldDesc BuildField(
        SchemaDesc schema,
        TypeIndex index,
        SchemaDefaults? defaults,
        FieldDescriptorProto f,
        string ownerFullName,
        string[] inheritedExportTargets)
    {
        var opts = f.Options?.GetExtension(OptionsExtensions.Field);
        var exportTargets = ResolveFieldExportTargets(schema, opts, inheritedExportTargets, $"{ownerFullName}.{f.Name}");
        var d = new FieldDesc
        {
            Id = f.Number,
            Name = f.Name,
            DisplayName = opts?.DisplayName is { Length: > 0 } dn ? dn : f.Name,
            HeaderComment = opts?.HeaderComment ?? "",
            Repeated = f.Label == FieldDescriptorProto.Types.Label.Repeated,
            KeyOrder = opts?.Key ?? 0,
            Required = opts?.Required ?? false,
            Unique = opts?.Unique ?? false,
            Labels = opts?.Labels ?? false,
            DefaultValue = opts?.DefaultValue ?? "",
            Regex = opts?.Regex ?? "",
            Min = opts?.HasMin == true ? opts.Min : null,
            Max = opts?.HasMax == true ? opts.Max : null,
            RefTable = opts?.RefTable ?? "",
            RefGroup = opts?.RefGroup ?? "",
            ExprSymbols = opts?.Expression?.SymbolsType ?? "",
            ExprResult = opts?.Expression != null ? opts.Expression.Result.ToString() : "",
            WeightField = opts?.Weighted?.WeightField ?? 0,
            ConditionField = opts?.Weighted?.ConditionField ?? 0,
            ExportTargets = exportTargets,
        };

        var outsideParent = exportTargets.Except(inheritedExportTargets, StringComparer.Ordinal).ToArray();
        if (outsideParent.Length > 0)
            schema.Lints.Add($"[blocker] XDB016 {ownerFullName}.{f.Name}: export target [{string.Join(",", outsideParent)}] 不属于上级 target 集");

        var typeName = f.TypeName.TrimStart('.');
        switch (f.Type)
        {
            case FieldDescriptorProto.Types.Type.Message when typeName == "exceldb.RowRef":
                d.Shape = ValueShape.InternalRef; d.TypeName = typeName;
                if (d.RefTable.Length == 0 && d.RefGroup.Length == 0)
                    schema.Lints.Add($"[blocker] XDB007 {ownerFullName}.{f.Name}: RowRef 未声明 ref_table/ref_group");
                break;
            case FieldDescriptorProto.Types.Type.Message when typeName == "exceldb.UnityResourceRef":
                d.Shape = ValueShape.UnityRef; d.TypeName = typeName; break;
            case FieldDescriptorProto.Types.Type.Message when typeName == "exceldb.LocalizedTextRef":
                d.Shape = ValueShape.LocalizedRef; d.TypeName = typeName; break;
            case FieldDescriptorProto.Types.Type.Message when typeName == "exceldb.Curve":
                d.Shape = ValueShape.Curve; d.TypeName = typeName; break;

            case FieldDescriptorProto.Types.Type.Message when index.Messages.TryGetValue(typeName, out var entry) && entry.Options?.MapEntry == true:
            {
                d.Shape = ValueShape.Map; d.TypeName = typeName;
                d.MapKeyType = ScalarCs(entry.Field[0], index);
                d.MapValueType = ScalarCs(entry.Field[1], index);
                break;
            }

            case FieldDescriptorProto.Types.Type.Message:
            {
                d.TypeName = typeName;
                var target = index.Messages[typeName];
                BuildFields(schema, index, defaults, target, typeName, d.ExportTargets, d.Children);
                if (d.Repeated)
                {
                    var joinLayers = opts?.Format?.Join?.Separators.Count ?? 0;
                    if (opts?.Weighted != null) d.Shape = ValueShape.Weighted;
                    else if (joinLayers == 2) d.Shape = ValueShape.StructListSingleCell;
                    else d.Shape = ValueShape.ChildTable;
                }
                else
                {
                    var expand = opts?.Expand ?? ExpandMode.ExpandAuto;
                    var allScalar = d.Children.All(c => c.Shape is ValueShape.Scalar or ValueShape.Enum);
                    d.Shape = expand switch
                    {
                        ExpandMode.SingleCell => ValueShape.StructSingleCell,
                        ExpandMode.ExpandedColumns => ValueShape.StructExpanded,
                        _ => allScalar ? ValueShape.StructSingleCell : ValueShape.StructExpanded,
                    };
                    if (d.Shape == ValueShape.StructSingleCell && !allScalar)
                        schema.Lints.Add($"[blocker] XDB005 {ownerFullName}.{f.Name}: 单 cell 嵌套深度超过 join 声明层数");
                }
                break;
            }

            case FieldDescriptorProto.Types.Type.Enum:
                d.Shape = ValueShape.Enum; d.TypeName = typeName;
                if (d.Repeated) d.Shape = ValueShape.ScalarList;
                break;

            case FieldDescriptorProto.Types.Type.String when d.ExprSymbols.Length > 0:
                d.Shape = ValueShape.Expression; d.TypeName = "string";
                if (!index.Messages.ContainsKey(d.ExprSymbols))
                    schema.Lints.Add($"[blocker] XDB009 {ownerFullName}.{f.Name}: symbols_type {d.ExprSymbols} 不存在");
                break;

            default:
                d.TypeName = ScalarCs(f, index);
                d.Shape = d.Repeated ? ValueShape.ScalarList : ValueShape.Scalar;
                break;
        }

        if (opts?.Format != null && opts.Format.KindCase != CellFormat.KindOneofCase.None)
            d.Format = ToSpec(opts.Format);
        else
            d.Format = DefaultFormat(d.Shape, defaults);
        return d;
    }

    static string[] ResolveTableExportTargets(SchemaDesc schema, TableOpts opts, string owner)
    {
        if (opts.ExportTargets != null)
        {
            if (opts.Export != ExportPolicy.ExportDefault)
                schema.Lints.Add($"[blocker] XDB020 {owner}: legacy export 与 export_targets 不能同时声明");
            return NormalizeExportTargets(schema, opts.ExportTargets.Ids, owner);
        }

        return opts.Export switch
        {
            ExportPolicy.EditorOnly => [],
            ExportPolicy.ExportAll or ExportPolicy.ExportDefault => DefaultExportTargets.ToArray(),
            _ => DefaultExportTargets.ToArray(),
        };
    }

    static string[] ResolveFieldExportTargets(SchemaDesc schema, FieldOpts? opts, string[] inherited, string owner)
    {
        if (opts?.ExportTargets != null)
        {
            if (opts.Export != ExportPolicy.ExportDefault)
                schema.Lints.Add($"[blocker] XDB020 {owner}: legacy export 与 export_targets 不能同时声明");
            return NormalizeExportTargets(schema, opts.ExportTargets.Ids, owner);
        }

        return opts?.Export switch
        {
            ExportPolicy.EditorOnly => [],
            ExportPolicy.ExportAll => DefaultExportTargets.ToArray(),
            _ => inherited.ToArray(),
        };
    }

    static string[] NormalizeExportTargets(SchemaDesc schema, IEnumerable<string> ids, string owner)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                schema.Lints.Add($"[blocker] XDB020 {owner}: 重复 export target '{id}'");
                continue;
            }

            if (!IsValidExportTargetId(id) || !KnownExportTargets.Contains(id))
                schema.Lints.Add($"[blocker] XDB020 {owner}: 非法或未知 export target '{id}'");
        }
        return seen.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    static bool IsValidExportTargetId(string id)
    {
        if (id.Length == 0 || id[0] is < 'a' or > 'z') return false;
        for (var i = 1; i < id.Length; i++)
        {
            var c = id[i];
            if (c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-') continue;
            return false;
        }
        return true;
    }

    static FormatSpec ToSpec(CellFormat f) => f.KindCase switch
    {
        CellFormat.KindOneofCase.Join => new FormatSpec { Kind = FormatKind.Join, Separators = f.Join.Separators.ToArray() },
        CellFormat.KindOneofCase.Named => new FormatSpec
        {
            Kind = FormatKind.Named,
            PairSeparator = f.Named.PairSeparator.Length > 0 ? f.Named.PairSeparator : ", ",
            KvSeparator = f.Named.KvSeparator.Length > 0 ? f.Named.KvSeparator : "=",
        },
        CellFormat.KindOneofCase.Codec => new FormatSpec { Kind = FormatKind.Codec, CodecId = f.Codec },
        _ => new FormatSpec(),
    };

    static FormatSpec DefaultFormat(ValueShape shape, SchemaDefaults? defaults) => shape switch
    {
        ValueShape.StructSingleCell => defaults?.StructFormat is { KindCase: not CellFormat.KindOneofCase.None } sf ? ToSpec(sf) : new FormatSpec { Kind = FormatKind.Named },
        ValueShape.ScalarList => defaults?.ScalarListFormat is { KindCase: not CellFormat.KindOneofCase.None } lf ? ToSpec(lf) : new FormatSpec { Kind = FormatKind.Join, Separators = [";"] },
        ValueShape.Map => defaults?.MapFormat is { KindCase: not CellFormat.KindOneofCase.None } mf ? ToSpec(mf) : new FormatSpec { Kind = FormatKind.Named },
        _ => new FormatSpec(),
    };

    static string ScalarCs(FieldDescriptorProto f, TypeIndex index) => f.Type switch
    {
        FieldDescriptorProto.Types.Type.Int32 or FieldDescriptorProto.Types.Type.Sint32 or FieldDescriptorProto.Types.Type.Sfixed32 => "int",
        FieldDescriptorProto.Types.Type.Int64 or FieldDescriptorProto.Types.Type.Sint64 or FieldDescriptorProto.Types.Type.Sfixed64 => "long",
        FieldDescriptorProto.Types.Type.Uint32 or FieldDescriptorProto.Types.Type.Fixed32 => "uint",
        FieldDescriptorProto.Types.Type.Uint64 or FieldDescriptorProto.Types.Type.Fixed64 => "ulong",
        FieldDescriptorProto.Types.Type.Float => "float",
        FieldDescriptorProto.Types.Type.Double => "double",
        FieldDescriptorProto.Types.Type.Bool => "bool",
        FieldDescriptorProto.Types.Type.String => "string",
        FieldDescriptorProto.Types.Type.Enum => f.TypeName.TrimStart('.'),
        _ => "object",
    };

    static void CollectKeys(List<FieldDesc> fields, string prefix, List<FieldDesc> keys, SchemaDesc schema, TableDesc table)
    {
        foreach (var f in fields)
        {
            var path = prefix.Length == 0 ? f.Name : $"{prefix}.{f.Name}";
            if (f.KeyOrder > 0)
            {
                if (f.Shape is not (ValueShape.Scalar or ValueShape.Enum))
                    schema.Lints.Add($"[blocker] XDB004 {table.Name}.{path}: key 落在非标量字段");
                var missingTargets = table.ExportTargets.Except(f.ExportTargets, StringComparer.Ordinal).ToArray();
                if (missingTargets.Length > 0)
                    schema.Lints.Add($"[blocker] XDB016 {table.Name}.{path}: key 未覆盖表 target [{string.Join(",", missingTargets)}]");
                keys.Add(f);
            }
            if (f.Shape == ValueShape.StructExpanded) CollectKeys(f.Children, path, keys, schema, table);
        }
        keys.Sort((a, b) => a.KeyOrder.CompareTo(b.KeyOrder));
    }

    static void ValidateReferenceTargets(SchemaDesc schema)
    {
        foreach (var table in schema.Tables.Where(t => !t.Retired))
            ValidateReferenceFields(schema, table.Fields, table.Name);
    }

    static void ValidateReferenceFields(SchemaDesc schema, IEnumerable<FieldDesc> fields, string ownerPath)
    {
        foreach (var field in fields)
        {
            var path = $"{ownerPath}.{field.Name}";
            if (field.Shape == ValueShape.InternalRef)
            {
                List<TableDesc> candidates;
                if (field.RefTable.Length > 0)
                {
                    var targetName = field.RefTable.TrimStart('.');
                    candidates = schema.Tables.Where(t => !t.Retired &&
                        (string.Equals(t.Name, targetName, StringComparison.Ordinal) ||
                         string.Equals(t.FullName, targetName, StringComparison.Ordinal))).ToList();
                    if (candidates.Count != 1)
                    {
                        schema.Lints.Add($"[blocker] XDB007 {path}: ref_table '{field.RefTable}' 解析到 {candidates.Count} 个表");
                        candidates.Clear();
                    }
                }
                else
                {
                    candidates = schema.Tables.Where(t => !t.Retired &&
                        t.Implements.Contains(field.RefGroup, StringComparer.Ordinal)).ToList();
                    if (candidates.Count == 0)
                        schema.Lints.Add($"[blocker] XDB007 {path}: ref_group '{field.RefGroup}' 无目标表");
                }

                foreach (var candidate in candidates)
                {
                    var missingTargets = field.ExportTargets.Except(candidate.ExportTargets, StringComparer.Ordinal).ToArray();
                    if (missingTargets.Length > 0)
                        schema.Lints.Add($"[blocker] XDB016 {path}: RowRef 目标表 {candidate.Name} 未导出 target [{string.Join(",", missingTargets)}]");
                }
            }

            if (field.Children.Count > 0)
                ValidateReferenceFields(schema, field.Children, path);
        }
    }

    // ---------- canonical hash ----------

    static ulong ComputeHash(SchemaDesc schema)
    {
        var sb = new StringBuilder();
        foreach (var t in schema.Tables.OrderBy(t => t.Id))
        {
            sb.Append($"T{t.Id}:{t.Name}:{(t.IsAsset ? "asset" : "embedded")};");
            WriteTargets(sb, t.ExportTargets);
            WriteFields(sb, t.Fields);
        }
        foreach (var e in schema.Enums.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            sb.Append($"E:{e.FullName}(");
            foreach (var v in e.Values.OrderBy(v => v.Number)) sb.Append($"{v.Number}={v.Name},");
            sb.Append(");");
        }
        return XxHash64.Hash(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    static void WriteFields(StringBuilder sb, List<FieldDesc> fields)
    {
        foreach (var f in fields.OrderBy(f => f.Id))
        {
            sb.Append($"F{f.Id}:{f.Name}:{f.Shape}:{f.TypeName}:k{f.KeyOrder}:r{(f.Required ? 1 : 0)}:u{(f.Unique ? 1 : 0)}");
            WriteTargets(sb, f.ExportTargets);
            if (f.Min.HasValue) sb.Append($":min{f.Min}");
            if (f.Max.HasValue) sb.Append($":max{f.Max}");
            if (f.Regex.Length > 0) sb.Append($":re{f.Regex}");
            if (f.RefTable.Length > 0) sb.Append($":rt{f.RefTable}");
            if (f.RefGroup.Length > 0) sb.Append($":rg{f.RefGroup}");
            if (f.ExprSymbols.Length > 0) sb.Append($":ex{f.ExprSymbols}>{f.ExprResult}");
            if (f.WeightField > 0) sb.Append($":w{f.WeightField},{f.ConditionField}");
            if (f.MapKeyType.Length > 0) sb.Append($":m{f.MapKeyType}->{f.MapValueType}");
            sb.Append($":fmt{f.Format}");
            if (f.Children.Count > 0) { sb.Append('['); WriteFields(sb, f.Children); sb.Append(']'); }
            sb.Append(';');
        }
    }

    static void WriteTargets(StringBuilder sb, IEnumerable<string> targets)
    {
        sb.Append(":targets[");
        foreach (var target in targets.OrderBy(t => t, StringComparer.Ordinal))
            sb.Append(target.Length).Append('#').Append(target).Append(',');
        sb.Append(']');
    }
}
