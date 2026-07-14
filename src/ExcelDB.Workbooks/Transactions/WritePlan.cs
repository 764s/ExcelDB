using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Core.Values;
using ExcelDb.Workbooks.Authoring;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;
using ExcelDb.Workbooks.References;

namespace ExcelDb.Workbooks.Transactions;

public enum CellOwnershipKind
{
    Unmanaged,
    Business,
    SystemIdentity,
    ProtocolMetadata,
}

public static class WorkbookOwnership
{
    public static CellOwnershipKind Resolve(
        WorkbookDefinition workbook,
        string sheetName,
        int row,
        int column)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        if (string.Equals(sheetName, WorkbookProtocol.MetadataSheetName, StringComparison.Ordinal)
            || string.Equals(sheetName, WorkbookProtocol.KeySheetName, StringComparison.Ordinal))
        {
            return CellOwnershipKind.ProtocolMetadata;
        }

        var table = workbook.Tables.SingleOrDefault(item =>
            string.Equals(item.SheetName, sheetName, StringComparison.Ordinal));
        if (table is null || row < table.DataStartRow || column <= 0 || column > table.Columns.Length + 2)
            return CellOwnershipKind.Unmanaged;
        return column <= table.Columns.Length
            ? CellOwnershipKind.Business
            : CellOwnershipKind.SystemIdentity;
    }
}

public interface IImpactClosureProvider
{
    ImmutableHashSet<AssetIdentity> Expand(
        IEnumerable<AssetIdentity> seeds,
        ImportSnapshot snapshot);
}

public sealed class IdentityOnlyImpactClosureProvider : IImpactClosureProvider
{
    public ImmutableHashSet<AssetIdentity> Expand(
        IEnumerable<AssetIdentity> seeds,
        ImportSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(snapshot);
        return seeds.ToImmutableHashSet();
    }
}

public sealed record PlannedRevision(AssetIdentity Identity, uint? Before, uint? After);

