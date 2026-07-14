namespace ExcelDb.Compatibility;

public enum CompatibilityOperation : byte
{
    SchemaBuild,
    GenerateDryRun,
    GenerateApply,
    ImportCheck,
    SaveAssets,
    Normalize,
    Convert,
    Publish,
    RuntimeOpen,
    DestructiveApply,
    Purge,
    Rekey,
    DataLossMigration,
}

public sealed record CompatibilityGateEvidence
{
    public bool HasFreshPlan { get; init; }

    public bool HasMigrationEvidence { get; init; }

    public bool HasExplicitDataLossConfirmation { get; init; }

    public bool HasOwnershipProof { get; init; }

    public bool HasCompleteWorkbookCoverage { get; init; }

    public bool HasStableSourceRevision { get; init; }

    public bool HasMappingIntegrity { get; init; }

    public bool LegacyParsingIsUnambiguous { get; init; }

    public bool HasNoUnparseableData { get; init; }

    public bool TargetPublicationReady { get; init; }

    public bool RuntimeIdentityMatches { get; init; }
}

public sealed record CompatibilityGateResult(
    CompatibilityOperation Operation,
    bool Allowed,
    CompatibilitySeverity MaximumSeverity,
    string Reason);

public static class CompatibilityGate
{
    public static CompatibilityGateResult Evaluate(
        CompatibilityReport report,
        CompatibilityOperation operation,
        CompatibilityGateEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(evidence);
        var maximum = report.MaximumSeverity;
        if (report.FormatVersion != CompatibilityReport.CurrentFormatVersion)
        {
            var readOnly = operation is CompatibilityOperation.GenerateDryRun
                or CompatibilityOperation.ImportCheck;
            return new CompatibilityGateResult(
                operation,
                readOnly,
                maximum,
                readOnly
                    ? "Unknown compatibility report format is restricted to read-only inspection."
                    : $"Compatibility report format {report.FormatVersion} is unsupported.");
        }

        var noBlocker = maximum < CompatibilitySeverity.Blocker;
        var noError = maximum < CompatibilitySeverity.Error;
        var workbookEvidenceIsCurrent = !report.Entries.Any(static item =>
            item.Kind is CompatibilityChangeKind.WorkbookOutdated
                or CompatibilityChangeKind.MigrationPending);
        var reportHasCompleteCoverage = !report.Entries.Any(static item =>
            item.Kind == CompatibilityChangeKind.WorkbookCoverageIncomplete);
        var allowed = operation switch
        {
            CompatibilityOperation.SchemaBuild => noBlocker,
            CompatibilityOperation.GenerateDryRun or CompatibilityOperation.ImportCheck => true,
            CompatibilityOperation.GenerateApply =>
                report.HistoryAvailable
                && evidence.HasFreshPlan
                && evidence.HasStableSourceRevision
                && evidence.HasOwnershipProof
                && evidence.HasMappingIntegrity
                && noBlocker,
            CompatibilityOperation.SaveAssets =>
                evidence.HasStableSourceRevision
                && evidence.HasMappingIntegrity
                && noError,
            CompatibilityOperation.Normalize =>
                evidence.HasFreshPlan
                && evidence.HasStableSourceRevision
                && evidence.HasOwnershipProof
                && evidence.HasMappingIntegrity
                && evidence.LegacyParsingIsUnambiguous
                && noBlocker,
            CompatibilityOperation.Convert =>
                report.HistoryAvailable
                && evidence.HasMigrationEvidence
                && evidence.HasStableSourceRevision
                && evidence.HasMappingIntegrity
                && evidence.HasNoUnparseableData
                && evidence.TargetPublicationReady
                && workbookEvidenceIsCurrent
                && noError,
            CompatibilityOperation.Publish =>
                report.HistoryAvailable
                && evidence.HasMigrationEvidence
                && evidence.HasCompleteWorkbookCoverage
                && evidence.HasStableSourceRevision
                && evidence.HasMappingIntegrity
                && evidence.HasNoUnparseableData
                && evidence.TargetPublicationReady
                && workbookEvidenceIsCurrent
                && reportHasCompleteCoverage
                && noError,
            CompatibilityOperation.RuntimeOpen => evidence.RuntimeIdentityMatches && noError,
            CompatibilityOperation.Rekey =>
                report.HistoryAvailable
                && evidence.HasFreshPlan
                && evidence.HasMigrationEvidence
                && evidence.HasCompleteWorkbookCoverage
                && evidence.HasStableSourceRevision
                && evidence.HasOwnershipProof
                && evidence.HasMappingIntegrity
                && evidence.HasNoUnparseableData
                && reportHasCompleteCoverage
                && noBlocker,
            CompatibilityOperation.DestructiveApply
                or CompatibilityOperation.Purge
                or CompatibilityOperation.DataLossMigration =>
                report.HistoryAvailable
                && evidence.HasFreshPlan
                && evidence.HasMigrationEvidence
                && evidence.HasExplicitDataLossConfirmation
                && evidence.HasCompleteWorkbookCoverage
                && evidence.HasStableSourceRevision
                && evidence.HasOwnershipProof
                && evidence.HasMappingIntegrity
                && evidence.HasNoUnparseableData
                && reportHasCompleteCoverage
                && noBlocker,
            _ => false,
        };

        return new CompatibilityGateResult(
            operation,
            allowed,
            maximum,
            allowed ? "Compatibility gate passed." : Explain(report, operation, evidence, maximum));
    }

