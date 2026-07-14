using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Core.Values;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.Identity;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.References;

namespace ExcelDb.Workbooks.Importing;

public readonly record struct TableKey(int TableId, string Key);

public enum ImportChangeKind
{
    Unchanged,
    Added,
    Modified,
    Renamed,
}

public sealed record ImportedRow(
    AssetIdentity? Identity,
    int TableId,
    string SheetName,
    int RowNumber,
    uint Revision,
    string? Key,
    ImmutableDictionary<string, CanonicalValue> Values,
    ImmutableDictionary<string, WorkbookCell> RawCells,
    bool IsIndexable,
    ImportChangeKind ChangeKind)
{
    public string Location => $"{SheetName}!{RowNumber}";

    /// <summary>
    /// Physical canonical states used for dirty/merge comparison.  In particular a missing cell
    /// remains Missing even when <see cref="Values"/> exposes a schema default, and a formula is
    /// represented by its formula text rather than its cached value.
    /// </summary>
    public ImmutableDictionary<string, CanonicalValue> RawValues { get; init; } = Values;
}

public sealed record SnapshotRow(
    AssetIdentity Identity,
    string? Key,
    uint Revision,
    ImmutableDictionary<string, CanonicalValue> Values,
    ImmutableDictionary<string, WorkbookCell> RawCells)
{
    public ImmutableDictionary<string, CanonicalValue> RawValues { get; init; } = Values;
}

public sealed record ImportSnapshot(
    string WorkbookPath,
    ContentFingerprint WorkbookFingerprint,
    ulong SchemaHash,
    ImmutableDictionary<AssetIdentity, SnapshotRow> Rows)
{
    public ImmutableArray<KnownIdentity> ToKnownIdentities() =>
        Rows.Values
            .OrderBy(static row => row.Identity.TableId)
            .ThenBy(static row => row.Identity.RowGuid.ToString(), StringComparer.Ordinal)
            .Select(static row => new KnownIdentity(row.Identity, row.Key))
            .ToImmutableArray();
}

public sealed record WorkbookImportResult(
    ImmutableArray<ImportedRow> Rows,
    ImmutableArray<SnapshotRow> RemovedRows,
    ImmutableDictionary<AssetIdentity, ImportedRow> IdentityIndex,
    ImmutableDictionary<TableKey, ImportedRow> KeyIndex,
    ImportSnapshot Snapshot,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool HasBlockers => Diagnostics.Any(static diagnostic => diagnostic.IsBlocker);
}

