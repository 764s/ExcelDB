using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Values;

namespace ExcelDb.Compatibility.Migrations;

public sealed record MigrationCellAddress(
    int TableId,
    string RowIdentity,
    ImmutableArray<int> FieldIdPath)
{
    public string StableKey =>
        $"{TableId}:{RowIdentity.Length}:{RowIdentity}:{string.Join('.', FieldIdPath.IsDefault ? [] : FieldIdPath)}";
}

public sealed record MigrationCell(
    MigrationCellAddress Address,
    CanonicalValue Value);

public sealed record MigrationWorkbookSnapshot(
    string WorkbookId,
    string Revision,
    ulong SchemaHash,
    ImmutableArray<MigrationCell> Cells,
    ImmutableHashSet<MigrationKey> AppliedMigrations);

public sealed record MigrationStep(
    int Order,
    MigrationKey Migration,
    int TableId,
    ImmutableArray<int> SourceFieldPath,
    ImmutableArray<int> TargetFieldPath,
    bool DataLossRisk = false,
    bool AllowOverwrite = false)
{
    public int? TargetTableId { get; init; }

    public bool MaterializeDefault { get; init; }

    public int EffectiveTargetTableId => TargetTableId ?? TableId;
}

public sealed record MigrationCellOutcome(
    string WorkbookId,
    MigrationKey Migration,
    MigrationDisposition Disposition,
    MigrationCellAddress? SourceAddress,
    MigrationCellAddress? TargetAddress,
    CanonicalValue Before,
    CanonicalValue After,
    string? Message);

public sealed record MigrationWorkbookPlan(
    MigrationWorkbookSnapshot Expected,
    MigrationWorkbookSnapshot Candidate,
    ImmutableArray<MigrationCellOutcome> Outcomes)
{
    public bool HasChanges => !MigrationSnapshotComparer.SemanticallyEquals(Expected, Candidate);
}

public sealed record MigrationPlan(
    int FormatVersion,
    ulong SourceSchemaHash,
    ulong TargetSchemaHash,
    ImmutableArray<MigrationStep> Steps,
    ImmutableArray<MigrationWorkbookPlan> Workbooks,
    ImmutableArray<Diagnostic> Diagnostics,
    string PlanHash)
{
    public const int CurrentFormatVersion = 1;

    public bool CanApply => !Diagnostics.Any(static item => item.IsFailure);

    public bool HasDataLossRisk => Steps.Any(static item => item.DataLossRisk);

    public int ConvertedCount => Workbooks.Sum(static item =>
        item.Outcomes.Count(static outcome => outcome.Disposition == MigrationDisposition.Converted));

    public int SkippedCount => Workbooks.Sum(static item =>
        item.Outcomes.Count(static outcome => outcome.Disposition == MigrationDisposition.Skipped));

    public int ErrorCount => Workbooks.Sum(static item =>
        item.Outcomes.Count(static outcome => outcome.Disposition == MigrationDisposition.Error));
}

public static class MigrationSnapshotComparer
{
    public static bool SemanticallyEquals(
        MigrationWorkbookSnapshot? left,
        MigrationWorkbookSnapshot? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null
            || !string.Equals(left.WorkbookId, right.WorkbookId, StringComparison.Ordinal)
            || !string.Equals(left.Revision, right.Revision, StringComparison.Ordinal)
            || left.SchemaHash != right.SchemaHash
            || !left.AppliedMigrations.SetEquals(right.AppliedMigrations))
        {
            return false;
        }

