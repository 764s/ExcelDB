using System.Collections.Immutable;

namespace ExcelDb.Core.Diagnostics;

public enum OperationExitCode
{
    Success = 0,
    Error = 1,
    Blocker = 2,
    UsageOrEnvironment = 3,
}

public sealed record ArtifactRecord(string Kind, string Path, string? ContentHash = null);

public sealed record OperationReport(
    string Operation,
    string ToolVersion,
    bool Applied,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<ArtifactRecord> Artifacts,
    string? PlanHash = null)
{
    public int FormatVersion { get; init; } = 1;

    public OperationExitCode ExitCode
    {
        get
        {
            if (Diagnostics.Any(static item => item.IsBlocker))
                return OperationExitCode.Blocker;
            if (Diagnostics.Any(static item => item.IsFailure))
                return OperationExitCode.Error;
            return OperationExitCode.Success;
        }
    }

    public bool Succeeded => ExitCode == OperationExitCode.Success;

    public static OperationReport Success(string operation, string toolVersion, bool applied = false) =>
        new(
            operation,
            toolVersion,
            applied,
            ImmutableArray<Diagnostic>.Empty,
            ImmutableArray<ArtifactRecord>.Empty);
}