public sealed record WorkbookWritePlan(
    string WorkbookPath,
    ContentFingerprint SourceFingerprint,
    ulong SchemaHash,
    ImmutableHashSet<AssetIdentity> AffectedIdentities,
    ImmutableArray<CellPatch> Patches,
    ImmutableArray<PlannedRevision> Revisions,
    ImmutableArray<Diagnostic> Diagnostics,
    string PlanHash)
{
    public bool CanApply => !Diagnostics.Any(static diagnostic => diagnostic.IsFailure);

    public static WorkbookWritePlan Create(
        string workbookPath,
        WorkbookDefinition workbook,
        ImportSnapshot snapshot,
        IReadOnlyDictionary<AssetIdentity, DraftChange> changes,
        IImpactClosureProvider? impactClosureProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changes);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var closureProvider = impactClosureProvider ?? new IdentityOnlyImpactClosureProvider();
        var seeds = changes.Keys.ToImmutableHashSet();
        var affected = closureProvider.Expand(seeds, snapshot).Union(seeds);
        var patches = new List<CellPatch>();
        var revisions = new List<PlannedRevision>();

        var currentRows = new Dictionary<AssetIdentity, (WorkbookTable Table, WorkbookRow Row)>();
        var keyRows = new Dictionary<AssetIdentity, int>();
        var projectionCells = new Dictionary<AssetIdentity, (int Row, int Column)>();
        var orderedTables = workbook.Tables.OrderBy(static table => table.TableId).ToArray();
        var keyRowNumber = 2;
        for (var tableIndex = 0; tableIndex < orderedTables.Length; tableIndex++)
        {
            var table = orderedTables[tableIndex];
            for (var rowIndex = 0; rowIndex < table.Rows.Length; rowIndex++)
            {
                var row = table.Rows[rowIndex];
                if (row.RowGuid is { } rowGuid)
                {
                    var identity = new AssetIdentity(table.TableId, rowGuid);
                    currentRows.TryAdd(identity, (table, row));
                    keyRows.TryAdd(identity, keyRowNumber);
                    projectionCells.TryAdd(identity, (rowIndex + 2, tableIndex + 6));
                }

                keyRowNumber++;
            }
        }

        var nextKeyRow = keyRowNumber;
        var nextProjectionRows = orderedTables.ToDictionary(
            static table => table.TableId,
            static table => table.Rows.Length + 2);
        var projectionColumns = orderedTables
            .Select((table, index) => (table.TableId, Column: index + 6))
            .ToDictionary(static item => item.TableId, static item => item.Column);
        var nextTableRows = workbook.Tables.ToDictionary(
            static table => table.TableId,
            static table => Math.Max(
                table.DataStartRow,
                table.Rows.Select(static row => row.SourceRowNumber ?? 0).DefaultIfEmpty(table.DataStartRow - 1).Max() + 1));
        foreach (var pair in changes
                     .OrderBy(static pair => pair.Key.TableId)
                     .ThenBy(static pair => pair.Key.RowGuid.ToString(), StringComparer.Ordinal))
        {
            var identity = pair.Key;
            var change = pair.Value;
            if (change.Row?.Values.Any(static value => value.Value.State == CanonicalValueState.Invalid) == true)
            {
                diagnostics.Add(Blocker("EXWB3008", identity, "Rows containing invalid raw values cannot enter a write plan."));
                continue;
            }

            if (change.Kind == DraftChangeKind.Added)
            {
                if (currentRows.ContainsKey(identity))
                {
                    diagnostics.Add(Blocker("EXWB3000", identity, "New-row identity already exists in the workbook."));
                    continue;
                }

                var table = workbook.Tables.SingleOrDefault(item => item.TableId == identity.TableId);
                if (table is null || change.Row is null)
                {
                    diagnostics.Add(Blocker("EXWB3001", identity, "New row refers to an unknown table."));
                    continue;
                }

                if (change.Row.Revision != 0)
                {
                    diagnostics.Add(Blocker("EXWB3002", identity, "New rows must start at revision 0."));
                    continue;
                }

                var rowNumber = nextTableRows[table.TableId]++;
                AddValuePatches(patches, table, rowNumber, null, change.Row);
                patches.Add(new CellPatch(table.SheetName, rowNumber, table.Columns.Length + 1, new WorkbookCell(identity.RowGuid.ToString())));
                patches.Add(new CellPatch(table.SheetName, rowNumber, table.Columns.Length + 2, new WorkbookCell("0")));
                AddKeyPatches(
                    patches,
                    nextKeyRow++,
                    rowNumber,
                    nextProjectionRows[identity.TableId]++,
                    projectionColumns[identity.TableId],
                    identity,
                    change.Row.Key);
                revisions.Add(new PlannedRevision(identity, null, 0));
                continue;
            }

            if (!currentRows.TryGetValue(identity, out var current))
            {
                diagnostics.Add(Blocker("EXWB3003", identity, "Edited row is absent from the workbook."));
                continue;
            }

            var sourceRowNumber = current.Row.SourceRowNumber
                ?? throw new InvalidDataException($"Managed row {identity} has no source position.");
            if (change.Kind == DraftChangeKind.Deleted)
            {
                for (var column = 1; column <= current.Table.Columns.Length + 2; column++)
                    patches.Add(new CellPatch(current.Table.SheetName, sourceRowNumber, column, null));
                if (keyRows.TryGetValue(identity, out var keyRow))
                {
                    for (var column = 1; column <= 5; column++)
                        patches.Add(new CellPatch(WorkbookProtocol.KeySheetName, keyRow, column, null));
                }
                if (projectionCells.TryGetValue(identity, out var projectionCell))
                {
                    patches.Add(new CellPatch(
                        WorkbookProtocol.KeySheetName,
                        projectionCell.Row,
                        projectionCell.Column,
                        null));
                }

                revisions.Add(new PlannedRevision(identity, current.Row.Revision, null));
                continue;
            }

            if (change.Row is null)
            {
                diagnostics.Add(Blocker("EXWB3004", identity, "Non-delete change has no row value."));
                continue;
            }

            if (current.Row.Revision == uint.MaxValue)
            {
                diagnostics.Add(Blocker("EXWB3005", identity, "Row revision overflow requires manual repair."));
                continue;
            }

            snapshot.Rows.TryGetValue(identity, out var baseRow);
            AddValuePatches(patches, current.Table, sourceRowNumber, baseRow, change.Row);
            var nextRevision = current.Row.Revision + 1;
            patches.Add(new CellPatch(
                current.Table.SheetName,
                sourceRowNumber,
                current.Table.Columns.Length + 2,
                new WorkbookCell(nextRevision.ToString(CultureInfo.InvariantCulture))));
            if (baseRow is null || !string.Equals(baseRow.Key, change.Row.Key, StringComparison.Ordinal))
            {
                if (keyRows.TryGetValue(identity, out var keyRow))
                {
                    patches.Add(new CellPatch(WorkbookProtocol.KeySheetName, keyRow, 3, ToTextCell(change.Row.Key)));
                    patches.Add(new CellPatch(
                        WorkbookProtocol.KeySheetName,
                        keyRow,
                        5,
                        ToTextCell(ToReferenceToken(identity.TableId, change.Row.Key))));
                }
                if (projectionCells.TryGetValue(identity, out var projectionCell))
                {
                    patches.Add(new CellPatch(
                        WorkbookProtocol.KeySheetName,
                        projectionCell.Row,
                        projectionCell.Column,
                        ToTextCell(ToReferenceToken(identity.TableId, change.Row.Key))));
                }
            }

            revisions.Add(new PlannedRevision(identity, current.Row.Revision, nextRevision));
        }

        var duplicatePatch = patches
            .GroupBy(static patch => (patch.SheetName, patch.Row, patch.Column))
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicatePatch is not null)
        {
            diagnostics.Add(new Diagnostic(
                "EXWB3006",
                DiagnosticSeverity.Blocker,
                duplicatePatch.Key.SheetName,
                $"Write plan owns cell {XlsxWorkbookCodec.ColumnName(duplicatePatch.Key.Column)}{duplicatePatch.Key.Row} more than once."));
        }

        foreach (var patch in patches)
        {
            if (WorkbookOwnership.Resolve(workbook, patch.SheetName, patch.Row, patch.Column) == CellOwnershipKind.Unmanaged)
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB3007",
                    DiagnosticSeverity.Blocker,
                    patch.SheetName,
                    $"Write plan attempted to modify unowned cell {XlsxWorkbookCodec.ColumnName(patch.Column)}{patch.Row}."));
            }
        }

        var patchArray = patches
            .OrderBy(static patch => patch.SheetName, StringComparer.Ordinal)
            .ThenBy(static patch => patch.Row)
            .ThenBy(static patch => patch.Column)
            .ToImmutableArray();
        var revisionArray = revisions.ToImmutableArray();
        var diagnosticArray = diagnostics.ToImmutable();
        var hash = ComputeHash(workbookPath, snapshot.WorkbookFingerprint, affected, patchArray, revisionArray);
        return new WorkbookWritePlan(
            workbookPath,
            snapshot.WorkbookFingerprint,
            workbook.SchemaHash,
            affected,
            patchArray,
            revisionArray,
            diagnosticArray,
            hash);
    }

    private static void AddValuePatches(
        List<CellPatch> patches,
        WorkbookTable table,
        int rowNumber,
        SnapshotRow? before,
        SnapshotRow after)
    {
        for (var index = 0; index < table.Columns.Length; index++)
        {
            var column = table.Columns[index];
            var oldValue = before?.Values.GetValueOrDefault(column.PropertyPath, CanonicalValue.Missing)
                ?? CanonicalValue.Missing;
            var newValue = after.Values.GetValueOrDefault(column.PropertyPath, CanonicalValue.Missing);
            if (before is not null && oldValue == newValue)
                continue;
            if (newValue.State == CanonicalValueState.Invalid)
                throw new InvalidOperationException($"Invalid value '{column.PropertyPath}' cannot enter a write plan.");
            after.RawCells.TryGetValue(column.PropertyPath, out var rawCell);
            patches.Add(new CellPatch(
                table.SheetName,
                rowNumber,
                index + 1,
                ToWorkbookCell(newValue, rawCell)));
        }
    }

    private static WorkbookCell? ToWorkbookCell(CanonicalValue value, WorkbookCell? rawCell) => value.State switch
    {
        CanonicalValueState.Missing or CanonicalValueState.Defaulted => null,
        CanonicalValueState.Null => new WorkbookCell(WorkbookProtocol.ExplicitNullToken),
        CanonicalValueState.Value when rawCell?.Formula is not null
            && string.Equals(value.Text, $"={rawCell.Formula}", StringComparison.Ordinal) => rawCell,
        CanonicalValueState.Value when value.Text?.StartsWith('=') == true =>
            WorkbookCell.FormulaCell(value.Text[1..]),
        CanonicalValueState.Value => new WorkbookCell(value.Text),
        CanonicalValueState.Invalid => throw new InvalidOperationException("Invalid values are never writable."),
        _ => throw new InvalidOperationException($"Unknown canonical value state {value.State}."),
    };

    private static void AddKeyPatches(
        List<CellPatch> patches,
        int keyRow,
        int sourceRow,
        int projectionRow,
        int projectionColumn,
        AssetIdentity identity,
        string? key)
    {
        patches.Add(new CellPatch(
            WorkbookProtocol.KeySheetName,
            keyRow,
            1,
            new WorkbookCell(identity.TableId.ToString(CultureInfo.InvariantCulture))));
        patches.Add(new CellPatch(
            WorkbookProtocol.KeySheetName,
            keyRow,
            2,
            new WorkbookCell(identity.RowGuid.ToString())));
        patches.Add(new CellPatch(WorkbookProtocol.KeySheetName, keyRow, 3, ToTextCell(key)));
        patches.Add(new CellPatch(
            WorkbookProtocol.KeySheetName,
            keyRow,
            4,
            new WorkbookCell(sourceRow.ToString(CultureInfo.InvariantCulture))));
        patches.Add(new CellPatch(
            WorkbookProtocol.KeySheetName,
            keyRow,
            5,
            ToTextCell(ToReferenceToken(identity.TableId, key))));
        patches.Add(new CellPatch(
            WorkbookProtocol.KeySheetName,
            projectionRow,
            projectionColumn,
            ToTextCell(ToReferenceToken(identity.TableId, key))));
    }

    private static string? ToReferenceToken(int tableId, string? canonicalKey) =>
        CanonicalKeyCodec.TryParse(canonicalKey, out var components)
        && components.All(static component => component.Length != 0)
            ? RowReferenceToken.Format(tableId, components)
            : null;

    private static WorkbookCell? ToTextCell(string? text) => text is null ? null : new WorkbookCell(text);

    private static Diagnostic Blocker(string code, AssetIdentity identity, string message) =>
        new(code, DiagnosticSeverity.Blocker, identity.ToString(), message);

    private static string ComputeHash(
        string path,
        ContentFingerprint fingerprint,
        IEnumerable<AssetIdentity> affected,
        IEnumerable<CellPatch> patches,
        IEnumerable<PlannedRevision> revisions)
    {
        var builder = new StringBuilder(path).Append('|').Append(fingerprint.Sha256);
        foreach (var identity in affected.OrderBy(static item => item.TableId).ThenBy(static item => item.RowGuid.ToString(), StringComparer.Ordinal))
            builder.Append("\na|").Append(identity);
        foreach (var patch in patches)
        {
            builder.Append("\np|").Append(patch.SheetName).Append('|').Append(patch.Row).Append('|').Append(patch.Column)
                .Append('|').Append(patch.Cell?.Formula).Append('|').Append(patch.Cell?.Text);
        }
        foreach (var revision in revisions)
            builder.Append("\nr|").Append(revision.Identity).Append('|').Append(revision.Before).Append('|').Append(revision.After);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }
}