        var leftCells = NormalizeCells(left.Cells);
        var rightCells = NormalizeCells(right.Cells);
        return leftCells.Length == rightCells.Length
               && leftCells.Zip(rightCells).All(static pair => CellEquals(pair.First, pair.Second));
    }

    internal static ImmutableArray<MigrationCell> NormalizeCells(IEnumerable<MigrationCell> cells) =>
        [.. cells
            .Select(static item => new MigrationCell(
                item.Address with
                {
                    FieldIdPath = item.Address.FieldIdPath.IsDefault ? [] : [.. item.Address.FieldIdPath],
                },
                item.Value))
            .OrderBy(static item => item.Address.TableId)
            .ThenBy(static item => item.Address.RowIdentity, StringComparer.Ordinal)
            .ThenBy(static item => PathKey(item.Address.FieldIdPath), StringComparer.Ordinal)
            .ThenBy(static item => (byte)item.Value.State)
            .ThenBy(static item => item.Value.Text, StringComparer.Ordinal)
            .ThenBy(static item => item.Value.RawText, StringComparer.Ordinal)];

    internal static bool AddressEquals(MigrationCellAddress left, MigrationCellAddress right) =>
        left.TableId == right.TableId
        && string.Equals(left.RowIdentity, right.RowIdentity, StringComparison.Ordinal)
        && (left.FieldIdPath.IsDefault ? [] : left.FieldIdPath)
            .SequenceEqual(right.FieldIdPath.IsDefault ? [] : right.FieldIdPath);

    internal static string PathKey(ImmutableArray<int> path) =>
        string.Join('.', path.IsDefault ? [] : path);

    private static bool CellEquals(MigrationCell left, MigrationCell right) =>
        AddressEquals(left.Address, right.Address) && left.Value == right.Value;
}

public static class MigrationPlanner
{
    public static MigrationPlan Create(
        ulong sourceSchemaHash,
        ulong targetSchemaHash,
        IEnumerable<MigrationStep> steps,
        IEnumerable<MigrationWorkbookSnapshot> workbooks,
        MigrationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(workbooks);
        ArgumentNullException.ThrowIfNull(registry);

        var diagnostics = new List<Diagnostic>();
        if (sourceSchemaHash == targetSchemaHash)
        {
            diagnostics.Add(Blocker(
                "migration.schema.same",
                ".",
                "Source and target schema hashes must identify different published descriptors."));
        }

        var orderedSteps = NormalizeSteps(steps, registry, diagnostics);
        var orderedWorkbooks = NormalizeWorkbooks(workbooks, diagnostics);
        if (orderedWorkbooks.Length == 0)
            diagnostics.Add(Blocker("migration.workbook.none", ".", "A migration plan must cover at least one workbook."));

        ImmutableArray<MigrationWorkbookPlan> workbookPlans;
        if (diagnostics.Any(static item => item.IsFailure))
        {
            workbookPlans = [.. orderedWorkbooks.Select(static workbook =>
                new MigrationWorkbookPlan(workbook, workbook, []))];
        }
        else
        {
            workbookPlans = [.. orderedWorkbooks.Select(workbook =>
                PlanWorkbook(workbook, sourceSchemaHash, targetSchemaHash, orderedSteps, registry, diagnostics))];
        }

        var orderedDiagnostics = OrderDiagnostics(diagnostics);
        if (orderedDiagnostics.Any(static item => item.IsFailure))
        {
            workbookPlans = [.. workbookPlans.Select(static plan =>
                plan with { Candidate = plan.Expected })];
        }

        var provisional = new MigrationPlan(
            MigrationPlan.CurrentFormatVersion,
            sourceSchemaHash,
            targetSchemaHash,
            orderedSteps,
            [.. workbookPlans.OrderBy(static item => item.Expected.WorkbookId, StringComparer.Ordinal)],
            orderedDiagnostics,
            string.Empty);
        return provisional with { PlanHash = MigrationPlanSerializer.ComputeHash(provisional) };
    }

