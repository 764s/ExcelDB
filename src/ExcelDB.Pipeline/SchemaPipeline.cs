using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private readonly ISystemProtoCatalog _systemProtoCatalog;
    private readonly EmbeddedProtocCompiler _protocCompiler;

    public SchemaPipeline(
        string toolVersion,
        ITableInitializer? tableInitializer = null,
        IExportTargetStrategy? exportTargetStrategy = null,
        ISystemProtoCatalog? systemProtoCatalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        _toolVersion = toolVersion;
        _tableInitializer = tableInitializer ?? new DefaultTableInitializer();
        _exportTargetStrategy = exportTargetStrategy ?? new StandardClientServerExportTargetStrategy();
        _systemProtoCatalog = systemProtoCatalog ?? PackageSystemProtoCatalog.Default;
        _protocCompiler = new EmbeddedProtocCompiler(_systemProtoCatalog);
    }

    public ISystemProtoCatalog SystemProtoCatalog => _systemProtoCatalog;

    public async Task<CompiledProjectSchema?> CompileAsync(
        PipelineProject project,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(diagnostics);
        try
        {
            var schemaInputSet = InputSetSnapshot.Capture(
                project.Context.RootDirectory,
                project.Context.SchemaDirectory,
                InputSetKind.SchemaProto);
            var mirror = new SystemProtoMirror().Analyze(project.Context, _systemProtoCatalog, explicitRepair: false);
            foreach (var diagnostic in mirror.Diagnostics)
                diagnostics.Add(diagnostic);
            if (mirror.HasBlockers)
                return null;

            var sourceSet = SchemaSourceSet.Discover(
                project.Context.SchemaDirectory,
                _systemProtoCatalog,
                mirror.TrustedStaleFiles);
            EnsureSourceSetMatchesSnapshot(schemaInputSet, sourceSet);
            if (sourceSet.Files.Count == 0)
            {
                diagnostics.Add(new Diagnostic("XDB000", DiagnosticSeverity.Blocker, project.Context.Project.SchemaDir, "Schema source set is empty."));
                return null;
            }

            var protoc = await _protocCompiler.CompileAsync(sourceSet, cancellationToken).ConfigureAwait(false);
            if (!InputSetSnapshot.Matches(project.Context.RootDirectory, schemaInputSet))
                throw new InvalidDataException("Schema inputs changed while they were being compiled; create a fresh plan.");
            var compilation = new SchemaCompiler().CompileDescriptorSet(protoc.DescriptorSet);
            foreach (var diagnostic in compilation.Diagnostics)
                diagnostics.Add(ToDiagnostic(diagnostic));
            if (!compilation.Succeeded)
                return null;
            return new CompiledProjectSchema(
                protoc.DescriptorSet,
                protoc.DescriptorBytes.ToArray(),
                compilation.Descriptor!,
                compilation.Diagnostics.Select(ToDiagnostic).ToImmutableArray(),
                schemaInputSet,
                mirror.ReservedInputSet,
                mirror.ManifestObservation);
        }
        catch (ProtocCompilationException exception)
        {
            diagnostics.Add(new Diagnostic("XDB-PROTOC", DiagnosticSeverity.Blocker, project.Context.Project.SchemaDir, exception.Message));
            return null;
        }
        catch (SystemProtoMirrorException exception)
        {
            diagnostics.Add(new Diagnostic(
                exception.Problem == SystemProtoMirrorProblem.Modified
                    ? "system-import.modified"
                    : "system-import.unknown-reserved",
                DiagnosticSeverity.Blocker,
                exception.LogicalPath,
                exception.Message));
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

        var generation = new SchemaCodeGenerator().Generate(schema.Descriptor, _systemProtoCatalog.CatalogHash);
        AddCompatibilityDiagnostics(project, schema, diagnostics);

        var builder = CreatePlanBuilder(project, "schema-build", schema.Descriptor.SchemaHash);
        ObserveSchemaInputs(builder, project, schema);
        if (!checkOnly)
            AddSystemProtoMirrorMutations(builder, project, explicitRepair: false);
        AddGeneratedArtifacts(builder, project, generation, checkOnly, diagnostics);
        var descriptorPath = DescriptorCachePath(project);
        var descriptorFresh = File.Exists(descriptorPath)
                              && File.ReadAllBytes(descriptorPath).AsSpan().SequenceEqual(schema.DescriptorBytes);
        if (checkOnly && !descriptorFresh)
            diagnostics.Add(new Diagnostic("descriptor.stale", DiagnosticSeverity.Error, Relative(project, descriptorPath), "Descriptor cache is stale; run schema build."));
        else if (!checkOnly && !descriptorFresh)
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
        ObserveSchemaInputs(builder, project, current);
        ObserveExcelInputs(builder, project);
        AddSystemProtoMirrorMutations(builder, project, explicitRepair: false);
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

        try
        {
            var workbookPath = ResolveWorkbooks(project, intent.WorkbookPath).Single();
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
        ObserveSchemaInputs(builder, project, current);
        ObserveExcelInputs(builder, project);
        AddSystemProtoMirrorMutations(builder, project, explicitRepair: false);
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
        {
            var candidate = Path.IsPathFullyQualified(explicitPath)
                ? Path.GetFullPath(explicitPath)
                : explicitPath.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0
                    ? Path.GetFullPath(explicitPath, project.Context.ExcelDirectory)
                    : project.Context.ResolvePath(explicitPath);
            var relative = Path.GetRelativePath(project.Context.ExcelDirectory, candidate);
            if (Path.IsPathFullyQualified(relative)
                || relative == ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Workbook paths must remain inside Project v2 excelDir.");
            }
            if (Path.GetFileName(candidate).StartsWith("~$", StringComparison.Ordinal)
                || !string.Equals(Path.GetExtension(candidate), ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A workbook path must name a non-temporary .xlsx file.");
            }
            EnsureNoReparseTraversal(project.Context.ExcelDirectory, candidate);
            return [candidate];
        }
        return project.Context.EnumerateExcelFiles();
    }

    private static void EnsureNoReparseTraversal(string root, string candidate)
    {
        var current = Path.GetFullPath(root);
        if (Directory.Exists(current)
            && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("excelDir must not be a symbolic link, junction, or other reparse point.");
        }

        var relative = Path.GetRelativePath(current, Path.GetFullPath(candidate));
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Workbook paths must not traverse a symbolic link, junction, or other reparse point.");
        }
    }

    internal static string Relative(PipelineProject project, string path) =>
        Path.GetRelativePath(project.Context.RootDirectory, path).Replace('\\', '/');

    internal MutationPlanBuilder CreatePlanBuilder(PipelineProject project, string operation, ulong schemaHash) =>
        new MutationPlanBuilder(
                operation,
                _toolVersion,
                project.Context.RootDirectory,
                project.Context.GeneratedCSharpDirectory,
                project.ConfigHash,
                _systemProtoCatalog.CatalogHash,
                schemaHash)
            .Observe(project.Context.ProjectFilePath);

    internal void ObserveSchemaInputs(
        MutationPlanBuilder builder,
        PipelineProject project,
        CompiledProjectSchema? compiled = null)
    {
        if (!Directory.Exists(project.Context.SchemaDirectory))
            return;
        try
        {
            if (compiled?.SchemaInputSet is not null)
            {
                builder.ObserveSchemaInputSet(compiled.SchemaInputSet);
                if (compiled.SystemProtoMirrorInputSet is not null)
                    builder.ObserveSystemProtoMirrorSet(compiled.SystemProtoMirrorInputSet);
                if (compiled.SystemImportsManifestObservation is not null)
                    builder.Observe(compiled.SystemImportsManifestObservation);
                return;
            }

            builder.ObserveSchemaInputSet(project.Context.SchemaDirectory);
            var mirror = new SystemProtoMirror().Analyze(project.Context, _systemProtoCatalog, explicitRepair: false);
            if (mirror.ReservedInputSet is not null)
                builder.ObserveSystemProtoMirrorSet(mirror.ReservedInputSet);
            if (mirror.ManifestObservation is not null)
                builder.Observe(mirror.ManifestObservation);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            builder.AddDiagnostic(new Diagnostic(
                "schema.observe",
                DiagnosticSeverity.Blocker,
                project.Context.Project.SchemaDir,
                exception.Message));
        }
    }

    internal static InputSetObservation CaptureExcelInputs(PipelineProject project) =>
        InputSetSnapshot.Capture(
            project.Context.RootDirectory,
            project.Context.ExcelDirectory,
            InputSetKind.ExcelWorkbook);

    internal static void ObserveExcelInputs(
        MutationPlanBuilder builder,
        PipelineProject project,
        InputSetObservation? frozen = null) =>
        builder.ObserveExcelInputSet(frozen ?? CaptureExcelInputs(project));

    private static void EnsureSourceSetMatchesSnapshot(
        InputSetObservation snapshot,
        SchemaSourceSet sourceSet)
    {
        if (snapshot.Kind != InputSetKind.SchemaProto
            || snapshot.RootKind != ObservedPathKind.Directory)
        {
            throw new InvalidDataException("Schema input snapshot does not describe an existing directory.");
        }

        var expected = snapshot.Entries.ToDictionary(
            static entry => entry.RelativePath,
            StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in sourceSet.Files)
        {
            if (!expected.TryGetValue(file.LogicalPath, out var entry)
                || entry.Kind != ObservedPathKind.File
                || entry.Length != file.Length
                || !string.Equals(entry.Sha256, file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Schema input '{file.LogicalPath}' changed between snapshot and source discovery.");
            }
            seen.Add(file.LogicalPath);
        }
        if (seen.Count != expected.Count || expected.Keys.Any(path => !seen.Contains(path)))
            throw new InvalidDataException("The recursive business Schema *.proto member set changed during source discovery.");
    }

    internal static void WriteIfChanged(MutationPlanBuilder builder, string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            return;
        builder.WriteFile(path, content);
    }

    internal static void AddDescriptorCache(
        MutationPlanBuilder builder,
        PipelineProject project,
        CompiledProjectSchema schema) =>
        WriteIfChanged(builder, DescriptorCachePath(project), schema.DescriptorBytes);

    internal SystemProtoMirrorAnalysis AddSystemProtoMirrorMutations(
        MutationPlanBuilder builder,
        PipelineProject project,
        bool explicitRepair)
    {
        var analysis = new SystemProtoMirror().Analyze(project.Context, _systemProtoCatalog, explicitRepair);
        if (analysis.ReservedInputSet is not null)
            builder.ObserveSystemProtoMirrorSet(analysis.ReservedInputSet);
        if (analysis.ManifestObservation is not null)
            builder.Observe(analysis.ManifestObservation);
        if (analysis.HasBlockers)
            return analysis;
        foreach (var mutation in analysis.Mutations)
        {
            if (mutation.Kind == SystemProtoMirrorMutationKind.DeleteFile)
                builder.DeleteFile(mutation.AbsolutePath);
            else
                builder.WriteFile(mutation.AbsolutePath, mutation.Content!);
        }
        return analysis;
    }

    private static void WriteGeneratedIfChanged(
        MutationPlanBuilder builder,
        PipelineProject project,
        string logicalPath,
        string absolutePath,
        byte[] content)
    {
        if (File.Exists(absolutePath) && File.ReadAllBytes(absolutePath).AsSpan().SequenceEqual(content))
            return;
        builder.WriteFile(PlanRootKind.GeneratedCSharp, logicalPath, content);
    }

    private async Task<CompiledProjectSchema?> CompileCurrentSetOrEmpty(
        PipelineProject project,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var schemaInputSet = InputSetSnapshot.Capture(
            project.Context.RootDirectory,
            project.Context.SchemaDirectory,
            InputSetKind.SchemaProto);
        var mirror = new SystemProtoMirror().Analyze(project.Context, _systemProtoCatalog, explicitRepair: false);
        foreach (var diagnostic in mirror.Diagnostics)
            diagnostics.Add(diagnostic);
        if (mirror.HasBlockers)
            return null;
        var sourceSet = SchemaSourceSet.Discover(
            project.Context.SchemaDirectory,
            _systemProtoCatalog,
            mirror.TrustedStaleFiles);
        EnsureSourceSetMatchesSnapshot(schemaInputSet, sourceSet);
        if (sourceSet.Files.Count == 0)
            return new CompiledProjectSchema(
                new FileDescriptorSet(),
                [],
                null!,
                [],
                schemaInputSet,
                mirror.ReservedInputSet,
                mirror.ManifestObservation);
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
        AddGeneratedArtifacts(
            builder,
            project,
            new SchemaCodeGenerator().Generate(candidate.Descriptor, _systemProtoCatalog.CatalogHash),
            checkOnly: false,
            diagnostics: null);
    }

    internal void AddGeneratedArtifacts(
        MutationPlanBuilder builder,
        PipelineProject project,
        SchemaCodeGenerationResult generation,
        bool checkOnly,
        ICollection<Diagnostic>? diagnostics)
    {
        var ownership = ReadCodegenOwnership(project, builder, diagnostics);
        if (!ownership.Valid)
            return;

        var expectedPaths = generation.Artifacts
            .Select(static artifact => artifact.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownershipFailure = false;
        foreach (var owned in ownership.Files)
        {
            var path = ResolveGeneratedPath(project, owned.Key);
            if (!File.Exists(path))
                continue;
            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (string.Equals(actualHash, owned.Value, StringComparison.Ordinal))
                continue;
            AddCodegenDiagnostic(
                builder,
                diagnostics,
                new Diagnostic(
                    "codegen.owned-modified",
                    DiagnosticSeverity.Blocker,
                    owned.Key,
                    "A file recorded by codegen.manifest.json was modified after generation; ExcelDB will not overwrite or delete it."));
            ownershipFailure = true;
        }

        foreach (var artifact in generation.Artifacts)
        {
            var target = ResolveGeneratedPath(project, artifact.RelativePath);
            var content = Encoding.UTF8.GetBytes(artifact.Content);
            if (File.Exists(target) && !ownership.Files.ContainsKey(artifact.RelativePath))
            {
                AddCodegenDiagnostic(
                    builder,
                    diagnostics,
                    new Diagnostic(
                        "codegen.path-collision",
                        DiagnosticSeverity.Blocker,
                        artifact.RelativePath,
                        "The generated target path contains an unknown file not owned by codegen.manifest.json."));
                ownershipFailure = true;
                continue;
            }
            if (checkOnly)
            {
                if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(content))
                    diagnostics?.Add(new Diagnostic("codegen.stale", DiagnosticSeverity.Error, Relative(project, target), "Generated C# is stale; run schema build."));
            }
            else if (!ownershipFailure)
                WriteGeneratedIfChanged(builder, project, artifact.RelativePath, target, content);
        }

        if (ownershipFailure)
            return;

        foreach (var ghost in ownership.Files.Where(pair => !expectedPaths.Contains(pair.Key)))
        {
            var ghostPath = ResolveGeneratedPath(project, ghost.Key);
            if (!File.Exists(ghostPath))
                continue;
            if (checkOnly)
            {
                diagnostics?.Add(new Diagnostic(
                    "codegen.ghost",
                    DiagnosticSeverity.Error,
                    ghost.Key,
                    "Generated C# is not present in the current target manifest; run schema build to remove the stale runtime surface."));
            }
            else
            {
                builder.DeleteFile(PlanRootKind.GeneratedCSharp, ghost.Key);
            }
        }

        var manifestPath = Path.Combine(project.Context.GeneratedCSharpDirectory, "codegen.manifest.json");
        var manifest = Encoding.UTF8.GetBytes(generation.Manifest.Json + "\n");
        if (checkOnly)
        {
            if (!File.Exists(manifestPath) || !File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(manifest))
                diagnostics?.Add(new Diagnostic("codegen.stale", DiagnosticSeverity.Error, Relative(project, manifestPath), "Codegen manifest is stale; run schema build."));
        }
        else
        {
            WriteGeneratedIfChanged(builder, project, "codegen.manifest.json", manifestPath, manifest);
        }
    }

    private static CodegenOwnership ReadCodegenOwnership(
        PipelineProject project,
        MutationPlanBuilder builder,
        ICollection<Diagnostic>? diagnostics)
    {
        var manifestPath = Path.Combine(project.Context.GeneratedCSharpDirectory, "codegen.manifest.json");
        if (!File.Exists(manifestPath))
            return new CodegenOwnership(true, ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != "exceldb.codegen-manifest.v1")
                throw new InvalidDataException("Unsupported codegen manifest format.");
            var files = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artifact in root.GetProperty("artifacts").EnumerateArray())
            {
                var logicalPath = artifact.GetProperty("path").GetString()
                    ?? throw new InvalidDataException("Codegen manifest artifact path is missing.");
                _ = ResolveGeneratedPath(project, logicalPath);
                var hash = artifact.GetProperty("sha256").GetString();
                if (hash is null || hash.Length != 64 || hash.Any(static character => !Uri.IsHexDigit(character)))
                    throw new InvalidDataException($"Codegen manifest hash is invalid for '{logicalPath}'.");
                if (!files.TryAdd(logicalPath, hash.ToLowerInvariant()))
                    throw new InvalidDataException($"Codegen manifest contains duplicate path '{logicalPath}'.");
            }
            return new CodegenOwnership(true, files.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or JsonException
                                           or KeyNotFoundException
                                           or InvalidOperationException)
        {
            AddCodegenDiagnostic(
                builder,
                diagnostics,
                new Diagnostic(
                    "codegen.manifest-invalid",
                    DiagnosticSeverity.Blocker,
                    "codegen.manifest.json",
                    exception.Message));
            return new CodegenOwnership(false, ImmutableDictionary<string, string>.Empty);
        }
    }

    private static string ResolveGeneratedPath(PipelineProject project, string logicalPath)
    {
        if (string.IsNullOrWhiteSpace(logicalPath)
            || logicalPath.IndexOf('\\') >= 0
            || Path.IsPathFullyQualified(logicalPath)
            || logicalPath.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException($"Generated path '{logicalPath}' is not canonical.");
        }
        var path = Path.GetFullPath(logicalPath.Replace('/', Path.DirectorySeparatorChar), project.Context.GeneratedCSharpDirectory);
        var relative = Path.GetRelativePath(project.Context.GeneratedCSharpDirectory, path);
        if (Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Generated path '{logicalPath}' escapes generatedCSharpDir.");
        }
        return path;
    }

    private static void AddCodegenDiagnostic(
        MutationPlanBuilder builder,
        ICollection<Diagnostic>? diagnostics,
        Diagnostic diagnostic)
    {
        if (diagnostics is null)
            builder.AddDiagnostic(diagnostic);
        else
            diagnostics.Add(diagnostic);
    }

    private sealed record CodegenOwnership(
        bool Valid,
        ImmutableDictionary<string, string> Files);

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
