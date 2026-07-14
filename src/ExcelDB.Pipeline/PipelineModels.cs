using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;
using ExcelDb.Schema.Authoring;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Mutation;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;
using Google.Protobuf.Reflection;

namespace ExcelDb.Pipeline;

public sealed record PipelineProject(ProjectContext Context, string ConfigHash)
{
    public static PipelineProject Load(
        string projectFile,
        string? schemaDirectoryOverride = null,
        ImmutableArray<string>? workbookOverrides = null)
    {
        var fullPath = Path.GetFullPath(projectFile);
        var project = ExcelDbProject.Load(fullPath);
        if (schemaDirectoryOverride is not null)
            project = project with { SchemaDir = schemaDirectoryOverride };
        if (workbookOverrides is { } overrides)
            project = project with { Workbooks = overrides };
        var configHash = ContentFingerprint.FromBytes(project.ToCanonicalJson()).Sha256;
        return new PipelineProject(project.Resolve(fullPath), configHash);
    }
}

public sealed record CompiledProjectSchema(
    FileDescriptorSet DescriptorSet,
    byte[] DescriptorBytes,
    CanonicalSchemaDescriptor Descriptor,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SchemaBuildOutcome(
    OperationReport Report,
    CompiledProjectSchema? Schema,
    MutationPlan? Plan);

public sealed record TableCreateIntent(
    string TableName,
    string WorkbookPath,
    ImmutableArray<SimpleFieldDefinition> Fields,
    bool AutoKey = true,
    string Package = "game.configs",
    int? TableId = null,
    ImmutableDictionary<string, ExportTargetSelection>? FieldTargets = null,
    ExportTargetSelection? TableTargets = null,
    string? ProtoFile = null);

public sealed record TableEditIntent(
    string TableFullName,
    ImmutableArray<AddSimpleFieldMutation> AddFields,
    ImmutableArray<RenameFieldMutation> RenameFields,
    ImmutableArray<int> RemoveFieldNumbers,
    bool Retire = false,
    string? NewName = null,
    ExportTargetSelection? TableTargets = null,
    ImmutableDictionary<int, ExportTargetSelection>? FieldTargets = null,
    string? WorkbookPath = null);

public sealed record WorkbookCheckOutcome(
    OperationReport Report,
    CompiledProjectSchema? Schema,
    ImmutableArray<string> WorkbookPaths,
    int PendingIdentityCount);

public sealed record WorkbookDiffEntry(
    string Kind,
    int TableId,
    string Sheet,
    string Identity,
    string? PropertyPath,
    string? Before,
    string? After);

public sealed record WorkbookDiffReport(
    int FormatVersion,
    string BasePath,
    string TargetPath,
    ImmutableArray<WorkbookDiffEntry> Entries,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Succeeded => !Diagnostics.Any(static item => item.IsFailure);
}
