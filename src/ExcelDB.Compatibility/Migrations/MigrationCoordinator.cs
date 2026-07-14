using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Values;

namespace ExcelDb.Compatibility.Migrations;

/// <summary>
/// Host adapter for one workbook participating in a closed migration transaction.
/// Prepare must not replace the live workbook. Rollback must remain valid after Commit
/// so a later participant failure can restore the whole closure.
/// </summary>
public interface IMigrationWorkbookParticipant
{
    string WorkbookId { get; }

    MigrationWorkbookSnapshot ReadCurrent();

    IPreparedMigrationWorkbook Prepare(
        MigrationWorkbookSnapshot expected,
        MigrationWorkbookSnapshot candidate);
}

public interface IPreparedMigrationWorkbook : IDisposable
{
    string WorkbookId { get; }

    void Commit();

    void Rollback();
}

public sealed record MigrationExecutionOptions(
    bool ConfirmDataLoss = false);

public sealed record MigrationExecutionReport(
    int FormatVersion,
    string PlanHash,
    bool Succeeded,
    bool Applied,
    bool RollbackAttempted,
    bool RolledBack,
    ImmutableArray<MigrationCellOutcome> Outcomes,
    ImmutableArray<Diagnostic> Diagnostics,
    string ReportHash)
{
    public const int CurrentFormatVersion = 1;

    public int ConvertedCount => Outcomes.Count(static item => item.Disposition == MigrationDisposition.Converted);

    public int SkippedCount => Outcomes.Count(static item => item.Disposition == MigrationDisposition.Skipped);

    public int ErrorCount => Outcomes.Count(static item => item.Disposition == MigrationDisposition.Error);
}