public sealed record PreparedWorkbookWrite(
    WorkbookWritePlan Plan,
    byte[] Bytes,
    ContentFingerprint Fingerprint);

public static class WorkbookWriteService
{
    public const string ToolVersion = "workbooks-v1";

    public static PreparedWorkbookWrite Prepare(WorkbookWritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
            throw new InvalidOperationException("Write plan contains diagnostics that block commit.");
        var source = File.ReadAllBytes(plan.WorkbookPath);
        if (ContentFingerprint.FromBytes(source) != plan.SourceFingerprint)
            throw new InvalidOperationException("Workbook content changed after planning.");
        var current = XlsxWorkbookCodec.Read(source);
        if (current.SchemaHash != plan.SchemaHash)
            throw new InvalidOperationException("Workbook schema changed after planning.");
        var bytes = XlsxWorkbookCodec.PatchCells(source, plan.Patches);
        Verify(plan, bytes);
        return new PreparedWorkbookWrite(plan, bytes, ContentFingerprint.FromBytes(bytes));
    }

    public static OperationReport Apply(WorkbookWritePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanApply)
        {
            return new OperationReport(
                "workbook-write",
                ToolVersion,
                false,
                plan.Diagnostics,
                [],
                plan.PlanHash);
        }

        try
        {
            var prepared = Prepare(plan);
            var transaction = MultiWorkbookTransaction.Commit([prepared]);
            if (!transaction.Report.Succeeded)
            {
                return transaction.Report with
                {
                    Operation = "workbook-write",
                    PlanHash = plan.PlanHash,
                };
            }

            return new OperationReport(
                "workbook-write",
                ToolVersion,
                true,
                [],
                [new ArtifactRecord("workbook", plan.WorkbookPath, prepared.Fingerprint.Sha256)],
                plan.PlanHash);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            return new OperationReport(
                "workbook-write",
                ToolVersion,
                false,
                [new Diagnostic("EXWB3010", DiagnosticSeverity.Blocker, plan.WorkbookPath, exception.Message)],
                [],
                plan.PlanHash);
        }
    }

    private static void Verify(WorkbookWritePlan plan, byte[] bytes)
    {
        var workbook = XlsxWorkbookCodec.Read(bytes);
        foreach (var revision in plan.Revisions)
        {
            var row = workbook.Tables
                .Where(table => table.TableId == revision.Identity.TableId)
                .SelectMany(static table => table.Rows)
                .SingleOrDefault(row => row.RowGuid == revision.Identity.RowGuid);
            if (revision.After is null)
            {
                if (row is not null)
                    throw new InvalidDataException($"Deleted row {revision.Identity} remains in the workbook.");
            }
            else if (row is null || row.Revision != revision.After.Value)
            {
                throw new InvalidDataException($"Revision verification failed for {revision.Identity}.");
            }
        }
    }
}
