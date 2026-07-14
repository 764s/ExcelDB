using System.Collections.Immutable;
using System.Text.RegularExpressions;
using ExcelDb.Protocol;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Diagnostics;
using Google.Protobuf.Reflection;
using ProtoFieldType = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type;
using ProtoLabel = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label;

namespace ExcelDb.Schema.Compilation;

/// <summary>
/// Builds the canonical M1 descriptor directly from the current proto source set.
/// Generated C# and cached descriptors are deliberately not accepted as inputs.
/// </summary>
public sealed partial class SchemaCompiler
{
    private static readonly ImmutableArray<string> DefaultExportTargets = ["client", "server"];

    public async Task<SchemaCompilationResult> CompileAsync(
        string schemaDirectory,
        EmbeddedProtocCompiler? protoc = null,
        CancellationToken cancellationToken = default)
    {
        var sourceSet = SchemaSourceSet.Discover(schemaDirectory);
        if (sourceSet.Files.Count == 0)
        {
            return Failed(new SchemaDiagnostic(
                "XDB000",
                SchemaDiagnosticSeverity.Blocker,
                ".",
                "Schema source set is empty."));
        }

        var compiled = await (protoc ?? new EmbeddedProtocCompiler())
            .CompileAsync(sourceSet, cancellationToken)
            .ConfigureAwait(false);
        return Compile(compiled.DescriptorSet);
    }

    /// <summary>
    /// Builds the same canonical descriptor from an already compiled descriptor
    /// set. The input is read-only and no protoc process or filesystem access occurs.
    /// </summary>
    public SchemaCompilationResult CompileDescriptorSet(FileDescriptorSet descriptorSet) =>
        Compile(descriptorSet);

    internal SchemaCompilationResult Compile(FileDescriptorSet descriptorSet)
    {
        ArgumentNullException.ThrowIfNull(descriptorSet);

        var diagnostics = new List<SchemaDiagnostic>();
        var index = DescriptorIndex.Create(descriptorSet);
        var tables = new List<CanonicalTableDescriptor>();
        var retired = new List<CanonicalRetiredTableDescriptor>();

        foreach (var symbol in index.Messages
                     .Where(static symbol => !IsSystemFile(symbol.File))
                     .OrderBy(static symbol => symbol.FullName, StringComparer.Ordinal))
        {
            var tableOptions = GetTableOptions(symbol.Descriptor);
            if (tableOptions is null)
                continue;

            var location = symbol.FullName;
            ValidateReservedFields(symbol, diagnostics);
            foreach (var validatorId in tableOptions.Validators.Where(static value => !string.IsNullOrWhiteSpace(value)))
                AddWarning(diagnostics, "XDB012", location, $"Validator '{validatorId}' requires host registration.");
            if (tableOptions.Kind == TableKind.Unspecified)
            {
                Add(diagnostics, "XDB015", location, "A message with TableOpts must explicitly declare ASSET or EMBEDDED.");
            }

            var kind = tableOptions.Kind == TableKind.Asset
                ? CanonicalTableKind.Asset
                : CanonicalTableKind.Embedded;
            var tableTargets = ResolveTableTargets(tableOptions, location, diagnostics);

            if (tableOptions.Retired)
            {
                if (tableOptions.Kind != TableKind.Asset || tableOptions.Id <= 0)
                {
                    Add(diagnostics, "XDB019", location, "A retired table must be an ASSET with a stable positive table id.");
                }

                retired.Add(new CanonicalRetiredTableDescriptor(
                    tableOptions.Id,
                    symbol.Name,
                    symbol.FullName));
                continue;
            }

            var fields = symbol.Descriptor.Field
                .OrderBy(static field => field.Number)
                .Select(field => BuildField(
                    field,
                    field.Name,
                    tableTargets,
                    symbol,
                    symbol,
                    index,
                    new HashSet<string>(StringComparer.Ordinal) { symbol.FullName },
                    diagnostics))
                .ToImmutableArray();

            var keyFields = fields
                .Where(static field => field.KeyOrder > 0)
                .OrderBy(static field => field.KeyOrder)
                .ThenBy(static field => field.Id)
                .ToArray();

            if (kind == CanonicalTableKind.Asset)
            {
                if (tableOptions.Id <= 0)
                    Add(diagnostics, "XDB001", location, "A live ASSET table requires a positive stable table id.");

                if (keyFields.Length == 0)
                    Add(diagnostics, "XDB002", location, "A live ASSET table requires at least one key field.");
            }
            else
            {
                if (tableOptions.Id != 0 || keyFields.Length != 0)
                    Add(diagnostics, "XDB015", location, "An EMBEDDED table cannot declare a table id or key field.");
            }

            foreach (var duplicateOrder in keyFields.GroupBy(static field => field.KeyOrder).Where(static group => group.Count() > 1))
            {
                Add(diagnostics, "XDB002", location, $"Key order {duplicateOrder.Key} is declared by more than one field.");
            }

            foreach (var keyField in keyFields)
            {
                if (keyField.Shape is not (CanonicalFieldShape.Scalar or CanonicalFieldShape.Enum))
                    Add(diagnostics, "XDB004", $"{location}.{keyField.PropertyPath}", "A key field must be scalar or enum-shaped.");

                var missingTargets = tableTargets.Except(keyField.ExportTargets, StringComparer.Ordinal).ToArray();
                if (missingTargets.Length > 0)
                {
                    Add(
                        diagnostics,
                        "XDB016",
                        $"{location}.{keyField.PropertyPath}",
                        $"Key field is missing table export target(s): {string.Join(",", missingTargets)}.");
                }
            }

            tables.Add(new CanonicalTableDescriptor(
                tableOptions.Id,
                symbol.Name,
                symbol.FullName,
                kind,
                string.IsNullOrEmpty(tableOptions.SheetName) ? symbol.Name : tableOptions.SheetName,
                tableOptions.Implements.ToImmutableArray(),
                tableOptions.Validators.ToImmutableArray(),
                tableTargets,
                BuildReservedRanges(symbol.Descriptor.ReservedRange.Select(static range => ((long)range.Start, (long)range.End))),
                fields,
                keyFields.Select(static field => field.Id).ToImmutableArray())
            {
                DisplayName = string.IsNullOrEmpty(tableOptions.DisplayName) ? null : tableOptions.DisplayName,
            });
        }

        ValidateTableIds(tables, retired, diagnostics);
        ValidateReferences(tables, diagnostics);
        ValidateDependencyTargets(tables, diagnostics);

        var enums = BuildEnums(index, diagnostics);
        var orderedDiagnostics = diagnostics
            .OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Location, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToImmutableArray();

        if (orderedDiagnostics.Any(static diagnostic => diagnostic.IsBlocking))
            return new SchemaCompilationResult(null, orderedDiagnostics);

        return new SchemaCompilationResult(
            CanonicalSchemaFactory.Create(tables, retired, enums),
            orderedDiagnostics);
    }

