using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Schema.Descriptors;

namespace ExcelDb.Compatibility;

public sealed class CompatibilityAnalyzer
{
    public CompatibilityReport Analyze(
        CanonicalSchemaDescriptor current,
        CanonicalSchemaDescriptor? previous,
        CompatibilityDataFacts? dataFacts = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        dataFacts ??= CompatibilityDataFacts.Empty;
        var entries = new List<CompatibilityEntry>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        if (previous is null)
        {
            entries.Add(new CompatibilityEntry(
                CompatibilityChangeKind.HistoryUnavailable,
                CompatibilitySeverity.Warning,
                Location(),
                "The previous published descriptor is unavailable; destructive apply and rename inference are disabled."));
            diagnostics.Add(new Diagnostic(
                "compat.history-missing",
                DiagnosticSeverity.Warning,
                ".",
                "Only current-schema lint and read-only compatibility analysis are allowed."));
        }
        else
        {
            AnalyzeTables(current, previous, dataFacts, entries);
            AnalyzeEnums(current, previous, entries);
        }

        AnalyzeData(current, previous, dataFacts, entries);
        return Build(current, previous, entries, diagnostics);
    }

    private static void AnalyzeTables(
        CanonicalSchemaDescriptor current,
        CanonicalSchemaDescriptor previous,
        CompatibilityDataFacts facts,
        List<CompatibilityEntry> entries)
    {
        var currentByFullName = current.Tables.ToDictionary(static table => table.FullName, StringComparer.Ordinal);
        foreach (var previousTable in previous.Tables.OrderBy(static table => table.FullName, StringComparer.Ordinal))
        {
            if (currentByFullName.TryGetValue(previousTable.FullName, out var sameName)
                && previousTable.Kind != sameName.Kind)
            {
                Add(entries, CompatibilityChangeKind.TableKindChanged, CompatibilitySeverity.Blocker,
                    Location(previousTable.Id, previousPath: previousTable.FullName, currentPath: sameName.FullName),
                    $"Table '{previousTable.FullName}' changed kind from {previousTable.Kind} to {sameName.Kind}.");
            }
        }

        var currentLiveById = current.Tables
            .Where(static table => table.Kind == CanonicalTableKind.Asset)
            .ToDictionary(static table => table.Id);
        var currentRetiredById = current.RetiredTables.ToDictionary(static table => table.Id);
        var previousLiveById = previous.Tables
            .Where(static table => table.Kind == CanonicalTableKind.Asset)
            .ToDictionary(static table => table.Id);
        var previousRetiredById = previous.RetiredTables.ToDictionary(static table => table.Id);

        foreach (var oldTable in previousLiveById.Values.OrderBy(static table => table.Id))
        {
            if (currentLiveById.TryGetValue(oldTable.Id, out var newTable))
            {
                if (!string.Equals(oldTable.FullName, newTable.FullName, StringComparison.Ordinal)
                    || !string.Equals(oldTable.Name, newTable.Name, StringComparison.Ordinal)
                    || !string.Equals(oldTable.SheetName, newTable.SheetName, StringComparison.Ordinal))
                {
                    Add(entries, CompatibilityChangeKind.TableRenamed, CompatibilitySeverity.Safe,
                        Location(
                            oldTable.Id,
                            previousPath: $"{oldTable.FullName}@{oldTable.SheetName}",
                            currentPath: $"{newTable.FullName}@{newTable.SheetName}"),
                        $"Table {oldTable.Id} declaration/sheet was renamed from '{oldTable.FullName}@{oldTable.SheetName}' to '{newTable.FullName}@{newTable.SheetName}'.");
                }

                AnalyzeTable(
                    oldTable,
                    newTable,
                    facts,
                    previous.SchemaHash,
                    facts.NonEmptyTableIds.Contains(oldTable.Id),
                    entries);
                continue;
            }

            if (currentRetiredById.TryGetValue(oldTable.Id, out var retired))
            {
                if (!string.Equals(oldTable.FullName, retired.FullName, StringComparison.Ordinal))
                {
                    Add(entries, CompatibilityChangeKind.TableRenamed, CompatibilitySeverity.Safe,
                        Location(oldTable.Id, previousPath: oldTable.FullName, currentPath: retired.FullName),
                        $"Table {oldTable.Id} was renamed from '{oldTable.FullName}' to '{retired.FullName}' while retiring.");
                }

                Add(entries,
                    CompatibilityChangeKind.TableRetired,
                    CompatibilitySeverity.Warning,
                    Location(oldTable.Id, previousPath: oldTable.FullName, currentPath: retired.FullName),
                    $"Table {oldTable.Id} was explicitly retired; historical workbook data must be preserved.");
                continue;
            }

            Add(entries, CompatibilityChangeKind.TableTombstoneMissing, CompatibilitySeverity.Blocker,
                Location(oldTable.Id, previousPath: oldTable.FullName),
                $"Published table {oldTable.Id} was removed without a retired tombstone.");
        }

        foreach (var oldRetired in previousRetiredById.Values.OrderBy(static table => table.Id))
        {
            if (currentRetiredById.TryGetValue(oldRetired.Id, out var stillRetired))
            {
                if (!string.Equals(oldRetired.FullName, stillRetired.FullName, StringComparison.Ordinal))
                {
                    Add(entries, CompatibilityChangeKind.TableRenamed, CompatibilitySeverity.Safe,
                        Location(oldRetired.Id, previousPath: oldRetired.FullName, currentPath: stillRetired.FullName),
                        $"Retired table tombstone {oldRetired.Id} was renamed without changing identity.");
                }
                continue;
            }

            if (currentLiveById.TryGetValue(oldRetired.Id, out var reactivated))
            {
                Add(entries, CompatibilityChangeKind.TableReactivated, CompatibilitySeverity.Blocker,
                    Location(oldRetired.Id, previousPath: oldRetired.FullName, currentPath: reactivated.FullName),
                    $"Retired table id {oldRetired.Id} cannot become live again.");
            }
            else
            {
                Add(entries, CompatibilityChangeKind.TableTombstoneMissing, CompatibilitySeverity.Blocker,
                    Location(oldRetired.Id, previousPath: oldRetired.FullName),
                    $"Retired table tombstone {oldRetired.Id} was removed.");
            }
        }

        foreach (var newTable in currentLiveById.Values
                     .Where(table => !previousLiveById.ContainsKey(table.Id) && !previousRetiredById.ContainsKey(table.Id))
                     .OrderBy(static table => table.Id))
        {
            Add(entries, CompatibilityChangeKind.TableAdded, CompatibilitySeverity.Safe,
                Location(newTable.Id, currentPath: newTable.FullName),
                $"New table {newTable.Id} '{newTable.FullName}' was added.");
        }

        // Embedded shapes do not have table ids; only compare stable full-name owners that remain present.
        var oldEmbedded = previous.Tables.Where(static table => table.Kind == CanonicalTableKind.Embedded)
            .ToDictionary(static table => table.FullName, StringComparer.Ordinal);
        foreach (var newEmbedded in current.Tables.Where(static table => table.Kind == CanonicalTableKind.Embedded))
        {
            if (oldEmbedded.TryGetValue(newEmbedded.FullName, out var oldShape))
                AnalyzeTable(oldShape, newEmbedded, facts, previous.SchemaHash, false, entries);
        }
    }