    private static ImmutableArray<MigrationStep> NormalizeSteps(
        IEnumerable<MigrationStep> steps,
        MigrationRegistry registry,
        List<Diagnostic> diagnostics)
    {
        var normalized = steps
            .Select(static step => step with
            {
                SourceFieldPath = step.SourceFieldPath.IsDefault ? [] : [.. step.SourceFieldPath],
                TargetFieldPath = step.TargetFieldPath.IsDefault ? [] : [.. step.TargetFieldPath],
                TargetTableId = step.EffectiveTargetTableId,
            })
            .OrderBy(static step => step.Order)
            .ThenBy(static step => step.Migration.Id, StringComparer.Ordinal)
            .ThenBy(static step => step.Migration.Version)
            .ThenBy(static step => step.TableId)
            .ThenBy(static step => step.EffectiveTargetTableId)
            .ThenBy(static step => MigrationSnapshotComparer.PathKey(step.SourceFieldPath), StringComparer.Ordinal)
            .ThenBy(static step => MigrationSnapshotComparer.PathKey(step.TargetFieldPath), StringComparer.Ordinal)
            .ToImmutableArray();

        foreach (var step in normalized)
        {
            var location = $"migration/{step.Migration}";
            if (string.IsNullOrWhiteSpace(step.Migration.Id) || step.Migration.Version <= 0)
                diagnostics.Add(Blocker("migration.step.key", location, "Migration id and positive version are required."));
            if (step.TableId <= 0
                || step.EffectiveTargetTableId <= 0
                || step.SourceFieldPath.IsEmpty
                || step.TargetFieldPath.IsEmpty
                || step.SourceFieldPath.Any(static number => number <= 0)
                || step.TargetFieldPath.Any(static number => number <= 0))
            {
                diagnostics.Add(Blocker(
                    "migration.step.identity",
                    location,
                    "Migration locations must use positive table and field numbers."));
            }

            if (step.TableId == step.EffectiveTargetTableId
                && step.SourceFieldPath.SequenceEqual(step.TargetFieldPath))
            {
                diagnostics.Add(Blocker(
                    "migration.step.in-place",
                    location,
                    "A semantic migration must target a new field number; in-place reinterpretation is forbidden."));
            }

            if (step.AllowOverwrite && !step.DataLossRisk)
            {
                diagnostics.Add(Blocker(
                    "migration.step.overwrite-risk",
                    location,
                    "Overwriting an existing target must be declared as a data-loss risk."));
            }

            if (!registry.TryGet(step.Migration, out _))
            {
                diagnostics.Add(Blocker(
                    "migration.step.unregistered",
                    location,
                    $"Migration '{step.Migration}' is not registered."));
            }
        }

        foreach (var duplicate in normalized
                     .GroupBy(static step => string.Join(
                         '|',
                         step.Migration,
                         step.TableId,
                         step.EffectiveTargetTableId,
                         MigrationSnapshotComparer.PathKey(step.SourceFieldPath),
                         MigrationSnapshotComparer.PathKey(step.TargetFieldPath)), StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
        {
            diagnostics.Add(Blocker(
                "migration.step.duplicate",
                duplicate.Key,
                "The same migration step is declared more than once."));
        }

        return normalized;
    }

    private static ImmutableArray<MigrationWorkbookSnapshot> NormalizeWorkbooks(
        IEnumerable<MigrationWorkbookSnapshot> workbooks,
        List<Diagnostic> diagnostics)
    {
        var normalized = workbooks
            .Select(workbook => NormalizeWorkbook(workbook, diagnostics))
            .OrderBy(static workbook => workbook.WorkbookId, StringComparer.Ordinal)
            .ThenBy(static workbook => workbook.Revision, StringComparer.Ordinal)
            .ThenBy(static workbook => SnapshotContentKey(workbook), StringComparer.Ordinal)
            .ToImmutableArray();

        foreach (var duplicate in normalized
                     .GroupBy(static workbook => workbook.WorkbookId, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
        {
            diagnostics.Add(Blocker(
                "migration.workbook.duplicate",
                duplicate.Key,
                $"Workbook id '{duplicate.Key}' is present more than once in the migration closure."));
        }

        return [.. normalized
            .GroupBy(static workbook => workbook.WorkbookId, StringComparer.Ordinal)
            .Select(static group => group.First())];
    }

    private static MigrationWorkbookSnapshot NormalizeWorkbook(
        MigrationWorkbookSnapshot workbook,
        List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        var workbookId = workbook.WorkbookId ?? string.Empty;
        var revision = workbook.Revision ?? string.Empty;
        var cells = MigrationSnapshotComparer.NormalizeCells(workbook.Cells.IsDefault ? [] : workbook.Cells);
        var migrations = workbook.AppliedMigrations ?? ImmutableHashSet<MigrationKey>.Empty;
        var normalized = workbook with
        {
            WorkbookId = workbookId,
            Revision = revision,
            Cells = cells,
            AppliedMigrations = migrations,
        };

        if (string.IsNullOrWhiteSpace(workbookId))
            diagnostics.Add(Blocker("migration.workbook.id", ".", "Workbook id is required."));
        if (string.IsNullOrWhiteSpace(revision))
            diagnostics.Add(Blocker("migration.workbook.revision", workbookId, "Workbook revision is required."));
        foreach (var cell in cells)
        {
            if (cell.Address is null
                || cell.Address.TableId <= 0
                || string.IsNullOrWhiteSpace(cell.Address.RowIdentity)
                || cell.Address.FieldIdPath.IsDefaultOrEmpty
                || cell.Address.FieldIdPath.Any(static number => number <= 0))
            {
                diagnostics.Add(Blocker(
                    "migration.cell.identity",
                    workbookId,
                    "Migration cells require positive numeric identities and a stable row identity."));
            }
        }

        foreach (var duplicate in cells
                     .Where(static cell => cell.Address is not null)
                     .GroupBy(static cell => cell.Address.StableKey, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
        {
            diagnostics.Add(Blocker(
                "migration.cell.duplicate",
                $"{workbookId}/{duplicate.Key}",
                "A workbook snapshot contains duplicate canonical cell identities."));
        }

        foreach (var key in migrations)
        {
            if (string.IsNullOrWhiteSpace(key.Id) || key.Version <= 0)
                diagnostics.Add(Blocker("migration.marker.invalid", workbookId, "Migration markers require an id and positive version."));
        }

        return normalized;
    }

    private static MigrationWorkbookPlan PlanWorkbook(
        MigrationWorkbookSnapshot workbook,
        ulong sourceSchemaHash,
        ulong targetSchemaHash,
        ImmutableArray<MigrationStep> steps,
        MigrationRegistry registry,
        List<Diagnostic> diagnostics)
    {
        var requiredKeys = steps.Select(static step => step.Migration).ToImmutableHashSet();
        if (workbook.SchemaHash == targetSchemaHash)
        {
            if (!requiredKeys.IsSubsetOf(workbook.AppliedMigrations))
            {
                diagnostics.Add(Blocker(
                    "migration.workbook.evidence",
                    workbook.WorkbookId,
                    "Workbook declares the target schema but lacks required migration markers."));
            }

            return new MigrationWorkbookPlan(workbook, workbook, []);
        }

        if (workbook.SchemaHash != sourceSchemaHash)
        {
            diagnostics.Add(Blocker(
                "migration.workbook.schema",
                workbook.WorkbookId,
                $"Workbook uses schema {workbook.SchemaHash:x16}; expected source {sourceSchemaHash:x16}."));
            return new MigrationWorkbookPlan(workbook, workbook, []);
        }

        var cells = workbook.Cells.ToDictionary(static cell => CellKey(cell.Address));
        var markers = workbook.AppliedMigrations.ToBuilder();
        var outcomes = new List<MigrationCellOutcome>();
        var failed = false;
        foreach (var group in steps
                     .GroupBy(static step => step.Migration)
                     .OrderBy(static group => group.Min(static step => step.Order))
                     .ThenBy(static group => group.Key.Id, StringComparer.Ordinal)
                     .ThenBy(static group => group.Key.Version))
        {
            if (markers.Contains(group.Key))
            {
                outcomes.Add(new MigrationCellOutcome(
                    workbook.WorkbookId,
                    group.Key,
                    MigrationDisposition.Skipped,
                    null,
                    null,
                    CanonicalValue.Missing,
                    CanonicalValue.Missing,
                    "Migration marker already present."));
                continue;
            }

            var migration = registry.GetRequired(group.Key);
            var groupFailed = false;
            foreach (var step in group.OrderBy(static item => item.Order)
                         .ThenBy(static item => item.TableId)
                         .ThenBy(static item => MigrationSnapshotComparer.PathKey(item.SourceFieldPath), StringComparer.Ordinal))
            {
                var sources = cells.Values
                    .Where(cell => cell.Address.TableId == step.TableId
                                   && cell.Address.FieldIdPath.SequenceEqual(step.SourceFieldPath))
                    .OrderBy(static cell => cell.Address.RowIdentity, StringComparer.Ordinal)
                    .ToArray();
                if (sources.Length == 0)
                {
                    outcomes.Add(new MigrationCellOutcome(
                        workbook.WorkbookId,
                        group.Key,
                        MigrationDisposition.Skipped,
                        null,
                        null,
                        CanonicalValue.Missing,
                        CanonicalValue.Missing,
                        $"No source values at table {step.TableId}, field {MigrationSnapshotComparer.PathKey(step.SourceFieldPath)}."));
                    continue;
                }

                foreach (var source in sources)
                {
                    var targetAddress = new MigrationCellAddress(
                        step.EffectiveTargetTableId,
                        source.Address.RowIdentity,
                        step.TargetFieldPath);
                    if (source.Value.State == CanonicalValueState.Invalid)
                    {
                        groupFailed = true;
                        const string message = "Unparseable source text was retained and cannot be migrated.";
                        outcomes.Add(new MigrationCellOutcome(
                            workbook.WorkbookId,
                            group.Key,
                            MigrationDisposition.Error,
                            source.Address,
                            targetAddress,
                            source.Value,
                            CanonicalValue.Missing,
                            message));
                        diagnostics.Add(Error(
                            "migration.source.invalid",
                            $"{workbook.WorkbookId}/{source.Address.StableKey}",
                            message));
                        continue;
                    }

                    if (source.Value.State == CanonicalValueState.Missing
                        || (source.Value.State == CanonicalValueState.Defaulted
                            && !step.MaterializeDefault))
                    {
                        outcomes.Add(new MigrationCellOutcome(
                            workbook.WorkbookId,
                            group.Key,
                            MigrationDisposition.Skipped,
                            source.Address,
                            targetAddress,
                            source.Value,
                            CanonicalValue.Missing,
                            source.Value.State == CanonicalValueState.Missing
                                ? "Missing source was not materialized."
                                : "Schema default was not materialized without an explicit step option."));
                        continue;
                    }

                    var context = new MigrationContext(step.TableId, step.SourceFieldPath, step.TargetFieldPath)
                    {
                        TargetTableId = step.EffectiveTargetTableId,
                        WorkbookId = workbook.WorkbookId,
                        RowIdentity = source.Address.RowIdentity,
                    };
                    MigrationResult result;
                    try
                    {
                        result = migration.Transform(in context, source.Value);
                        var verification = migration.Transform(in context, source.Value);
                        if (result != verification)
                        {
                            result = MigrationResult.Error(
                                $"Migration '{group.Key}' returned a non-deterministic result for the same canonical input.");
                        }
                        else if (!Enum.IsDefined(result.Disposition))
                        {
                            result = MigrationResult.Error(
                                $"Migration '{group.Key}' returned an unknown disposition value.");
                        }
                        else if (result.Disposition == MigrationDisposition.Converted
                                 && result.Value.State == CanonicalValueState.Invalid)
                        {
                            result = MigrationResult.Error(
                                $"Migration '{group.Key}' returned an invalid canonical value instead of an error.");
                        }
                        else if (result.Disposition == MigrationDisposition.Converted
                                 && result.Value.State == CanonicalValueState.Missing)
                        {
                            result = MigrationResult.Error(
                                $"Migration '{group.Key}' returned Missing as a target value; old values must be retained and physical deletion is a separate operation.");
                        }
                    }
                    catch (Exception exception)
                    {
                        result = MigrationResult.Error(
                            $"Migration '{group.Key}' threw {exception.GetType().Name}: {exception.Message}");
                    }

                    if (result.Disposition == MigrationDisposition.Error)
                    {
                        groupFailed = true;
                        outcomes.Add(Outcome(source, targetAddress, result, workbook.WorkbookId, group.Key));
                        diagnostics.Add(Error(
                            "migration.transform.error",
                            $"{workbook.WorkbookId}/{source.Address.StableKey}",
                            result.Message ?? $"Migration '{group.Key}' failed."));
                        continue;
                    }

                    if (result.Disposition == MigrationDisposition.Skipped)
                    {
                        outcomes.Add(Outcome(source, targetAddress, result, workbook.WorkbookId, group.Key));
                        continue;
                    }

                    var targetKey = CellKey(targetAddress);
                    if (cells.TryGetValue(targetKey, out var target))
                    {
                        if (target.Value == result.Value)
                        {
                            outcomes.Add(new MigrationCellOutcome(
                                workbook.WorkbookId,
                                group.Key,
                                MigrationDisposition.Skipped,
                                source.Address,
                                targetAddress,
                                source.Value,
                                result.Value,
                                "Target already contains the converted canonical value."));
                            continue;
                        }

                        if (!step.AllowOverwrite)
                        {
                            groupFailed = true;
                            var message = "Target already contains a different canonical value; source was retained.";
                            outcomes.Add(new MigrationCellOutcome(
                                workbook.WorkbookId,
                                group.Key,
                                MigrationDisposition.Error,
                                source.Address,
                                targetAddress,
                                source.Value,
                                result.Value,
                                message));
                            diagnostics.Add(Error(
                                "migration.target.conflict",
                                $"{workbook.WorkbookId}/{targetAddress.StableKey}",
                                message));
                            continue;
                        }
                    }

                    cells[targetKey] = new MigrationCell(targetAddress, result.Value);
                    outcomes.Add(Outcome(source, targetAddress, result, workbook.WorkbookId, group.Key));
                }
            }

            if (groupFailed)
            {
                failed = true;
                break;
            }

            markers.Add(group.Key);
        }

        var orderedOutcomes = OrderOutcomes(outcomes);
        if (failed)
            return new MigrationWorkbookPlan(workbook, workbook, orderedOutcomes);

        var candidateCells = MigrationSnapshotComparer.NormalizeCells(cells.Values);
        var changedContent = workbook.SchemaHash != targetSchemaHash
                             || !markers.ToImmutable().SetEquals(workbook.AppliedMigrations)
                             || !CellsEqual(workbook.Cells, candidateCells);
        var candidate = workbook with
        {
            SchemaHash = targetSchemaHash,
            AppliedMigrations = markers.ToImmutable(),
            Cells = candidateCells,
        };
        if (changedContent)
            candidate = candidate with { Revision = ComputeRevision(candidate) };
        return new MigrationWorkbookPlan(workbook, candidate, orderedOutcomes);
    }

    private static MigrationCellOutcome Outcome(
        MigrationCell source,
        MigrationCellAddress targetAddress,
        MigrationResult result,
        string workbookId,
        MigrationKey key) =>
        new(
            workbookId,
            key,
            result.Disposition,
            source.Address,
            targetAddress,
            source.Value,
            result.Value,
            result.Message);

    private static ImmutableArray<MigrationCellOutcome> OrderOutcomes(IEnumerable<MigrationCellOutcome> outcomes) =>
        [.. outcomes
            .OrderBy(static item => item.WorkbookId, StringComparer.Ordinal)
            .ThenBy(static item => item.Migration.Id, StringComparer.Ordinal)
            .ThenBy(static item => item.Migration.Version)
            .ThenBy(static item => item.SourceAddress?.StableKey, StringComparer.Ordinal)
            .ThenBy(static item => item.TargetAddress?.StableKey, StringComparer.Ordinal)
            .ThenBy(static item => item.Disposition)
            .ThenBy(static item => item.Message, StringComparer.Ordinal)];

    private static bool CellsEqual(
        ImmutableArray<MigrationCell> left,
        ImmutableArray<MigrationCell> right)
    {
        var leftSnapshot = new MigrationWorkbookSnapshot("compare", "compare", 1, left, []);
        var rightSnapshot = new MigrationWorkbookSnapshot("compare", "compare", 1, right, []);
        return MigrationSnapshotComparer.SemanticallyEquals(leftSnapshot, rightSnapshot);
    }

    private static string ComputeRevision(MigrationWorkbookSnapshot snapshot)
        => MigrationPlanSerializer.ComputeSnapshotRevision(snapshot);

    private static string SnapshotContentKey(MigrationWorkbookSnapshot snapshot) =>
        Convert.ToHexString(SHA256.HashData(MigrationPlanSerializer.SerializeSnapshotUtf8(snapshot, includeRevision: true)));

    private static string CellKey(MigrationCellAddress address) => address.StableKey;

    private static ImmutableArray<Diagnostic> OrderDiagnostics(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics
            .OrderBy(static item => item.Code, StringComparer.Ordinal)
            .ThenBy(static item => item.Location, StringComparer.Ordinal)
            .ThenBy(static item => item.Message, StringComparer.Ordinal)];

    private static Diagnostic Error(string code, string location, string message) =>
        new(code, DiagnosticSeverity.Error, location, message);

    private static Diagnostic Blocker(string code, string location, string message) =>
        new(code, DiagnosticSeverity.Blocker, location, message);
}

public static class MigrationPlanSerializer
{
    public static string ComputeSnapshotRevision(MigrationWorkbookSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var bytes = SerializeSnapshotUtf8(snapshot, includeRevision: false);
        return $"migration-{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }

    public static byte[] SerializeUtf8(MigrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return SerializeUtf8(plan, includePlanHash: true);
    }

    public static string Serialize(MigrationPlan plan) => Encoding.UTF8.GetString(SerializeUtf8(plan));

    public static string ComputeHash(MigrationPlan plan) =>
        Convert.ToHexString(SHA256.HashData(SerializeUtf8(plan, includePlanHash: false))).ToLowerInvariant();

    public static bool Verify(MigrationPlan plan) =>
        !string.IsNullOrWhiteSpace(plan.PlanHash)
        && string.Equals(plan.PlanHash, ComputeHash(plan), StringComparison.Ordinal);

    internal static byte[] SerializeSnapshotUtf8(
        MigrationWorkbookSnapshot snapshot,
        bool includeRevision)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteSnapshot(writer, snapshot, includeRevision);
        return stream.ToArray();
    }

    private static byte[] SerializeUtf8(MigrationPlan plan, bool includePlanHash)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", plan.FormatVersion);
            writer.WriteString("sourceSchemaHash", plan.SourceSchemaHash.ToString("x16"));
            writer.WriteString("targetSchemaHash", plan.TargetSchemaHash.ToString("x16"));
            if (includePlanHash)
                writer.WriteString("planHash", plan.PlanHash);
            writer.WriteStartArray("steps");
            foreach (var step in plan.Steps
                         .OrderBy(static item => item.Order)
                         .ThenBy(static item => item.Migration.Id, StringComparer.Ordinal)
                         .ThenBy(static item => item.Migration.Version)
                         .ThenBy(static item => item.TableId)
                         .ThenBy(static item => MigrationSnapshotComparer.PathKey(item.SourceFieldPath), StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteNumber("order", step.Order);
                WriteKey(writer, step.Migration);
                writer.WriteNumber("tableId", step.TableId);
                writer.WriteNumber("targetTableId", step.EffectiveTargetTableId);
                WritePath(writer, "sourceFieldPath", step.SourceFieldPath);
                WritePath(writer, "targetFieldPath", step.TargetFieldPath);
                writer.WriteBoolean("dataLossRisk", step.DataLossRisk);
                writer.WriteBoolean("allowOverwrite", step.AllowOverwrite);
                writer.WriteBoolean("materializeDefault", step.MaterializeDefault);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("workbooks");
            foreach (var workbook in plan.Workbooks.OrderBy(static item => item.Expected.WorkbookId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("expected");
                WriteSnapshot(writer, workbook.Expected, includeRevision: true);
                writer.WritePropertyName("candidate");
                WriteSnapshot(writer, workbook.Candidate, includeRevision: true);
                writer.WriteStartArray("outcomes");
                foreach (var outcome in workbook.Outcomes)
                    WriteOutcome(writer, outcome);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("diagnostics");
            foreach (var diagnostic in plan.Diagnostics)
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

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        MigrationWorkbookSnapshot snapshot,
        bool includeRevision)
    {
        writer.WriteStartObject();
        writer.WriteString("workbookId", snapshot.WorkbookId);
        if (includeRevision)
            writer.WriteString("revision", snapshot.Revision);
        writer.WriteString("schemaHash", snapshot.SchemaHash.ToString("x16"));
        writer.WriteStartArray("cells");
        foreach (var cell in MigrationSnapshotComparer.NormalizeCells(snapshot.Cells.IsDefault ? [] : snapshot.Cells))
        {
            writer.WriteStartObject();
            writer.WriteNumber("tableId", cell.Address.TableId);
            writer.WriteString("rowIdentity", cell.Address.RowIdentity);
            WritePath(writer, "fieldIdPath", cell.Address.FieldIdPath);
            WriteValue(writer, cell.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("appliedMigrations");
        foreach (var key in snapshot.AppliedMigrations
                     .OrderBy(static item => item.Id, StringComparer.Ordinal)
                     .ThenBy(static item => item.Version))
        {
            writer.WriteStartObject();
            WriteKey(writer, key);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteOutcome(Utf8JsonWriter writer, MigrationCellOutcome outcome)
    {
        writer.WriteStartObject();
        writer.WriteString("workbookId", outcome.WorkbookId);
        WriteKey(writer, outcome.Migration);
        writer.WriteString("disposition", outcome.Disposition.ToString());
        WriteAddress(writer, "source", outcome.SourceAddress);
        WriteAddress(writer, "target", outcome.TargetAddress);
        writer.WritePropertyName("before");
        WriteValueObject(writer, outcome.Before);
        writer.WritePropertyName("after");
        WriteValueObject(writer, outcome.After);
        if (outcome.Message is null) writer.WriteNull("message"); else writer.WriteString("message", outcome.Message);
        writer.WriteEndObject();
    }

    private static void WriteAddress(Utf8JsonWriter writer, string name, MigrationCellAddress? address)
    {
        if (address is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteNumber("tableId", address.TableId);
        writer.WriteString("rowIdentity", address.RowIdentity);
        WritePath(writer, "fieldIdPath", address.FieldIdPath);
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, CanonicalValue value)
    {
        writer.WritePropertyName("value");
        WriteValueObject(writer, value);
    }

    private static void WriteValueObject(Utf8JsonWriter writer, CanonicalValue value)
    {
        writer.WriteStartObject();
        writer.WriteString("state", value.State.ToString());
        if (value.Text is null) writer.WriteNull("text"); else writer.WriteString("text", value.Text);
        if (value.RawText is null) writer.WriteNull("rawText"); else writer.WriteString("rawText", value.RawText);
        writer.WriteEndObject();
    }

    private static void WriteKey(Utf8JsonWriter writer, MigrationKey key)
    {
        writer.WriteString("migrationId", key.Id);
        writer.WriteNumber("migrationVersion", key.Version);
    }

    private static void WritePath(Utf8JsonWriter writer, string name, ImmutableArray<int> path)
    {
        writer.WriteStartArray(name);
        foreach (var number in path.IsDefault ? [] : path)
            writer.WriteNumberValue(number);
        writer.WriteEndArray();
    }
}
