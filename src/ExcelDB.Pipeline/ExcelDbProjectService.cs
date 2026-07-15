using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Core.Values;
using ExcelDb.Runtime;
using ExcelDb.Runtime.Bytes;
using ExcelDb.Schema.Authoring;
using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Generation;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;
using ExcelDb.Workbooks.References;

namespace ExcelDb.Pipeline;

public sealed class ExcelDbProjectService : IExcelDbProjectService
{
    private readonly string _toolVersion;
    private readonly SchemaPipeline _schemas;
    private readonly CellFormatRegistry _cellFormats;
    private readonly WorkbookValidatorRegistry _validators;
    private readonly ISystemProtoCatalog _systemProtoCatalog;

    public ExcelDbProjectService(
        string toolVersion,
        ITableInitializer? tableInitializer = null,
        IExportTargetStrategy? exportTargetStrategy = null,
        CellFormatRegistry? cellFormats = null,
        IEnumerable<IWorkbookValidator>? workbookValidators = null,
        ISystemProtoCatalog? systemProtoCatalog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        _toolVersion = toolVersion;
        _cellFormats = cellFormats ?? new CellFormatRegistry();
        _validators = new WorkbookValidatorRegistry(workbookValidators ?? []);
        _systemProtoCatalog = systemProtoCatalog ?? PackageSystemProtoCatalog.Default;
        _schemas = new SchemaPipeline(
            toolVersion,
            tableInitializer,
            exportTargetStrategy,
            _systemProtoCatalog);
    }

    public ProjectInspection Inspect(ProjectLocation project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var root = ResolveProjectRootPath(project.Path);
        try
        {
            ProjectPathSafety.EnsureProjectRootAndInternalDirectoryArePlain(root);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return UninitializedInspection(
                root,
                ProjectStatus.Invalid,
                [new Diagnostic("project.path-unsafe", DiagnosticSeverity.Blocker, root, exception.Message)],
                includeDiagnosticLocations: false);
        }
        var projectFile = Path.Combine(root, ExcelDbProject.RelativeFilePath);
        var legacyFile = Path.Combine(root, ExcelDbProject.LegacyFileName);
        if (File.Exists(legacyFile))
        {
            var message = File.Exists(projectFile)
                ? "Legacy Project v1 exists beside Project v2. Dual configuration is blocked; remove the ambiguity and reinitialize or arrange one Project v2 source explicitly."
                : "Legacy Project v1 is not migrated automatically. Choose an empty directory and initialize Project v2, or arrange the four v2 directories manually.";
            return UninitializedInspection(
                root,
                ProjectStatus.Legacy,
                [new Diagnostic("project.legacy-v1", DiagnosticSeverity.Blocker, ExcelDbProject.LegacyFileName, message)]);
        }

        var recoveryDiagnostics = ImmutableArray<Diagnostic>.Empty;
        var recoveryDiagnostic = InspectRecoveryState(root, out var recoveryPending);
        if (recoveryDiagnostic is not null)
        {
            return UninitializedInspection(
                root,
                ProjectStatus.Invalid,
                [recoveryDiagnostic],
                File.Exists(projectFile) ? projectFile : null);
        }
        if (recoveryPending)
        {
            var recovery = ProjectRecovery.RecoverPending(root);
            recoveryDiagnostics = recovery.Diagnostics;
            recoveryDiagnostic = InspectRecoveryState(root, out recoveryPending);
            if (recoveryDiagnostic is not null)
            {
                return UninitializedInspection(
                    root,
                    ProjectStatus.Invalid,
                    recoveryDiagnostics.Add(recoveryDiagnostic),
                    File.Exists(projectFile) ? projectFile : null);
            }
            if (!recovery.Succeeded || recoveryPending)
            {
                return UninitializedInspection(
                    root,
                    ProjectStatus.RecoveryRequired,
                    recoveryDiagnostics.Add(new Diagnostic(
                        "project.recovery-required",
                        DiagnosticSeverity.Blocker,
                        ".exceldb/recovery",
                        "A previous cross-root operation could not be recovered automatically. Open the recovery directory and resolve the reported blocker before continuing.")),
                    File.Exists(projectFile) ? projectFile : null);
            }
        }
        if (!File.Exists(projectFile))
            return UninitializedInspection(root, ProjectStatus.Uninitialized, []);

        ProjectContext context;
        ExcelDbProject projectConfiguration;
        try
        {
            projectConfiguration = ExcelDbProject.Load(projectFile);
            context = projectConfiguration.Resolve(projectFile);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or System.Text.Json.JsonException)
        {
            return UninitializedInspection(
                root,
                ProjectStatus.Invalid,
                [new Diagnostic("project.invalid", DiagnosticSeverity.Blocker, ExcelDbProject.RelativeFilePath, exception.Message)],
                projectFile);
        }

        var pipelineProject = new PipelineProject(
            context,
            ContentFingerprint.FromBytes(projectConfiguration.ToCanonicalJson()).Sha256);
        var diagnostics = new List<Diagnostic>(recoveryDiagnostics);
        SystemProtoMirrorAnalysis mirror;
        try
        {
            mirror = new SystemProtoMirror().Analyze(context, _systemProtoCatalog, explicitRepair: false);
            diagnostics.AddRange(mirror.Diagnostics);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or System.Text.Json.JsonException)
        {
            var diagnostic = new Diagnostic(
                "system-import.inspect",
                DiagnosticSeverity.Blocker,
                context.Project.SchemaDir,
                exception.Message);
            diagnostics.Add(diagnostic);
            mirror = new SystemProtoMirrorAnalysis(
                SystemProtoMirrorStatus.Conflicted,
                [],
                [diagnostic],
                ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal));
        }