    public static CompatibilityGateResult Evaluate(
        CompatibilityReport report,
        CompatibilityOperation operation,
        bool hasFreshPlan = false,
        bool hasMigrationEvidence = false,
        bool hasExplicitDataLossConfirmation = false) =>
        Evaluate(
            report,
            operation,
            new CompatibilityGateEvidence
            {
                HasFreshPlan = hasFreshPlan,
                HasMigrationEvidence = hasMigrationEvidence,
                HasExplicitDataLossConfirmation = hasExplicitDataLossConfirmation,
                HasOwnershipProof = hasFreshPlan,
                HasCompleteWorkbookCoverage = hasMigrationEvidence,
                HasStableSourceRevision = hasFreshPlan || hasMigrationEvidence,
                HasMappingIntegrity = true,
                LegacyParsingIsUnambiguous = true,
                HasNoUnparseableData = true,
                TargetPublicationReady = hasMigrationEvidence,
                RuntimeIdentityMatches = hasMigrationEvidence,
            });

    private static string Explain(
        CompatibilityReport report,
        CompatibilityOperation operation,
        CompatibilityGateEvidence evidence,
        CompatibilitySeverity maximum)
    {
        if (maximum == CompatibilitySeverity.Blocker)
            return "Compatibility report contains a blocker.";
        if (maximum == CompatibilitySeverity.Error
            && operation is CompatibilityOperation.SaveAssets
                or CompatibilityOperation.Convert
                or CompatibilityOperation.Publish
                or CompatibilityOperation.RuntimeOpen)
        {
            return "Compatibility report contains an unresolved data error.";
        }

        if (!report.HistoryAvailable
            && operation is not (CompatibilityOperation.SchemaBuild
                or CompatibilityOperation.GenerateDryRun
                or CompatibilityOperation.ImportCheck
                or CompatibilityOperation.RuntimeOpen))
        {
            return "Previous published descriptor is required for this operation.";
        }

        if (!evidence.HasFreshPlan
            && operation is CompatibilityOperation.GenerateApply
                or CompatibilityOperation.Normalize
                or CompatibilityOperation.DestructiveApply
                or CompatibilityOperation.Purge
                or CompatibilityOperation.Rekey
                or CompatibilityOperation.DataLossMigration)
        {
            return "A fresh immutable plan is required.";
        }

        if (!evidence.HasStableSourceRevision
            && operation is not (CompatibilityOperation.SchemaBuild
                or CompatibilityOperation.GenerateDryRun
                or CompatibilityOperation.ImportCheck
                or CompatibilityOperation.RuntimeOpen))
        {
            return "The source revision no longer matches the plan.";
        }

        if (!evidence.HasOwnershipProof
            && operation is CompatibilityOperation.GenerateApply
                or CompatibilityOperation.Normalize
                or CompatibilityOperation.DestructiveApply
                or CompatibilityOperation.Purge
                or CompatibilityOperation.Rekey
                or CompatibilityOperation.DataLossMigration)
        {
            return "Workbook ownership proof is incomplete.";
        }

        if (!evidence.HasMigrationEvidence
            && operation is CompatibilityOperation.Convert
                or CompatibilityOperation.Publish
                or CompatibilityOperation.DestructiveApply
                or CompatibilityOperation.Purge
                or CompatibilityOperation.Rekey
                or CompatibilityOperation.DataLossMigration)
        {
            return "Migration completion evidence is missing.";
        }

        if (!evidence.HasExplicitDataLossConfirmation
            && operation is CompatibilityOperation.DestructiveApply
                or CompatibilityOperation.Purge
                or CompatibilityOperation.DataLossMigration)
        {
            return "Explicit data-loss confirmation is missing.";
        }

        if (!evidence.TargetPublicationReady
            && operation is CompatibilityOperation.Convert or CompatibilityOperation.Publish)
        {
            return "The complete target artifact set is not staged with one schema hash.";
        }

        if (!evidence.RuntimeIdentityMatches && operation == CompatibilityOperation.RuntimeOpen)
            return "Runtime (SchemaHash, ExportTargetId) identity does not match generated code.";
        return "Required mapping, coverage, parse or migration evidence is missing.";
    }
}
