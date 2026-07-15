using System.Collections.Immutable;
using System.Buffers.Binary;
using ExcelDb.Compatibility;
using ExcelDb.Compatibility.Publishing;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Runtime.Bytes;
using ExcelDb.Schema.Generation;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.OpenXml;

namespace ExcelDb.Pipeline;

public sealed record CompatibilityAnalysisOutcome(
    CompiledProjectSchema? Schema,
    PublishedSchemaSnapshot? Previous,
    CompatibilityDataFacts DataFacts,
    CompatibilityReport? Report,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<string> WorkbookPaths)
{
    public bool Succeeded => Schema is not null
                             && Report is not null
                             && !Diagnostics.Any(static item => item.IsFailure);
}

/// <summary>Connects descriptor history and real controlled XLSX evidence to M8 analysis.</summary>
public sealed class CompatibilityPipeline
{
    private readonly string _toolVersion;
    private readonly SchemaPipeline _schemas;
    private readonly CellFormatRegistry _cellFormats;
    private readonly WorkbookValidatorRegistry _validators;

    public CompatibilityPipeline(
        string toolVersion,
        SchemaPipeline schemas,
        CellFormatRegistry? cellFormats = null,
        WorkbookValidatorRegistry? validators = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        _toolVersion = toolVersion;
        _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
        _cellFormats = cellFormats ?? new CellFormatRegistry();
        _validators = validators ?? new WorkbookValidatorRegistry();
    }

    public async Task<CompatibilityAnalysisOutcome> AnalyzeAsync(
        PipelineProject project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var diagnostics = new List<Diagnostic>();
        var schema = await _schemas.CompileAsync(project, diagnostics, cancellationToken).ConfigureAwait(false);
        if (schema is null)
        {
            return new CompatibilityAnalysisOutcome(
                null,
                null,
                CompatibilityDataFacts.Empty,
                null,
                diagnostics.ToImmutableArray(),
                []);
        }

        var history = PublishedSchemaHistoryStore.LoadLatest(project);
        diagnostics.AddRange(history.Diagnostics);
        var (facts, workbookPaths, factDiagnostics) = BuildWorkbookFacts(project, schema);
        diagnostics.AddRange(factDiagnostics);
        if (!history.IsValid)
        {
            return new CompatibilityAnalysisOutcome(
                schema,
                null,
                facts,
                null,
                diagnostics.ToImmutableArray(),
                workbookPaths);
        }

        var report = new CompatibilityAnalyzer().Analyze(schema.Descriptor, history.Latest?.Descriptor, facts);
        diagnostics.AddRange(report.Diagnostics);
        return new CompatibilityAnalysisOutcome(
            schema,
            history.Latest,
            facts,
            report,
            diagnostics
                .Distinct()
                .OrderByDescending(static item => item.Severity)
                .ThenBy(static item => item.Code, StringComparer.Ordinal)
                .ThenBy(static item => item.Location, StringComparer.Ordinal)
                .ToImmutableArray(),
            workbookPaths);
    }

