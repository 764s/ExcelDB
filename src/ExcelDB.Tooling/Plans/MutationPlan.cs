using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;

namespace ExcelDb.Tooling.Plans;

/// <summary>The declared filesystem roots a frozen plan is allowed to address.</summary>
public enum PlanRootKind : byte
{
    Project = 0,
    GeneratedCSharp = 1,
}

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
    string? Sha256 = null,
    PlanRootKind Root = PlanRootKind.Project);

/// <summary>The deterministic discovery rule used to freeze a complete logical input set.</summary>
public enum InputSetKind : byte
{
    /// <summary>Every <c>*.proto</c> below schemaDir, including executable-owned mirrors.</summary>
    SchemaProto = 0,

    /// <summary>Project v2 recursive workbook discovery, including all of its exclusion rules.</summary>
    ExcelWorkbook = 1,

    /// <summary>Every file below the reserved <c>exceldb</c> and <c>google/protobuf</c> namespaces.</summary>
    SystemProtoMirror = 2,
}

/// <summary>One canonical member of a frozen input set.</summary>
public sealed record InputSetEntry(
    string RelativePath,
    ObservedPathKind Kind,
    long Length = 0,
    string? Sha256 = null);

/// <summary>
/// A filtered directory snapshot. The digest is derived from the filter kind,
/// root state, and the ordinally sorted path/kind/length/hash member sequence.
/// </summary>
public sealed record InputSetObservation(
    string RelativeRoot,
    InputSetKind Kind,
    ObservedPathKind RootKind,
    long RootLength,
    string? RootSha256,
    ImmutableArray<InputSetEntry> Entries,
    string Digest,
    PlanRootKind Root = PlanRootKind.Project);

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
    string? ContentSha256 = null,
    PlanRootKind Root = PlanRootKind.Project);

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
    string PlanHash,
    string? GeneratedCSharpRoot = null,
    string SystemCatalogHash = "",
    ImmutableArray<InputSetObservation> InputSets = default)
{
    public const int CurrentFormatVersion = 2;

    public bool HasBlockers => Diagnostics.Any(static item => item.IsBlocker);

    public string GetRoot(PlanRootKind kind) => kind switch
    {
        PlanRootKind.Project => ProjectRoot,
        PlanRootKind.GeneratedCSharp => GeneratedCSharpRoot
            ?? throw new InvalidDataException("MutationPlan has no Generated C# root."),
        _ => throw new InvalidDataException($"Unknown MutationPlan root kind '{kind}'."),
    };
}