    private static void AnalyzeTable(
        CanonicalTableDescriptor previous,
        CanonicalTableDescriptor current,
        CompatibilityDataFacts facts,
        ulong evidenceSchemaHash,
        bool hasData,
        List<CompatibilityEntry> entries)
    {
        if (!previous.KeyFieldIds.SequenceEqual(current.KeyFieldIds))
        {
            var requiresExplicitRekey = hasData
                                        || (previous.Kind == CanonicalTableKind.Asset
                                            && !facts.HasCompleteWorkbookCoverage);
            Add(entries, CompatibilityChangeKind.KeyChanged,
                requiresExplicitRekey ? CompatibilitySeverity.Blocker : CompatibilitySeverity.Safe,
                Location(current.Id, previousPath: previous.FullName, currentPath: current.FullName),
                hasData
                    ? "A non-empty table changed its key field set/order and requires an explicit rekey plan."
                    : requiresExplicitRekey
                        ? "Table key fields changed without complete evidence that every controlled workbook is empty."
                        : "A proven-empty table changed its key field set/order.");
        }

        CompareTargets(previous.Id, [], previous.FullName, current.FullName, previous.ExportTargets, current.ExportTargets, entries);
        AnalyzeFields(
            previous.Id,
            [],
            previous.Fields,
            current.Fields,
            current.ReservedFieldNumbers,
            facts,
            evidenceSchemaHash,
            entries);
    }

