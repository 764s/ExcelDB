using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ExcelDb.Protocol;
using Google.Protobuf.Reflection;
using ProtoFieldType = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;
using ProtoLabel = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;

namespace ExcelDb.Schema.Mutation;

internal static class ProtoDescriptorEmitter
{
    public static ImmutableDictionary<string, string> EmitProjectFiles(FileDescriptorSet descriptorSet)
    {
        var index = MessageIndex.Create(descriptorSet);
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var file in descriptorSet.File
                     .Where(static file => !IsSystemFile(file))
                     .OrderBy(static file => file.Name, StringComparer.Ordinal))
        {
            result.Add(file.Name, EmitFile(file, index));
        }

        return result.ToImmutable();
    }

    private static string EmitFile(FileDescriptorProto file, MessageIndex index)
    {
        var builder = new StringBuilder();
        builder.Append("syntax = ").Append(Quote(string.IsNullOrEmpty(file.Syntax) ? "proto3" : file.Syntax)).AppendLine(";");
        if (!string.IsNullOrEmpty(file.Package))
            builder.Append("package ").Append(file.Package).AppendLine(";");
        builder.AppendLine();

        foreach (var dependency in file.Dependency
                     .Where(dependency => !string.Equals(dependency, file.Name, StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(static dependency => dependency, StringComparer.Ordinal))
        {
            builder.Append("import ").Append(Quote(dependency)).AppendLine(";");
        }

        if (file.Dependency.Count != 0)
            builder.AppendLine();
        if (GetSchemaDefaults(file) is { } defaults)
        {
            builder.Append("option (exceldb.defaults) = ")
                .Append(EmitSchemaDefaults(defaults)).AppendLine(";");
            builder.AppendLine();
        }

        foreach (var descriptor in file.EnumType.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            EmitEnum(builder, descriptor, 0);
            builder.AppendLine();
        }

        foreach (var descriptor in file.MessageType.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            EmitMessage(builder, descriptor, Join(file.Package, descriptor.Name), index, 0);
            builder.AppendLine();
        }

        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
    }

    private static void EmitMessage(
        StringBuilder builder,
        DescriptorProto descriptor,
        string fullName,
        MessageIndex index,
        int indent)
    {
        if (descriptor.Options?.MapEntry == true)
            return;

        AppendIndent(builder, indent).Append("message ").Append(descriptor.Name).AppendLine(" {");
        if (GetTableOptions(descriptor) is { } tableOptions)
        {
            AppendIndent(builder, indent + 1).Append("option (exceldb.table) = ")
                .Append(EmitTableOptions(tableOptions)).AppendLine(";");
        }

        EmitMessageReserved(builder, descriptor, indent + 1);

        var realOneOfIndexes = descriptor.OneofDecl
            .Select((_, oneOfIndex) => oneOfIndex)
            .Where(oneOfIndex => descriptor.Field.Any(field =>
                field.HasOneofIndex && field.OneofIndex == oneOfIndex && !field.Proto3Optional))
            .ToHashSet();
        foreach (var field in descriptor.Field
                     .Where(field => !field.HasOneofIndex || field.Proto3Optional || !realOneOfIndexes.Contains(field.OneofIndex))
                     .OrderBy(static field => field.Number))
        {
            EmitField(builder, field, descriptor, fullName, index, indent + 1, insideOneOf: false);
        }

        foreach (var oneOfIndex in realOneOfIndexes.Order())
        {
            AppendIndent(builder, indent + 1).Append("oneof ")
                .Append(descriptor.OneofDecl[oneOfIndex].Name).AppendLine(" {");
            foreach (var field in descriptor.Field
                         .Where(field => field.HasOneofIndex
                             && !field.Proto3Optional
                             && field.OneofIndex == oneOfIndex)
                         .OrderBy(static field => field.Number))
            {
                EmitField(builder, field, descriptor, fullName, index, indent + 2, insideOneOf: true);
            }

            AppendIndent(builder, indent + 1).AppendLine("}");
        }

        foreach (var enumDescriptor in descriptor.EnumType.OrderBy(static item => item.Name, StringComparer.Ordinal))
            EmitEnum(builder, enumDescriptor, indent + 1);
        foreach (var nested in descriptor.NestedType
                     .Where(static nested => nested.Options?.MapEntry != true)
                     .OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            EmitMessage(builder, nested, Join(fullName, nested.Name), index, indent + 1);
        }

        AppendIndent(builder, indent).AppendLine("}");
    }

    private static void EmitMessageReserved(StringBuilder builder, DescriptorProto descriptor, int indent)
    {
        foreach (var range in descriptor.ReservedRange.OrderBy(static range => range.Start).ThenBy(static range => range.End))
        {
            AppendIndent(builder, indent).Append("reserved ").Append(range.Start);
            if (range.End != range.Start + 1)
                builder.Append(" to ").Append(range.End == 536_870_912 ? "max" : (range.End - 1).ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(";");
        }

        if (descriptor.ReservedName.Count > 0)
        {
            AppendIndent(builder, indent).Append("reserved ")
                .Append(string.Join(", ", descriptor.ReservedName.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(Quote)))
                .AppendLine(";");
        }
    }

    private static void EmitField(
        StringBuilder builder,
        FieldDescriptorProto field,
        DescriptorProto owner,
        string ownerFullName,
        MessageIndex index,
        int indent,
        bool insideOneOf)
    {
        AppendIndent(builder, indent);
        if (!insideOneOf)
        {
            if (field.Proto3Optional)
                builder.Append("optional ");
            else if (field.Label == ProtoLabel.Repeated && !IsMap(field, index))
                builder.Append("repeated ");
        }

        if (IsMap(field, index)
            && index.ByFullName.TryGetValue(field.TypeName.TrimStart('.'), out var entry))
        {
            var key = entry.Field.Single(static candidate => candidate.Number == 1);
            var value = entry.Field.Single(static candidate => candidate.Number == 2);
            builder.Append("map<").Append(EmitType(key)).Append(", ").Append(EmitType(value)).Append("> ");
        }
        else
        {
            builder.Append(EmitType(field)).Append(' ');
        }

        builder.Append(field.Name).Append(" = ").Append(field.Number.ToString(CultureInfo.InvariantCulture));
        if (GetFieldOptions(field) is { } fieldOptions)
            builder.Append(" [(exceldb.field) = ").Append(EmitFieldOptions(fieldOptions)).Append(']');
        builder.AppendLine(";");
        _ = owner;
        _ = ownerFullName;
    }

    private static void EmitEnum(StringBuilder builder, EnumDescriptorProto descriptor, int indent)
    {
        AppendIndent(builder, indent).Append("enum ").Append(descriptor.Name).AppendLine(" {");
        foreach (var range in descriptor.ReservedRange.OrderBy(static range => range.Start).ThenBy(static range => range.End))
        {
            AppendIndent(builder, indent + 1).Append("reserved ").Append(range.Start);
            if (range.End != range.Start)
                builder.Append(" to ").Append(range.End == int.MaxValue ? "max" : range.End.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(";");
        }

        if (descriptor.ReservedName.Count > 0)
        {
            AppendIndent(builder, indent + 1).Append("reserved ")
                .Append(string.Join(", ", descriptor.ReservedName.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(Quote)))
                .AppendLine(";");
        }

        foreach (var value in descriptor.Value.OrderBy(static value => value.Number).ThenBy(static value => value.Name, StringComparer.Ordinal))
        {
            AppendIndent(builder, indent + 1).Append(value.Name).Append(" = ").Append(value.Number);
            if (GetEnumValueOptions(value) is { } options)
                builder.Append(" [(exceldb.enum_value) = ").Append(EmitEnumValueOptions(options)).Append(']');
            builder.AppendLine(";");
        }

        AppendIndent(builder, indent).AppendLine("}");
    }

    private static string EmitTableOptions(TableOpts options)
    {
        var fields = new List<string>
        {
            $"kind: {EmitTableKind(options.Kind)}",
            $"id: {options.Id.ToString(CultureInfo.InvariantCulture)}",
        };
        fields.AddRange(options.Implements.Select(value => $"implements: {Quote(value)}"));
        if (!string.IsNullOrEmpty(options.DisplayName)) fields.Add($"display_name: {Quote(options.DisplayName)}");
        if (!string.IsNullOrEmpty(options.SheetName)) fields.Add($"sheet_name: {Quote(options.SheetName)}");
#pragma warning disable CS0612
        if (options.Export != ExportPolicy.ExportDefault) fields.Add($"export: {EmitExportPolicy(options.Export)}");
#pragma warning restore CS0612
        fields.AddRange(options.Validators.Select(value => $"validators: {Quote(value)}"));
        if (options.Retired) fields.Add("retired: true");
        if (options.ExportTargets is not null) fields.Add($"export_targets: {EmitTargets(options.ExportTargets)}");
        return $"{{ {string.Join(", ", fields)} }}";
    }

    private static string EmitFieldOptions(FieldOpts options)
    {
        var fields = new List<string>();
        if (options.Key != 0) fields.Add($"key: {options.Key}");
        if (!string.IsNullOrEmpty(options.DisplayName)) fields.Add($"display_name: {Quote(options.DisplayName)}");
        if (!string.IsNullOrEmpty(options.HeaderComment)) fields.Add($"header_comment: {Quote(options.HeaderComment)}");
        fields.AddRange(options.Aliases.Select(value => $"aliases: {Quote(value)}"));
        if (options.Required) fields.Add("required: true");
        if (!string.IsNullOrEmpty(options.DefaultValue)) fields.Add($"default_value: {Quote(options.DefaultValue)}");
        if (options.HasMin) fields.Add($"min: {options.Min.ToString("R", CultureInfo.InvariantCulture)}");
        if (options.HasMax) fields.Add($"max: {options.Max.ToString("R", CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrEmpty(options.Regex)) fields.Add($"regex: {Quote(options.Regex)}");
        if (options.Unique) fields.Add("unique: true");
        if (!string.IsNullOrEmpty(options.RefTable)) fields.Add($"ref_table: {Quote(options.RefTable)}");
        if (!string.IsNullOrEmpty(options.RefGroup)) fields.Add($"ref_group: {Quote(options.RefGroup)}");
        if (options.DeletePolicy != RefDeletePolicy.RefDeleteUnspecified) fields.Add($"delete_policy: {EmitDeletePolicy(options.DeletePolicy)}");
        if (options.Expand != ExpandMode.ExpandAuto) fields.Add($"expand: {EmitExpandMode(options.Expand)}");
        if (options.ChildTable is not null) fields.Add($"child_table: {{ sheet_name: {Quote(options.ChildTable.SheetName)} }}");
        if (options.Labels) fields.Add("labels: true");
        if (options.Format is not null) fields.Add($"format: {EmitCellFormat(options.Format)}");
        if (options.Expression is not null) fields.Add($"expression: {{ symbols_type: {Quote(options.Expression.SymbolsType)}, result: {EmitExprResult(options.Expression.Result)} }}");
        if (options.Weighted is not null) fields.Add($"weighted: {{ weight_field: {options.Weighted.WeightField}, condition_field: {options.Weighted.ConditionField} }}");
#pragma warning disable CS0612
        if (options.Export != ExportPolicy.ExportDefault) fields.Add($"export: {EmitExportPolicy(options.Export)}");
#pragma warning restore CS0612
        if (!string.IsNullOrEmpty(options.MapKeyEnum)) fields.Add($"map_key_enum: {Quote(options.MapKeyEnum)}");
        if (options.ExportTargets is not null) fields.Add($"export_targets: {EmitTargets(options.ExportTargets)}");
        return $"{{ {string.Join(", ", fields)} }}";
    }

    private static string EmitEnumValueOptions(EnumValueOpts options)
    {
        var fields = new List<string>();
        if (!string.IsNullOrEmpty(options.DisplayName)) fields.Add($"display_name: {Quote(options.DisplayName)}");
        fields.AddRange(options.Aliases.Select(value => $"aliases: {Quote(value)}"));
        return $"{{ {string.Join(", ", fields)} }}";
    }

    private static string EmitSchemaDefaults(SchemaDefaults defaults)
    {
        var fields = new List<string>();
        if (defaults.StructFormat is not null) fields.Add($"struct_format: {EmitCellFormat(defaults.StructFormat)}");
        if (defaults.ScalarListFormat is not null) fields.Add($"scalar_list_format: {EmitCellFormat(defaults.ScalarListFormat)}");
        if (defaults.MapFormat is not null) fields.Add($"map_format: {EmitCellFormat(defaults.MapFormat)}");
        return $"{{ {string.Join(", ", fields)} }}";
    }

    private static string EmitCellFormat(CellFormat format)
    {
        var fields = new List<string>();
        switch (format.KindCase)
        {
            case CellFormat.KindOneofCase.Join:
                fields.Add($"join: {{ {string.Join(" ", format.Join.Separators.Select(value => $"separators: {Quote(value)}"))} }}");
                break;
            case CellFormat.KindOneofCase.Named:
                fields.Add($"named: {{ pair_separator: {Quote(format.Named.PairSeparator)}, kv_separator: {Quote(format.Named.KvSeparator)} }}");
                break;
            case CellFormat.KindOneofCase.Codec:
                fields.Add($"codec: {Quote(format.Codec)}");
                break;
        }

        fields.AddRange(format.Legacy.Select(value => $"legacy: {EmitCellFormat(value)}"));
        return $"{{ {string.Join(", ", fields)} }}";
    }

    private static string EmitTargets(ExportTargetSet targets) =>
        $"{{ {string.Join(" ", targets.Ids.Select(value => $"ids: {Quote(value)}"))} }}";

    private static string EmitType(FieldDescriptorProto field)
    {
        if (!string.IsNullOrEmpty(field.TypeName))
            return field.TypeName.StartsWith(".", StringComparison.Ordinal) ? field.TypeName : $".{field.TypeName}";
        return field.Type switch
        {
            ProtoFieldType.Double => "double",
            ProtoFieldType.Float => "float",
            ProtoFieldType.Int64 => "int64",
            ProtoFieldType.Uint64 => "uint64",
            ProtoFieldType.Int32 => "int32",
            ProtoFieldType.Fixed64 => "fixed64",
            ProtoFieldType.Fixed32 => "fixed32",
            ProtoFieldType.Bool => "bool",
            ProtoFieldType.String => "string",
            ProtoFieldType.Bytes => "bytes",
            ProtoFieldType.Uint32 => "uint32",
            ProtoFieldType.Sfixed32 => "sfixed32",
            ProtoFieldType.Sfixed64 => "sfixed64",
            ProtoFieldType.Sint32 => "sint32",
            ProtoFieldType.Sint64 => "sint64",
            _ => throw new InvalidDataException($"Unsupported proto field type '{field.Type}'."),
        };
    }

    private static bool IsMap(FieldDescriptorProto field, MessageIndex index) =>
        field.Type == ProtoFieldType.Message
        && index.ByFullName.TryGetValue(field.TypeName.TrimStart('.'), out var descriptor)
        && descriptor.Options?.MapEntry == true;

    private static string EmitTableKind(TableKind value) => value switch
    {
        TableKind.Asset => "ASSET",
        TableKind.Embedded => "EMBEDDED",
        _ => "TABLE_KIND_UNSPECIFIED",
    };

    private static string EmitExportPolicy(ExportPolicy value) => value switch
    {
        ExportPolicy.ExportAll => "EXPORT_ALL",
        ExportPolicy.EditorOnly => "EDITOR_ONLY",
        _ => "EXPORT_DEFAULT",
    };

    private static string EmitDeletePolicy(RefDeletePolicy value) => value switch
    {
        RefDeletePolicy.Block => "BLOCK",
        RefDeletePolicy.SetNull => "SET_NULL",
        RefDeletePolicy.Cascade => "CASCADE",
        _ => "REF_DELETE_UNSPECIFIED",
    };

    private static string EmitExpandMode(ExpandMode value) => value switch
    {
        ExpandMode.SingleCell => "SINGLE_CELL",
        ExpandMode.ExpandedColumns => "EXPANDED_COLUMNS",
        _ => "EXPAND_AUTO",
    };

    private static string EmitExprResult(ExprResult value) => value switch
    {
        ExprResult.ExprInt => "EXPR_INT",
        ExprResult.ExprBool => "EXPR_BOOL",
        _ => "EXPR_FLOAT",
    };

    private static TableOpts? GetTableOptions(DescriptorProto descriptor) =>
        descriptor.Options is { } options && options.HasExtension(OptionsExtensions.Table)
            ? options.GetExtension(OptionsExtensions.Table)
            : null;

    private static FieldOpts? GetFieldOptions(FieldDescriptorProto descriptor) =>
        descriptor.Options is { } options && options.HasExtension(OptionsExtensions.Field)
            ? options.GetExtension(OptionsExtensions.Field)
            : null;

    private static EnumValueOpts? GetEnumValueOptions(EnumValueDescriptorProto descriptor) =>
        descriptor.Options is { } options && options.HasExtension(OptionsExtensions.EnumValue)
            ? options.GetExtension(OptionsExtensions.EnumValue)
            : null;

    private static SchemaDefaults? GetSchemaDefaults(FileDescriptorProto descriptor) =>
        descriptor.Options is { } options && options.HasExtension(OptionsExtensions.Defaults)
            ? options.GetExtension(OptionsExtensions.Defaults)
            : null;

    private static bool IsSystemFile(FileDescriptorProto file) =>
        file.Name.StartsWith("google/protobuf/", StringComparison.Ordinal)
        || string.Equals(file.Name, "exceldb/options.proto", StringComparison.Ordinal);

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static StringBuilder AppendIndent(StringBuilder builder, int indent) => builder.Append(' ', indent * 2);

    private static string Join(string prefix, string name) =>
        string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";

    private sealed class MessageIndex
    {
        private MessageIndex(IReadOnlyDictionary<string, DescriptorProto> byFullName) => ByFullName = byFullName;

        public IReadOnlyDictionary<string, DescriptorProto> ByFullName { get; }

        public static MessageIndex Create(FileDescriptorSet set)
        {
            var result = new Dictionary<string, DescriptorProto>(StringComparer.Ordinal);
            foreach (var file in set.File)
            {
                foreach (var descriptor in file.MessageType)
                    Add(result, descriptor, file.Package);
            }

            return new MessageIndex(result);
        }

        private static void Add(Dictionary<string, DescriptorProto> result, DescriptorProto descriptor, string prefix)
        {
            var fullName = Join(prefix, descriptor.Name);
            result.Add(fullName, descriptor);
            foreach (var nested in descriptor.NestedType)
                Add(result, nested, fullName);
        }
    }
}