    private static CanonicalFieldDescriptor BuildField(
        FieldDescriptorProto field,
        string propertyPath,
        ImmutableArray<string> parentTargets,
        MessageSymbol rootOwner,
        MessageSymbol declaringMessage,
        DescriptorIndex index,
        HashSet<string> typeStack,
        List<SchemaDiagnostic> diagnostics)
    {
        var location = $"{rootOwner.FullName}.{propertyPath}";
        var options = GetFieldOptions(field) ?? new FieldOpts();
        var targets = ResolveFieldTargets(options, parentTargets, location, diagnostics);
        var shape = GetShape(field, index);
        var map = BuildMapDescriptor(field, shape, options, declaringMessage, index, location, diagnostics);
        var typeName = map is null
            ? GetTypeName(field)
            : $"map<{map.KeyTypeName},{map.ValueTypeName}>";
        var oneOfGroup = GetOneOfGroup(field, declaringMessage, location, diagnostics);

        if (options.Labels && (field.Label != ProtoLabel.Repeated || field.Type != ProtoFieldType.String))
            Add(diagnostics, "XDB011", location, "labels is only valid on repeated string fields.");

        var hasReferenceTable = !string.IsNullOrEmpty(options.RefTable);
        var hasReferenceGroup = !string.IsNullOrEmpty(options.RefGroup);
        if (string.Equals(typeName, "exceldb.RowRef", StringComparison.Ordinal))
        {
            if (hasReferenceTable == hasReferenceGroup)
                Add(diagnostics, "XDB007", location, "RowRef must declare exactly one of ref_table or ref_group.");
        }
        else if (hasReferenceTable || hasReferenceGroup)
        {
            Add(diagnostics, "XDB007", location, "ref_table/ref_group can only be declared on an exceldb.RowRef field.");
        }

        var isSingularMessage = field.Type == ProtoFieldType.Message
            && field.Label != ProtoLabel.Repeated
            && shape != CanonicalFieldShape.Map;
        if (options.Expand != ExpandMode.ExpandAuto && !isSingularMessage)
        {
            Add(diagnostics, "XDB006", location, "ExpandMode can only be set on a singular message field.");
        }

        if (options.ChildTable is not null && shape != CanonicalFieldShape.RepeatedMessage)
            Add(diagnostics, "XDB006", location, "child_table can only be set on a repeated message field.");

        if (shape == CanonicalFieldShape.OneOfVariant
            && (field.Label == ProtoLabel.Repeated || IsMapField(field, index)))
        {
            Add(diagnostics, "XDB014", location, "A oneof variant cannot be repeated or map-shaped.");
        }

        var children = ImmutableArray<CanonicalFieldDescriptor>.Empty;
        MessageSymbol? childMessage = null;
        var referencedMessageName = map?.ValueShape == CanonicalFieldShape.Message
            ? map.ValueTypeName
            : NormalizeTypeName(field.TypeName);
        if (field.Type == ProtoFieldType.Message && shape != CanonicalFieldShape.Map)
            index.MessageByFullName.TryGetValue(referencedMessageName, out childMessage);
        else if (map?.ValueShape == CanonicalFieldShape.Message)
            index.MessageByFullName.TryGetValue(map.ValueTypeName, out childMessage);

        if (childMessage is not null
            && !IsBuiltInValueType(childMessage.FullName)
            && typeStack.Add(childMessage.FullName))
        {
            try
            {
                children = childMessage.Descriptor.Field
                    .OrderBy(static child => child.Number)
                    .Select(child => BuildField(
                        child,
                        shape == CanonicalFieldShape.Map
                            ? $"{propertyPath}.value.{child.Name}"
                            : $"{propertyPath}.{child.Name}",
                        targets,
                        rootOwner,
                        childMessage,
                        index,
                        typeStack,
                        diagnostics))
                    .ToImmutableArray();
            }
            finally
            {
                typeStack.Remove(childMessage.FullName);
            }
        }
        else if (childMessage is not null
                 && !IsBuiltInValueType(childMessage.FullName)
                 && typeStack.Contains(childMessage.FullName))
        {
            Add(diagnostics, "XDB005", location, $"Recursive single-cell shape '{childMessage.FullName}' is not supported.");
        }

        var expandMode = MaterializeExpandMode(field, options, children);
        if (isSingularMessage
            && expandMode == CanonicalExpandMode.SingleCell
            && ComputeStructDepth(children) > 2)
        {
            Add(diagnostics, "XDB005", location, "A single-cell message requires more than the supported two format layers.");
        }

        var cellFormat = MaterializeFormat(
            field,
            shape,
            map,
            options,
            GetSchemaDefaults(declaringMessage.File),
            expandMode,
            children);
        ValidateFormat(location, cellFormat, field, shape, map, expandMode, children, diagnostics);

        if (shape == CanonicalFieldShape.RepeatedMessage
            && options.ChildTable is not null
            && cellFormat is not null)
        {
            Add(diagnostics, "XDB006", location, "child_table and a single-cell format cannot both be declared.");
        }

        var childTableSheetName = shape switch
        {
            CanonicalFieldShape.RepeatedMessage when cellFormat is null =>
                string.IsNullOrEmpty(options.ChildTable?.SheetName) ? field.Name : options.ChildTable.SheetName,
            CanonicalFieldShape.Map when map?.ValueShape == CanonicalFieldShape.Message => field.Name,
            _ => null,
        };

        var expression = BuildExpression(field, options.Expression, declaringMessage, index, location, diagnostics);
        var weighted = BuildWeighted(field, options.Weighted, declaringMessage, index, location, diagnostics);

        var childTable = childTableSheetName is null
            ? null
            : new CanonicalChildTableDescriptor(
                shape == CanonicalFieldShape.Map
                    ? CanonicalChildTableKind.MessageMap
                    : CanonicalChildTableKind.RepeatedMessage,
                childTableSheetName,
                [],
                "__parent_guid",
                shape == CanonicalFieldShape.RepeatedMessage ? "__ordinal" : null,
                shape == CanonicalFieldShape.Map ? "__map_key" : null);

        return new CanonicalFieldDescriptor(
            field.Number,
            field.Name,
            propertyPath,
            shape,
            typeName,
            field.Proto3Optional || field.Type == ProtoFieldType.Message || shape == CanonicalFieldShape.OneOfVariant,
            oneOfGroup,
            map,
            options.Required,
            string.IsNullOrEmpty(options.DefaultValue) ? null : options.DefaultValue,
            options.HasMin ? options.Min : null,
            options.HasMax ? options.Max : null,
            string.IsNullOrEmpty(options.Regex) ? null : options.Regex,
            options.Unique,
            options.Key,
            string.IsNullOrEmpty(options.RefTable) ? null : options.RefTable,
            string.IsNullOrEmpty(options.RefGroup) ? null : options.RefGroup,
            options.DeletePolicy switch
            {
                RefDeletePolicy.SetNull => CanonicalDeletePolicy.SetNull,
                RefDeletePolicy.Cascade => CanonicalDeletePolicy.Cascade,
                _ => CanonicalDeletePolicy.Block,
            },
            expandMode,
            options.Labels,
            childTableSheetName,
            cellFormat,
            expression,
            weighted,
            targets,
            childMessage is null || IsBuiltInValueType(childMessage.FullName)
                ? ImmutableArray<CanonicalReservedNumberRange>.Empty
                : BuildReservedRanges(childMessage.Descriptor.ReservedRange.Select(static range => ((long)range.Start, (long)range.End))),
            children)
        {
            DisplayName = string.IsNullOrEmpty(options.DisplayName) ? null : options.DisplayName,
            HeaderComment = string.IsNullOrEmpty(options.HeaderComment) ? null : options.HeaderComment,
            Aliases = options.Aliases.ToImmutableArray(),
            ChildTable = childTable,
        };
    }