    private static void AnalyzeFields(
        int tableId,
        ImmutableArray<int> parentPath,
        ImmutableArray<CanonicalFieldDescriptor> previous,
        ImmutableArray<CanonicalFieldDescriptor> current,
        ImmutableArray<CanonicalReservedNumberRange> currentReserved,
        CompatibilityDataFacts facts,
        ulong evidenceSchemaHash,
        List<CompatibilityEntry> entries)
    {
        var currentById = current.ToDictionary(static field => field.Id);
        var previousById = previous.ToDictionary(static field => field.Id);
        foreach (var oldField in previous.OrderBy(static field => field.Id))
        {
            var idPath = parentPath.Add(oldField.Id);
            if (!currentById.TryGetValue(oldField.Id, out var newField))
            {
                var reserved = currentReserved.Any(range => range.Contains(oldField.Id));
                Add(entries,
                    reserved ? CompatibilityChangeKind.FieldRemoved : CompatibilityChangeKind.FieldIdentityReused,
                    reserved ? CompatibilitySeverity.Warning : CompatibilitySeverity.Blocker,
                    Location(tableId, idPath, previousPath: oldField.PropertyPath),
                    reserved
                        ? $"Field {FormatPath(idPath)} was removed and its number is reserved."
                        : $"Field {FormatPath(idPath)} was removed without reserving its number.");
                continue;
            }

            if (!string.Equals(oldField.Name, newField.Name, StringComparison.Ordinal))
            {
                Add(entries, CompatibilityChangeKind.FieldRenamed, CompatibilitySeverity.Safe,
                    Location(tableId, idPath, previousPath: oldField.PropertyPath, currentPath: newField.PropertyPath),
                    $"Field {FormatPath(idPath)} was renamed from '{oldField.Name}' to '{newField.Name}'.");
            }

            if (!SemanticIdentityEquals(oldField, newField))
            {
                Add(entries, CompatibilityChangeKind.FieldSemanticChanged, CompatibilitySeverity.Blocker,
                    Location(tableId, idPath, previousPath: oldField.PropertyPath, currentPath: newField.PropertyPath),
                    $"Field {FormatPath(idPath)} changed canonical type, shape, reference, format or validation semantics in place.");
            }

            AnalyzeCellFormat(
                tableId,
                idPath,
                oldField.CellFormat,
                newField.CellFormat,
                facts,
                evidenceSchemaHash,
                entries);

            CompareTargets(tableId, idPath, oldField.PropertyPath, newField.PropertyPath, oldField.ExportTargets, newField.ExportTargets, entries);
            AnalyzeFields(
                tableId,
                idPath,
                oldField.Children,
                newField.Children,
                newField.ReservedChildFieldNumbers,
                facts,
                evidenceSchemaHash,
                entries);
        }

        foreach (var newField in current.Where(field => !previousById.ContainsKey(field.Id)).OrderBy(static field => field.Id))
        {
            var idPath = parentPath.Add(newField.Id);
            var severity = newField.Required && newField.DefaultValue is null
                ? CompatibilitySeverity.Error
                : CompatibilitySeverity.Safe;
            Add(entries, CompatibilityChangeKind.FieldAdded, severity,
                Location(tableId, idPath, currentPath: newField.PropertyPath),
                severity == CompatibilitySeverity.Error
                    ? $"Required field {FormatPath(idPath)} was added without a default."
                    : $"Field {FormatPath(idPath)} was added.");
        }
    }

