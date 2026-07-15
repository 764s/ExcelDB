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
    public static PipelineProject Load(string projectFile)
    {
        var fullPath = Path.GetFullPath(projectFile);
        var internalDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("The project file has no parent directory.");
        if (!string.Equals(Path.GetFileName(fullPath), "project.json", StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(internalDirectory), ".exceldb", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Project v2 configuration must be located at '<ProjectRoot>/.exceldb/project.json'.");
        }
        var projectRoot = Path.GetDirectoryName(internalDirectory)
            ?? throw new InvalidDataException("The project internal directory has no parent directory.");
        ProjectPathSafety.EnsureProjectRootAndInternalDirectoryArePlain(projectRoot);
        if (File.Exists(Path.Combine(projectRoot, ExcelDbProject.LegacyFileName)))
        {
            throw new InvalidDataException(
                $"Legacy project configuration '{ExcelDbProject.LegacyFileName}' exists beside Project v2; dual project configuration is not allowed.");
        }
        ProjectRecovery.EnsureRecovered(projectRoot);
        var project = ExcelDbProject.Load(fullPath);
        var configHash = ContentFingerprint.FromBytes(project.ToCanonicalJson()).Sha256;
        return new PipelineProject(project.Resolve(fullPath), configHash);
    }
}

public sealed record CompiledProjectSchema(
    FileDescriptorSet DescriptorSet,
    byte[] DescriptorBytes,
    CanonicalSchemaDescriptor Descriptor,
    ImmutableArray<Diagnostic> Diagnostics,
    InputSetObservation? SchemaInputSet = null,
    InputSetObservation? SystemProtoMirrorInputSet = null,
    PathObservation? SystemImportsManifestObservation = null);

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