    private static ImmutableArray<string> ResolveTableTargets(
        TableOpts options,
        string location,
        List<SchemaDiagnostic> diagnostics)
    {
        var legacy = GetLegacyExport(options);
        if (!IsKnownLegacyExport(legacy))
            Add(diagnostics, "XDB020", location, $"Unknown legacy export value {(int)legacy}.");

        if (options.ExportTargets is not null)
        {
            if (legacy != ExportPolicy.ExportDefault)
                Add(diagnostics, "XDB020", location, "Legacy export and export_targets cannot both be declared.");
            return NormalizeTargets(options.ExportTargets.Ids, location, diagnostics);
        }

        return legacy switch
        {
            ExportPolicy.EditorOnly => ImmutableArray<string>.Empty,
            ExportPolicy.ExportDefault or ExportPolicy.ExportAll => DefaultExportTargets,
            _ => DefaultExportTargets,
        };
    }

    private static ImmutableArray<string> ResolveFieldTargets(
        FieldOpts options,
        ImmutableArray<string> parentTargets,
        string location,
        List<SchemaDiagnostic> diagnostics)
    {
        var legacy = GetLegacyExport(options);
        if (!IsKnownLegacyExport(legacy))
            Add(diagnostics, "XDB020", location, $"Unknown legacy export value {(int)legacy}.");

        ImmutableArray<string> result;
        if (options.ExportTargets is not null)
        {
            if (legacy != ExportPolicy.ExportDefault)
                Add(diagnostics, "XDB020", location, "Legacy export and export_targets cannot both be declared.");
            result = NormalizeTargets(options.ExportTargets.Ids, location, diagnostics);
        }
        else
        {
            result = legacy switch
            {
                ExportPolicy.EditorOnly => ImmutableArray<string>.Empty,
                ExportPolicy.ExportDefault or ExportPolicy.ExportAll => parentTargets,
                _ => parentTargets,
            };
        }

        var outsideParent = result.Except(parentTargets, StringComparer.Ordinal).ToArray();
        if (outsideParent.Length > 0)
        {
            Add(
                diagnostics,
                "XDB016",
                location,
                $"Field export target(s) are outside the parent target set: {string.Join(",", outsideParent)}.");
        }

        return result;
    }