        var sourceCount = 0;
        var sourceDiscoverySucceeded = false;
        try
        {
            sourceCount = SchemaSourceSet.Discover(
                context.SchemaDirectory,
                _systemProtoCatalog,
                mirror.TrustedStaleFiles).Files.Count;
            sourceDiscoverySucceeded = true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic(
                "schema.inspect",
                DiagnosticSeverity.Blocker,
                context.Project.SchemaDir,
                exception.Message));
        }

        ImmutableArray<string> workbookPaths;
        try
        {
            workbookPaths = context.EnumerateExcelFiles();
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic(
                "project.excel-invalid",
                DiagnosticSeverity.Blocker,
                context.Project.ExcelDir,
                exception.Message));
            workbookPaths = [];
        }

        WorkbookCheckOutcome? check = null;
        try
        {
            check = new WorkbookPipeline(_toolVersion, _schemas, _cellFormats, _validators)
                .CheckAsync(pipelineProject, pendingIsBlocker: false)
                .GetAwaiter()
                .GetResult();
            diagnostics.AddRange(check.Report.Diagnostics);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic(
                "project.inspect",
                DiagnosticSeverity.Blocker,
                context.RootDirectory,
                exception.Message));
        }

        var schema = check?.Schema?.Descriptor;
        var tableCount = schema?.Tables.Count(static table => table.Kind == CanonicalTableKind.Asset) ?? 0;
        var hasExportTargets = schema?.Tables
            .Where(static table => table.Kind == CanonicalTableKind.Asset)
            .SelectMany(static table => table.ExportTargets)
            .Any() == true;
        var pendingIdentityCount = check?.PendingIdentityCount ?? 0;
        var codegenStaleCount = diagnostics
            .Where(static diagnostic => string.Equals(diagnostic.Code, "codegen.stale", StringComparison.Ordinal))
            .Select(static diagnostic => diagnostic.Location)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var hasCodegenStale = codegenStaleCount != 0;
        var hasMissingWorkbook = schema is not null
                                 && tableCount != 0
                                 && (workbookPaths.IsEmpty
                                     || diagnostics.Any(static diagnostic =>
                                         string.Equals(diagnostic.Code, "workbook.missing", StringComparison.Ordinal)));
        var hasDataErrors = diagnostics.Any(IsDataFailure);

        IReadOnlyDictionary<string, ConvertedBytesPackage> expectedPackages =
            ImmutableDictionary<string, ConvertedBytesPackage>.Empty;
        if (schema is not null
            && tableCount != 0
            && !hasMissingWorkbook
            && pendingIdentityCount == 0
            && !hasDataErrors)
        {
            expectedPackages = BuildExpectedBytePackages(pipelineProject, schema, workbookPaths, diagnostics);
            hasDataErrors = diagnostics.Any(IsDataFailure);
            if (hasDataErrors)
                expectedPackages = ImmutableDictionary<string, ConvertedBytesPackage>.Empty;
        }

        var byteTargets = InspectGeneratedBytes(
            context,
            schema,
            expectedPackages,
            diagnostics);
        var byteTargetsCurrent = byteTargets.All(static target =>
            target.Status == ProjectTargetArtifactStatus.Current);
        var status = DetermineStatus(
            sourceDiscoverySucceeded,
            sourceCount,
            schema,
            tableCount,
            mirror,
            hasCodegenStale,
            hasMissingWorkbook,
            hasDataErrors,
            pendingIdentityCount,
            byteTargetsCurrent);

        var schemaReady = schema is not null && tableCount != 0 && !mirror.HasBlockers;
        var schemaCanBeMutated = sourceDiscoverySucceeded
                                 && !mirror.HasBlockers
                                 && (sourceCount == 0 || schema is not null);
        var hasWorkbooks = !workbookPaths.IsEmpty;
        var prepareAllowed = schemaReady
                             && hasWorkbooks
                             && !hasCodegenStale
                             && !hasDataErrors;
        var convertAllowed = prepareAllowed && pendingIdentityCount == 0 && hasExportTargets;
        var mirrorRepair = mirror.Status != SystemProtoMirrorStatus.Current;
        var actions = new ProjectActions(
            Disabled("Project v2 is already initialized."),
            Available(schemaCanBeMutated, "Fix the invalid schema or Proto dependency conflict before creating a table."),
            Available(schemaReady, "Create or fix a table before editing one."),
            Available(schemaReady, "Create or fix a table before regenerating projections."),
            Available(prepareAllowed, PrepareDisabledReason(schemaReady, hasWorkbooks, hasCodegenStale, hasDataErrors)),
            Available(schemaReady && hasWorkbooks, "A valid schema and at least one Excel workbook are required."),
            Available(convertAllowed, ConvertDisabledReason(
                schemaReady,
                hasWorkbooks,
                hasCodegenStale,
                hasDataErrors,
                pendingIdentityCount,
                hasExportTargets)),
            Available(mirrorRepair, mirrorRepair ? null : "System Proto dependencies are current."),
            Available(true, null));

        var generatedCount = CountFiles(context.GeneratedCSharpDirectory, "*.cs");
        var schemaCardStatus = SchemaStatusText(
            sourceDiscoverySucceeded,
            sourceCount,
            schema,
            tableCount,
            mirror);
        var excelCardStatus = ExcelStatusText(
            tableCount,
            workbookPaths.Length,
            hasDataErrors,
            pendingIdentityCount);
        var generatedCSharpStatus = schema is null || tableCount == 0
            ? "Waiting for a valid table schema"
            : hasCodegenStale
                ? $"Regeneration required: {codegenStaleCount} missing or modified item(s)"
                : $"Current: {generatedCount} generated C# file(s)";
        var generatedBytesStatus = GeneratedBytesStatusText(tableCount, hasExportTargets, byteTargets);
        var orderedDiagnostics = OrderAndDeduplicateDiagnostics(diagnostics);

        return new ProjectInspection(
            context.RootDirectory,
            context.ProjectFilePath,
            status,
            new ProjectArtifactInspection("Schema", context.SchemaDirectory, schemaCardStatus, tableCount),
            new ProjectArtifactInspection("Excel", context.ExcelDirectory, excelCardStatus, workbookPaths.Length),
            new ProjectArtifactInspection(
                "Generated C#",
                context.GeneratedCSharpDirectory,
                generatedCSharpStatus,
                generatedCount,
                context.HasExternalGeneratedCSharpDirectory),
            new ProjectArtifactInspection(
                "Generated Bytes",
                context.GeneratedBytesDirectory,
                generatedBytesStatus,
                byteTargets.Length),
            actions,
            orderedDiagnostics,
            LatestFile(context.ReportsDirectory, "*.json"),
            context.RecoveryDirectory,
            context.InternalDirectory)
        {
            PendingIdentityCount = pendingIdentityCount,
            GeneratedByteTargets = byteTargets,
        };
    }

    public OperationReport RepairProjectOwnedAttributes(ProjectLocation project)
    {
        ArgumentNullException.ThrowIfNull(project);
        try
        {
            var root = ResolveProjectRoot(project.Path);
            if (File.Exists(Path.Combine(root, ExcelDbProject.LegacyFileName)))
            {
                throw new InvalidDataException(
                    $"Legacy project configuration '{ExcelDbProject.LegacyFileName}' exists beside Project v2; attributes were not changed.");
            }
            var projectFile = Path.Combine(root, ExcelDbProject.RelativeFilePath);
            var context = ExcelDbProject.Load(projectFile).Resolve(projectFile);
            var diagnostics = ProjectOwnedAttributes.Repair(
                context,
                _systemProtoCatalog.Files.Select(static file => file.LogicalPath));
            return new OperationReport(
                "project-repair-attributes",
                _toolVersion,
                OperatingSystem.IsWindows(),
                diagnostics,
                []);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException
                                           or System.Text.Json.JsonException)
        {
            return new OperationReport(
                "project-repair-attributes",
                _toolVersion,
                false,
                [new Diagnostic(
                    "project.attribute-warning",
                    DiagnosticSeverity.Warning,
                    project.Path,
                    $"Tool-owned presentation attributes were not changed: {exception.Message}")],
                []);
        }
    }

    public MutationPlan PlanInitialize(InitializeProjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = ResolveProjectRoot(request.Project.Path);
        var initializer = new ProjectInitializer(_toolVersion, _systemProtoCatalog.CatalogHash);
        var legacyPath = Path.Combine(root, ExcelDbProject.LegacyFileName);
        if (!File.Exists(legacyPath) && !Directory.Exists(legacyPath))
            EnsureRecovered(root);
        return initializer.Plan(
            root,
            request.GeneratedCSharpDirectory,
            (builder, context) =>
            {
                var mirror = new SystemProtoMirror().Analyze(context, _systemProtoCatalog, explicitRepair: false);
                if (mirror.ReservedInputSet is not null)
                    builder.ObserveSystemProtoMirrorSet(mirror.ReservedInputSet);
                if (mirror.ManifestObservation is not null)
                    builder.Observe(mirror.ManifestObservation);
                foreach (var diagnostic in mirror.Diagnostics)
                    builder.AddDiagnostic(diagnostic);
                if (mirror.HasBlockers)
                    return;
                AddMirrorMutations(builder, mirror);
            });
    }

    public MutationPlan PlanConfigure(ConfigureProjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = Load(request.Project);
        var project = new ExcelDbProject(
            ExcelDbProject.CurrentFormatVersion,
            NormalizeContainedDirectory(current.Context, request.SchemaDirectory, current.Context.Project.SchemaDir, nameof(request.SchemaDirectory)),
            NormalizeContainedDirectory(current.Context, request.ExcelDirectory, current.Context.Project.ExcelDir, nameof(request.ExcelDirectory)),
            NormalizeGeneratedCSharpDirectory(current.Context, request.GeneratedCSharpDirectory),
            NormalizeContainedDirectory(current.Context, request.GeneratedBytesDirectory, current.Context.Project.GeneratedBytesDir, nameof(request.GeneratedBytesDirectory)));
        var candidate = project.Resolve(current.Context.ProjectFilePath);
        var builder = new MutationPlanBuilder(
                "project-configure",
                _toolVersion,
                current.Context.RootDirectory,
                candidate.GeneratedCSharpDirectory,
                current.ConfigHash,
                _systemProtoCatalog.CatalogHash,
                schemaHash: 0)
            .Observe(current.Context.ProjectFilePath);
        PlanDirectory(builder, PlanRootKind.Project, current.Context.RootDirectory, candidate.SchemaDirectory, "schemaDir");
        PlanDirectory(builder, PlanRootKind.Project, current.Context.RootDirectory, candidate.ExcelDirectory, "excelDir");
        PlanDirectory(builder, PlanRootKind.Project, current.Context.RootDirectory, candidate.GeneratedBytesDirectory, "generatedBytesDir");
        PlanDirectory(builder, PlanRootKind.GeneratedCSharp, candidate.GeneratedCSharpDirectory, candidate.GeneratedCSharpDirectory, "generatedCSharpDir");
        PlanSchemaGitIgnore(builder, current.Context.RootDirectory, candidate.SchemaDirectory);

        var candidateProject = new PipelineProject(
            candidate,
            ContentFingerprint.FromBytes(project.ToCanonicalJson()).Sha256);
        var mirror = _schemas.AddSystemProtoMirrorMutations(builder, candidateProject, explicitRepair: false);
        foreach (var diagnostic in mirror.Diagnostics)
            builder.AddDiagnostic(diagnostic);
        var bytes = project.ToCanonicalJson();
        if (!File.ReadAllBytes(current.Context.ProjectFilePath).AsSpan().SequenceEqual(bytes))
            builder.WriteFile(current.Context.ProjectFilePath, bytes);
        if (candidate.HasExternalGeneratedCSharpDirectory)
            builder.AddRisk($"External Generated C# root: {candidate.GeneratedCSharpDirectory}");
        return builder.Build();
    }

    private static void PlanDirectory(
        MutationPlanBuilder builder,
        PlanRootKind rootKind,
        string declaredRoot,
        string directory,
        string settingName)
    {
        var relative = rootKind == PlanRootKind.GeneratedCSharp
            ? "."
            : Path.GetRelativePath(declaredRoot, directory).Replace('\\', '/');
        builder.Observe(rootKind, relative);
        if (File.Exists(directory))
        {
            builder.AddDiagnostic(new Diagnostic(
                "project.path-collision",
                DiagnosticSeverity.Blocker,
                settingName,
                $"The configured directory path is occupied by a file: '{directory}'."));
            return;
        }
        if (Directory.Exists(directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                builder.AddDiagnostic(new Diagnostic(
                    "project.path-reparse",
                    DiagnosticSeverity.Blocker,
                    settingName,
                    $"The configured directory must not be a symbolic link, junction, or reparse point: '{directory}'."));
            }
            return;
        }
        builder.CreateDirectory(rootKind, relative);
    }

    private static void PlanSchemaGitIgnore(
        MutationPlanBuilder builder,
        string projectRoot,
        string schemaDirectory)
    {
        var path = Path.Combine(schemaDirectory, ".gitignore");
        var relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');
        builder.Observe(path);
        try
        {
            var existing = File.Exists(path)
                ? new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path))
                : string.Empty;
            var lines = existing.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(static line => line.Trim())
                .ToHashSet(StringComparer.Ordinal);
            var missing = new[] { "/exceldb/", "/google/protobuf/" }
                .Where(entry => !lines.Contains(entry))
                .ToArray();
            if (missing.Length == 0)
                return;
            var updated = existing;
            if (updated.Length != 0 && !updated.EndsWith('\n'))
                updated += "\n";
            updated += string.Join("\n", missing) + "\n";
            builder.WriteFile(path, new UTF8Encoding(false, true).GetBytes(updated));
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or DecoderFallbackException
                                           or InvalidDataException)
        {
            builder.AddDiagnostic(new Diagnostic(
                "project.gitignore-invalid",
                DiagnosticSeverity.Blocker,
                relative,
                exception.Message));
        }
    }

    public Task<MutationPlan> PlanCreateTableAsync(CreateTableRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _schemas.CreateTableAsync(Load(request.Project), request.Intent, cancellationToken);
    }

    public Task<MutationPlan> PlanEditTableAsync(EditTableRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _schemas.EditTableAsync(Load(request.Project), request.Intent, cancellationToken);
    }

    public Task<MutationPlan> PlanRegenerateAsync(RegenerateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = Load(request.Project);
        return new WorkbookPipeline(_toolVersion, _schemas, _cellFormats, _validators)
            .GenerateAsync(project, cancellationToken: cancellationToken);
    }

    public Task<MutationPlan> PlanDataPrepareAsync(DataPrepareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = Load(request.Project);
        return new WorkbookPipeline(_toolVersion, _schemas, _cellFormats, _validators)
            .DataPrepareAsync(project, request.WorkbookPath, cancellationToken);
    }

    public MutationPlan PlanRepairSystemImports(RepairSystemImportsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = Load(request.Project);
        var builder = _schemas.CreatePlanBuilder(project, "project-repair-imports", schemaHash: 0);
        var analysis = new SystemProtoMirror().Analyze(project.Context, _systemProtoCatalog, request.ExplicitRepair);
        if (analysis.ReservedInputSet is not null)
            builder.ObserveSystemProtoMirrorSet(analysis.ReservedInputSet);
        if (analysis.ManifestObservation is not null)
            builder.Observe(analysis.ManifestObservation);
        foreach (var diagnostic in analysis.Diagnostics)
            builder.AddDiagnostic(diagnostic);
        if (!analysis.HasBlockers)
            AddMirrorMutations(builder, analysis);
        return builder.Build();
    }

    public async Task<OperationReport> CheckAsync(CheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = Load(request.Project);
        var outcome = await new WorkbookPipeline(_toolVersion, _schemas, _cellFormats, _validators)
            .CheckAsync(project, request.WorkbookPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return OperationReportStore.TryPersist(
            project.Context,
            outcome.Report with { ToolVersion = _toolVersion });
    }

    public async Task<OperationReport> ConvertAsync(ConvertRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = Load(request.Project);
        var output = request.OutputPath ?? WorkbookPipeline.DefaultOutputPath(project, request.Target);
        var report = await new WorkbookPipeline(_toolVersion, _schemas, _cellFormats, _validators)
            .ConvertAsync(project, request.Target, output, request.WorkbookPath, cancellationToken)
            .ConfigureAwait(false);
        return OperationReportStore.TryPersist(project.Context, report);
    }

    public OperationReport CleanCache(ProjectLocation project)
    {
        var loaded = Load(project);
        var recovery = ProjectRecovery.RecoverPending(loaded.Context.RootDirectory);
        if (!recovery.Succeeded)
            return recovery with { Operation = "project-clean-cache", ToolVersion = _toolVersion };
        try
        {
            if (Directory.Exists(loaded.Context.CacheDirectory))
            {
                EnsureTreeContainsNoReparsePoints(loaded.Context.CacheDirectory, "cache");
                Directory.Delete(loaded.Context.CacheDirectory, recursive: true);
            }
            Directory.CreateDirectory(loaded.Context.CacheDirectory);
            return OperationReportStore.TryPersist(loaded.Context, new OperationReport(
                "project-clean-cache",
                _toolVersion,
                true,
                [new Diagnostic("project.cache-cleaned", DiagnosticSeverity.Info, ".exceldb/cache", "Local cache was cleared; user artifacts were not changed.")],
                []));
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            return OperationReportStore.TryPersist(loaded.Context, new OperationReport(
                "project-clean-cache",
                _toolVersion,
                false,
                [new Diagnostic("project.cache-clean-failed", DiagnosticSeverity.Blocker, ".exceldb/cache", exception.Message)],
                []));
        }
    }

    public OperationReport Apply(MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var authorityFailure = ValidatePlanAuthority(plan);
        if (authorityFailure is not null)
        {
            var rejected = new OperationReport(
                string.IsNullOrWhiteSpace(plan.Operation) ? "apply-plan" : plan.Operation,
                _toolVersion,
                false,
                [authorityFailure],
                [],
                plan.PlanHash);
            try
            {
                return string.IsNullOrWhiteSpace(plan.ProjectRoot)
                    ? rejected
                    : PersistForExistingProject(plan.ProjectRoot, rejected);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or ArgumentException
                                               or NotSupportedException)
            {
                return rejected;
            }
        }

        var report = ProjectRecovery.Apply(plan);
        var projectFile = Path.Combine(plan.ProjectRoot, ExcelDbProject.RelativeFilePath);
        if (!File.Exists(projectFile))
            return report;
        try
        {
            var context = ExcelDbProject.Load(projectFile).Resolve(projectFile);
            if (report.Succeeded)
            {
                var warnings = ProjectOwnedAttributes.Repair(
                    context,
                    _systemProtoCatalog.Files.Select(static file => file.LogicalPath));
                if (!warnings.IsEmpty)
                    report = report with { Diagnostics = report.Diagnostics.AddRange(warnings) };
            }
            return OperationReportStore.TryPersist(context, report);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or System.Text.Json.JsonException)
        {
            return report with
            {
                Diagnostics = report.Diagnostics.Add(new Diagnostic(
                    "project.attribute-warning",
                    DiagnosticSeverity.Warning,
                    ExcelDbProject.RelativeFilePath,
                    $"Project files were committed, but tool-owned attributes could not be repaired: {exception.Message}")),
            };
        }
    }

    private Diagnostic? ValidatePlanAuthority(MutationPlan plan)
    {
        if (!MutationPlanCodec.Validate(plan))
        {
            return new Diagnostic(
                "plan.invalid",
                DiagnosticSeverity.Blocker,
                plan.ProjectRoot,
                "The frozen MutationPlan is not canonical or its planHash is invalid.");
        }
        if (!string.Equals(plan.SystemCatalogHash, _systemProtoCatalog.CatalogHash, StringComparison.Ordinal))
        {
            return new Diagnostic(
                "plan.catalog-stale",
                DiagnosticSeverity.Blocker,
                ".exceldb/project.json",
                "The frozen MutationPlan was created for a different system Proto catalog; create a new plan with this executable.");
        }

        try
        {
            var root = Path.GetFullPath(plan.ProjectRoot);
            ProjectPathSafety.EnsureProjectRootAndInternalDirectoryArePlain(root);
            var projectFile = Path.Combine(root, ExcelDbProject.RelativeFilePath);
            var configObservations = plan.Observations
                .Where(static observation =>
                    observation.Root == PlanRootKind.Project
                    && IsProjectConfigPath(observation.RelativePath))
                .ToArray();
            if (configObservations.Length != 1)
            {
                return new Diagnostic(
                    "plan.config-unobserved",
                    DiagnosticSeverity.Blocker,
                    ExcelDbProject.RelativeFilePath,
                    "A frozen MutationPlan must contain exactly one observation of the Project v2 configuration.");
            }

            var observation = configObservations[0];
            var configMutations = plan.Mutations
                .Where(static mutation =>
                    mutation.Root == PlanRootKind.Project
                    && IsProjectConfigPath(mutation.RelativePath))
                .ToArray();
            if (File.Exists(projectFile))
            {
                var bytes = File.ReadAllBytes(projectFile);
                var project = ExcelDbProject.Parse(bytes);
                var currentContext = project.Resolve(projectFile);
                var rawHash = ContentFingerprint.FromBytes(bytes).Sha256;
                var canonicalHash = ContentFingerprint.FromBytes(project.ToCanonicalJson()).Sha256;
                if (observation.Kind != ObservedPathKind.File
                    || observation.Length != bytes.LongLength
                    || !string.Equals(observation.Sha256, rawHash, StringComparison.Ordinal))
                {
                    return new Diagnostic(
                        "plan.config-stale",
                        DiagnosticSeverity.Blocker,
                        ExcelDbProject.RelativeFilePath,
                        "The exact Project v2 configuration observed by the frozen MutationPlan no longer matches the live file; create a new plan.");
                }
                if (!string.Equals(plan.ProjectConfigHash, rawHash, StringComparison.Ordinal)
                    && !string.Equals(plan.ProjectConfigHash, canonicalHash, StringComparison.Ordinal))
                {
                    return new Diagnostic(
                        "plan.config-stale",
                        DiagnosticSeverity.Blocker,
                        ExcelDbProject.RelativeFilePath,
                        "The Project v2 configuration changed after this MutationPlan was created; create a new plan.");
                }

                if (configMutations.Length == 0)
                {
                    return PathsEqual(currentContext.GeneratedCSharpDirectory, plan.GetRoot(PlanRootKind.GeneratedCSharp))
                        ? null
                        : InvalidCandidate(
                            "The MutationPlan Generated C# root is not authorized by the current Project v2 configuration.");
                }
                if (configMutations.Length != 1 || configMutations[0].Kind != FileMutationKind.WriteFile)
                {
                    return InvalidCandidate(
                        "A MutationPlan may only change project.json through one canonical Project v2 write.");
                }

                return ValidateCandidateProjectWrite(
                    plan,
                    projectFile,
                    configMutations[0],
                    requirePlanConfigHash: false);
            }

            if (Directory.Exists(projectFile)
                || observation.Kind != ObservedPathKind.Missing
                || observation.Length != 0
                || observation.Sha256 is not null)
            {
                return new Diagnostic(
                    "plan.config-stale",
                    DiagnosticSeverity.Blocker,
                    ExcelDbProject.RelativeFilePath,
                    "The Project v2 configuration state no longer matches this initialization plan.");
            }

            if (configMutations.Length != 1 || configMutations[0].Kind != FileMutationKind.WriteFile)
            {
                return InvalidCandidate(
                    "An initialization plan for a missing Project must carry one canonical Project v2 configuration write.");
            }
            return ValidateCandidateProjectWrite(
                plan,
                projectFile,
                configMutations[0],
                requirePlanConfigHash: true);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or System.Text.Json.JsonException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return new Diagnostic(
                "plan.config-stale",
                DiagnosticSeverity.Blocker,
                ExcelDbProject.RelativeFilePath,
                $"The Project v2 configuration could not be verified against the frozen plan: {exception.Message}");
        }
    }

    private static Diagnostic? ValidateCandidateProjectWrite(
        MutationPlan plan,
        string projectFile,
        FileMutation mutation,
        bool requirePlanConfigHash)
    {
        if (mutation.ContentBase64 is null || mutation.ContentSha256 is null)
            return InvalidCandidate("The candidate Project v2 configuration write has no content evidence.");

        byte[] candidateBytes;
        try
        {
            candidateBytes = Convert.FromBase64String(mutation.ContentBase64);
        }
        catch (FormatException exception)
        {
            return InvalidCandidate($"The candidate Project v2 configuration content is invalid: {exception.Message}");
        }

        var candidateHash = ContentFingerprint.FromBytes(candidateBytes).Sha256;
        var candidate = ExcelDbProject.Parse(candidateBytes);
        var candidateContext = candidate.Resolve(projectFile);
        if (!candidate.ToCanonicalJson().AsSpan().SequenceEqual(candidateBytes)
            || !string.Equals(mutation.ContentSha256, candidateHash, StringComparison.Ordinal)
            || (requirePlanConfigHash && !string.Equals(plan.ProjectConfigHash, candidateHash, StringComparison.Ordinal))
            || !PathsEqual(candidateContext.GeneratedCSharpDirectory, plan.GetRoot(PlanRootKind.GeneratedCSharp)))
        {
            return InvalidCandidate(
                "The MutationPlan is not bound to its canonical candidate Project v2 configuration and Generated C# root.");
        }
        return null;
    }

    private static Diagnostic InvalidCandidate(string message) =>
        new(
            "plan.config-candidate-invalid",
            DiagnosticSeverity.Blocker,
            ExcelDbProject.RelativeFilePath,
            message);

    private static bool IsProjectConfigPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return string.Equals(
            normalized,
            ExcelDbProject.RelativeFilePath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static OperationReport PersistForExistingProject(string root, OperationReport report)
    {
        var projectFile = Path.Combine(root, ExcelDbProject.RelativeFilePath);
        if (!File.Exists(projectFile))
            return report;
        try
        {
            return OperationReportStore.TryPersist(
                ExcelDbProject.Load(projectFile).Resolve(projectFile),
                report);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or System.Text.Json.JsonException)
        {
            return report;
        }
    }

    private static void EnsureTreeContainsNoReparsePoints(string root, string purpose)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var current = pending.Pop();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The {purpose} tree contains a symbolic link, junction, or reparse point: '{current}'.");
            foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"The {purpose} tree contains a symbolic link or reparse point: '{file}'.");
            }
            foreach (var child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"The {purpose} tree contains a symbolic link, junction, or reparse point: '{child}'.");
                pending.Push(child);
            }
        }
    }

    private static PipelineProject Load(ProjectLocation location)
    {
        var root = ResolveProjectRoot(location.Path);
        if (File.Exists(Path.Combine(root, ExcelDbProject.LegacyFileName)))
        {
            throw new InvalidDataException(
                $"Legacy project configuration '{ExcelDbProject.LegacyFileName}' exists beside Project v2; dual project configuration is not allowed.");
        }
        EnsureRecovered(root);
        return PipelineProject.Load(Path.Combine(root, ExcelDbProject.RelativeFilePath));
    }

    private static void EnsureRecovered(string root)
    {
        ProjectRecovery.EnsureRecovered(root);
    }

    private static string ResolveProjectRoot(string path)
    {
        var root = ResolveProjectRootPath(path);
        ProjectPathSafety.EnsureProjectRootAndInternalDirectoryArePlain(root);
        return root;
    }

    private static string ResolveProjectRootPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (string.Equals(Path.GetFileName(full), "project.json", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(full)), ".exceldb", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(Path.GetDirectoryName(full)!)!;
        }
        if (File.Exists(full))
        {
            return Path.GetDirectoryName(full)
                   ?? throw new InvalidDataException("The project location has no parent directory.");
        }
        return full;
    }

    private static void AddMirrorMutations(MutationPlanBuilder builder, SystemProtoMirrorAnalysis analysis)
    {
        foreach (var mutation in analysis.Mutations)
        {
            if (mutation.Kind == SystemProtoMirrorMutationKind.DeleteFile)
                builder.DeleteFile(mutation.AbsolutePath);
            else
                builder.WriteFile(mutation.AbsolutePath, mutation.Content!);
        }
    }

    private static ProjectInspection UninitializedInspection(
        string root,
        ProjectStatus status,
        ImmutableArray<Diagnostic> diagnostics,
        string? projectFile = null,
        bool includeDiagnosticLocations = true)
    {
        var canInitialize = status == ProjectStatus.Uninitialized;
        var actions = new ProjectActions(
            Available(canInitialize, canInitialize ? null : "Legacy or invalid configuration must be moved aside before initialization."),
            Disabled("Initialize Project v2 first."),
            Disabled("Initialize Project v2 first."),
            Disabled("Initialize Project v2 first."),
            Disabled("Initialize Project v2 first."),
            Disabled("Initialize Project v2 first."),
            Disabled("Initialize Project v2 first."),
            Disabled("Initialize Project v2 first."),
            Disabled("Initialize Project v2 first."));
        return new ProjectInspection(
            root,
            projectFile,
            status,
            new ProjectArtifactInspection("Schema", Path.Combine(root, "Schema"), "Not initialized", 0),
            new ProjectArtifactInspection("Excel", Path.Combine(root, "Excel"), "Not initialized", 0),
            new ProjectArtifactInspection("Generated C#", Path.Combine(root, "Generated", "CSharp"), "Not initialized", 0),
            new ProjectArtifactInspection("Generated Bytes", Path.Combine(root, "Generated", "Bytes"), "Not initialized", 0),
            actions,
            diagnostics,
            includeDiagnosticLocations
                ? LatestFile(Path.Combine(root, ".exceldb", "reports"), "*.json")
                : null,
            includeDiagnosticLocations && Directory.Exists(Path.Combine(root, ".exceldb", "recovery"))
                ? Path.Combine(root, ".exceldb", "recovery")
                : null,
            includeDiagnosticLocations && Directory.Exists(Path.Combine(root, ".exceldb"))
                ? Path.Combine(root, ".exceldb")
                : null);
    }

    private IReadOnlyDictionary<string, ConvertedBytesPackage> BuildExpectedBytePackages(
        PipelineProject project,
        CanonicalSchemaDescriptor schema,
        ImmutableArray<string> workbookPaths,
        ICollection<Diagnostic> diagnostics)
        => new WorkbookPipeline(_toolVersion, _schemas, _cellFormats, _validators)
            .BuildExpectedBytePackages(project, schema, workbookPaths, diagnostics);

    private static ImmutableArray<ProjectTargetArtifactInspection> InspectGeneratedBytes(
        ProjectContext context,
        CanonicalSchemaDescriptor? schema,
        IReadOnlyDictionary<string, ConvertedBytesPackage> expectedPackages,
        ICollection<Diagnostic> diagnostics)
    {
        if (schema is null || schema.Tables.IsEmpty)
            return [];

        return schema.Tables
            .Where(static table => table.Kind == CanonicalTableKind.Asset)
            .SelectMany(static table => table.ExportTargets)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(target => InspectGeneratedBytesTarget(
                context,
                schema.SchemaHash,
                target,
                expectedPackages.TryGetValue(target, out var expected) ? expected : null,
                diagnostics))
            .ToImmutableArray();
    }

    private static ProjectTargetArtifactInspection InspectGeneratedBytesTarget(
        ProjectContext context,
        ulong schemaHash,
        string target,
        ConvertedBytesPackage? expected,
        ICollection<Diagnostic> diagnostics)
    {
        var bytesPath = context.GetBytesOutputPath(target);
        var manifestPath = context.GetBytesManifestPath(target);
        try
        {
            var targetDirectory = context.GetBytesDirectory(target);
            ProjectPathSafety.EnsureContainedPathIsPlain(
                context.GeneratedBytesDirectory,
                targetDirectory,
                $"Generated Bytes target '{target}'",
                targetMustBeDirectory: true);
            ProjectPathSafety.EnsureContainedPathIsPlain(
                context.GeneratedBytesDirectory,
                bytesPath,
                $"Generated Bytes target '{target}' content");
            ProjectPathSafety.EnsureContainedPathIsPlain(
                context.GeneratedBytesDirectory,
                manifestPath,
                $"Generated Bytes target '{target}' manifest");

            if (Directory.Exists(bytesPath) || Directory.Exists(manifestPath))
                throw new InvalidDataException("A generated bytes file path is occupied by a directory.");
            var hasBytes = File.Exists(bytesPath);
            var hasManifest = File.Exists(manifestPath);
            if (!hasBytes && !hasManifest)
            {
                diagnostics.Add(new Diagnostic(
                    "bytes.missing",
                    DiagnosticSeverity.Warning,
                    target,
                    $"Generated bytes for target '{target}' have not been created."));
                return new ProjectTargetArtifactInspection(
                    target,
                    bytesPath,
                    manifestPath,
                    ProjectTargetArtifactStatus.Missing,
                    "Not generated");
            }
            if (!hasBytes || !hasManifest)
                throw new InvalidDataException("The bytes file and its manifest must either both exist or both be absent.");

            var actualBytes = File.ReadAllBytes(bytesPath);
            var actualManifestBytes = File.ReadAllBytes(manifestPath);
            var manifestJson = new UTF8Encoding(false, true).GetString(actualManifestBytes);
            var read = ConvertedBytesReader.Read(actualBytes, manifestJson);
            using (read.Snapshot)
            {
                if (read.Snapshot.SchemaHash != schemaHash
                    || !string.Equals(read.Snapshot.ExportTarget.Value, target, StringComparison.Ordinal))
                {
                    diagnostics.Add(new Diagnostic(
                        "bytes.stale",
                        DiagnosticSeverity.Error,
                        target,
                        $"Generated bytes for target '{target}' belong to a different schema or target."));
                    return new ProjectTargetArtifactInspection(
                        target,
                        bytesPath,
                        manifestPath,
                        ProjectTargetArtifactStatus.Stale,
                        "Stale schema or target identity");
                }
            }

            if (expected is null)
            {
                return new ProjectTargetArtifactInspection(
                    target,
                    bytesPath,
                    manifestPath,
                    ProjectTargetArtifactStatus.Unavailable,
                    "Integrity is valid; current source content cannot be confirmed");
            }

            var expectedManifestBytes = Encoding.UTF8.GetBytes(expected.ManifestJson + "\n");
            if (actualBytes.AsSpan().SequenceEqual(expected.Bytes)
                && actualManifestBytes.AsSpan().SequenceEqual(expectedManifestBytes))
            {
                return new ProjectTargetArtifactInspection(
                    target,
                    bytesPath,
                    manifestPath,
                    ProjectTargetArtifactStatus.Current,
                    "Current");
            }

            diagnostics.Add(new Diagnostic(
                "bytes.stale",
                DiagnosticSeverity.Error,
                target,
                $"Generated bytes for target '{target}' do not match the current schema, workbooks, identities, and tool output."));
            return new ProjectTargetArtifactInspection(
                target,
                bytesPath,
                manifestPath,
                ProjectTargetArtifactStatus.Stale,
                "Source content changed; regenerate bytes");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or System.Text.Json.JsonException
                                           or DecoderFallbackException)
        {
            diagnostics.Add(new Diagnostic(
                "bytes.corrupt",
                DiagnosticSeverity.Error,
                target,
                $"Generated bytes for target '{target}' are incomplete or corrupt: {exception.Message}"));
            return new ProjectTargetArtifactInspection(
                target,
                bytesPath,
                manifestPath,
                ProjectTargetArtifactStatus.Corrupt,
                "Incomplete or corrupt");
        }
    }

    private static ProjectStatus DetermineStatus(
        bool sourceDiscoverySucceeded,
        int sourceCount,
        CanonicalSchemaDescriptor? schema,
        int assetTableCount,
        SystemProtoMirrorAnalysis mirror,
        bool hasCodegenStale,
        bool hasMissingWorkbook,
        bool hasDataErrors,
        int pendingIdentityCount,
        bool byteTargetsCurrent)
    {
        if (mirror.HasBlockers)
            return ProjectStatus.SystemImportsConflicted;
        if (!sourceDiscoverySucceeded)
            return ProjectStatus.Invalid;
        if (sourceCount == 0)
            return ProjectStatus.NoTables;
        if (schema is null)
            return ProjectStatus.Invalid;
        if (assetTableCount == 0)
            return ProjectStatus.NoTables;
        if (mirror.Status == SystemProtoMirrorStatus.Missing)
            return ProjectStatus.SystemImportsMissing;
        if (mirror.Status == SystemProtoMirrorStatus.Outdated)
            return ProjectStatus.SystemImportsOutdated;
        if (hasCodegenStale || hasMissingWorkbook)
            return ProjectStatus.RegenerationRequired;
        if (hasDataErrors)
            return ProjectStatus.DataErrors;
        if (pendingIdentityCount != 0)
            return ProjectStatus.PendingRows;
        return byteTargetsCurrent ? ProjectStatus.Ready : ProjectStatus.BytesGenerationRequired;
    }

    private static bool IsDataFailure(Diagnostic diagnostic)
    {
        if (!diagnostic.IsFailure)
            return false;
        if (diagnostic.Code is "codegen.stale" or "identity.pending" or "workbook.missing" or "XDB000")
            return false;
        if (diagnostic.Code.StartsWith("system-import.", StringComparison.Ordinal)
            || diagnostic.Code is "bytes.missing" or "bytes.stale" or "bytes.corrupt" or "bytes.unavailable")
        {
            return false;
        }
        return true;
    }

    private static string SchemaStatusText(
        bool sourceDiscoverySucceeded,
        int sourceCount,
        CanonicalSchemaDescriptor? schema,
        int assetTableCount,
        SystemProtoMirrorAnalysis mirror)
    {
        if (mirror.HasBlockers)
            return "Proto dependency conflict";
        if (!sourceDiscoverySucceeded)
            return "Schema paths are invalid";
        if (sourceCount == 0 || (schema is not null && assetTableCount == 0))
            return "No tables";
        if (schema is null)
            return "Invalid schema";
        return mirror.Status switch
        {
            SystemProtoMirrorStatus.Missing => $"{assetTableCount} table(s); Proto dependencies need repair",
            SystemProtoMirrorStatus.Outdated => $"{assetTableCount} table(s); Proto dependencies are outdated",
            _ => $"Ready: {assetTableCount} table(s)",
        };
    }

    private static string ExcelStatusText(
        int assetTableCount,
        int workbookCount,
        bool hasDataErrors,
        int pendingIdentityCount)
    {
        if (assetTableCount == 0)
            return "Waiting for a valid table schema";
        if (workbookCount == 0)
            return "Regeneration required: no workbook";
        if (hasDataErrors)
            return "Data errors";
        if (pendingIdentityCount != 0)
            return $"{pendingIdentityCount} row(s) need identity";
        return $"Ready: {workbookCount} workbook(s)";
    }

    private static string GeneratedBytesStatusText(
        int assetTableCount,
        bool hasExportTargets,
        ImmutableArray<ProjectTargetArtifactInspection> targets)
    {
        if (assetTableCount == 0)
            return "Waiting for a valid table schema";
        if (!hasExportTargets)
            return "No export targets declared";
        var current = targets.Count(static target => target.Status == ProjectTargetArtifactStatus.Current);
        if (current == targets.Length)
            return $"Current: {current}/{targets.Length} target(s)";

        var details = new List<string>();
        AddTargetCount(details, targets, ProjectTargetArtifactStatus.Missing, "missing");
        AddTargetCount(details, targets, ProjectTargetArtifactStatus.Stale, "stale");
        AddTargetCount(details, targets, ProjectTargetArtifactStatus.Corrupt, "corrupt");
        AddTargetCount(details, targets, ProjectTargetArtifactStatus.Unavailable, "waiting for valid source data");
        return $"{current}/{targets.Length} target(s) current; {string.Join(", ", details)}. Generate bytes for the affected target(s).";
    }

    private static void AddTargetCount(
        ICollection<string> details,
        ImmutableArray<ProjectTargetArtifactInspection> targets,
        ProjectTargetArtifactStatus status,
        string label)
    {
        var count = targets.Count(target => target.Status == status);
        if (count != 0)
            details.Add($"{count} {label}");
    }

    private static string PrepareDisabledReason(
        bool schemaReady,
        bool hasWorkbooks,
        bool hasCodegenStale,
        bool hasDataErrors)
    {
        if (!schemaReady)
            return "A valid table schema is required.";
        if (!hasWorkbooks)
            return "Regenerate the Excel structure before preparing rows.";
        if (hasCodegenStale)
            return "Regenerate C# and Excel structure first.";
        return hasDataErrors
            ? "Fix Excel data errors before preparing row identities."
            : "Unavailable.";
    }

    private static string ConvertDisabledReason(
        bool schemaReady,
        bool hasWorkbooks,
        bool hasCodegenStale,
        bool hasDataErrors,
        int pendingIdentityCount,
        bool hasExportTargets)
    {
        if (!schemaReady)
            return "A valid table schema is required.";
        if (!hasWorkbooks)
            return "Regenerate the Excel structure before generating bytes.";
        if (hasCodegenStale)
            return "Regenerate C# and Excel structure first.";
        if (hasDataErrors)
            return "Fix Excel data errors before generating bytes.";
        if (pendingIdentityCount != 0)
            return $"Prepare identities for {pendingIdentityCount} pending row(s) before generating bytes.";
        return hasExportTargets
            ? "Unavailable."
            : "No export targets are declared by the live table schema.";
    }

    private static ImmutableArray<Diagnostic> OrderAndDeduplicateDiagnostics(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics
            .Distinct()
            .OrderByDescending(static diagnostic => diagnostic.Severity)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Location, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToImmutableArray();

    private static Diagnostic? InspectRecoveryState(string root, out bool pending)
    {
        pending = false;
        var recoveryDirectory = Path.Combine(root, ".exceldb", "recovery");
        try
        {
            ProjectPathSafety.EnsureContainedPathIsPlain(
                root,
                recoveryDirectory,
                "Project recovery directory",
                targetMustBeDirectory: true);
            if (!Directory.Exists(recoveryDirectory))
                return null;
            pending = Directory.EnumerateFileSystemEntries(recoveryDirectory).Any();
            return null;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return new Diagnostic(
                "project.path-unsafe",
                DiagnosticSeverity.Blocker,
                ".exceldb/recovery",
                exception.Message);
        }
    }

    private static bool IsRowReference(CanonicalFieldDescriptor field) =>
        field.Shape == CanonicalFieldShape.Message
        && string.Equals(field.TypeName, "exceldb.RowRef", StringComparison.Ordinal);

    private static ImmutableArray<int> EffectiveFieldPath(CanonicalFieldDescriptor field) =>
        field.FieldIdPath.IsDefaultOrEmpty ? [field.Id] : field.FieldIdPath;

    private static string FieldPathKey(CanonicalFieldDescriptor field) =>
        string.Join('.', EffectiveFieldPath(field));

    private static IEnumerable<CanonicalFieldDescriptor> FlattenFields(
        IEnumerable<CanonicalFieldDescriptor> fields)
    {
        foreach (var field in fields)
        {
            yield return field;
            foreach (var child in FlattenFields(field.Children))
                yield return child;
        }
    }

    private static string ComputeTargetContentHash(
        ulong schemaHash,
        ExcelDb.Core.Identity.ExportTargetId target,
        IEnumerable<RuntimeAssetRecord> records)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true);
        writer.Write(schemaHash);
        WriteHashString(writer, target.Value);
        foreach (var record in records
                     .OrderBy(static record => record.Identity.TableId)
                     .ThenBy(static record => record.Identity.RowGuid.ToString(), StringComparer.Ordinal))
        {
            writer.Write(record.Identity.TableId);
            WriteHashString(writer, record.Identity.RowGuid.ToString());
            WriteHashString(writer, record.Key);
            WriteHashString(writer, record.Path);
            writer.Write(record.Fields.Length);
            foreach (var field in record.Fields
                         .OrderBy(static field => string.Join('.', field.FieldIdPath), StringComparer.Ordinal))
            {
                writer.Write(field.FieldIdPath.Length);
                foreach (var fieldNumber in field.FieldIdPath)
                    writer.Write(fieldNumber);
                writer.Write(field.Data.Length);
                writer.Write(field.Data.Span);
            }
            writer.Write(record.Dependencies.Length);
            foreach (var dependency in record.Dependencies
                         .OrderBy(static dependency => dependency.TableId)
                         .ThenBy(static dependency => dependency.RowGuid.ToString(), StringComparer.Ordinal))
            {
                writer.Write(dependency.TableId);
                WriteHashString(writer, dependency.RowGuid.ToString());
            }
        }
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void WriteHashString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string NormalizeContainedDirectory(
        ProjectContext context,
        string? requested,
        string current,
        string name)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return current;
        var absolute = Path.IsPathFullyQualified(requested)
            ? Path.GetFullPath(requested)
            : Path.GetFullPath(requested, context.RootDirectory);
        var relative = Path.GetRelativePath(context.RootDirectory, absolute);
        if (Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{name} must remain inside the project root.");
        }
        return relative.Replace('\\', '/');
    }

    private static string NormalizeGeneratedCSharpDirectory(ProjectContext context, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return context.Project.GeneratedCSharpDir;
        var absolute = Path.IsPathFullyQualified(requested)
            ? Path.GetFullPath(requested)
            : Path.GetFullPath(requested, context.RootDirectory);
        var relative = Path.GetRelativePath(context.RootDirectory, absolute);
        var contained = !Path.IsPathFullyQualified(relative)
                        && relative != ".."
                        && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        return contained ? relative.Replace('\\', '/') : absolute;
    }

    private static int CountFiles(string directory, string pattern)
    {
        try
        {
            if (!Directory.Exists(directory)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return 0;
            var count = 0;
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count != 0)
            {
                var current = pending.Pop();
                count += Directory.EnumerateFiles(current, pattern, SearchOption.TopDirectoryOnly).Count(path =>
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0);
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
            }
            return count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string? LatestFile(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
            return null;
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ProjectActionAvailability Available(bool enabled, string? reason) =>
        enabled ? new ProjectActionAvailability(true) : Disabled(reason ?? "Unavailable.");

    private static ProjectActionAvailability Disabled(string reason) => new(false, reason);
}
