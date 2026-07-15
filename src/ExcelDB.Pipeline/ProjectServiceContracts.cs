using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Tooling.Plans;

namespace ExcelDb.Pipeline;

public sealed record ProjectLocation(string Path)
{
    public static ProjectLocation From(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ProjectLocation(System.IO.Path.GetFullPath(path));
    }
}

public static class ProjectArtifactPaths
{
    public const string DefaultWorkbookFileName = "game.xlsx";

    public static string GetDefaultWorkbookPath(string projectRoot, string excelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(excelDirectory);
        var root = System.IO.Path.GetFullPath(projectRoot);
        var workbook = System.IO.Path.Combine(
            System.IO.Path.GetFullPath(excelDirectory),
            DefaultWorkbookFileName);
        var relative = System.IO.Path.GetRelativePath(root, workbook);
        if (System.IO.Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith("../", StringComparison.Ordinal)
            || relative.StartsWith("..\\", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Excel artifact directory must remain inside the project root.");
        }

        return relative.Replace('\\', '/');
    }
}

public enum ProjectStatus
{
    Uninitialized = 0,
    Legacy = 1,
    Invalid = 2,
    RecoveryRequired = 3,
    NoTables = 4,
    SystemImportsMissing = 5,
    SystemImportsOutdated = 6,
    SystemImportsConflicted = 7,
    RegenerationRequired = 8,
    BytesGenerationRequired = 9,
    PendingRows = 10,
    DataErrors = 11,
    Ready = 12,
}

public sealed record ProjectArtifactInspection(
    string Name,
    string Path,
    string Status,
    int ItemCount,
    bool IsExternal = false);

public enum ProjectTargetArtifactStatus
{
    Current = 0,
    Missing = 1,
    Stale = 2,
    Corrupt = 3,
    Unavailable = 4,
}

public sealed record ProjectTargetArtifactInspection(
    string Target,
    string BytesPath,
    string ManifestPath,
    ProjectTargetArtifactStatus Status,
    string Detail);

public sealed record ProjectActionAvailability(bool Enabled, string? DisabledReason = null);

public sealed record ProjectActions(
    ProjectActionAvailability Initialize,
    ProjectActionAvailability CreateTable,
    ProjectActionAvailability EditTable,
    ProjectActionAvailability Regenerate,
    ProjectActionAvailability PrepareRows,
    ProjectActionAvailability Check,
    ProjectActionAvailability Convert,
    ProjectActionAvailability RepairSystemImports,
    ProjectActionAvailability Configure);

public sealed record ProjectInspection(
    string ProjectRoot,
    string? ProjectFilePath,
    ProjectStatus Status,
    ProjectArtifactInspection Schema,
    ProjectArtifactInspection Excel,
    ProjectArtifactInspection GeneratedCSharp,
    ProjectArtifactInspection GeneratedBytes,
    ProjectActions Actions,
    ImmutableArray<Diagnostic> Diagnostics,
    string? LatestReportPath = null,
    string? RecoveryDirectory = null,
    string? InternalDirectory = null)
{
    public int PendingIdentityCount { get; init; }

    public ImmutableArray<ProjectTargetArtifactInspection> GeneratedByteTargets { get; init; } = [];
}

public sealed record InitializeProjectRequest(
    ProjectLocation Project,
    string? GeneratedCSharpDirectory = null);

public sealed record ConfigureProjectRequest(
    ProjectLocation Project,
    string? SchemaDirectory = null,
    string? ExcelDirectory = null,
    string? GeneratedCSharpDirectory = null,
    string? GeneratedBytesDirectory = null);

public sealed record CreateTableRequest(ProjectLocation Project, TableCreateIntent Intent);

public sealed record EditTableRequest(ProjectLocation Project, TableEditIntent Intent);

public sealed record RegenerateRequest(ProjectLocation Project);

public sealed record DataPrepareRequest(ProjectLocation Project, string? WorkbookPath = null);

public sealed record RepairSystemImportsRequest(ProjectLocation Project, bool ExplicitRepair = true);

public sealed record CheckRequest(ProjectLocation Project, string? WorkbookPath = null);

public sealed record ConvertRequest(
    ProjectLocation Project,
    string Target = "client",
    string? OutputPath = null,
    string? WorkbookPath = null);

/// <summary>
/// The single typed application boundary used by the WinForms Project Hub, CLI/BAT,
/// and host adapters. UI layers never spawn exceldb or parse console output.
/// </summary>
public interface IExcelDbProjectService
{
    ProjectInspection Inspect(ProjectLocation project);

    OperationReport RepairProjectOwnedAttributes(ProjectLocation project);

    MutationPlan PlanInitialize(InitializeProjectRequest request);

    MutationPlan PlanConfigure(ConfigureProjectRequest request);

    Task<MutationPlan> PlanCreateTableAsync(CreateTableRequest request, CancellationToken cancellationToken = default);

    Task<MutationPlan> PlanEditTableAsync(EditTableRequest request, CancellationToken cancellationToken = default);

    Task<MutationPlan> PlanRegenerateAsync(RegenerateRequest request, CancellationToken cancellationToken = default);

    Task<MutationPlan> PlanDataPrepareAsync(DataPrepareRequest request, CancellationToken cancellationToken = default);

    MutationPlan PlanRepairSystemImports(RepairSystemImportsRequest request);

    Task<OperationReport> CheckAsync(CheckRequest request, CancellationToken cancellationToken = default);

    Task<OperationReport> ConvertAsync(ConvertRequest request, CancellationToken cancellationToken = default);

    OperationReport CleanCache(ProjectLocation project);

    OperationReport Apply(MutationPlan plan);
}
