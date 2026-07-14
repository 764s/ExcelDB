using System.Collections.Immutable;
using System.Text;
using ExcelDb.Compatibility;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Schema.Authoring;
using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Diagnostics;
using ExcelDb.Schema.Generation;
using ExcelDb.Schema.Mutation;
using ExcelDb.Tooling.Plans;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ExcelDb.Pipeline;

public sealed class SchemaPipeline
{
    private readonly string _toolVersion;
    private readonly ITableInitializer _tableInitializer;
    private readonly IExportTargetStrategy _exportTargetStrategy;

    public SchemaPipeline(
        string toolVersion,
        ITableInitializer? tableInitializer = null,
        IExportTargetStrategy? exportTargetStrategy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        _toolVersion = toolVersion;
        _tableInitializer = tableInitializer ?? new DefaultTableInitializer();
        _exportTargetStrategy = exportTargetStrategy ?? new StandardClientServerExportTargetStrategy();
    }

    public async Task<CompiledProjectSchema?> CompileAsync(
        PipelineProject project,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(diagnostics);
        try
        {
            var sourceSet = SchemaSourceSet.Discover(project.Context.SchemaDirectory);
            if (sourceSet.Files.Count == 0)
            {
                diagnostics.Add(new Diagnostic("XDB000", DiagnosticSeverity.Blocker, project.Context.Project.SchemaDir, "Schema source set is empty."));
                return null;
            }

            var protoc = await new EmbeddedProtocCompiler().CompileAsync(sourceSet, cancellationToken).ConfigureAwait(false);
            var compilation = new SchemaCompiler().CompileDescriptorSet(protoc.DescriptorSet);
            foreach (var diagnostic in compilation.Diagnostics)
                diagnostics.Add(ToDiagnostic(diagnostic));
            if (!compilation.Succeeded)
                return null;
            return new CompiledProjectSchema(
                protoc.DescriptorSet,
                protoc.DescriptorBytes.ToArray(),
                compilation.Descriptor!,
                compilation.Diagnostics.Select(ToDiagnostic).ToImmutableArray());
        }
        catch (ProtocCompilationException exception)
        {
            diagnostics.Add(new Diagnostic("XDB-PROTOC", DiagnosticSeverity.Blocker, project.Context.Project.SchemaDir, exception.Message));
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("XDB-ENV", DiagnosticSeverity.Blocker, project.Context.Project.SchemaDir, exception.Message));
            return null;
        }
    }

    public async Task<SchemaBuildOutcome> BuildAsync(
        PipelineProject project,
        bool checkOnly,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<Diagnostic>();
        var schema = await CompileAsync(project, diagnostics, cancellationToken).ConfigureAwait(false);
        if (schema is null)
            return FailedBuild("schema-build", diagnostics);

        var generation = new SchemaCodeGenerator().Generate(schema.Descriptor);
        AddCompatibilityDiagnostics(project, schema, diagnostics);

        var builder = CreatePlanBuilder(project, "schema-build", schema.Descriptor.SchemaHash);
        ObserveSchemaInputs(builder, project);
        AddGeneratedArtifacts(builder, project, generation, checkOnly, diagnostics);
        var descriptorPath = DescriptorCachePath(project);
        if (!checkOnly || !File.Exists(descriptorPath) || !File.ReadAllBytes(descriptorPath).AsSpan().SequenceEqual(schema.DescriptorBytes))
            WriteIfChanged(builder, descriptorPath, schema.DescriptorBytes);

        var plan = builder.Build();
        foreach (var diagnostic in diagnostics)
            builder.AddDiagnostic(diagnostic);
        plan = builder.Build();
        var report = new OperationReport("schema-build", _toolVersion, false, plan.Diagnostics, [], plan.PlanHash);
        return new SchemaBuildOutcome(report, schema, plan);
    }

    public async Task<MutationPlan> CreateTableAsync(
        PipelineProject project,
        TableCreateIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(intent);
        var diagnostics = new List<Diagnostic>();
        var current = await CompileCurrentSetOrEmpty(project, diagnostics, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            var failed = CreatePlanBuilder(project, "table-create", 0);
            ObserveSchemaInputs(failed, project);
            foreach (var diagnostic in diagnostics)
                failed.AddDiagnostic(diagnostic);
            return failed.Build();
        }
        var logicalFile = intent.ProtoFile ?? $"{intent.TableName}.proto";
        var mutation = new SchemaMutationEngine().CreateTable(
            current.DescriptorSet,
            new CreateTableMutationRequest
            {
                FileName = logicalFile.Replace('\\', '/'),
                Package = intent.Package,
                TableName = intent.TableName,
                Fields = intent.Fields,
                AutoKey = intent.AutoKey,
                TableId = intent.TableId,
                TableExportTargets = intent.TableTargets,
                FieldExportTargets = intent.FieldTargets
                    ?? ImmutableDictionary<string, ExportTargetSelection>.Empty.WithComparers(StringComparer.Ordinal),
            },
            _tableInitializer,
            _exportTargetStrategy);
        diagnostics.AddRange(mutation.Diagnostics.Select(ToDiagnostic));

        var builder = CreatePlanBuilder(project, "table-create", current.Descriptor?.SchemaHash ?? 0);
        ObserveSchemaInputs(builder, project);
        builder.AddDiagnostic(new Diagnostic("table.initializer", DiagnosticSeverity.Info, intent.TableName, $"Initializer '{_tableInitializer.Id}' materialized the table draft."));
        builder.AddDiagnostic(new Diagnostic("table.export-strategy", DiagnosticSeverity.Info, intent.TableName, $"Export target strategy '{_exportTargetStrategy.Id}' materialized automatic selections."));
        if (!mutation.Succeeded)
        {
            foreach (var diagnostic in diagnostics)
                builder.AddDiagnostic(diagnostic);
            return builder.Build();
        }

        var candidate = CompileCandidate(mutation.CandidateDescriptorSet!, diagnostics);
        if (candidate is null)
        {
            foreach (var diagnostic in diagnostics)
                builder.AddDiagnostic(diagnostic);
            return builder.Build();
        }

        AddCompatibilityDiagnostics(current.Descriptor, candidate.Descriptor, diagnostics);
        AddCandidateSchemaAndCode(builder, project, mutation, candidate);

        var workbookPath = project.Context.ResolvePath(intent.WorkbookPath);
        try
        {
            var workbookBytes = WorkbookProjection.CreateOrAddTable(
                workbookPath,
                candidate.Descriptor,
                mutation.AssignedTableId!.Value);
            WriteIfChanged(builder, workbookPath, workbookBytes);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic("workbook.generate", DiagnosticSeverity.Blocker, intent.WorkbookPath, exception.Message));
        }

        foreach (var diagnostic in diagnostics)
            builder.AddDiagnostic(diagnostic);
        builder.AddRisk($"table-id={mutation.AssignedTableId}");
        foreach (var pair in mutation.AssignedFieldNumbers.OrderBy(static pair => pair.Value))
            builder.AddRisk($"field={pair.Value}:{pair.Key}");
        return builder.Build();
    }

    public async Task<MutationPlan> EditTableAsync(
        PipelineProject project,
        TableEditIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(intent);
        var diagnostics = new List<Diagnostic>();
        var current = await CompileRequired(project, diagnostics, cancellationToken).ConfigureAwait(false);
        var builder = CreatePlanBuilder(project, intent.Retire ? "table-retire" : "table-edit", current?.Descriptor.SchemaHash ?? 0);
        ObserveSchemaInputs(builder, project);
        if (current is null)
        {
            foreach (var diagnostic in diagnostics)
                builder.AddDiagnostic(diagnostic);
            return builder.Build();
        }

        var engine = new SchemaMutationEngine();
        var mutation = intent.Retire
            ? engine.RetireTable(current.DescriptorSet, new RetireTableMutationRequest(intent.TableFullName))
            : engine.EditTable(
                current.DescriptorSet,
                new EditTableMutationRequest
                {
                    TableFullName = intent.TableFullName,
                    NewName = intent.NewName,
                    AddFields = intent.AddFields,
                    RenameFields = intent.RenameFields,
                    RemoveFieldNumbers = intent.RemoveFieldNumbers,
                    TableExportTargets = intent.TableTargets,
                    FieldExportTargets = intent.FieldTargets ?? ImmutableDictionary<int, ExportTargetSelection>.Empty,
                },
                _exportTargetStrategy);
        diagnostics.AddRange(mutation.Diagnostics.Select(ToDiagnostic));
        if (!mutation.Succeeded)
        {
            foreach (var diagnostic in diagnostics)
                builder.AddDiagnostic(diagnostic);
            return builder.Build();
        }

        var candidate = CompileCandidate(mutation.CandidateDescriptorSet!, diagnostics);
        if (candidate is not null)
        {
            AddCompatibilityDiagnostics(current.Descriptor, candidate.Descriptor, diagnostics);
            AddCandidateSchemaAndCode(builder, project, mutation, candidate);
            foreach (var workbook in ResolveWorkbooks(project, intent.WorkbookPath))
            {
                try
                {
                    var projected = WorkbookProjection.UpdateExistingTables(workbook, candidate.Descriptor);
                    if (projected is not null)
                        WriteIfChanged(builder, workbook, projected);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException)
                {
                    diagnostics.Add(new Diagnostic("workbook.generate", DiagnosticSeverity.Blocker, Relative(project, workbook), exception.Message));
                }
            }
        }

        foreach (var diagnostic in diagnostics)
            builder.AddDiagnostic(diagnostic);
        return builder.Build();
    }

    internal static Diagnostic ToDiagnostic(SchemaDiagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Severity switch
        {
            SchemaDiagnosticSeverity.Info => DiagnosticSeverity.Info,
            SchemaDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
            SchemaDiagnosticSeverity.Error => DiagnosticSeverity.Error,
            _ => DiagnosticSeverity.Blocker,
        },
        diagnostic.Location,
        diagnostic.Message);

    internal static IEnumerable<string> ResolveWorkbooks(PipelineProject project, string? explicitPath = null)
    {
        if (explicitPath is not null)
            return [project.Context.ResolvePath(explicitPath)];
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var glob in project.Context.Project.Workbooks)
        {
            var normalized = glob.Replace('/', Path.DirectorySeparatorChar);
            var directoryPart = Path.GetDirectoryName(normalized) ?? ".";
            var pattern = Path.GetFileName(normalized);
            var directory = project.Context.ResolvePath(directoryPart);
            if (!Directory.Exists(directory))
                continue;
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
                result.Add(Path.GetFullPath(file));
        }
        return result;
    }

    internal static string Relative(PipelineProject project, string path) =>
        Path.GetRelativePath(project.Context.RootDirectory, path).Replace('\\', '/');

    internal MutationPlanBuilder CreatePlanBuilder(PipelineProject project, string operation, ulong schemaHash) =>
        new MutationPlanBuilder(operation, _toolVersion, project.Context.RootDirectory, project.ConfigHash, schemaHash)
            .Observe(project.Context.ProjectFilePath);

    internal static void ObserveSchemaInputs(MutationPlanBuilder builder, PipelineProject project)
    {
        if (!Directory.Exists(project.Context.SchemaDirectory))
            return;
        foreach (var file in Directory.EnumerateFiles(project.Context.SchemaDirectory, "*.proto", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            builder.Observe(file);
    }

    internal static void WriteIfChanged(MutationPlanBuilder builder, string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            return;
        builder.WriteFile(path, content);
    }

    private async Task<CompiledProjectSchema?> CompileCurrentSetOrEmpty(
        PipelineProject project,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var sourceSet = SchemaSourceSet.Discover(project.Context.SchemaDirectory);
        if (sourceSet.Files.Count == 0)
            return new CompiledProjectSchema(new FileDescriptorSet(), [], null!, []);
        return await CompileAsync(project, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CompiledProjectSchema?> CompileRequired(PipelineProject project, ICollection<Diagnostic> diagnostics, CancellationToken cancellationToken) =>
        await CompileAsync(project, diagnostics, cancellationToken).ConfigureAwait(false);

    private static CompiledProjectSchema? CompileCandidate(FileDescriptorSet descriptorSet, ICollection<Diagnostic> diagnostics)
    {
        var compilation = new SchemaCompiler().CompileDescriptorSet(descriptorSet);
        foreach (var diagnostic in compilation.Diagnostics)
            diagnostics.Add(ToDiagnostic(diagnostic));
        return compilation.Succeeded
            ? new CompiledProjectSchema(descriptorSet, descriptorSet.ToByteArray(), compilation.Descriptor!, compilation.Diagnostics.Select(ToDiagnostic).ToImmutableArray())
            : null;
    }

    private void AddCandidateSchemaAndCode(
        MutationPlanBuilder builder,
        PipelineProject project,
        SchemaMutationResult mutation,
        CompiledProjectSchema candidate)
    {
        foreach (var proto in mutation.CandidateProtoFiles.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            WriteIfChanged(builder, Path.Combine(project.Context.SchemaDirectory, proto.Key.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8.GetBytes(proto.Value));
        WriteIfChanged(builder, DescriptorCachePath(project), candidate.DescriptorBytes);
        AddGeneratedArtifacts(builder, project, new SchemaCodeGenerator().Generate(candidate.Descriptor), checkOnly: false, diagnostics: null);
    }

    private static void AddGeneratedArtifacts(
        MutationPlanBuilder builder,
        PipelineProject project,
        SchemaCodeGenerationResult generation,
        bool checkOnly,
        ICollection<Diagnostic>? diagnostics)
    {
        var expectedFiles = generation.Artifacts
            .Select(artifact => Path.GetFullPath(Path.Combine(
                project.Context.GeneratedDirectory,
                artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var artifact in generation.Artifacts)
        {
            var target = Path.Combine(project.Context.GeneratedDirectory, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var content = Encoding.UTF8.GetBytes(artifact.Content);
            if (checkOnly)
            {
                if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(content))
                    diagnostics?.Add(new Diagnostic("codegen.stale", DiagnosticSeverity.Error, Relative(project, target), "Generated C# is stale; run schema build."));
            }
            else
            {
                WriteIfChanged(builder, target, content);
            }
        }

        foreach (var ghost in EnumerateOwnedGeneratedFiles(project.Context.GeneratedDirectory)
                     .Where(path => !expectedFiles.Contains(path)))
        {
            if (checkOnly)
            {
                diagnostics?.Add(new Diagnostic(
                    "codegen.ghost",
                    DiagnosticSeverity.Error,
                    Relative(project, ghost),
                    "Generated C# is not present in the current target manifest; run schema build to remove the stale runtime surface."));
            }
            else
            {
                builder.DeleteFile(ghost);
            }
        }

        var manifestPath = Path.Combine(project.Context.GeneratedDirectory, "codegen.manifest.json");
        var manifest = Encoding.UTF8.GetBytes(generation.Manifest.Json + "\n");
        if (checkOnly)
        {
            if (!File.Exists(manifestPath) || !File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(manifest))
                diagnostics?.Add(new Diagnostic("codegen.stale", DiagnosticSeverity.Error, Relative(project, manifestPath), "Codegen manifest is stale; run schema build."));
        }
        else
        {
            WriteIfChanged(builder, manifestPath, manifest);
        }
    }

    private static IEnumerable<string> EnumerateOwnedGeneratedFiles(string generatedDirectory)
    {
        foreach (var ownedDirectory in new[] { "authoring", "runtime" })
        {
            var root = Path.Combine(generatedDirectory, ownedDirectory);
            if (!Directory.Exists(root))
                continue;
            foreach (var path in Directory.EnumerateFiles(root, "*.g.cs", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal))
            {
                yield return Path.GetFullPath(path);
            }
        }
    }

    private static string DescriptorCachePath(PipelineProject project) => Path.Combine(project.Context.CacheDirectory, "schema", "descriptor.pb");

    private static void AddCompatibilityDiagnostics(PipelineProject project, CompiledProjectSchema current, ICollection<Diagnostic> diagnostics)
    {
        var history = PublishedSchemaHistoryStore.LoadLatest(project);
        foreach (var diagnostic in history.Diagnostics)
            diagnostics.Add(diagnostic);
        if (history.IsValid && history.Latest is not null)
            AddCompatibilityDiagnostics(history.Latest.Descriptor, current.Descriptor, diagnostics);
    }

    private static void AddCompatibilityDiagnostics(CanonicalSchemaDescriptor? previous, CanonicalSchemaDescriptor current, ICollection<Diagnostic> diagnostics)
    {
        if (previous is null)
            return;
        var report = new CompatibilityAnalyzer().Analyze(current, previous);
        foreach (var entry in report.Entries)
        {
            diagnostics.Add(new Diagnostic(
                $"compat.{entry.Kind.ToString().ToLowerInvariant()}",
                entry.Severity switch
                {
                    CompatibilitySeverity.Safe => DiagnosticSeverity.Info,
                    CompatibilitySeverity.Warning => DiagnosticSeverity.Warning,
                    CompatibilitySeverity.Error => DiagnosticSeverity.Error,
                    _ => DiagnosticSeverity.Blocker,
                },
                entry.Location.CurrentPath ?? entry.Location.PreviousPath ?? ".",
                entry.Message));
        }
    }

    private SchemaBuildOutcome FailedBuild(string operation, IEnumerable<Diagnostic> diagnostics)
    {
        var immutable = diagnostics.ToImmutableArray();
        return new SchemaBuildOutcome(new OperationReport(operation, _toolVersion, false, immutable, []), null, null);
    }
}
