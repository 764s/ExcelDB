using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Runtime;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;
using ExcelDbEditor;

namespace ExcelDb.Editor;

public enum EditorAssetBadge
{
    None,
    Dirty,
    Conflicted,
    Missing,
    Error,
}

public sealed record EditorBrowserRow(
    GUID Guid,
    string Path,
    string Key,
    string? DisplayName,
    Type AssetType,
    object Asset,
    ImmutableArray<EditorAssetBadge> Badges);

public sealed record EditorBrowserTable(string WorkbookPath, string TableName, ImmutableArray<EditorBrowserRow> Rows);

public sealed record EditorBrowserSnapshot(
    ImmutableArray<EditorBrowserTable> Tables,
    int DirtyCount,
    int ConflictCount,
    int ErrorCount);

public sealed record EditorProjectMountState(
    bool HasProject,
    string ProjectFile,
    ImmutableArray<string> ConfiguredWorkbooks,
    ImmutableArray<string> MountedWorkbooks,
    ImmutableArray<string> MissingPatterns);

public sealed record EditorRowReferenceConstraint(string RefTable, string? RefGroup = null);

public sealed record EditorWorkbookSummary(
    string WorkbookPath,
    bool IsMounted,
    int TableCount,
    int RowCount,
    ulong? ExpectedSchemaHash,
    ulong? ActualSchemaHash)
{
    public bool SchemaMatches => ExpectedSchemaHash is null
        || ActualSchemaHash is null
        || ExpectedSchemaHash == ActualSchemaHash;
}

public sealed record EditorPlanPreview(
    string Operation,
    string PlanHash,
    ImmutableArray<PathObservation> Sources,
    ImmutableArray<FileMutation> Mutations,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<string> Risks)
{
    public bool CanApply => !Diagnostics.Any(static item => item.IsBlocker);

    public static EditorPlanPreview FromPlan(MutationPlan plan) => new(
        plan.Operation,
        plan.PlanHash,
        plan.Observations,
        plan.Mutations,
        plan.Diagnostics,
        plan.Risks);
}

public sealed record EditorReportEntry(DateTimeOffset Timestamp, OperationReport Report, string Summary);

public enum DirtyGuardAction
{
    Save,
    Discard,
    Cancel,
    ResolveConflicts,
}

public enum DirtyGuardTrigger
{
    ScriptCompilation,
    EnterPlayMode,
    QuitEditor,
    UnmountWorkbook,
}

public sealed record DirtyGuardState(
    bool HasPendingChanges,
    bool HasUnresolvedConflicts,
    ImmutableArray<DirtyGuardAction> AllowedActions);

public enum FreshnessAction
{
    ConvertClientAndContinue,
    ContinueStale,
    Cancel,
}

public sealed record EditorFreshnessState(
    bool IsFresh,
    string Reason,
    string ExpectedTarget,
    string? ActualTarget,
    string? ExpectedContentHash,
    string? ActualContentHash);

public sealed record EditorFreshnessDecision(bool Continue, bool Converted, EditorFreshnessState State);

public enum EditorConflictAction
{
    ReloadFromExcel,
    KeepEditorValue,
}

public sealed record EditorConflictSnapshot(
    ImmutableArray<ConflictRecord> Conflicts,
    bool CanSave);

public sealed record PendingFieldChange(string PropertyPath, string? Before, string? After);

public sealed record PendingAssetChange(
    GUID Guid,
    string Path,
    string Kind,
    ImmutableArray<PendingFieldChange> Fields);

public sealed record EditorPlaySourceRequest(
    IDataSource Source,
    RuntimeSchemaRegistry Registry,
    RuntimeBootstrapOptions Options);

public sealed record EditorPlayRuntimeState(
    bool IsOpen,
    string Target,
    string SourceKind,
    string Revision,
    bool HotReloadEnabled,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record EditorRuntimeActionResult(
    bool Succeeded,
    string Reason,
    EditorPlayRuntimeState State);

public sealed record ProjectSettingsProjection(
    string SchemaDir,
    string GeneratedDir,
    ImmutableArray<string> Workbooks,
    string BytesOutput,
    string CacheDir,
    ulong? SchemaHash,
    string ToolVersion)
{
    public static ProjectSettingsProjection FromProject(ExcelDbProject project, ulong? schemaHash, string toolVersion) =>
        new(project.SchemaDir, project.GeneratedDir, project.Workbooks, project.BytesOutput, project.CacheDir, schemaHash, toolVersion);
}

public interface IEditorAssetStatusProvider
{
    ImmutableArray<EditorAssetBadge> GetBadges(object asset);

    string? GetDisplayName(object asset);
}

public interface IPendingChangeSource
{
    ImmutableArray<PendingAssetChange> GetPendingChanges();

    bool DiscardAll();
}

public interface IEditorWorkbookSummaryProvider
{
    EditorWorkbookSummary? TryGetSummary(string workbookPath, ulong? expectedSchemaHash);
}

public interface IEditorConsole
{
    void Error(string message);

    void Warning(string message);
}