    public async Task<MutationPlan> CreatePublishPlanAsync(
        PipelineProject project,
        IEnumerable<string>? completedHostMigrations = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var bootstrapDiagnostics = new List<Diagnostic>();
        var bootstrapSchema = await _schemas.CompileAsync(project, bootstrapDiagnostics, cancellationToken).ConfigureAwait(false);
        var bootstrapHash = bootstrapSchema?.Descriptor.SchemaHash ?? 0;
        var builder = _schemas.CreatePlanBuilder(project, "schema-publish", bootstrapHash);
        _schemas.ObserveSchemaInputs(builder, project);
        if (bootstrapSchema is null)
        {
            foreach (var diagnostic in bootstrapDiagnostics)
                builder.AddDiagnostic(diagnostic);
            return builder.Build();
        }

        // Recompile only after the plan has frozen every schema input. A source edit
        // racing the bootstrap compile therefore either blocks this plan immediately
        // or makes it stale at apply time.
        var diagnostics = new List<Diagnostic>();
        var schema = await _schemas.CompileAsync(project, diagnostics, cancellationToken).ConfigureAwait(false);
        if (schema is null)
        {
            foreach (var diagnostic in diagnostics)
                builder.AddDiagnostic(diagnostic);
            return builder.Build();
        }
        if (schema.Descriptor.SchemaHash != bootstrapHash)
        {
            builder.AddDiagnostic(new Diagnostic(
                "compat.publish.schema-race",
                DiagnosticSeverity.Blocker,
                project.Context.Project.SchemaDir,
                "Schema inputs changed while the publish plan was being created; retry publication."));
            return builder.Build();
        }

        var history = PublishedSchemaHistoryStore.LoadLatest(project, builder);
        diagnostics.AddRange(history.Diagnostics);
        var excelInputs = SchemaPipeline.CaptureExcelInputs(project);
        SchemaPipeline.ObserveExcelInputs(builder, project, excelInputs);
        var workbookPaths = SchemaPipeline.ResolveWorkbooks(project).ToImmutableArray();
        foreach (var workbook in workbookPaths)
            builder.Observe(workbook);
        var (facts, _, factDiagnostics) = BuildWorkbookFacts(project, schema, workbookPaths);
        diagnostics.AddRange(factDiagnostics);
        CompatibilityReport? compatibilityReport = null;
        if (history.IsValid)
        {
            compatibilityReport = new CompatibilityAnalyzer().Analyze(
                schema.Descriptor,
                history.Latest?.Descriptor,
                facts);
            diagnostics.AddRange(compatibilityReport.Diagnostics);
        }
        var analysis = new CompatibilityAnalysisOutcome(
            schema,
            history.Latest,
            facts,
            compatibilityReport,
            diagnostics
                .Distinct()
                .OrderByDescending(static item => item.Severity)
                .ThenBy(static item => item.Code, StringComparer.Ordinal)
                .ThenBy(static item => item.Location, StringComparer.Ordinal)
                .ToImmutableArray(),
            workbookPaths);
        var workbookPipeline = new WorkbookPipeline(_toolVersion, _schemas, _cellFormats, _validators);
        var readiness = await workbookPipeline
            .CheckAsync(
                project,
                pendingIsBlocker: true,
                cancellationToken: cancellationToken,
                frozenExcelInputs: excelInputs)
            .ConfigureAwait(false);
        foreach (var diagnostic in analysis.Diagnostics)
            builder.AddDiagnostic(diagnostic);
        foreach (var diagnostic in readiness.Report.Diagnostics)
            builder.AddDiagnostic(diagnostic);
        if (analysis.Schema is null || analysis.Report is null)
            return builder.Build();

        foreach (var entry in analysis.Report.Entries)
        {
            if (entry.Severity < CompatibilitySeverity.Error)
                continue;
            builder.AddDiagnostic(new Diagnostic(
                "compat.publish.analysis",
                entry.Severity == CompatibilitySeverity.Blocker
                    ? DiagnosticSeverity.Blocker
                    : DiagnosticSeverity.Error,
                CompatibilityLocationText(entry.Location),
                entry.Message));
        }
        if (!analysis.DataFacts.HasCompleteWorkbookCoverage)
        {
            builder.AddDiagnostic(new Diagnostic(
                "compat.publish.coverage",
                DiagnosticSeverity.Blocker,
                "workbooks",
                "Every configured workbook must be readable and mapped before publishing schema history."));
        }

        IReadOnlyDictionary<string, ConvertedBytesPackage> expectedPackages =
            ImmutableDictionary<string, ConvertedBytesPackage>.Empty;
        if (readiness.Schema is not null && readiness.Report.Succeeded)
        {
            var projectionDiagnostics = new List<Diagnostic>();
            expectedPackages = workbookPipeline.BuildExpectedBytePackages(
                project,
                readiness.Schema.Descriptor,
                readiness.WorkbookPaths,
                projectionDiagnostics,
                excelInputs);
            foreach (var diagnostic in projectionDiagnostics)
                builder.AddDiagnostic(diagnostic);
        }

        var publication = EvaluateTargetPublication(
            builder,
            project,
            analysis,
            expectedPackages,
            completedHostMigrations ?? []);
        foreach (var diagnostic in publication.Diagnostics)
            builder.AddDiagnostic(diagnostic);
        var preview = builder.Build();
        if (preview.HasBlockers
            || preview.Diagnostics.Any(static item => item.IsFailure)
            || !publication.Allowed)
        {
            return preview;
        }
        PublishedSchemaHistoryStore.AddPublishMutations(builder, project, analysis.Schema, history);
        var reportPath = Path.Combine(
            PublishedSchemaHistoryStore.DirectoryPath(project),
            $"compat-{analysis.Schema.Descriptor.SchemaHash:x16}.json");
        SchemaPipeline.WriteIfChanged(
            builder,
            reportPath,
            [.. CompatibilityReportSerializer.SerializeUtf8(analysis.Report), (byte)'\n']);
        builder.AddRisk($"publish-schema={analysis.Schema.Descriptor.SchemaHash:x16}");
        builder.AddRisk($"publication-evidence={publication.EvidenceHash}");
        return builder.Build();
    }