    private static ImmutableArray<string> NormalizeTargets(
        IEnumerable<string> values,
        string location,
        List<SchemaDiagnostic> diagnostics)
    {
        var input = values.ToArray();
        foreach (var duplicate in input.GroupBy(static value => value, StringComparer.Ordinal).Where(static group => group.Count() > 1))
            Add(diagnostics, "XDB020", location, $"Duplicate export target id '{duplicate.Key}'.");

        foreach (var value in input.Distinct(StringComparer.Ordinal))
        {
            if (!ExportTargetPattern().IsMatch(value))
                Add(diagnostics, "XDB020", location, $"Invalid export target id '{value}'.");
        }

        return input
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static void ValidateTableIds(
        IEnumerable<CanonicalTableDescriptor> tables,
        IEnumerable<CanonicalRetiredTableDescriptor> retired,
        List<SchemaDiagnostic> diagnostics)
    {
        var occupied = tables
            .Where(static table => table.Kind == CanonicalTableKind.Asset && table.Id > 0)
            .Select(static table => (table.Id, table.FullName))
            .Concat(retired.Where(static table => table.Id > 0).Select(static table => (table.Id, table.FullName)));

        foreach (var group in occupied.GroupBy(static item => item.Id).Where(static group => group.Count() > 1))
        {
            Add(
                diagnostics,
                "XDB001",
                string.Join(",", group.Select(static item => item.FullName).OrderBy(static value => value, StringComparer.Ordinal)),
                $"Table id {group.Key} is used more than once in the live/retired id domain.");
        }
    }

    private static void ValidateReferences(
        IReadOnlyList<CanonicalTableDescriptor> tables,
        List<SchemaDiagnostic> diagnostics)
    {
        foreach (var owner in tables)
        {
            foreach (var field in Flatten(owner.Fields))
            {
                if (field.ReferenceTable is not null)
                {
                    var candidates = ResolveTableReference(owner, field.ReferenceTable, tables);
                    if (candidates.Length != 1)
                    {
                        Add(
                            diagnostics,
                            "XDB007",
                            $"{owner.FullName}.{field.PropertyPath}",
                            candidates.Length == 0
                                ? $"Reference table '{field.ReferenceTable}' does not exist."
                                : $"Reference table '{field.ReferenceTable}' is ambiguous.");
                        continue;
                    }

                    ValidateReferenceTargets(owner, field, candidates[0], diagnostics);
                }

                if (field.ReferenceGroup is not null)
                {
                    var candidates = tables
                        .Where(table => table.Implements.Contains(field.ReferenceGroup, StringComparer.Ordinal))
                        .ToArray();
                    if (candidates.Length == 0)
                    {
                        Add(
                            diagnostics,
                            "XDB007",
                            $"{owner.FullName}.{field.PropertyPath}",
                            $"Reference group '{field.ReferenceGroup}' has no implementing table.");
                    }

                    foreach (var candidate in candidates)
                        ValidateReferenceTargets(owner, field, candidate, diagnostics);
                }
            }
        }
    }

    private static void ValidateDependencyTargets(
        IReadOnlyList<CanonicalTableDescriptor> tables,
        List<SchemaDiagnostic> diagnostics)
    {
        var byFullName = tables.ToDictionary(static table => table.FullName, StringComparer.Ordinal);
        foreach (var owner in tables)
        {
            foreach (var field in Flatten(owner.Fields))
            {
                var dependencyType = field.Map?.ValueShape == CanonicalFieldShape.Message
                    ? field.Map.ValueTypeName
                    : field.Shape is CanonicalFieldShape.Message
                        or CanonicalFieldShape.RepeatedMessage
                        or CanonicalFieldShape.OneOfVariant
                        ? field.TypeName
                        : null;
                if (dependencyType is null
                    || string.Equals(dependencyType, "exceldb.RowRef", StringComparison.Ordinal)
                    || !byFullName.TryGetValue(dependencyType, out var dependency))
                {
                    continue;
                }

                var missing = field.ExportTargets.Except(dependency.ExportTargets, StringComparer.Ordinal).ToArray();
                if (missing.Length == 0)
                    continue;

                Add(
                    diagnostics,
                    "XDB016",
                    $"{owner.FullName}.{field.PropertyPath}",
                    $"Message dependency '{dependency.FullName}' is not visible for export target(s): {string.Join(",", missing)}.");
            }
        }
    }

    private static void ValidateReferenceTargets(
        CanonicalTableDescriptor owner,
        CanonicalFieldDescriptor field,
        CanonicalTableDescriptor target,
        List<SchemaDiagnostic> diagnostics)
    {
        if (target.Kind != CanonicalTableKind.Asset)
        {
            Add(
                diagnostics,
                "XDB007",
                $"{owner.FullName}.{field.PropertyPath}",
                $"Reference target '{target.FullName}' is not a live ASSET table.");
            return;
        }

        var missing = field.ExportTargets.Except(target.ExportTargets, StringComparer.Ordinal).ToArray();
        if (missing.Length == 0)
            return;

        Add(
            diagnostics,
            "XDB016",
            $"{owner.FullName}.{field.PropertyPath}",
            $"Reference target '{target.FullName}' is not visible for export target(s): {string.Join(",", missing)}.");
    }

    private static CanonicalTableDescriptor[] ResolveTableReference(
        CanonicalTableDescriptor owner,
        string reference,
        IReadOnlyList<CanonicalTableDescriptor> tables)
    {
        var explicitlyAbsolute = reference.StartsWith(".", StringComparison.Ordinal);
        var normalized = NormalizeTypeName(reference);
        if (explicitlyAbsolute)
            return tables.Where(table => string.Equals(table.FullName, normalized, StringComparison.Ordinal)).ToArray();

        if (normalized.Contains('.'))
        {
            var exact = tables.Where(table => string.Equals(table.FullName, normalized, StringComparison.Ordinal)).ToArray();
            if (exact.Length > 0)
                return exact;
        }

        var separator = owner.FullName.LastIndexOf('.');
        var scope = separator < 0 ? string.Empty : owner.FullName[..separator];
        while (!string.IsNullOrEmpty(scope))
        {
            var scopedName = $"{scope}.{normalized}";
            var scoped = tables.Where(table => string.Equals(table.FullName, scopedName, StringComparison.Ordinal)).ToArray();
            if (scoped.Length > 0)
                return scoped;

            separator = scope.LastIndexOf('.');
            scope = separator < 0 ? string.Empty : scope[..separator];
        }

        var global = tables.Where(table => string.Equals(table.FullName, normalized, StringComparison.Ordinal)).ToArray();
        if (global.Length > 0)
            return global;

        return tables.Where(table => string.Equals(table.Name, normalized, StringComparison.Ordinal)).ToArray();
    }

    private static ImmutableArray<CanonicalEnumDescriptor> BuildEnums(
        DescriptorIndex index,
        List<SchemaDiagnostic> diagnostics)
    {
        var result = ImmutableArray.CreateBuilder<CanonicalEnumDescriptor>();
        foreach (var symbol in index.Enums
                     .Where(static symbol => !IsSystemFile(symbol.File))
                     .OrderBy(static symbol => symbol.FullName, StringComparer.Ordinal))
        {
            if (!symbol.Descriptor.Value.Any(static value => value.Number == 0)
                || symbol.Descriptor.Value.GroupBy(static value => value.Number).Any(static group => group.Count() > 1))
            {
                Add(diagnostics, "XDB013", symbol.FullName, "Enum requires a zero value and unique numeric identities.");
            }

            foreach (var value in symbol.Descriptor.Value)
            {
                var reservedNumber = symbol.Descriptor.ReservedRange.Any(range =>
                    value.Number >= range.Start && value.Number <= range.End);
                var reservedName = symbol.Descriptor.ReservedName.Contains(value.Name, StringComparer.Ordinal);
                if (reservedNumber || reservedName)
                    Add(diagnostics, "XDB003", $"{symbol.FullName}.{value.Name}", "Enum value reuses a reserved numeric or name identity.");
            }

            result.Add(new CanonicalEnumDescriptor(
                symbol.Name,
                symbol.FullName,
                BuildReservedRanges(symbol.Descriptor.ReservedRange.Select(static range => ((long)range.Start, (long)range.End + 1L))),
                symbol.Descriptor.Value
                    .Select(static value => new CanonicalEnumValueDescriptor(value.Number, value.Name))
                    .ToImmutableArray()));
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<CanonicalReservedNumberRange> BuildReservedRanges(
        IEnumerable<(long Start, long EndExclusive)> ranges) =>
        ranges.Select(static range => new CanonicalReservedNumberRange(range.Start, range.EndExclusive))
            .OrderBy(static range => range.Start)
            .ThenBy(static range => range.EndExclusive)
            .ToImmutableArray();

    private static IEnumerable<CanonicalFieldDescriptor> Flatten(IEnumerable<CanonicalFieldDescriptor> fields)
    {
        foreach (var field in fields)
        {
            yield return field;
            foreach (var child in Flatten(field.Children))
                yield return child;
        }
    }

    private static CanonicalMapDescriptor? BuildMapDescriptor(
        FieldDescriptorProto field,
        CanonicalFieldShape shape,
        FieldOpts options,
        MessageSymbol declaringMessage,
        DescriptorIndex index,
        string location,
        List<SchemaDiagnostic> diagnostics)
    {
        if (shape != CanonicalFieldShape.Map)
        {
            if (!string.IsNullOrEmpty(options.MapKeyEnum))
                Add(diagnostics, "XDB010", location, "map_key_enum can only be declared on a map field.");
            return null;
        }

        if (!index.MessageByFullName.TryGetValue(NormalizeTypeName(field.TypeName), out var entry))
        {
            Add(diagnostics, "XDB010", location, "Map entry descriptor is missing.");
            return new CanonicalMapDescriptor("unknown", "unknown", CanonicalFieldShape.Scalar, null);
        }

        var key = entry.Descriptor.Field.SingleOrDefault(static candidate => candidate.Number == 1);
        var value = entry.Descriptor.Field.SingleOrDefault(static candidate => candidate.Number == 2);
        if (key is null || value is null)
        {
            Add(diagnostics, "XDB010", location, "Map entry must contain key field 1 and value field 2.");
            return new CanonicalMapDescriptor("unknown", "unknown", CanonicalFieldShape.Scalar, null);
        }

        if (!IsSupportedMapKey(key.Type))
            Add(diagnostics, "XDB010", location, $"Map key type '{GetTypeName(key)}' is not supported.");

        string? keyEnumType = null;
        if (!string.IsNullOrEmpty(options.MapKeyEnum))
        {
            if (key.Type != ProtoFieldType.Int32)
                Add(diagnostics, "XDB010", location, "map_key_enum requires an int32 map key.");

            var enumSymbol = ResolveEnumSymbol(options.MapKeyEnum, declaringMessage, index);
            if (enumSymbol is null)
            {
                Add(diagnostics, "XDB010", location, $"map_key_enum target '{options.MapKeyEnum}' is not an enum.");
            }
            else
            {
                keyEnumType = enumSymbol.FullName;
            }
        }

        var valueShape = value.Type switch
        {
            ProtoFieldType.Enum => CanonicalFieldShape.Enum,
            ProtoFieldType.Message => CanonicalFieldShape.Message,
            _ => CanonicalFieldShape.Scalar,
        };

        return new CanonicalMapDescriptor(
            GetTypeName(key),
            GetTypeName(value),
            valueShape,
            keyEnumType);
    }

    private static bool IsSupportedMapKey(ProtoFieldType type) => type is
        ProtoFieldType.Bool
        or ProtoFieldType.Int32
        or ProtoFieldType.Int64
        or ProtoFieldType.Uint32
        or ProtoFieldType.Uint64
        or ProtoFieldType.Sint32
        or ProtoFieldType.Sint64
        or ProtoFieldType.Fixed32
        or ProtoFieldType.Fixed64
        or ProtoFieldType.Sfixed32
        or ProtoFieldType.Sfixed64
        or ProtoFieldType.String;

    private static string? GetOneOfGroup(
        FieldDescriptorProto field,
        MessageSymbol declaringMessage,
        string location,
        List<SchemaDiagnostic> diagnostics)
    {
        if (!field.HasOneofIndex || field.Proto3Optional)
            return null;

        if (field.OneofIndex < 0 || field.OneofIndex >= declaringMessage.Descriptor.OneofDecl.Count)
        {
            Add(diagnostics, "XDB014", location, $"oneof index {field.OneofIndex} does not identify a group.");
            return null;
        }

        return declaringMessage.Descriptor.OneofDecl[field.OneofIndex].Name;
    }

    private static CanonicalExpression? BuildExpression(
        FieldDescriptorProto field,
        ExpressionOpts? expression,
        MessageSymbol declaringMessage,
        DescriptorIndex index,
        string location,
        List<SchemaDiagnostic> diagnostics)
    {
        if (expression is null)
            return null;

        if (field.Type != ProtoFieldType.String || field.Label == ProtoLabel.Repeated)
            Add(diagnostics, "XDB009", location, "expression can only be declared on a singular string field.");

        var symbols = ResolveMessageSymbol(expression.SymbolsType, declaringMessage, index);
        var symbolsType = expression.SymbolsType;
        if (symbols is null || string.IsNullOrEmpty(expression.SymbolsType))
        {
            Add(diagnostics, "XDB009", location, $"Expression symbols_type '{expression.SymbolsType}' does not exist.");
        }
        else
        {
            symbolsType = symbols.FullName;
            foreach (var symbolField in symbols.Descriptor.Field)
            {
                if (symbolField.Label == ProtoLabel.Repeated
                    || symbolField.Type == ProtoFieldType.Message
                    || (symbolField.HasOneofIndex && !symbolField.Proto3Optional))
                {
                    Add(
                        diagnostics,
                        "XDB009",
                        location,
                        $"Expression symbols_type '{symbols.FullName}' contains non-scalar field '{symbolField.Name}'.");
                }
            }
        }

        var resultType = expression.Result switch
        {
            ExprResult.ExprFloat => "float",
            ExprResult.ExprInt => "int",
            ExprResult.ExprBool => "bool",
            _ => $"unknown:{(int)expression.Result}",
        };
        if (resultType.StartsWith("unknown:", StringComparison.Ordinal))
            Add(diagnostics, "XDB009", location, $"Unknown expression result value {(int)expression.Result}.");

        return new CanonicalExpression(symbolsType, resultType);
    }

    private static CanonicalWeighted? BuildWeighted(
        FieldDescriptorProto field,
        WeightedOpts? weighted,
        MessageSymbol declaringMessage,
        DescriptorIndex index,
        string location,
        List<SchemaDiagnostic> diagnostics)
    {
        if (weighted is null)
            return null;

        if (field.Label != ProtoLabel.Repeated
            || field.Type != ProtoFieldType.Message
            || IsMapField(field, index))
        {
            Add(diagnostics, "XDB008", location, "weighted can only be declared on a repeated message field.");
            return new CanonicalWeighted(weighted.WeightField, weighted.ConditionField);
        }

        if (!index.MessageByFullName.TryGetValue(NormalizeTypeName(field.TypeName), out var element))
        {
            Add(diagnostics, "XDB008", location, $"Weighted element type '{field.TypeName}' does not exist.");
            return new CanonicalWeighted(weighted.WeightField, weighted.ConditionField);
        }

        var weightField = element.Descriptor.Field.SingleOrDefault(candidate => candidate.Number == weighted.WeightField);
        if (weighted.WeightField <= 0
            || weightField is null
            || weightField.Label == ProtoLabel.Repeated
            || !IsNumericScalar(weightField.Type))
        {
            Add(diagnostics, "XDB008", location, $"weight_field {weighted.WeightField} must identify a singular numeric element field.");
        }

        if (weighted.ConditionField != 0)
        {
            var condition = element.Descriptor.Field.SingleOrDefault(candidate => candidate.Number == weighted.ConditionField);
            var conditionOptions = condition is null ? null : GetFieldOptions(condition);
            var validExpression = conditionOptions?.Expression?.Result == ExprResult.ExprBool;
            var validBoolean = condition is not null
                && condition.Type == ProtoFieldType.Bool
                && condition.Label != ProtoLabel.Repeated;
            if (condition is null || (!validExpression && !validBoolean))
            {
                Add(
                    diagnostics,
                    "XDB008",
                    location,
                    $"condition_field {weighted.ConditionField} must identify bool or a bool expression field.");
            }
        }

        _ = declaringMessage;
        return new CanonicalWeighted(weighted.WeightField, weighted.ConditionField);
    }

    private static bool IsNumericScalar(ProtoFieldType type) => type is
        ProtoFieldType.Double
        or ProtoFieldType.Float
        or ProtoFieldType.Int64
        or ProtoFieldType.Uint64
        or ProtoFieldType.Int32
        or ProtoFieldType.Fixed64
        or ProtoFieldType.Fixed32
        or ProtoFieldType.Uint32
        or ProtoFieldType.Sfixed32
        or ProtoFieldType.Sfixed64
        or ProtoFieldType.Sint32
        or ProtoFieldType.Sint64;

    private static bool IsMapField(FieldDescriptorProto field, DescriptorIndex index) =>
        field.Type == ProtoFieldType.Message
        && index.MessageByFullName.TryGetValue(NormalizeTypeName(field.TypeName), out var message)
        && message.Descriptor.Options?.MapEntry == true;

    private static MessageSymbol? ResolveMessageSymbol(
        string reference,
        MessageSymbol declaringMessage,
        DescriptorIndex index) =>
        ResolveSymbol(reference, declaringMessage.FullName, index.MessageByFullName, index.Messages, static symbol => symbol.Name);

    private static EnumSymbol? ResolveEnumSymbol(
        string reference,
        MessageSymbol declaringMessage,
        DescriptorIndex index) =>
        ResolveSymbol(reference, declaringMessage.FullName, index.EnumByFullName, index.Enums, static symbol => symbol.Name);

    private static TSymbol? ResolveSymbol<TSymbol>(
        string reference,
        string declaringScope,
        IReadOnlyDictionary<string, TSymbol> byFullName,
        ImmutableArray<TSymbol> all,
        Func<TSymbol, string> getName)
        where TSymbol : class
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        var absolute = reference.StartsWith(".", StringComparison.Ordinal);
        var normalized = NormalizeTypeName(reference);
        if (absolute)
            return byFullName.TryGetValue(normalized, out var exactAbsolute) ? exactAbsolute : null;

        var scope = declaringScope;
        while (!string.IsNullOrEmpty(scope))
        {
            if (byFullName.TryGetValue($"{scope}.{normalized}", out var scoped))
                return scoped;
            var separator = scope.LastIndexOf('.');
            scope = separator < 0 ? string.Empty : scope[..separator];
        }

        if (byFullName.TryGetValue(normalized, out var exact))
            return exact;

        var shortMatches = all.Where(symbol => string.Equals(getName(symbol), normalized, StringComparison.Ordinal)).ToArray();
        return shortMatches.Length == 1 ? shortMatches[0] : null;
    }

    private static CanonicalFieldShape GetShape(FieldDescriptorProto field, DescriptorIndex index)
    {
        if (field.HasOneofIndex && !field.Proto3Optional)
            return CanonicalFieldShape.OneOfVariant;

        var repeated = field.Label == ProtoLabel.Repeated;
        if (field.Type == ProtoFieldType.Message
            && index.MessageByFullName.TryGetValue(NormalizeTypeName(field.TypeName), out var message)
            && message.Descriptor.Options?.MapEntry == true)
        {
            return CanonicalFieldShape.Map;
        }

        return (field.Type, repeated) switch
        {
            (ProtoFieldType.Enum, false) => CanonicalFieldShape.Enum,
            (ProtoFieldType.Enum, true) => CanonicalFieldShape.RepeatedEnum,
            (ProtoFieldType.Message, false) => CanonicalFieldShape.Message,
            (ProtoFieldType.Message, true) => CanonicalFieldShape.RepeatedMessage,
            (_, true) => CanonicalFieldShape.RepeatedScalar,
            _ => CanonicalFieldShape.Scalar,
        };
    }

    private static CanonicalExpandMode MaterializeExpandMode(
        FieldDescriptorProto field,
        FieldOpts options,
        ImmutableArray<CanonicalFieldDescriptor> children)
    {
        if (field.Type != ProtoFieldType.Message
            || field.Label == ProtoLabel.Repeated)
            return CanonicalExpandMode.SingleCell;

        return options.Expand switch
        {
            ExpandMode.SingleCell => CanonicalExpandMode.SingleCell,
            ExpandMode.ExpandedColumns => CanonicalExpandMode.ExpandedColumns,
            _ => children.Any(static child => child.Shape is not (CanonicalFieldShape.Scalar or CanonicalFieldShape.Enum))
                ? CanonicalExpandMode.ExpandedColumns
                : CanonicalExpandMode.SingleCell,
        };
    }

    private static CanonicalCellFormat? MaterializeFormat(
        FieldDescriptorProto field,
        CanonicalFieldShape shape,
        CanonicalMapDescriptor? map,
        FieldOpts options,
        SchemaDefaults? defaults,
        CanonicalExpandMode expandMode,
        ImmutableArray<CanonicalFieldDescriptor> children)
    {
        if (options.Format is not null)
            return BuildFormat(options.Format);

        if (shape == CanonicalFieldShape.Map)
        {
            return defaults?.MapFormat is null
                ? BuiltInNamed("; ", "=")
                : BuildFormat(defaults.MapFormat);
        }

        if (shape is CanonicalFieldShape.RepeatedScalar or CanonicalFieldShape.RepeatedEnum)
        {
            return defaults?.ScalarListFormat is null
                ? BuiltInJoin(";")
                : BuildFormat(defaults.ScalarListFormat);
        }

        if (field.Type == ProtoFieldType.Message
            && field.Label != ProtoLabel.Repeated
            && expandMode == CanonicalExpandMode.SingleCell
            && !IsBuiltInValueType(NormalizeTypeName(field.TypeName)))
        {
            return defaults?.StructFormat is null
                ? BuiltInNamed(", ", "=")
                : BuildFormat(defaults.StructFormat);
        }

        _ = map;
        _ = children;
        return null;
    }

    private static CanonicalCellFormat BuiltInJoin(string separator) =>
        new("join", ImmutableArray.Create(separator), ImmutableArray<CanonicalCellFormat>.Empty);

    private static CanonicalCellFormat BuiltInNamed(string pairSeparator, string keyValueSeparator) =>
        new(
            "named",
            ImmutableArray.Create(pairSeparator, keyValueSeparator),
            ImmutableArray<CanonicalCellFormat>.Empty);

    private static void ValidateFormat(
        string location,
        CanonicalCellFormat? format,
        FieldDescriptorProto field,
        CanonicalFieldShape shape,
        CanonicalMapDescriptor? map,
        CanonicalExpandMode expandMode,
        ImmutableArray<CanonicalFieldDescriptor> children,
        List<SchemaDiagnostic> diagnostics)
    {
        if (format is null)
            return;

        var expectedJoinDepth = shape switch
        {
            CanonicalFieldShape.RepeatedScalar or CanonicalFieldShape.RepeatedEnum => 1,
            CanonicalFieldShape.RepeatedMessage => 2,
            CanonicalFieldShape.Map => 2,
            _ when field.Type == ProtoFieldType.Message
                && field.Label != ProtoLabel.Repeated
                && expandMode == CanonicalExpandMode.SingleCell => ComputeStructDepth(children),
            _ => 1,
        };
        var namedAllowed = shape == CanonicalFieldShape.Map
            || (field.Type == ProtoFieldType.Message
                && field.Label != ProtoLabel.Repeated
                && expandMode == CanonicalExpandMode.SingleCell);

        ValidateFormatNode(location, format, expectedJoinDepth, namedAllowed, diagnostics);

        var canonicalIdentity = FormatIdentity(format);
        var legacyIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var legacy in format.Legacy)
        {
            if (!legacyIdentities.Add(FormatIdentity(legacy)))
                Add(diagnostics, "XDB018", location, "A legacy format identity is declared more than once.");
            if (string.Equals(canonicalIdentity, FormatIdentity(legacy), StringComparison.Ordinal))
                Add(diagnostics, "XDB018", location, "A legacy format has the same materialized identity as the canonical format.");
            if (!legacy.Legacy.IsEmpty)
                Add(diagnostics, "XDB018", location, "A legacy format cannot contain nested legacy formats.");

            ValidateFormatNode(location, legacy, expectedJoinDepth, namedAllowed, diagnostics);
        }

        _ = map;
    }

    private static void ValidateFormatNode(
        string location,
        CanonicalCellFormat format,
        int expectedJoinDepth,
        bool namedAllowed,
        List<SchemaDiagnostic> diagnostics)
    {
        switch (format.Kind)
        {
            case "join":
                if (format.Parameters.Length is < 1 or > 2
                    || format.Parameters.Length != expectedJoinDepth)
                {
                    Add(
                        diagnostics,
                        "XDB017",
                        location,
                        $"join declares {format.Parameters.Length} layer(s), but the value shape requires {expectedJoinDepth}.");
                }

                ValidateSeparators(location, format.Parameters, diagnostics);
                break;
            case "named":
                if (!namedAllowed)
                    Add(diagnostics, "XDB017", location, "named format is only valid for a struct or map single-cell shape.");
                if (format.Parameters.Length != 2)
                    Add(diagnostics, "XDB017", location, "named format requires pair and key/value separators.");
                ValidateSeparators(location, format.Parameters, diagnostics);
                break;
            case "codec":
                if (format.Parameters.Length != 1 || string.IsNullOrWhiteSpace(format.Parameters[0]))
                    Add(diagnostics, "XDB017", location, "codec format requires a non-empty stable id/version identity.");
                else
                    AddWarning(diagnostics, "XDB012", location, $"Codec '{format.Parameters[0]}' requires host registration.");
                break;
            default:
                Add(diagnostics, "XDB017", location, "CellFormat must declare exactly one of join, named, or codec.");
                break;
        }
    }

    private static void ValidateSeparators(
        string location,
        ImmutableArray<string> separators,
        List<SchemaDiagnostic> diagnostics)
    {
        foreach (var separator in separators)
        {
            if (string.IsNullOrEmpty(separator)
                || separator.Contains('"')
                || separator.Contains('\\'))
            {
                Add(diagnostics, "XDB017", location, "Format separators must be non-empty and cannot contain quote or escape characters.");
            }
        }

        for (var left = 0; left < separators.Length; left++)
        {
            for (var right = left + 1; right < separators.Length; right++)
            {
                if (separators[left].Contains(separators[right], StringComparison.Ordinal)
                    || separators[right].Contains(separators[left], StringComparison.Ordinal))
                {
                    Add(diagnostics, "XDB017", location, "Format separators cannot contain one another.");
                }
            }
        }
    }

    private static int ComputeStructDepth(ImmutableArray<CanonicalFieldDescriptor> children)
    {
        var childDepth = children.IsEmpty ? 0 : children.Max(ComputeNestedDepth);
        return 1 + childDepth;
    }

    private static int ComputeNestedDepth(CanonicalFieldDescriptor field) => field.Shape switch
    {
        CanonicalFieldShape.Message or CanonicalFieldShape.OneOfVariant when !field.Children.IsEmpty =>
            1 + field.Children.Max(ComputeNestedDepth),
        CanonicalFieldShape.RepeatedScalar or CanonicalFieldShape.RepeatedEnum => 1,
        CanonicalFieldShape.RepeatedMessage or CanonicalFieldShape.Map => 2,
        _ => 0,
    };

    private static string FormatIdentity(CanonicalCellFormat format) =>
        $"{format.Kind}\u001e{string.Join("\u001f", format.Parameters)}";

    private static CanonicalCellFormat? BuildFormat(CellFormat? format)
    {
        if (format is null)
            return null;

        var (kind, parameters) = format.KindCase switch
        {
            CellFormat.KindOneofCase.Join => ("join", format.Join.Separators.ToImmutableArray()),
            CellFormat.KindOneofCase.Named => (
                "named",
                ImmutableArray.Create(
                    string.IsNullOrEmpty(format.Named.PairSeparator) ? ", " : format.Named.PairSeparator,
                    string.IsNullOrEmpty(format.Named.KvSeparator) ? "=" : format.Named.KvSeparator)),
            CellFormat.KindOneofCase.Codec => ("codec", ImmutableArray.Create(format.Codec)),
            _ => ("none", ImmutableArray<string>.Empty),
        };

        return new CanonicalCellFormat(
            kind,
            parameters,
            format.Legacy.Select(BuildFormat).Where(static value => value is not null).Cast<CanonicalCellFormat>().ToImmutableArray());
    }

    private static string GetTypeName(FieldDescriptorProto field)
    {
        if (!string.IsNullOrEmpty(field.TypeName))
            return NormalizeTypeName(field.TypeName);

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
            _ => field.Type.ToString(),
        };
    }