    private static void CompareTargets(
        int tableId,
        ImmutableArray<int> fieldPath,
        string previousPath,
        string currentPath,
        ImmutableArray<string> previous,
        ImmutableArray<string> current,
        List<CompatibilityEntry> entries)
    {
        foreach (var added in current.Except(previous, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Add(entries, CompatibilityChangeKind.ExportTargetAdded, CompatibilitySeverity.Safe,
                Location(tableId, fieldPath, target: added, previousPath: previousPath, currentPath: currentPath),
                $"Export target '{added}' was added.");
        }

        foreach (var removed in previous.Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Add(entries, CompatibilityChangeKind.ExportTargetRemoved, CompatibilitySeverity.Warning,
                Location(tableId, fieldPath, target: removed, previousPath: previousPath, currentPath: currentPath),
                $"Export target '{removed}' was removed and requires host/runtime artifact coordination.");
        }
    }

    private static void AnalyzeCellFormat(
        int tableId,
        ImmutableArray<int> fieldPath,
        CanonicalCellFormat? previous,
        CanonicalCellFormat? current,
        CompatibilityDataFacts facts,
        ulong evidenceSchemaHash,
        List<CompatibilityEntry> entries)
    {
        if (CellFormatEquals(previous, current))
            return;

        if (previous is null || current is null)
        {
            Add(entries, CompatibilityChangeKind.CellFormatChangedInPlace, CompatibilitySeverity.Blocker,
                Location(tableId, fieldPath),
                $"Field {FormatPath(fieldPath)} added or removed its canonical cell format without a migration window.");
            return;
        }

        var previousCanonical = FormatIdentity(previous);
        var currentCanonical = FormatIdentity(current);
        var previousLegacy = previous.Legacy.Select(FormatIdentity).ToHashSet(StringComparer.Ordinal);
        var currentLegacy = current.Legacy.Select(FormatIdentity).ToHashSet(StringComparer.Ordinal);
        var canonicalChanged = !string.Equals(previousCanonical, currentCanonical, StringComparison.Ordinal);
        if (canonicalChanged)
        {
            if (currentLegacy.Contains(previousCanonical))
            {
                Add(entries, CompatibilityChangeKind.CellFormatMigrationStarted, CompatibilitySeverity.Warning,
                    Location(tableId, fieldPath),
                    $"Field {FormatPath(fieldPath)} changed canonical format and retained the previous format as legacy.");
            }
            else
            {
                Add(entries, CompatibilityChangeKind.CellFormatChangedInPlace, CompatibilitySeverity.Blocker,
                    Location(tableId, fieldPath),
                    $"Field {FormatPath(fieldPath)} changed canonical format without retaining the previous format as legacy.");
            }
        }

        foreach (var added in currentLegacy.Except(previousLegacy, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (canonicalChanged && string.Equals(added, previousCanonical, StringComparison.Ordinal))
                continue;
            Add(entries, CompatibilityChangeKind.CellFormatLegacyAdded, CompatibilitySeverity.Safe,
                Location(tableId, fieldPath),
                $"Field {FormatPath(fieldPath)} added legacy format '{added}'.");
        }

        foreach (var removed in previousLegacy.Except(currentLegacy, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (string.Equals(removed, currentCanonical, StringComparison.Ordinal))
                continue;
            var matchingEvidence = facts.LegacyFormatEvidence
                .Where(item => item.TableId == tableId && item.FieldIdPath.SequenceEqual(fieldPath))
                .ToArray();
            var safe = matchingEvidence.Length == 1
                       && ProvesSafeLegacyRemoval(matchingEvidence[0], evidenceSchemaHash, facts);
            Add(entries, CompatibilityChangeKind.CellFormatLegacyRemoved,
                safe ? CompatibilitySeverity.Safe : CompatibilitySeverity.Blocker,
                Location(tableId, fieldPath),
                safe
                    ? $"Field {FormatPath(fieldPath)} removed legacy format '{removed}' after complete zero-hit evidence."
                    : $"Field {FormatPath(fieldPath)} removed legacy format '{removed}' without complete zero-hit evidence.");
        }
    }

    private static bool ProvesSafeLegacyRemoval(
        LegacyFormatEvidence evidence,
        ulong expectedSchemaHash,
        CompatibilityDataFacts facts)
    {
        if (!evidence.ProvesSafeRemoval
            || !facts.HasCompleteWorkbookCoverage
            || evidence.SchemaHash != expectedSchemaHash)
        {
            return false;
        }

        var workbooks = facts.Workbooks;
        if (workbooks.Select(static item => item.WorkbookId).Distinct(StringComparer.Ordinal).Count() != workbooks.Length
            || evidence.WorkbookRevisions.Count != workbooks.Length)
        {
            return false;
        }

        return workbooks.All(workbook =>
            evidence.WorkbookRevisions.TryGetValue(workbook.WorkbookId, out var revision)
            && string.Equals(revision, workbook.Revision, StringComparison.Ordinal));
    }

    private static void AnalyzeData(
        CanonicalSchemaDescriptor current,
        CanonicalSchemaDescriptor? previous,
        CompatibilityDataFacts facts,
        List<CompatibilityEntry> entries)
    {
        foreach (var duplicate in facts.Workbooks
                     .GroupBy(static item => item.WorkbookId, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            Add(entries, CompatibilityChangeKind.WorkbookEvidenceInvalid, CompatibilitySeverity.Blocker,
                Location(workbookId: duplicate.Key),
                $"Compatibility evidence contains workbook id '{duplicate.Key}' more than once.");
        }

        if (!facts.Workbooks.IsEmpty && !facts.HasCompleteWorkbookCoverage)
        {
            Add(entries, CompatibilityChangeKind.WorkbookCoverageIncomplete, CompatibilitySeverity.Warning,
                Location(),
                "Compatibility evidence does not cover every controlled workbook.");
        }

        foreach (var workbook in facts.Workbooks.OrderBy(static item => item.WorkbookId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(workbook.WorkbookId)
                || string.IsNullOrWhiteSpace(workbook.Revision)
                || workbook.UnparseableValueCount < 0
                || workbook.PendingMigrationCount < 0
                || workbook.LegacyFormatHitCount < 0
                || workbook.LegacyFormatErrorCount < 0)
            {
                Add(entries, CompatibilityChangeKind.WorkbookEvidenceInvalid, CompatibilitySeverity.Blocker,
                    Location(workbookId: workbook.WorkbookId),
                    "Workbook compatibility evidence has a missing identity/revision or a negative count.");
            }

            if (workbook.SchemaHash != current.SchemaHash)
            {
                if (previous is not null && workbook.SchemaHash == previous.SchemaHash)
                {
                    Add(entries, CompatibilityChangeKind.WorkbookOutdated, CompatibilitySeverity.Warning,
                        Location(workbookId: workbook.WorkbookId),
                        $"Workbook '{workbook.WorkbookId}' still records previous schema {workbook.SchemaHash:x16}.");
                }
                else
                {
                    Add(entries, CompatibilityChangeKind.WorkbookSchemaDrift, CompatibilitySeverity.Error,
                        Location(workbookId: workbook.WorkbookId),
                        $"Workbook '{workbook.WorkbookId}' records unknown schema {workbook.SchemaHash:x16}.");
                }
            }

            if (!workbook.MappingComplete)
            {
                Add(entries, CompatibilityChangeKind.WorkbookMappingInvalid, CompatibilitySeverity.Blocker,
                    Location(workbookId: workbook.WorkbookId),
                    $"Workbook '{workbook.WorkbookId}' has an incomplete or ambiguous numeric mapping.");
            }

            if (!workbook.OwnershipSafe)
            {
                Add(entries, CompatibilityChangeKind.WorkbookOwnershipConflict, CompatibilitySeverity.Blocker,
                    Location(workbookId: workbook.WorkbookId),
                    $"Workbook '{workbook.WorkbookId}' has schema-owned changes overlapping unmanaged content.");
            }

            if (workbook.UnparseableValueCount > 0)
            {
                Add(entries, CompatibilityChangeKind.UnparseableData, CompatibilitySeverity.Error,
                    Location(workbookId: workbook.WorkbookId),
                    $"Workbook '{workbook.WorkbookId}' contains {workbook.UnparseableValueCount} unparseable value(s).");
            }

            if (workbook.PendingMigrationCount > 0)
            {
                Add(entries, CompatibilityChangeKind.MigrationPending, CompatibilitySeverity.Error,
                    Location(workbookId: workbook.WorkbookId),
                    $"Workbook '{workbook.WorkbookId}' has {workbook.PendingMigrationCount} pending migration item(s).");
            }

            if (workbook.LegacyFormatHitCount > 0 || workbook.LegacyFormatErrorCount > 0)
            {
                var severity = workbook.LegacyFormatErrorCount > 0
                    ? CompatibilitySeverity.Error
                    : CompatibilitySeverity.Warning;
                Add(entries, CompatibilityChangeKind.LegacyFormatHits, severity,
                    Location(workbookId: workbook.WorkbookId),
                    $"Workbook '{workbook.WorkbookId}' has {workbook.LegacyFormatHitCount} legacy hit(s) and {workbook.LegacyFormatErrorCount} format error(s).");
            }
        }
    }

    private static bool SemanticIdentityEquals(CanonicalFieldDescriptor left, CanonicalFieldDescriptor right) =>
        left.Shape == right.Shape
        && string.Equals(left.TypeName, right.TypeName, StringComparison.Ordinal)
        && left.HasPresence == right.HasPresence
        && string.Equals(left.OneOfGroup, right.OneOfGroup, StringComparison.Ordinal)
        && Equals(left.Map, right.Map)
        && left.Required == right.Required
        && string.Equals(left.DefaultValue, right.DefaultValue, StringComparison.Ordinal)
        && left.Minimum == right.Minimum
        && left.Maximum == right.Maximum
        && string.Equals(left.Regex, right.Regex, StringComparison.Ordinal)
        && left.Unique == right.Unique
        && left.KeyOrder == right.KeyOrder
        && string.Equals(left.ReferenceTable, right.ReferenceTable, StringComparison.Ordinal)
        && string.Equals(left.ReferenceGroup, right.ReferenceGroup, StringComparison.Ordinal)
        && left.DeletePolicy == right.DeletePolicy
        && left.ExpandMode == right.ExpandMode
        && left.Labels == right.Labels
        && string.Equals(left.ChildTableSheetName, right.ChildTableSheetName, StringComparison.Ordinal)
        && Equals(left.Expression, right.Expression)
        && Equals(left.Weighted, right.Weighted);

    private static bool CellFormatEquals(CanonicalCellFormat? left, CanonicalCellFormat? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;
        return string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
            && left.Parameters.SequenceEqual(right.Parameters, StringComparer.Ordinal)
            && left.Legacy.Length == right.Legacy.Length
            && left.Legacy.Zip(right.Legacy).All(static pair => CellFormatEquals(pair.First, pair.Second));
    }

    private static string FormatIdentity(CanonicalCellFormat format) =>
        $"{format.Kind}\u001e{string.Join("\u001f", format.Parameters)}";

    private static void AnalyzeEnums(
        CanonicalSchemaDescriptor current,
        CanonicalSchemaDescriptor previous,
        List<CompatibilityEntry> entries)
    {
        var currentByName = current.Enums.ToDictionary(static item => item.FullName, StringComparer.Ordinal);
        foreach (var oldEnum in previous.Enums.OrderBy(static item => item.FullName, StringComparer.Ordinal))
        {
            if (!currentByName.TryGetValue(oldEnum.FullName, out var newEnum))
                continue; // A field type change/removal owns the blocking classification.

            var currentValues = newEnum.Values.ToDictionary(static item => item.Number);
            var previousValues = oldEnum.Values.ToDictionary(static item => item.Number);
            foreach (var oldValue in oldEnum.Values.OrderBy(static value => value.Number))
            {
                if (currentValues.TryGetValue(oldValue.Number, out var newValue))
                {
                    if (!string.Equals(oldValue.Name, newValue.Name, StringComparison.Ordinal))
                    {
                        Add(entries, CompatibilityChangeKind.EnumValueRenamed, CompatibilitySeverity.Safe,
                            Location(enumName: oldEnum.FullName, enumNumber: oldValue.Number, previousPath: oldValue.Name, currentPath: newValue.Name),
                            $"Enum value {oldEnum.FullName}:{oldValue.Number} was renamed.");
                    }
                }
                else
                {
                    var reserved = newEnum.ReservedNumbers.Any(range => range.Contains(oldValue.Number));
                    Add(entries,
                        reserved ? CompatibilityChangeKind.EnumValueRemoved : CompatibilityChangeKind.EnumValueIdentityReused,
                        reserved ? CompatibilitySeverity.Warning : CompatibilitySeverity.Blocker,
                        Location(enumName: oldEnum.FullName, enumNumber: oldValue.Number, previousPath: oldValue.Name),
                        reserved
                            ? $"Enum value {oldEnum.FullName}:{oldValue.Number} was removed and reserved."
                            : $"Enum value {oldEnum.FullName}:{oldValue.Number} was removed without reserving its number.");
                }
            }

            foreach (var newValue in newEnum.Values.Where(value => !previousValues.ContainsKey(value.Number)).OrderBy(static value => value.Number))
            {
                Add(entries, CompatibilityChangeKind.EnumValueAdded, CompatibilitySeverity.Safe,
                    Location(enumName: newEnum.FullName, enumNumber: newValue.Number, currentPath: newValue.Name),
                    $"Enum value {newEnum.FullName}:{newValue.Number} was added.");
            }
        }

        foreach (var newEnum in current.Enums.Where(item => !previous.Enums.Any(old => string.Equals(old.FullName, item.FullName, StringComparison.Ordinal))))
        {
            Add(entries, CompatibilityChangeKind.EnumAdded, CompatibilitySeverity.Safe,
                Location(enumName: newEnum.FullName, currentPath: newEnum.FullName),
                $"Enum '{newEnum.FullName}' was added.");
        }
    }

    private static CompatibilityReport Build(
        CanonicalSchemaDescriptor current,
        CanonicalSchemaDescriptor? previous,
        List<CompatibilityEntry> entries,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            CompatibilityReport.CurrentFormatVersion,
            previous?.SchemaHash,
            current.SchemaHash,
            previous is not null,
            entries.OrderByDescending(static item => item.Severity)
                .ThenBy(static item => item.Location.TableId ?? int.MaxValue)
                .ThenBy(static item => string.Join('.', item.Location.FieldIdPath), StringComparer.Ordinal)
                .ThenBy(static item => item.Location.EnumName, StringComparer.Ordinal)
                .ThenBy(static item => item.Location.EnumNumber ?? int.MaxValue)
                .ThenBy(static item => item.Kind)
                .ThenBy(static item => item.Location.ExportTarget, StringComparer.Ordinal)
                .ThenBy(static item => item.Location.WorkbookId, StringComparer.Ordinal)
                .ThenBy(static item => item.Location.Cell, StringComparer.Ordinal)
                .ToImmutableArray(),
            diagnostics.ToImmutable());

    private static void Add(
        List<CompatibilityEntry> entries,
        CompatibilityChangeKind kind,
        CompatibilitySeverity severity,
        CompatibilityLocation location,
        string message) => entries.Add(new CompatibilityEntry(kind, severity, location, message));

    private static CompatibilityLocation Location(
        int? tableId = null,
        ImmutableArray<int> fieldPath = default,
        string? enumName = null,
        int? enumNumber = null,
        string? target = null,
        string? previousPath = null,
        string? currentPath = null,
        string? workbookId = null,
        string? cell = null) =>
        new(tableId, fieldPath.IsDefault ? [] : fieldPath, enumName, enumNumber, target, previousPath, currentPath, workbookId, cell);

    private static string FormatPath(ImmutableArray<int> path) => string.Join('.', path);
}
