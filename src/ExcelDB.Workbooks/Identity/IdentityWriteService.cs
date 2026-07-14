using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;

namespace ExcelDb.Workbooks.Identity;

public enum IdentityWriteKind
{
    DataPrepare,
    IdentityRepair,
}

public sealed record IdentityAssignment(
    RowLocation Location,
    RowGuid CandidateGuid,
    string? ExpectedRawGuid,
    string ExpectedBusinessHash);

public sealed record IdentityWritePlan(
    IdentityWriteKind Kind,
    string WorkbookPath,
    ContentFingerprint SourceFingerprint,
    ulong SchemaHash,
    ImmutableArray<IdentityAssignment> Assignments,
    string PlanHash)
{
    public static IdentityWritePlan CreateDataPrepare(
        IdentityScanResult scan,
        WorkbookSource source)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(source);
        if (scan.HasBlockers)
            throw new InvalidOperationException("Identity scan has blockers; data prepare cannot be planned.");
        var assignments = scan.Observations
            .Where(observation => IsSource(observation, source)
                && observation.Classification == IdentityClassification.PendingNew)
            .Select(CreateAssignment)
            .OrderBy(static item => item.Location.SheetName, StringComparer.Ordinal)
            .ThenBy(static item => item.Location.RowNumber)
            .ToImmutableArray();
        return Create(IdentityWriteKind.DataPrepare, source, assignments);
    }

    /// <summary>Only explicitly selected recovered rows are eligible for identity repair.</summary>
    public static IdentityWritePlan CreateIdentityRepair(
        IdentityScanResult scan,
        WorkbookSource source,
        IEnumerable<RowLocation> selectedRows)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selectedRows);
        var selected = selectedRows.ToHashSet();
        var observations = scan.Observations
            .Where(observation => IsSource(observation, source) && selected.Contains(observation.Location))
            .ToArray();
        if (observations.Length != selected.Count)
            throw new ArgumentException("At least one selected row is not part of this workbook scan.", nameof(selectedRows));
        if (observations.Any(static observation => observation.Classification != IdentityClassification.Recovered))
            throw new InvalidOperationException("Identity repair accepts recovered rows only.");
        var assignments = observations
            .Select(CreateAssignment)
            .OrderBy(static item => item.Location.SheetName, StringComparer.Ordinal)
            .ThenBy(static item => item.Location.RowNumber)
            .ToImmutableArray();
        return Create(IdentityWriteKind.IdentityRepair, source, assignments);
    }

    private static IdentityWritePlan Create(
        IdentityWriteKind kind,
        WorkbookSource source,
        ImmutableArray<IdentityAssignment> assignments)
    {
        var hash = ComputePlanHash(kind, source, assignments);
        return new IdentityWritePlan(kind, source.Path, source.Fingerprint, source.Workbook.SchemaHash, assignments, hash);
    }

    private static IdentityAssignment CreateAssignment(IdentityObservation observation) =>
        new(
            observation.Location,
            observation.CandidateGuid
                ?? throw new InvalidOperationException("An identity assignment has no candidate guid."),
            observation.RawGuid,
            observation.BusinessHash);

    private static bool IsSource(IdentityObservation observation, WorkbookSource source) =>
        ReferenceEquals(observation.Source, source)
        || (string.Equals(observation.Source.Path, source.Path, StringComparison.Ordinal)
            && observation.Source.Fingerprint == source.Fingerprint);

    private static string ComputePlanHash(
        IdentityWriteKind kind,
        WorkbookSource source,
        IEnumerable<IdentityAssignment> assignments)
    {
        var builder = new StringBuilder()
            .Append((int)kind).Append('|')
            .Append(source.Path).Append('|')
            .Append(source.Fingerprint.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(source.Fingerprint.Sha256).Append('|')
            .Append(source.Workbook.SchemaHash.ToString("x16", CultureInfo.InvariantCulture));
        foreach (var assignment in assignments)
        {
            builder.Append('\n')
                .Append(assignment.Location.TableId.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(assignment.Location.SheetName).Append('|')
                .Append(assignment.Location.RowNumber.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(assignment.ExpectedRawGuid).Append('|')
                .Append(assignment.CandidateGuid).Append('|')
                .Append(assignment.ExpectedBusinessHash);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }
}

public static class IdentityWriteService
{
    public const string ToolVersion = "workbooks-v1";

    public static OperationReport Apply(
        IdentityWritePlan plan,
        IReadOnlySet<RowGuid>? occupiedProjectGuids = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!File.Exists(plan.WorkbookPath))
        {
            return Failure(plan, "EXWB1100", $"Workbook '{plan.WorkbookPath}' does not exist.");
        }

        var sourceBytes = File.ReadAllBytes(plan.WorkbookPath);
        var actualFingerprint = ContentFingerprint.FromBytes(sourceBytes);
        if (actualFingerprint != plan.SourceFingerprint)
        {
            return Failure(
                plan,
                "EXWB1101",
                "Workbook content changed after planning; the stale plan was not applied.");
        }

        WorkbookDefinition before;
        try
        {
            before = XlsxWorkbookCodec.Read(sourceBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or XmlException)
        {
            return Failure(plan, "EXWB1102", $"Workbook cannot be read: {exception.Message}");
        }

        if (before.SchemaHash != plan.SchemaHash)
            return Failure(plan, "EXWB1103", "Workbook schema hash changed after planning.");

        var occupied = new HashSet<RowGuid>(occupiedProjectGuids ?? new HashSet<RowGuid>());
        foreach (var table in before.Tables)
        {
            foreach (var row in table.Rows)
            {
                if (row.RowGuid is { } rowGuid)
                    occupied.Add(rowGuid);
            }
        }

        var candidateDuplicate = plan.Assignments
            .GroupBy(static assignment => assignment.CandidateGuid)
            .FirstOrDefault(static group => group.Count() > 1);
        if (candidateDuplicate is not null)
            return Failure(plan, "EXWB1104", $"Plan repeats candidate guid {candidateDuplicate.Key}.");

        var keyRows = new Dictionary<(int TableId, int RowNumber), int>();
        var keyRowNumber = 2;
        foreach (var table in before.Tables.OrderBy(static table => table.TableId))
        {
            foreach (var row in table.Rows)
            {
                keyRows[(table.TableId, row.SourceRowNumber ?? table.DataStartRow)] = keyRowNumber++;
            }
        }

        var patches = new List<CellPatch>(plan.Assignments.Length);
        foreach (var assignment in plan.Assignments)
        {
            var table = before.Tables.SingleOrDefault(table => table.TableId == assignment.Location.TableId);
            if (table is null || !string.Equals(table.SheetName, assignment.Location.SheetName, StringComparison.Ordinal))
                return Failure(plan, "EXWB1105", $"Managed table at {assignment.Location} no longer exists.");
            var row = table.Rows.SingleOrDefault(row =>
                (row.SourceRowNumber ?? -1) == assignment.Location.RowNumber);
            if (row is null)
                return Failure(plan, "EXWB1106", $"Managed row at {assignment.Location} no longer exists.");
            var rawGuid = row.RawRowGuid ?? row.RowGuid?.ToString();
            if (!string.Equals(rawGuid, assignment.ExpectedRawGuid, StringComparison.Ordinal)
                || !string.Equals(
                    WorkbookRowHash.Compute(table.TableId, row),
                    assignment.ExpectedBusinessHash,
                    StringComparison.Ordinal))
            {
                return Failure(plan, "EXWB1107", $"Managed row at {assignment.Location} changed after planning.");
            }

            if (occupied.Contains(assignment.CandidateGuid))
            {
                return Failure(
                    plan,
                    "EXWB1108",
                    $"Candidate guid {assignment.CandidateGuid} is already live in the project domain.");
            }

            occupied.Add(assignment.CandidateGuid);
            patches.Add(new CellPatch(
                assignment.Location.SheetName,
                assignment.Location.RowNumber,
                table.Columns.Length + 1,
                new WorkbookCell(assignment.CandidateGuid.ToString())));
            if (keyRows.TryGetValue((table.TableId, assignment.Location.RowNumber), out var keyRow))
            {
                patches.Add(new CellPatch(
                    WorkbookProtocol.KeySheetName,
                    keyRow,
                    2,
                    new WorkbookCell(assignment.CandidateGuid.ToString())));
            }
        }

        var businessDigest = ComputeBusinessDigest(before);
        byte[] outputBytes;
        try
        {
            outputBytes = XlsxWorkbookCodec.PatchCells(sourceBytes, patches);
            var after = XlsxWorkbookCodec.Read(outputBytes);
            if (!string.Equals(businessDigest, ComputeBusinessDigest(after), StringComparison.Ordinal))
                return Failure(plan, "EXWB1109", "Identity write changed business data or row revisions.");
            foreach (var assignment in plan.Assignments)
            {
                var table = after.Tables.Single(item => item.TableId == assignment.Location.TableId);
                var row = table.Rows.Single(item => item.SourceRowNumber == assignment.Location.RowNumber);
                if (row.RowGuid != assignment.CandidateGuid)
                    return Failure(plan, "EXWB1110", $"Identity verification failed at {assignment.Location}.");
            }

            AtomicFile.WriteAllBytes(plan.WorkbookPath, outputBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Failure(plan, "EXWB1111", $"Identity write failed atomically: {exception.Message}");
        }

        diagnostics.Add(new Diagnostic(
            "EXWB1112",
            DiagnosticSeverity.Info,
            plan.WorkbookPath,
            $"Applied {plan.Assignments.Length} identity assignment(s)."));
        return new OperationReport(
            plan.Kind == IdentityWriteKind.DataPrepare ? "data-prepare" : "identity-repair",
            ToolVersion,
            true,
            diagnostics.ToImmutable(),
            [new ArtifactRecord("workbook", plan.WorkbookPath, ContentFingerprint.FromBytes(outputBytes).Sha256)],
            plan.PlanHash);
    }

    private static OperationReport Failure(IdentityWritePlan plan, string code, string message) =>
        new(
            plan.Kind == IdentityWriteKind.DataPrepare ? "data-prepare" : "identity-repair",
            ToolVersion,
            false,
            [new Diagnostic(code, DiagnosticSeverity.Blocker, plan.WorkbookPath, message)],
            [],
            plan.PlanHash);

    private static string ComputeBusinessDigest(WorkbookDefinition workbook)
    {
        var builder = new StringBuilder();
        foreach (var table in workbook.Tables.OrderBy(static table => table.TableId))
        {
            foreach (var row in table.Rows.OrderBy(static row => row.SourceRowNumber))
            {
                builder.Append(table.TableId.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(row.SourceRowNumber?.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(WorkbookRowHash.Compute(table.TableId, row)).Append('\n');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }
}