    private TargetPublicationGateResult EvaluateTargetPublication(
        MutationPlanBuilder builder,
        PipelineProject project,
        CompatibilityAnalysisOutcome analysis,
        IReadOnlyDictionary<string, ConvertedBytesPackage> expectedPackages,
        IEnumerable<string> completedHostMigrations)
    {
        var schema = analysis.Schema
            ?? throw new InvalidOperationException("Publication requires a compiled schema.");
        var report = analysis.Report
            ?? throw new InvalidOperationException("Publication requires a compatibility report.");
        var evidence = ImmutableArray.CreateBuilder<TargetArtifactEvidence>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var generation = new SchemaCodeGenerator().Generate(
            schema.Descriptor,
            _schemas.SystemProtoCatalog.CatalogHash);
        var generatedSnapshots = CaptureExpectedGeneratedArtifacts(
            builder,
            project,
            generation,
            diagnostics);
        foreach (var targetText in schema.Descriptor.Tables
                     .SelectMany(static table => table.ExportTargets)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (!ExportTargetId.TryParse(targetText, out var target))
                continue;
            var targetArtifacts = generation.Artifacts
                .Where(artifact => string.Equals(artifact.ExportTarget, targetText, StringComparison.Ordinal))
                .ToArray();
            var registryFiles = targetArtifacts.Where(static artifact => artifact.Kind == "runtime-registry-csharp").ToArray();
            var codegenFiles = targetArtifacts.Where(static artifact => artifact.Kind == "runtime-csharp").ToArray();
            var registry = BuildGeneratedComponent(schema.Descriptor.SchemaHash, target, registryFiles, generatedSnapshots);
            var codegen = BuildGeneratedComponent(schema.Descriptor.SchemaHash, target, codegenFiles, generatedSnapshots);

            var bytesPath = WorkbookPipeline.DefaultOutputPath(project, targetText);
            var manifestPath = bytesPath + ".manifest.json";
            TargetArtifactComponentEvidence? bytesEvidence = null;
            TargetArtifactComponentEvidence? manifestEvidence = null;
            try
            {
                builder.Observe(bytesPath);
                builder.Observe(manifestPath);
                if (!File.Exists(bytesPath) || !File.Exists(manifestPath))
                    throw new InvalidDataException($"Converted target '{targetText}' is missing bytes or manifest at '{bytesPath}'.");
                ProjectPathSafety.EnsurePlainFile(bytesPath, $"Converted target '{targetText}' bytes");
                ProjectPathSafety.EnsurePlainFile(manifestPath, $"Converted target '{targetText}' manifest");
                var bytes = File.ReadAllBytes(bytesPath);
                var manifestBytes = File.ReadAllBytes(manifestPath);
                var manifestText = System.Text.Encoding.UTF8.GetString(manifestBytes);
                var manifest = ConvertedBytesManifest.Parse(manifestText);
                using var converted = ConvertedBytesReader.Read(bytes, manifestText).Snapshot;
                if (manifest.SchemaHash != schema.Descriptor.SchemaHash || manifest.ExportTarget != target)
                    throw new InvalidDataException($"Converted target '{targetText}' does not match the current schema identity.");
                if (!expectedPackages.TryGetValue(targetText, out var expectedPackage))
                {
                    throw new InvalidDataException(
                        $"Converted target '{targetText}' cannot be proven against the current Excel source projection.");
                }
                var expectedManifestBytes = System.Text.Encoding.UTF8.GetBytes(expectedPackage.ManifestJson + "\n");
                if (!bytes.AsSpan().SequenceEqual(expectedPackage.Bytes)
                    || !manifestBytes.AsSpan().SequenceEqual(expectedManifestBytes))
                {
                    throw new InvalidDataException(
                        $"Converted target '{targetText}' is stale and must be regenerated from the current Excel data before publishing.");
                }
                bytesEvidence = new TargetArtifactComponentEvidence(
                    manifest.SchemaHash,
                    manifest.ExportTarget,
                    ContentFingerprint.FromBytes(bytes).Sha256);
                manifestEvidence = new TargetArtifactComponentEvidence(
                    manifest.SchemaHash,
                    manifest.ExportTarget,
                    ContentFingerprint.FromBytes(manifestBytes).Sha256);
            }
            catch (Exception exception) when (exception is IOException
                                               or InvalidDataException
                                               or UnauthorizedAccessException
                                               or System.Text.Json.JsonException)
            {
                diagnostics.Add(new Diagnostic(
                    "compat.publish.target-artifact",
                    DiagnosticSeverity.Blocker,
                    targetText,
                    exception.Message));
            }

            evidence.Add(new TargetArtifactEvidence(target, registry, codegen, bytesEvidence, manifestEvidence));
        }

        var migrationsComplete = analysis.DataFacts.HasCompleteWorkbookCoverage
                                 && analysis.DataFacts.Workbooks.All(workbook =>
                                     workbook.SchemaHash == schema.Descriptor.SchemaHash
                                     && workbook.MappingComplete
                                     && workbook.UnparseableValueCount == 0
                                     && workbook.PendingMigrationCount == 0);
        var request = new TargetPublicationRequest(
            schema.Descriptor,
            report,
            evidence.ToImmutable(),
            completedHostMigrations.ToImmutableHashSet(StringComparer.Ordinal),
            migrationsComplete)
        {
            InitialPublication = analysis.Previous is null,
        };
        var gate = TargetPublicationGate.Evaluate(request);
        return gate with
        {
            Diagnostics = gate.Diagnostics.AddRange(diagnostics)
                .OrderBy(static item => item.Code, StringComparer.Ordinal)
                .ThenBy(static item => item.Location, StringComparer.Ordinal)
                .ToImmutableArray(),
            Allowed = gate.Allowed && !diagnostics.Any(static item => item.IsFailure),
        };
    }