public static class WorkbookImporter
{
    public static WorkbookImportResult Import(
        string workbookPath,
        byte[] workbookBytes,
        WorkbookDefinition workbook,
        CanonicalSchemaDescriptor schema,
        ImportSnapshot? previousSnapshot = null,
        CellFormatRegistry? cellFormats = null,
        WorkbookValidatorRegistry? validators = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentNullException.ThrowIfNull(workbookBytes);
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentNullException.ThrowIfNull(schema);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (workbook.SchemaHash != schema.SchemaHash)
        {
            diagnostics.Add(new Diagnostic(
                "EXWB2000",
                DiagnosticSeverity.Blocker,
                workbookPath,
                $"Workbook schema hash {workbook.SchemaHash:x16} does not match {schema.SchemaHash:x16}."));
        }

        var schemaTables = schema.Tables.ToDictionary(static table => table.Id);
        var retiredTableIds = schema.RetiredTables.Select(static table => table.Id).ToHashSet();
        var imported = new List<ImportedRow>();
        foreach (var table in workbook.Tables.OrderBy(static table => table.TableId))
        {
            if (table.IsRetiredPreserved || retiredTableIds.Contains(table.TableId))
            {
                diagnostics.Add(new Diagnostic(
                    "table.retired-present",
                    DiagnosticSeverity.Info,
                    table.SheetName,
                    $"Historical table id {table.TableId} is retired and was preserved outside the live import domain."));
                continue;
            }

            if (!schemaTables.TryGetValue(table.TableId, out var tableSchema))
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB2001",
                    DiagnosticSeverity.Blocker,
                    table.SheetName,
                    $"Managed table id {table.TableId} is absent from the schema."));
                continue;
            }

            var fieldBindings = FlattenFieldBindings(tableSchema.Fields).ToArray();
            var fields = fieldBindings
                .GroupBy(static binding => binding.Field.PropertyPath, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First().Field, StringComparer.Ordinal);
            var boundColumns = new Dictionary<string, WorkbookColumn>(StringComparer.Ordinal);
            foreach (var binding in fieldBindings)
            {
                var column = ResolveColumn(table.Columns, binding);
                if (column is not null)
                    boundColumns[binding.Field.PropertyPath] = column;
            }

            foreach (var column in table.Columns.Where(column => !fieldBindings.Any(binding => ColumnMatches(column, binding))))
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB2002",
                    DiagnosticSeverity.Info,
                    $"{table.SheetName}!{column.PropertyPath}",
                    "Workbook helper/unknown column is not owned by the current schema and was ignored."));
            }

            foreach (var binding in fieldBindings.Where(binding => !boundColumns.ContainsKey(binding.Field.PropertyPath)))
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB2009",
                    DiagnosticSeverity.Error,
                    $"{table.SheetName}!{binding.Field.PropertyPath}",
                    "Schema field has no workbook column; run generate before write or convert."));
            }

            for (var index = 0; index < table.Rows.Length; index++)
            {
                var sourceRow = table.Rows[index];
                var rowNumber = sourceRow.SourceRowNumber ?? (table.DataStartRow + index);
                var values = ImmutableDictionary.CreateBuilder<string, CanonicalValue>(StringComparer.Ordinal);
                var rawValues = ImmutableDictionary.CreateBuilder<string, CanonicalValue>(StringComparer.Ordinal);
                var rawCells = ImmutableDictionary.CreateBuilder<string, WorkbookCell>(StringComparer.Ordinal);
                var rowIsValid = fieldBindings.All(binding => boundColumns.ContainsKey(binding.Field.PropertyPath));
                foreach (var binding in fieldBindings.OrderBy(static binding => binding.FieldPath, StringComparer.Ordinal))
                {
                    var field = binding.Field;
                    WorkbookCell? cell = null;
                    if (boundColumns.TryGetValue(field.PropertyPath, out var column)
                        && sourceRow.Cells.TryGetValue(column.PropertyPath, out var sourceCell))
                    {
                        cell = sourceCell;
                        rawCells[field.PropertyPath] = sourceCell;
                    }
                    var parsed = CanonicalCellParser.Parse(cell, field, schema, cellFormats);
                    values[field.PropertyPath] = parsed.Value;
                    rawValues[field.PropertyPath] = parsed.PhysicalValue;
                    if (parsed.UsedLegacyFormat)
                    {
                        diagnostics.Add(new Diagnostic(
                            "EXWB2008",
                            DiagnosticSeverity.Info,
                            $"{table.SheetName}!{field.PropertyPath}{rowNumber}",
                            "Cell matched a legacy CellFormat and can be rewritten by normalize."));
                    }
                    if (parsed.Error is not null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "EXWB2003",
                            DiagnosticSeverity.Error,
                            $"{table.SheetName}!{field.PropertyPath}{rowNumber}",
                            parsed.Error));
                        rowIsValid = false;
                    }
                }

                if (!ValidateOneOfGroups(tableSchema, rawValues, table.SheetName, rowNumber, diagnostics))
                    rowIsValid = false;

                var rawGuid = sourceRow.RawRowGuid ?? sourceRow.RowGuid?.ToString();
                AssetIdentity? identity = sourceRow.RowGuid is { } rowGuid
                    ? new AssetIdentity(table.TableId, rowGuid)
                    : null;
                if (identity is null)
                {
                    diagnostics.Add(new Diagnostic(
                        string.IsNullOrEmpty(rawGuid) ? "EXWB2004" : "EXWB2005",
                        string.IsNullOrEmpty(rawGuid) ? DiagnosticSeverity.Warning : DiagnosticSeverity.Blocker,
                        $"{table.SheetName}!{rowNumber}",
                        string.IsNullOrEmpty(rawGuid)
                            ? "Row has no identity and must pass data prepare before indexing."
                            : $"Row contains malformed identity '{rawGuid}'."));
                    rowIsValid = false;
                }

                if (sourceRow.RawRevision is { } rawRevision
                    && !uint.TryParse(rawRevision, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    diagnostics.Add(new Diagnostic(
                        "EXWB2006",
                        DiagnosticSeverity.Blocker,
                        $"{table.SheetName}!{rowNumber}",
                        $"Invalid row revision '{rawRevision}'."));
                    rowIsValid = false;
                }

                var key = BuildKey(tableSchema, fields, values, out var keyError);
                if (keyError is not null)
                {
                    diagnostics.Add(new Diagnostic(
                        "EXWB2007",
                        DiagnosticSeverity.Error,
                        $"{table.SheetName}!{rowNumber}",
                        keyError));
                    rowIsValid = false;
                }

                var immutableValues = values.ToImmutable();
                var immutableRawValues = rawValues.ToImmutable();
                var changeKind = DetermineChange(
                    identity,
                    key,
                    sourceRow.Revision,
                    immutableValues,
                    immutableRawValues,
                    previousSnapshot);
                var importedRow = new ImportedRow(
                    identity,
                    table.TableId,
                    table.SheetName,
                    rowNumber,
                    sourceRow.Revision,
                    key,
                    immutableValues,
                    rawCells.ToImmutable(),
                    rowIsValid,
                    changeKind)
                {
                    RawValues = immutableRawValues,
                };
                var validationDiagnostics = ValidateRegisteredRules(
                    workbookPath,
                    schema,
                    tableSchema,
                    importedRow,
                    validators);
                diagnostics.AddRange(validationDiagnostics);
                if (validationDiagnostics.Any(static diagnostic => diagnostic.IsFailure))
                    importedRow = importedRow with { IsIndexable = false };
                imported.Add(importedRow);
            }
        }

        MaterializeChildTables(workbook, schema, schemaTables, imported, diagnostics, cellFormats);

        var excludedLocations = new HashSet<string>(StringComparer.Ordinal);
        ExcludeDuplicateIdentities(imported, excludedLocations, diagnostics);
        ExcludeDuplicateKeys(imported, excludedLocations, diagnostics);
        ExcludeDuplicateUniqueFields(imported, schemaTables, excludedLocations, diagnostics);
        var finalRows = imported
            .Select(row => excludedLocations.Contains(row.Location) ? row with { IsIndexable = false } : row)
            .ToImmutableArray();

        var identityIndex = finalRows
            .Where(static row => row.IsIndexable && row.Identity is not null)
            .ToImmutableDictionary(static row => row.Identity!.Value);
        var keyIndex = finalRows
            .Where(static row => row.IsIndexable && row.Key is not null)
            .ToImmutableDictionary(static row => new TableKey(row.TableId, row.Key!));

        // Invalid values remain in Rows and Snapshot.RawCells, but are intentionally absent from indexes.
        var snapshotRows = finalRows
            .Where(static row => row.Identity is not null)
            .GroupBy(static row => row.Identity!.Value)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.Single())
            .ToImmutableDictionary(
                static row => row.Identity!.Value,
                static row => new SnapshotRow(
                    row.Identity!.Value,
                    row.Key,
                    row.Revision,
                    row.Values,
                    row.RawCells)
                {
                    RawValues = row.RawValues,
                });
        var snapshot = new ImportSnapshot(
            workbookPath,
            ContentFingerprint.FromBytes(workbookBytes),
            workbook.SchemaHash,
            snapshotRows);
        var removedRows = previousSnapshot is null
            ? ImmutableArray<SnapshotRow>.Empty
            : previousSnapshot.Rows.Values
                .Where(row => !snapshot.Rows.ContainsKey(row.Identity))
                .OrderBy(static row => row.Identity.TableId)
                .ThenBy(static row => row.Identity.RowGuid.ToString(), StringComparer.Ordinal)
                .ToImmutableArray();
        return new WorkbookImportResult(
            finalRows,
            removedRows,
            identityIndex,
            keyIndex,
            snapshot,
            diagnostics.ToImmutable());
    }

    private static IEnumerable<FieldBinding> FlattenFieldBindings(
        ImmutableArray<CanonicalFieldDescriptor> fields,
        string? parentPath = null)
    {
        foreach (var field in fields)
        {
            if (field.ChildTable is { } childTable
                && field.FieldIdPath.AsSpan().SequenceEqual(childTable.OwnerFieldIdPath.AsSpan()))
                continue;

            var fieldPath = !field.FieldIdPath.IsDefaultOrEmpty
                ? string.Join('.', field.FieldIdPath)
                : parentPath is null ? field.Id.ToString(CultureInfo.InvariantCulture) : $"{parentPath}.{field.Id}";
            if (!field.Children.IsDefaultOrEmpty && field.ExpandMode == CanonicalExpandMode.ExpandedColumns)
            {
                foreach (var child in FlattenFieldBindings(field.Children, fieldPath))
                    yield return child;
            }
            else
            {
                yield return new FieldBinding(field, fieldPath);
            }
        }
    }

    private static void MaterializeChildTables(
        WorkbookDefinition workbook,
        CanonicalSchemaDescriptor schema,
        IReadOnlyDictionary<int, CanonicalTableDescriptor> schemaTables,
        List<ImportedRow> imported,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CellFormatRegistry? cellFormats)
    {
        var parentRows = imported
            .Select((row, index) => (row, index))
            .Where(static item => item.row.Identity is not null)
            .GroupBy(static item => item.row.Identity!.Value)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        foreach (var childTable in workbook.EffectiveChildTables
                     .OrderBy(static table => table.OwnerTableId)
                     .ThenBy(static table => string.Join('.', table.OwnerFieldIdPath), StringComparer.Ordinal))
        {
            if (!schemaTables.TryGetValue(childTable.OwnerTableId, out var ownerTable))
                continue;
            var ownerField = FindField(ownerTable.Fields, childTable.OwnerFieldIdPath);
            if (ownerField?.ChildTable is null)
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB2020",
                    DiagnosticSeverity.Error,
                    childTable.SheetName,
                    "Child worksheet has no matching schema owner field."));
                continue;
            }

            var bindings = FlattenFieldBindings(ownerField.Children).ToArray();
            var boundColumns = bindings.ToDictionary(
                static binding => binding,
                binding => ResolveColumn(childTable.Columns, binding));
            var materialized = new Dictionary<AssetIdentity, List<(WorkbookChildRow Row, JsonObject Value)>>();
            foreach (var sourceRow in childTable.Rows)
            {
                var rowNumber = sourceRow.SourceRowNumber ?? childTable.DataStartRow;
                if (sourceRow.ParentRowGuid is not { } parentGuid)
                {
                    diagnostics.Add(new Diagnostic(
                        "EXWB2021",
                        DiagnosticSeverity.Error,
                        $"{childTable.SheetName}!{rowNumber}",
                        "Child row requires a valid parent row guid."));
                    continue;
                }

                var identity = new AssetIdentity(childTable.OwnerTableId, parentGuid);
                if (!parentRows.TryGetValue(identity, out var parents) || parents.Length != 1)
                {
                    diagnostics.Add(new Diagnostic(
                        "EXWB2022",
                        DiagnosticSeverity.Error,
                        $"{childTable.SheetName}!{rowNumber}",
                        $"Child row references missing or ambiguous parent '{parentGuid}'."));
                    continue;
                }

                var value = new JsonObject();
                var valid = true;
                foreach (var binding in bindings.OrderBy(static binding => binding.FieldPath, StringComparer.Ordinal))
                {
                    var column = boundColumns[binding];
                    WorkbookCell? cell = null;
                    if (column is not null)
                        sourceRow.Cells.TryGetValue(column.PropertyPath, out cell);
                    var parsed = CanonicalCellParser.Parse(cell, binding.Field, schema, cellFormats);
                    if (parsed.Error is not null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "EXWB2023",
                            DiagnosticSeverity.Error,
                            $"{childTable.SheetName}!{binding.Field.PropertyPath}{rowNumber}",
                            parsed.Error));
                        valid = false;
                        continue;
                    }

                    if (parsed.Value.State is CanonicalValueState.Missing or CanonicalValueState.Defaulted)
                    {
                        if (parsed.Value.State == CanonicalValueState.Missing)
                            continue;
                    }
                    SetJsonValue(value, ownerField, binding.Field, parsed.Value);
                }

                if (!valid)
                    continue;
                if (!materialized.TryGetValue(identity, out var values))
                    materialized.Add(identity, values = []);
                values.Add((sourceRow, value));
            }

            foreach (var (identity, values) in materialized)
            {
                JsonNode aggregate;
                if (childTable.Kind == CanonicalChildTableKind.RepeatedMessage)
                {
                    aggregate = new JsonArray(values
                        .OrderBy(static item => item.Row.Ordinal)
                        .Select(static item => (JsonNode)item.Value)
                        .ToArray());
                }
                else
                {
                    var map = new JsonObject();
                    foreach (var item in values.OrderBy(static item => item.Row.MapKey, StringComparer.Ordinal))
                        map[item.Row.MapKey!] = item.Value;
                    aggregate = map;
                }

                var parent = parentRows[identity][0];
                var currentParent = imported[parent.index];
                var canonical = CanonicalValue.FromValue(CanonicalJson.Serialize(aggregate));
                var valuesBuilder = currentParent.Values.ToBuilder();
                var rawBuilder = currentParent.RawValues.ToBuilder();
                valuesBuilder[ownerField.PropertyPath] = canonical;
                rawBuilder[ownerField.PropertyPath] = canonical;
                imported[parent.index] = currentParent with
                {
                    Values = valuesBuilder.ToImmutable(),
                    RawValues = rawBuilder.ToImmutable(),
                };
            }
        }
    }

    private static CanonicalFieldDescriptor? FindField(
        IEnumerable<CanonicalFieldDescriptor> fields,
        ImmutableArray<int> path)
    {
        IEnumerable<CanonicalFieldDescriptor> siblings = fields;
        CanonicalFieldDescriptor? current = null;
        foreach (var id in path)
        {
            current = siblings.FirstOrDefault(field => field.Id == id);
            if (current is null)
                return null;
            siblings = current.Children;
        }
        return current;
    }

    private static void SetJsonValue(
        JsonObject root,
        CanonicalFieldDescriptor owner,
        CanonicalFieldDescriptor leaf,
        CanonicalValue value)
    {
        var relativePath = leaf.FieldIdPath.Skip(owner.FieldIdPath.Length).ToArray();
        JsonObject target = root;
        IEnumerable<CanonicalFieldDescriptor> siblings = owner.Children;
        for (var index = 0; index < relativePath.Length; index++)
        {
            var descriptor = siblings.Single(field => field.Id == relativePath[index]);
            var propertyName = descriptor.Name;
            if (index == relativePath.Length - 1)
            {
                target[propertyName] = ToJsonNode(value, descriptor);
                return;
            }

            if (target[propertyName] is not JsonObject nested)
            {
                nested = new JsonObject();
                target[propertyName] = nested;
            }
            target = nested;
            siblings = descriptor.Children;
        }
    }

    private static JsonNode? ToJsonNode(CanonicalValue value, CanonicalFieldDescriptor field)
    {
        if (value.State == CanonicalValueState.Null)
            return null;
        var text = value.Text ?? string.Empty;
        if (field.Shape is CanonicalFieldShape.Message
            or CanonicalFieldShape.RepeatedMessage
            or CanonicalFieldShape.RepeatedScalar
            or CanonicalFieldShape.RepeatedEnum
            or CanonicalFieldShape.Map)
            return JsonNode.Parse(text);
        if (field.Shape == CanonicalFieldShape.Enum)
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enumNumber)
                ? JsonValue.Create(enumNumber)
                : JsonValue.Create(text);
        return field.TypeName switch
        {
            "bool" => JsonValue.Create(bool.Parse(text)),
            "double" or "float" => JsonValue.Create(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)),
            "int32" or "sint32" or "sfixed32" or "int64" or "sint64" or "sfixed64" =>
                JsonValue.Create(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            "uint32" or "fixed32" or "uint64" or "fixed64" =>
                JsonValue.Create(ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(text),
        };
    }

    private static IEnumerable<CanonicalFieldDescriptor> FlattenFields(
        ImmutableArray<CanonicalFieldDescriptor> fields) =>
        FlattenFieldBindings(fields).Select(static binding => binding.Field);

    private static WorkbookColumn? ResolveColumn(
        ImmutableArray<WorkbookColumn> columns,
        FieldBinding binding)
    {
        var byNumericIdentity = columns.FirstOrDefault(column =>
            string.Equals(column.FieldPath, binding.FieldPath, StringComparison.Ordinal));
        if (byNumericIdentity is not null)
            return byNumericIdentity;

        var byCurrentName = columns.FirstOrDefault(column =>
            string.Equals(column.PropertyPath, binding.Field.PropertyPath, StringComparison.Ordinal));
        if (byCurrentName is not null)
            return byCurrentName;

        return columns.FirstOrDefault(column =>
            binding.Field.Aliases.Any(alias => string.Equals(alias, column.PropertyPath, StringComparison.Ordinal))
            || column.EffectiveAliases.Any(alias =>
                string.Equals(alias, binding.Field.PropertyPath, StringComparison.Ordinal)));
    }

    private static bool ColumnMatches(WorkbookColumn column, FieldBinding binding) =>
        string.Equals(column.FieldPath, binding.FieldPath, StringComparison.Ordinal)
        || string.Equals(column.PropertyPath, binding.Field.PropertyPath, StringComparison.Ordinal)
        || binding.Field.Aliases.Any(alias => string.Equals(alias, column.PropertyPath, StringComparison.Ordinal))
        || column.EffectiveAliases.Any(alias =>
            string.Equals(alias, binding.Field.PropertyPath, StringComparison.Ordinal));

    private static bool ValidateOneOfGroups(
        CanonicalTableDescriptor table,
        ImmutableDictionary<string, CanonicalValue>.Builder rawValues,
        string sheetName,
        int rowNumber,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var valid = true;
        foreach (var group in table.Fields
                     .Where(static field => !string.IsNullOrEmpty(field.OneOfGroup))
                     .GroupBy(static field => field.OneOfGroup!, StringComparer.Ordinal))
        {
            var selected = group
                .Where(field => rawValues.TryGetValue(field.PropertyPath, out var value)
                    && value.State is CanonicalValueState.Value or CanonicalValueState.Defaulted)
                .ToArray();
            if (selected.Length <= 1)
                continue;
            diagnostics.Add(new Diagnostic(
                "EXWB2013",
                DiagnosticSeverity.Error,
                $"{sheetName}!{rowNumber}",
                $"Oneof '{group.Key}' selects multiple variants: {string.Join(", ", selected.Select(static field => field.Name))}."));
            valid = false;
        }

        return valid;
    }

    private static ImmutableArray<Diagnostic> ValidateRegisteredRules(
        string workbookPath,
        CanonicalSchemaDescriptor schema,
        CanonicalTableDescriptor table,
        ImportedRow row,
        WorkbookValidatorRegistry? validators)
    {
        if (table.ValidatorIds.IsDefaultOrEmpty)
            return [];
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var validatorId in table.ValidatorIds)
        {
            if (validators is null || !validators.TryGet(validatorId, out var validator))
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB2020",
                    DiagnosticSeverity.Error,
                    row.Location,
                    $"Workbook validator '{validatorId}' is not registered in this host."));
                continue;
            }

            try
            {
                diagnostics.AddRange(validator.Validate(
                    new WorkbookValidationContext(workbookPath, schema, table, row)) ?? []);
            }
            catch (Exception exception)
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB2021",
                    DiagnosticSeverity.Blocker,
                    row.Location,
                    $"Workbook validator '{validatorId}' failed: {exception.Message}"));
            }
        }

        return diagnostics.ToImmutable();
    }

    private static ImportChangeKind DetermineChange(
        AssetIdentity? identity,
        string? key,
        uint revision,
        ImmutableDictionary<string, CanonicalValue> values,
        ImmutableDictionary<string, CanonicalValue> rawValues,
        ImportSnapshot? previousSnapshot)
    {
        if (identity is null || previousSnapshot is null || !previousSnapshot.Rows.TryGetValue(identity.Value, out var previous))
            return ImportChangeKind.Added;
        if (!string.Equals(previous.Key, key, StringComparison.Ordinal))
            return ImportChangeKind.Renamed;
        return DictionaryEqual(previous.RawValues, rawValues)
            ? ImportChangeKind.Unchanged
            : ImportChangeKind.Modified;
    }

    private static bool DictionaryEqual(
        ImmutableDictionary<string, CanonicalValue> left,
        ImmutableDictionary<string, CanonicalValue> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private sealed record FieldBinding(CanonicalFieldDescriptor Field, string FieldPath);

    private static string? BuildKey(
        CanonicalTableDescriptor table,
        IReadOnlyDictionary<string, CanonicalFieldDescriptor> fieldsByPath,
        ImmutableDictionary<string, CanonicalValue>.Builder values,
        out string? error)
    {
        error = null;
        if (table.KeyFieldIds.IsDefaultOrEmpty)
            return null;
        var topLevelById = table.Fields.ToDictionary(static field => field.Id);
        var parts = new List<string>();
        foreach (var fieldId in table.KeyFieldIds)
        {
            if (!topLevelById.TryGetValue(fieldId, out var field)
                || !fieldsByPath.ContainsKey(field.PropertyPath)
                || !values.TryGetValue(field.PropertyPath, out var value))
            {
                error = $"Key field id {fieldId} is not represented by a workbook column.";
                return null;
            }

            if (value.State is not (CanonicalValueState.Value or CanonicalValueState.Defaulted))
            {
                error = $"Key field '{field.PropertyPath}' is {value.State} and cannot be indexed.";
                return null;
            }

            parts.Add(value.Text!);
        }

        return CanonicalKeyCodec.Format(parts);
    }

    private static void ExcludeDuplicateIdentities(
        IEnumerable<ImportedRow> rows,
        HashSet<string> excluded,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (var group in rows
                     .Where(static row => row.Identity is not null)
                     .GroupBy(static row => row.Identity!.Value)
                     .Where(static group => group.Count() > 1))
        {
            foreach (var row in group)
                excluded.Add(row.Location);
            diagnostics.Add(new Diagnostic(
                "EXWB2010",
                DiagnosticSeverity.Blocker,
                string.Join(", ", group.Select(static row => row.Location)),
                $"Identity {group.Key} occurs more than once; no row was indexed."));
        }
    }

    private static void ExcludeDuplicateKeys(
        IEnumerable<ImportedRow> rows,
        HashSet<string> excluded,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (var group in rows
                     .Where(static row => row.Key is not null)
                     .GroupBy(static row => new TableKey(row.TableId, row.Key!))
                     .Where(static group => group.Count() > 1))
        {
            foreach (var row in group)
                excluded.Add(row.Location);
            diagnostics.Add(new Diagnostic(
                "EXWB2011",
                DiagnosticSeverity.Blocker,
                string.Join(", ", group.Select(static row => row.Location)),
                $"Key '{group.Key.Key}' is duplicated in table {group.Key.TableId}; no row was indexed."));
        }
    }

    private static void ExcludeDuplicateUniqueFields(
        IEnumerable<ImportedRow> rows,
        IReadOnlyDictionary<int, CanonicalTableDescriptor> schemas,
        HashSet<string> excluded,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (var tableRows in rows.GroupBy(static row => row.TableId))
        {
            if (!schemas.TryGetValue(tableRows.Key, out var schema))
                continue;
            foreach (var field in FlattenFields(schema.Fields).Where(static field => field.Unique))
            {
                foreach (var group in tableRows
                             .Where(row => row.Values.TryGetValue(field.PropertyPath, out var value)
                                 && value.State is CanonicalValueState.Value or CanonicalValueState.Defaulted)
                             .GroupBy(row => row.Values[field.PropertyPath].Text!, StringComparer.Ordinal)
                             .Where(static group => group.Count() > 1))
                {
                    foreach (var row in group)
                        excluded.Add(row.Location);
                    diagnostics.Add(new Diagnostic(
                        "EXWB2012",
                        DiagnosticSeverity.Error,
                        string.Join(", ", group.Select(static row => row.Location)),
                        $"Unique field '{field.PropertyPath}' repeats value '{group.Key}'; affected rows were not indexed."));
                }
            }
        }
    }
}

