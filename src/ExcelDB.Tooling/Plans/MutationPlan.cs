using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;

namespace ExcelDb.Tooling.Plans;

public enum ObservedPathKind : byte
{
    Missing = 0,
    File = 1,
    Directory = 2,
}

public sealed record PathObservation(
    string RelativePath,
    ObservedPathKind Kind,
    long Length = 0,
    string? Sha256 = null);

public enum FileMutationKind : byte
{
    CreateDirectory = 0,
    WriteFile = 1,
    DeleteFile = 2,
}

public sealed record FileMutation(
    FileMutationKind Kind,
    string RelativePath,
    string? ContentBase64 = null,
    string? ContentSha256 = null);

public sealed record MutationPlan(
    int FormatVersion,
    string Operation,
    string ToolVersion,
    string ProjectRoot,
    string ProjectConfigHash,
    ulong SchemaHash,
    ImmutableArray<PathObservation> Observations,
    ImmutableArray<FileMutation> Mutations,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<string> Risks,
    string PlanHash)
{
    public const int CurrentFormatVersion = 1;

    public bool HasBlockers => Diagnostics.Any(static item => item.IsBlocker);
}