public static class MigrationCoordinator
{
    public static MigrationExecutionReport Execute(
        MigrationPlan plan,
        IEnumerable<IMigrationWorkbookParticipant> participants,
        MigrationExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(participants);
        options ??= new MigrationExecutionOptions();

        var diagnostics = new List<Diagnostic>(plan.Diagnostics);
        var participantArray = participants.ToArray();
        if (participantArray.Any(static item => item is null))
        {
            diagnostics.Add(Blocker(
                "migration.apply.participant-null",
                ".",
                "Migration participant collection contains null."));
            return Report(plan, false, false, false, false, diagnostics);
        }

        if (!MigrationPlanSerializer.Verify(plan))
        {
            diagnostics.Add(Blocker(
                "migration.apply.plan-hash",
                ".",
                "Migration plan hash is missing or does not match its immutable contents."));
        }

        if (plan.FormatVersion != MigrationPlan.CurrentFormatVersion)
        {
            diagnostics.Add(Blocker(
                "migration.apply.plan-format",
                ".",
                $"Migration plan format {plan.FormatVersion} is unsupported."));
        }

        ValidatePlanStructure(plan, diagnostics);

        if (!plan.CanApply)
        {
            diagnostics.Add(Blocker(
                "migration.apply.plan-blocked",
                ".",
                "Migration plan contains unresolved errors or blockers."));
        }

        if (plan.HasDataLossRisk && !options.ConfirmDataLoss)
        {
            diagnostics.Add(Blocker(
                "migration.apply.data-loss-confirmation",
                ".",
                "Explicit confirmation is required for every plan containing data-loss risk."));
        }

        var duplicateParticipants = participantArray
            .GroupBy(static item => item.WorkbookId, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToArray();
        foreach (var duplicate in duplicateParticipants)
        {
            diagnostics.Add(Blocker(
                "migration.apply.participant-duplicate",
                duplicate.Key,
                $"Workbook participant '{duplicate.Key}' is registered more than once."));
        }

        var expectedIds = plan.Workbooks
            .Select(static item => item.Expected.WorkbookId)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var actualIds = participantArray
            .Select(static item => item.WorkbookId)
            .ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var missing in expectedIds.Except(actualIds).Order(StringComparer.Ordinal))
        {
            diagnostics.Add(Blocker(
                "migration.apply.participant-missing",
                missing,
                $"Workbook '{missing}' is missing from the migration transaction closure."));
        }

        foreach (var extra in actualIds.Except(expectedIds).Order(StringComparer.Ordinal))
        {
            diagnostics.Add(Blocker(
                "migration.apply.participant-extra",
                extra,
                $"Workbook '{extra}' is not part of the sealed migration plan."));
        }

        if (HasFailures(diagnostics))
            return Report(plan, false, false, false, false, diagnostics);

        var byId = participantArray.ToDictionary(static item => item.WorkbookId, StringComparer.Ordinal);
        foreach (var workbook in plan.Workbooks.OrderBy(static item => item.Expected.WorkbookId, StringComparer.Ordinal))
        {
            MigrationWorkbookSnapshot current;
            try
            {
                current = byId[workbook.Expected.WorkbookId].ReadCurrent();
            }
            catch (Exception exception)
            {
                diagnostics.Add(Blocker(
                    "migration.apply.preflight-read",
                    workbook.Expected.WorkbookId,
                    Describe(exception, "Could not read workbook during preflight")));
                continue;
            }

            if (!MigrationSnapshotComparer.SemanticallyEquals(current, workbook.Expected))
            {
                diagnostics.Add(Blocker(
                    "migration.apply.stale",
                    workbook.Expected.WorkbookId,
                    "Workbook revision, schema identity, migration markers, or canonical values changed after planning."));
            }
        }

        if (HasFailures(diagnostics))
            return Report(plan, false, false, false, false, diagnostics);

        var changed = plan.Workbooks
            .Where(static item => item.HasChanges)
            .OrderBy(static item => item.Expected.WorkbookId, StringComparer.Ordinal)
            .ToArray();
        if (changed.Length == 0)
            return Report(plan, true, false, false, false, diagnostics);

        var prepared = new List<PreparedItem>(changed.Length);
        try
        {
            foreach (var workbook in changed)
            {
                var participant = byId[workbook.Expected.WorkbookId];
                var staged = participant.Prepare(workbook.Expected, workbook.Candidate)
                             ?? throw new InvalidOperationException("Participant returned no prepared transaction.");
                if (!string.Equals(staged.WorkbookId, workbook.Expected.WorkbookId, StringComparison.Ordinal))
                {
                    staged.Dispose();
                    throw new InvalidOperationException(
                        $"Prepared transaction identifies workbook '{staged.WorkbookId}'.");
                }

                prepared.Add(new PreparedItem(workbook, participant, staged));
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add(Blocker(
                "migration.apply.prepare",
                prepared.Count < changed.Length ? changed[prepared.Count].Expected.WorkbookId : ".",
                Describe(exception, "Could not prepare the closed workbook transaction")));
            var rollback = RollbackAndVerify(prepared, diagnostics);
            Dispose(prepared, diagnostics);
            return Report(plan, false, false, prepared.Count > 0, rollback, diagnostics);
        }

        try
        {
            foreach (var item in prepared)
                item.Prepared.Commit();
        }
        catch (Exception exception)
        {
            diagnostics.Add(Blocker(
                "migration.apply.commit",
                ".",
                Describe(exception, "A workbook commit failed; the full closure is being restored")));
            var rollback = RollbackAndVerify(prepared, diagnostics);
            Dispose(prepared, diagnostics);
            return Report(plan, false, false, true, rollback, diagnostics);
        }

        foreach (var item in prepared)
        {
            MigrationWorkbookSnapshot current;
            try
            {
                current = item.Participant.ReadCurrent();
            }
            catch (Exception exception)
            {
                diagnostics.Add(Blocker(
                    "migration.apply.verify-read",
                    item.Plan.Expected.WorkbookId,
                    Describe(exception, "Could not re-read a committed workbook")));
                continue;
            }

            if (!MigrationSnapshotComparer.SemanticallyEquals(current, item.Plan.Candidate))
            {
                diagnostics.Add(Blocker(
                    "migration.apply.verify",
                    item.Plan.Expected.WorkbookId,
                    "Committed workbook does not match the sealed candidate snapshot."));
            }
        }

        if (HasFailures(diagnostics))
        {
            var rollback = RollbackAndVerify(prepared, diagnostics);
            Dispose(prepared, diagnostics);
            return Report(plan, false, false, true, rollback, diagnostics);
        }

        Dispose(prepared, diagnostics);
        return Report(plan, true, true, false, false, diagnostics);
    }

    private static bool RollbackAndVerify(
        IReadOnlyList<PreparedItem> prepared,
        List<Diagnostic> diagnostics)
    {
        var complete = true;
        for (var index = prepared.Count - 1; index >= 0; index--)
        {
            var item = prepared[index];
            try
            {
                item.Prepared.Rollback();
            }
            catch (Exception exception)
            {
                complete = false;
                diagnostics.Add(Blocker(
                    "migration.apply.rollback",
                    item.Plan.Expected.WorkbookId,
                    Describe(exception, "Workbook rollback failed")));
            }
        }

        foreach (var item in prepared)
        {
            try
            {
                if (!MigrationSnapshotComparer.SemanticallyEquals(item.Participant.ReadCurrent(), item.Plan.Expected))
                {
                    complete = false;
                    diagnostics.Add(Blocker(
                        "migration.apply.rollback-verify",
                        item.Plan.Expected.WorkbookId,
                        "Rollback did not restore the exact planned source revision and canonical values."));
                }
            }
            catch (Exception exception)
            {
                complete = false;
                diagnostics.Add(Blocker(
                    "migration.apply.rollback-read",
                    item.Plan.Expected.WorkbookId,
                    Describe(exception, "Could not verify the restored workbook")));
            }
        }

        return complete;
    }

    private static void ValidatePlanStructure(MigrationPlan plan, List<Diagnostic> diagnostics)
    {
        if (plan.SourceSchemaHash == plan.TargetSchemaHash)
        {
            diagnostics.Add(Blocker(
                "migration.apply.schema-same",
                ".",
                "A migration plan must bind distinct source and target schema hashes."));
        }

        if (plan.Workbooks.IsEmpty)
        {
            diagnostics.Add(Blocker(
                "migration.apply.plan-workbook-none",
                ".",
                "A migration plan must seal a non-empty workbook closure."));
        }

        foreach (var duplicate in plan.Workbooks
                     .GroupBy(static item => item.Expected.WorkbookId, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            diagnostics.Add(Blocker(
                "migration.apply.plan-workbook-duplicate",
                duplicate.Key,
                "The sealed plan contains duplicate workbook identities."));
        }

        foreach (var workbook in plan.Workbooks)
        {
            var id = workbook.Expected.WorkbookId;
            if (!string.Equals(id, workbook.Candidate.WorkbookId, StringComparison.Ordinal))
            {
                diagnostics.Add(Blocker(
                    "migration.apply.plan-workbook-id",
                    id,
                    "A migration candidate cannot change workbook identity."));
            }

            if (workbook.Expected.SchemaHash != plan.SourceSchemaHash
                && workbook.Expected.SchemaHash != plan.TargetSchemaHash)
            {
                diagnostics.Add(Blocker(
                    "migration.apply.plan-source-schema",
                    id,
                    "Planned workbook source does not match either sealed schema endpoint."));
            }

            var expectedCandidateHash = workbook.HasChanges
                ? plan.TargetSchemaHash
                : workbook.Expected.SchemaHash;
            if (workbook.Candidate.SchemaHash != expectedCandidateHash)
            {
                diagnostics.Add(Blocker(
                    "migration.apply.plan-target-schema",
                    id,
                    "Planned workbook candidate has an inconsistent target schema hash."));
            }

            if (workbook.Outcomes.Any(outcome =>
                    !string.Equals(outcome.WorkbookId, id, StringComparison.Ordinal)))
            {
                diagnostics.Add(Blocker(
                    "migration.apply.plan-outcome-workbook",
                    id,
                    "A migration outcome is attributed to another workbook."));
            }
        }
    }

    private static void Dispose(IEnumerable<PreparedItem> prepared, List<Diagnostic> diagnostics)
    {
        foreach (var item in prepared.Reverse())
        {
            try
            {
                item.Prepared.Dispose();
            }
            catch (Exception exception)
            {
                diagnostics.Add(new Diagnostic(
                    "migration.apply.dispose",
                    DiagnosticSeverity.Warning,
                    item.Plan.Expected.WorkbookId,
                    Describe(exception, "Prepared transaction cleanup failed")));
            }
        }
    }

    private static MigrationExecutionReport Report(
        MigrationPlan plan,
        bool succeeded,
        bool applied,
        bool rollbackAttempted,
        bool rolledBack,
        IEnumerable<Diagnostic> diagnostics)
    {
        var outcomes = plan.Workbooks
            .SelectMany(static item => item.Outcomes)
            .OrderBy(static item => item.WorkbookId, StringComparer.Ordinal)
            .ThenBy(static item => item.Migration.Id, StringComparer.Ordinal)
            .ThenBy(static item => item.Migration.Version)
            .ThenBy(static item => item.SourceAddress?.StableKey, StringComparer.Ordinal)
            .ThenBy(static item => item.TargetAddress?.StableKey, StringComparer.Ordinal)
            .ThenBy(static item => item.Disposition)
            .ThenBy(static item => item.Message, StringComparer.Ordinal)
            .ToImmutableArray();
        var orderedDiagnostics = diagnostics
            .Distinct()
            .OrderBy(static item => item.Code, StringComparer.Ordinal)
            .ThenBy(static item => item.Location, StringComparer.Ordinal)
            .ThenBy(static item => item.Message, StringComparer.Ordinal)
            .ToImmutableArray();
        var provisional = new MigrationExecutionReport(
            MigrationExecutionReport.CurrentFormatVersion,
            plan.PlanHash,
            succeeded && !orderedDiagnostics.Any(static item => item.IsFailure),
            applied,
            rollbackAttempted,
            rolledBack,
            outcomes,
            orderedDiagnostics,
            string.Empty);
        return provisional with { ReportHash = MigrationExecutionReportSerializer.ComputeHash(provisional) };
    }

    private static bool HasFailures(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(static item => item.IsFailure);

    private static string Describe(Exception exception, string prefix) =>
        $"{prefix}: {exception.GetType().Name}: {exception.Message}";

    private static Diagnostic Blocker(string code, string location, string message) =>
        new(code, DiagnosticSeverity.Blocker, location, message);

    private sealed record PreparedItem(
        MigrationWorkbookPlan Plan,
        IMigrationWorkbookParticipant Participant,
        IPreparedMigrationWorkbook Prepared);
}

public static class MigrationExecutionReportSerializer
{
    public static byte[] SerializeUtf8(MigrationExecutionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return SerializeUtf8(report, includeHash: true);
    }

    public static string Serialize(MigrationExecutionReport report) =>
        Encoding.UTF8.GetString(SerializeUtf8(report));

    public static string ComputeHash(MigrationExecutionReport report) =>
        Convert.ToHexString(SHA256.HashData(SerializeUtf8(report, includeHash: false))).ToLowerInvariant();

    public static bool Verify(MigrationExecutionReport report) =>
        !string.IsNullOrWhiteSpace(report.ReportHash)
        && string.Equals(report.ReportHash, ComputeHash(report), StringComparison.Ordinal);

    private static byte[] SerializeUtf8(MigrationExecutionReport report, bool includeHash)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", report.FormatVersion);
            writer.WriteString("planHash", report.PlanHash);
            if (includeHash)
                writer.WriteString("reportHash", report.ReportHash);
            writer.WriteBoolean("succeeded", report.Succeeded);
            writer.WriteBoolean("applied", report.Applied);
            writer.WriteBoolean("rollbackAttempted", report.RollbackAttempted);
            writer.WriteBoolean("rolledBack", report.RolledBack);
            writer.WriteNumber("convertedCount", report.ConvertedCount);
            writer.WriteNumber("skippedCount", report.SkippedCount);
            writer.WriteNumber("errorCount", report.ErrorCount);
            writer.WriteStartArray("outcomes");
            foreach (var outcome in report.Outcomes)
            {
                writer.WriteStartObject();
                writer.WriteString("workbookId", outcome.WorkbookId);
                writer.WriteString("migrationId", outcome.Migration.Id);
                writer.WriteNumber("migrationVersion", outcome.Migration.Version);
                writer.WriteString("disposition", outcome.Disposition.ToString());
                WriteAddress(writer, "source", outcome.SourceAddress);
                WriteAddress(writer, "target", outcome.TargetAddress);
                WriteValue(writer, "before", outcome.Before);
                WriteValue(writer, "after", outcome.After);
                if (outcome.Message is null) writer.WriteNull("message"); else writer.WriteString("message", outcome.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("diagnostics");
            foreach (var diagnostic in report.Diagnostics)
            {
                writer.WriteStartObject();
                writer.WriteString("code", diagnostic.Code);
                writer.WriteString("severity", diagnostic.Severity.ToString());
                writer.WriteString("location", diagnostic.Location);
                writer.WriteString("message", diagnostic.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteAddress(
        Utf8JsonWriter writer,
        string name,
        MigrationCellAddress? address)
    {
        if (address is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteNumber("tableId", address.TableId);
        writer.WriteString("rowIdentity", address.RowIdentity);
        writer.WriteStartArray("fieldIdPath");
        foreach (var fieldId in address.FieldIdPath.IsDefault ? [] : address.FieldIdPath)
            writer.WriteNumberValue(fieldId);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteValue(
        Utf8JsonWriter writer,
        string name,
        CanonicalValue value)
    {
        writer.WriteStartObject(name);
        writer.WriteString("state", value.State.ToString());
        if (value.Text is null) writer.WriteNull("text"); else writer.WriteString("text", value.Text);
        if (value.RawText is null) writer.WriteNull("rawText"); else writer.WriteString("rawText", value.RawText);
        writer.WriteEndObject();
    }
}