public readonly record struct CanonicalCellParseResult(
    CanonicalValue Value,
    string? Error,
    bool UsedLegacyFormat = false,
    string? CanonicalPhysicalText = null,
    CanonicalValue? RawValue = null)
{
    public CanonicalValue PhysicalValue => RawValue ?? Value;
}

public static class CanonicalCellParser
{
    private static readonly CellFormatRegistry BuiltInOnly = new();

    public static CanonicalCellParseResult Parse(
        WorkbookCell? cell,
        CanonicalFieldDescriptor field,
        CanonicalSchemaDescriptor schema,
        CellFormatRegistry? formats = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(schema);
        if (cell is null || (cell.Formula is null && string.IsNullOrEmpty(cell.Text)))
        {
            var rawMissing = CanonicalValue.Missing;
            var effective = rawMissing.MaterializeDefault(field.DefaultValue);
            return new CanonicalCellParseResult(
                effective,
                field.Required ? $"Required field '{field.PropertyPath}' is empty." : null,
                RawValue: rawMissing);
        }

        if (cell.Formula is not null)
        {
            var formulaValue = CanonicalValue.FromValue($"={cell.Formula}");
            if (string.IsNullOrEmpty(cell.Text))
            {
                return new CanonicalCellParseResult(
                    CanonicalValue.Invalid(cell.ComparisonText),
                    $"Formula field '{field.PropertyPath}' has no cached value to validate.",
                    RawValue: formulaValue);
            }

            var cached = Parse(new WorkbookCell(cell.Text), field, schema, formats);
            return cached with { RawValue = formulaValue };
        }
        var raw = cell.Text!;
        if (string.Equals(raw, WorkbookProtocol.ExplicitNullToken, StringComparison.Ordinal))
        {
            return field.Required
                ? new CanonicalCellParseResult(CanonicalValue.Null, $"Required field '{field.PropertyPath}' is explicitly null.")
                : new CanonicalCellParseResult(CanonicalValue.Null, null, CanonicalPhysicalText: WorkbookProtocol.ExplicitNullToken);
        }

        if (string.Equals(field.TypeName, "string", StringComparison.Ordinal)
            && string.Equals(raw, "%7E", StringComparison.OrdinalIgnoreCase))
        {
            return new CanonicalCellParseResult(
                CanonicalValue.FromValue(WorkbookProtocol.ExplicitNullToken),
                null,
                CanonicalPhysicalText: "%7E");
        }

        var usedLegacy = false;
        string? physical = null;
        string canonical;
        string? error;
        if (field.CellFormat is not null)
        {
            if (!CellFormatWindow.TryParse(
                    raw,
                    field,
                    schema,
                    formats ?? BuiltInOnly,
                    out var formatted,
                    out error))
                return new CanonicalCellParseResult(CanonicalValue.Invalid(raw), error);
            canonical = formatted.CanonicalValue;
            physical = formatted.CanonicalPhysicalText;
            usedLegacy = formatted.UsedLegacyFormat;
        }
        else if (!TryCanonicalize(raw, field, schema, out canonical, out error))
            return new CanonicalCellParseResult(CanonicalValue.Invalid(raw), error);
        if (field.Minimum is { } minimum
            && double.TryParse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericMinimum)
            && numericMinimum < minimum)
        {
            return new CanonicalCellParseResult(CanonicalValue.Invalid(raw), $"Value is below minimum {minimum}.");
        }