    private static void ValidateReservedFields(
        MessageSymbol symbol,
        List<SchemaDiagnostic> diagnostics)
    {
        foreach (var duplicate in symbol.Descriptor.Field
                     .GroupBy(static field => field.Number)
                     .Where(static group => group.Count() > 1))
        {
            Add(diagnostics, "XDB003", symbol.FullName, $"Field number {duplicate.Key} is declared more than once.");
        }

        foreach (var field in symbol.Descriptor.Field)
        {
            var reservedNumber = symbol.Descriptor.ReservedRange.Any(range =>
                field.Number >= range.Start && field.Number < range.End);
            var reservedName = symbol.Descriptor.ReservedName.Contains(field.Name, StringComparer.Ordinal);
            if (reservedNumber || reservedName)
                Add(diagnostics, "XDB003", $"{symbol.FullName}.{field.Name}", "Field reuses a reserved numeric or name identity.");
        }
    }

    private static TableOpts? GetTableOptions(DescriptorProto descriptor) =>
        descriptor.Options is { } options && options.HasExtension(OptionsExtensions.Table)
            ? options.GetExtension(OptionsExtensions.Table)
            : null;

    private static FieldOpts? GetFieldOptions(FieldDescriptorProto descriptor) =>
        descriptor.Options is { } options && options.HasExtension(OptionsExtensions.Field)
            ? options.GetExtension(OptionsExtensions.Field)
            : null;

