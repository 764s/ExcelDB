using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;

namespace ExcelDb.Compatibility;

public enum CompatibilitySeverity : byte
{
    Safe = 0,
    Warning = 1,
    Error = 2,
    Blocker = 3,
}

public enum CompatibilityChangeKind : byte
{
    HistoryUnavailable,
    TableAdded,
    TableRenamed,
    TableRetired,
    TableTombstoneMissing,
    TableIdentityReused,
    TableReactivated,
    TableKindChanged,
    KeyChanged,
    FieldAdded,
    FieldRenamed,
    FieldRemoved,
    FieldIdentityReused,
    FieldSemanticChanged,
    CellFormatMigrationStarted,
    CellFormatLegacyAdded,
    CellFormatLegacyRemoved,
    CellFormatChangedInPlace,
    ExportTargetAdded,
    ExportTargetRemoved,
    EnumAdded,
    EnumValueAdded,
    EnumValueRenamed,
    EnumValueRemoved,
    EnumValueIdentityReused,
    WorkbookOutdated,
    WorkbookSchemaDrift,
    WorkbookMappingInvalid,
    WorkbookOwnershipConflict,
    WorkbookCoverageIncomplete,
    UnparseableData,
    MigrationPending,
    LegacyFormatHits,
    WorkbookEvidenceInvalid,
}

public sealed record CompatibilityLocation(
    int? TableId,
    ImmutableArray<int> FieldIdPath,
    string? EnumName,
    int? EnumNumber,
    string? ExportTarget,
    string? PreviousPath,
    string? CurrentPath,
    string? WorkbookId = null,
    string? Cell = null);

public sealed record CompatibilityEntry(
    CompatibilityChangeKind Kind,
    CompatibilitySeverity Severity,
    CompatibilityLocation Location,
    string Message);

public sealed record CompatibilityDataFacts(
    ImmutableHashSet<int> NonEmptyTableIds,
    bool HasCompleteWorkbookCoverage = false)
{
    public static CompatibilityDataFacts Empty { get; } = new([], false);

    public ImmutableArray<WorkbookCompatibilityFacts> Workbooks { get; init; } = [];

    public ImmutableArray<LegacyFormatEvidence> LegacyFormatEvidence { get; init; } = [];
}

public sealed record WorkbookCompatibilityFacts(
    string WorkbookId,
    string Revision,
    ulong SchemaHash,
    bool MappingComplete = true,
    bool OwnershipSafe = true,
    int UnparseableValueCount = 0,
    int PendingMigrationCount = 0,
    int LegacyFormatHitCount = 0,
    int LegacyFormatErrorCount = 0);

public sealed record LegacyFormatEvidence(
    int TableId,
    ImmutableArray<int> FieldIdPath,
    bool CompleteCoverage,
    int RemainingHits,
    int ErrorCount)
{
    public ulong? SchemaHash { get; init; }

    public ImmutableDictionary<string, string> WorkbookRevisions { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    public bool ProvesSafeRemoval => CompleteCoverage && RemainingHits == 0 && ErrorCount == 0;
}

public sealed record CompatibilityReport(
    int FormatVersion,
    ulong? PreviousSchemaHash,
    ulong CurrentSchemaHash,
    bool HistoryAvailable,
    ImmutableArray<CompatibilityEntry> Entries,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public const int CurrentFormatVersion = 1;

    public CompatibilitySeverity MaximumSeverity
    {
        get
        {
            var entrySeverity = Entries.IsEmpty
                ? CompatibilitySeverity.Safe
                : Entries.Max(static item => item.Severity);
            var diagnosticSeverity = Diagnostics.IsEmpty
                ? CompatibilitySeverity.Safe
                : Diagnostics.Max(static item => item.Severity switch
                {
                    DiagnosticSeverity.Info => CompatibilitySeverity.Safe,
                    DiagnosticSeverity.Warning => CompatibilitySeverity.Warning,
                    DiagnosticSeverity.Error => CompatibilitySeverity.Error,
                    DiagnosticSeverity.Blocker => CompatibilitySeverity.Blocker,
                    _ => CompatibilitySeverity.Blocker,
                });
            return (CompatibilitySeverity)Math.Max((byte)entrySeverity, (byte)diagnosticSeverity);
        }
    }

    public bool HasBlockers => MaximumSeverity == CompatibilitySeverity.Blocker;
}