    private static ImmutableDictionary<string, byte[]> CaptureExpectedGeneratedArtifacts(
        MutationPlanBuilder builder,
        PipelineProject project,
        SchemaCodeGenerationResult generation,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var snapshots = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);
        var manifestPath = Path.Combine(project.Context.GeneratedCSharpDirectory, "codegen.manifest.json");
        builder.Observe(PlanRootKind.GeneratedCSharp, "codegen.manifest.json");
        try
        {
            if (!File.Exists(manifestPath))
                throw new InvalidDataException("codegen.manifest.json is missing; generated files have no current ownership authority.");
            ProjectPathSafety.EnsurePlainFile(manifestPath, "Generated C# ownership manifest");
            var actualManifest = File.ReadAllBytes(manifestPath);
            var expectedManifest = System.Text.Encoding.UTF8.GetBytes(generation.Manifest.Json + "\n");
            if (!actualManifest.AsSpan().SequenceEqual(expectedManifest))
            {
                throw new InvalidDataException(
                    "codegen.manifest.json does not exactly match the current generator output; generated-file ownership is stale or unknown.");
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic(
                "compat.publish.generated-ownership",
                DiagnosticSeverity.Blocker,
                "codegen.manifest.json",
                exception.Message));
        }

        foreach (var artifact in generation.Artifacts.OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            var path = Path.Combine(
                project.Context.GeneratedCSharpDirectory,
                artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            builder.Observe(PlanRootKind.GeneratedCSharp, artifact.RelativePath);
            try
            {
                if (!File.Exists(path))
                    throw new InvalidDataException("The expected generated C# file is missing.");
                ProjectPathSafety.EnsurePlainFile(path, "Generated C# artifact");
                var actual = File.ReadAllBytes(path);
                var expected = System.Text.Encoding.UTF8.GetBytes(artifact.Content);
                var actualHash = ContentFingerprint.FromBytes(actual).Sha256;
                if (!actual.AsSpan().SequenceEqual(expected)
                    || !string.Equals(actualHash, artifact.ContentHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Generated C# differs from the current SchemaCodeGenerator output: expected {artifact.ContentHash}, actual {actualHash}.");
                }
                snapshots.Add(artifact.RelativePath, actual);
            }
            catch (Exception exception) when (exception is IOException
                                               or InvalidDataException
                                               or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic(
                    "compat.publish.generated-invalid",
                    DiagnosticSeverity.Blocker,
                    artifact.RelativePath,
                    exception.Message));
            }
        }
        return snapshots.ToImmutable();
    }