    private static SchemaDefaults? GetSchemaDefaults(FileDescriptorProto descriptor) =>
        descriptor.Options is { } options && options.HasExtension(OptionsExtensions.Defaults)
            ? options.GetExtension(OptionsExtensions.Defaults)
            : null;

#pragma warning disable CS0612 // Reading deprecated ExportPolicy is required for the normative legacy migration path.
    private static ExportPolicy GetLegacyExport(TableOpts options) => options.Export;

    private static ExportPolicy GetLegacyExport(FieldOpts options) => options.Export;
#pragma warning restore CS0612

    private static bool IsKnownLegacyExport(ExportPolicy value) => value is
        ExportPolicy.ExportDefault
        or ExportPolicy.ExportAll
        or ExportPolicy.EditorOnly;

    private static bool IsBuiltInValueType(string fullName) => fullName is
        "exceldb.RowRef"
        or "exceldb.UnityResourceRef"
        or "exceldb.LocalizedTextRef"
        or "exceldb.Curve"
        or "exceldb.CurvePoint";

    private static bool IsSystemFile(FileDescriptorProto file) =>
        file.Name.StartsWith("google/protobuf/", StringComparison.Ordinal)
        || string.Equals(file.Name, "exceldb/options.proto", StringComparison.Ordinal);