        if (field.Maximum is { } maximum
            && double.TryParse(canonical, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericMaximum)
            && numericMaximum > maximum)
        {
            return new CanonicalCellParseResult(CanonicalValue.Invalid(raw), $"Value is above maximum {maximum}.");
        }

        if (field.Regex is { } pattern && !Regex.IsMatch(canonical, pattern, RegexOptions.CultureInvariant))
            return new CanonicalCellParseResult(CanonicalValue.Invalid(raw), $"Value does not match /{pattern}/.");
        physical ??= string.Equals(field.TypeName, "string", StringComparison.Ordinal)
                     && string.Equals(canonical, WorkbookProtocol.ExplicitNullToken, StringComparison.Ordinal)
            ? "%7E"
            : canonical;
        return new CanonicalCellParseResult(
            CanonicalValue.FromValue(canonical),
            null,
            UsedLegacyFormat: usedLegacy,
            CanonicalPhysicalText: physical);
    }

    private static bool TryCanonicalize(
        string raw,
        CanonicalFieldDescriptor field,
        CanonicalSchemaDescriptor schema,
        out string canonical,
        out string? error)
    {
        canonical = raw;
        error = null;
        if (field.Shape == CanonicalFieldShape.Message
            && string.Equals(field.TypeName, "exceldb.RowRef", StringComparison.Ordinal))
        {
            if (!RowReferenceToken.TryParse(raw, out var token, out error))
                return false;
            canonical = token.ToString();
            return true;
        }

        var type = field.TypeName.Split('.').Last().ToLowerInvariant();
        if (field.Shape == CanonicalFieldShape.Enum)
        {
            var enumType = schema.Enums.FirstOrDefault(item =>
                string.Equals(item.FullName, field.TypeName, StringComparison.Ordinal)
                || string.Equals(item.Name, field.TypeName, StringComparison.Ordinal));
            var validName = enumType?.Values.Any(value => string.Equals(value.Name, raw, StringComparison.Ordinal)) == true;
            var validNumber = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                && enumType?.Values.Any(value => value.Number == number) == true;
            if (validName || validNumber)
                return true;
            error = $"'{raw}' is not a member of enum '{field.TypeName}'.";
            return false;
        }

        if (field.Shape is CanonicalFieldShape.RepeatedScalar
            or CanonicalFieldShape.RepeatedEnum
            or CanonicalFieldShape.RepeatedMessage
            or CanonicalFieldShape.Map
            or CanonicalFieldShape.Message)
        {
            return CanonicalJson.TryNormalize(raw, out canonical, out error);
        }

        switch (type)
        {
            case "string":
            case "bytes":
                return true;
            case "bool":
            case "boolean":
                if (bool.TryParse(raw, out var boolean))
                {
                    canonical = CanonicalValue.CanonicalizeBoolean(boolean);
                    return true;
                }

                if (raw is "0" or "1")
                {
                    canonical = raw == "1" ? "true" : "false";
                    return true;
                }

                error = $"'{raw}' is not a boolean.";
                return false;
            case "int32":
            case "sint32":
            case "sfixed32":
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int32))
                {
                    canonical = int32.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                break;
            case "int64":
            case "sint64":
            case "sfixed64":
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int64))
                {
                    canonical = CanonicalValue.CanonicalizeInt64(int64);
                    return true;
                }

                break;
            case "uint32":
            case "fixed32":
                if (uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uint32))
                {
                    canonical = uint32.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                break;
            case "uint64":
            case "fixed64":
                if (ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uint64))
                {
                    canonical = CanonicalValue.CanonicalizeUInt64(uint64);
                    return true;
                }

                break;
            case "float":
            case "double":
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating)
                    && double.IsFinite(floating))
                {
                    canonical = CanonicalValue.CanonicalizeDouble(floating);
                    return true;
                }

                break;
            default:
                // Scalar wrappers and project-specific cell formats are already canonical strings here.
                return true;
        }

        error = $"'{raw}' is not a valid {field.TypeName}.";
        return false;
    }
}