    private static TargetArtifactComponentEvidence? BuildGeneratedComponent(
        ulong schemaHash,
        ExportTargetId target,
        IReadOnlyCollection<GeneratedSchemaArtifact> artifacts,
        IReadOnlyDictionary<string, byte[]> snapshots)
    {
        if (artifacts.Count == 0)
            return null;
        using var stream = new MemoryStream();
        var length = new byte[sizeof(int)];
        foreach (var artifact in artifacts.OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            if (!snapshots.TryGetValue(artifact.RelativePath, out var bytes))
                return null;
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(artifact.RelativePath);
            BinaryPrimitives.WriteInt32LittleEndian(length, pathBytes.Length);
            stream.Write(length);
            stream.Write(pathBytes);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
        return new TargetArtifactComponentEvidence(
            schemaHash,
            target,
            ContentFingerprint.FromBytes(stream.ToArray()).Sha256);
    }

    private (CompatibilityDataFacts Facts, ImmutableArray<string> Paths, ImmutableArray<Diagnostic> Diagnostics)
        BuildWorkbookFacts(
            PipelineProject project,
            CompiledProjectSchema schema,
            ImmutableArray<string> frozenPaths = default)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var paths = frozenPaths.IsDefault
            ? SchemaPipeline.ResolveWorkbooks(project).ToImmutableArray()
            : frozenPaths;
        var workbookFacts = ImmutableArray.CreateBuilder<WorkbookCompatibilityFacts>();
        var nonEmpty = ImmutableHashSet.CreateBuilder<int>();

        foreach (var path in paths)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var inspection = XlsxWorkbookCodec.Inspect(bytes, schema.Descriptor);
                diagnostics.AddRange(inspection.Diagnostics);
                var workbook = inspection.Workbook;
                var import = WorkbookImporter.Import(path, bytes, workbook, schema.Descriptor, cellFormats: _cellFormats);
                foreach (var tableId in import.Rows.Select(static row => row.TableId).Distinct())
                    nonEmpty.Add(tableId);
                var mappingComplete = !import.Diagnostics.Any(static item => item.Code is "EXWB2000" or "EXWB2001" or "EXWB2009");
                var unparseable = import.Rows.Sum(static row => row.Values.Count(static value => value.Value.State == ExcelDb.Core.Values.CanonicalValueState.Invalid));
                var pending = import.Rows.Count(static row => row.Identity is null);
                var legacy = import.Diagnostics.Count(static item => item.Code == "EXWB2008");
                workbookFacts.Add(new WorkbookCompatibilityFacts(
                    workbook.WorkbookGuid.ToString("N"),
                    ContentFingerprint.FromBytes(bytes).Sha256,
                    workbook.SchemaHash,
                    mappingComplete,
                    true,
                    unparseable,
                    pending,
                    legacy,
                    import.Diagnostics.Count(static item => item.Code == "EXWB2003")));
                diagnostics.AddRange(import.Diagnostics.Where(static item => item.IsFailure));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic(
                    "compat.workbook.read",
                    DiagnosticSeverity.Blocker,
                    SchemaPipeline.Relative(project, path),
                    exception.Message));
            }
        }

        var requiresWorkbook = schema.Descriptor.Tables.Any(static table =>
            table.Kind == ExcelDb.Schema.Descriptors.CanonicalTableKind.Asset);
        var complete = (!requiresWorkbook || paths.Length != 0)
                       && workbookFacts.Count == paths.Length
                       && !diagnostics.Any(static item => item.IsFailure);
        var facts = new CompatibilityDataFacts(nonEmpty.ToImmutable(), complete)
        {
            Workbooks = workbookFacts.ToImmutable(),
        };
        return (facts, paths, diagnostics.ToImmutable());
    }

    private static string CompatibilityLocationText(CompatibilityLocation location)
    {
        var field = location.FieldIdPath.IsDefaultOrEmpty
            ? string.Empty
            : "/" + string.Join('.', location.FieldIdPath);
        return location.TableId is { } tableId ? $"table/{tableId}{field}" : ".";
    }
}