    private static string NormalizeTypeName(string value) => value.TrimStart('.');

    private static SchemaCompilationResult Failed(params SchemaDiagnostic[] diagnostics) =>
        new(null, diagnostics.ToImmutableArray());

    private static void Add(
        List<SchemaDiagnostic> diagnostics,
        string code,
        string location,
        string message) =>
        diagnostics.Add(new SchemaDiagnostic(code, SchemaDiagnosticSeverity.Blocker, location, message));

    private static void AddWarning(
        List<SchemaDiagnostic> diagnostics,
        string code,
        string location,
        string message) =>
        diagnostics.Add(new SchemaDiagnostic(code, SchemaDiagnosticSeverity.Warning, location, message));

    [GeneratedRegex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExportTargetPattern();

    private sealed record MessageSymbol(
        string Name,
        string FullName,
        DescriptorProto Descriptor,
        FileDescriptorProto File);

    private sealed record EnumSymbol(
        string Name,
        string FullName,
        EnumDescriptorProto Descriptor,
        FileDescriptorProto File);

    private sealed class DescriptorIndex
    {
        private DescriptorIndex(
            ImmutableArray<MessageSymbol> messages,
            ImmutableArray<EnumSymbol> enums)
        {
            Messages = messages;
            Enums = enums;
            MessageByFullName = messages.ToDictionary(static item => item.FullName, StringComparer.Ordinal);
            EnumByFullName = enums.ToDictionary(static item => item.FullName, StringComparer.Ordinal);
        }

        public ImmutableArray<MessageSymbol> Messages { get; }

        public ImmutableArray<EnumSymbol> Enums { get; }

        public IReadOnlyDictionary<string, MessageSymbol> MessageByFullName { get; }

        public IReadOnlyDictionary<string, EnumSymbol> EnumByFullName { get; }

        public static DescriptorIndex Create(FileDescriptorSet set)
        {
            var messages = ImmutableArray.CreateBuilder<MessageSymbol>();
            var enums = ImmutableArray.CreateBuilder<EnumSymbol>();

            foreach (var file in set.File)
            {
                var packagePrefix = string.IsNullOrEmpty(file.Package) ? string.Empty : file.Package;
                foreach (var descriptor in file.MessageType)
                    AddMessage(descriptor, packagePrefix, file, messages, enums);
                foreach (var descriptor in file.EnumType)
                    enums.Add(new EnumSymbol(descriptor.Name, Join(packagePrefix, descriptor.Name), descriptor, file));
            }

            return new DescriptorIndex(messages.ToImmutable(), enums.ToImmutable());
        }

        private static void AddMessage(
            DescriptorProto descriptor,
            string parent,
            FileDescriptorProto file,
            ImmutableArray<MessageSymbol>.Builder messages,
            ImmutableArray<EnumSymbol>.Builder enums)
        {
            var fullName = Join(parent, descriptor.Name);
            messages.Add(new MessageSymbol(descriptor.Name, fullName, descriptor, file));

            foreach (var child in descriptor.NestedType)
                AddMessage(child, fullName, file, messages, enums);
            foreach (var child in descriptor.EnumType)
                enums.Add(new EnumSymbol(child.Name, Join(fullName, child.Name), child, file));
        }

        private static string Join(string prefix, string name) =>
            string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
    }
}
