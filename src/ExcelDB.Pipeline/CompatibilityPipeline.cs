using System.Collections.Immutable;
using ExcelDb.Compatibility;
using ExcelDb.Compatibility.Publishing;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Runtime.Bytes;
using ExcelDb.Schema.Generation;
using ExcelDb.Tooling.Plans;
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

    public CompatibilityPipeline(
        string toolVersion,
        SchemaPipeline schemas,
        CellFormatRegistry? cellFormats = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        _toolVersion = toolVersion;
        _schemas = schemas ?? throw new ArgumentNullException(nameof(schemas));
        _cellFormats = cellFormats ?? new CellFormatRegistry();
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
        var analysis = await AnalyzeAsync(project, cancellationToken).ConfigureAwait(false);
        var readiness = await new WorkbookPipeline(_toolVersion, _schemas, _cellFormats)
            .CheckAsync(project, pendingIsBlocker: true, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var schemaHash = analysis.Schema?.Descriptor.SchemaHash ?? 0;
        var builder = _schemas.CreatePlanBuilder(project, "schema-publish", schemaHash);
        SchemaPipeline.ObserveSchemaInputs(builder, project);
        foreach (var workbook in analysis.WorkbookPaths)
            builder.Observe(workbook);
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

        var preview = builder.Build();
        if (preview.HasBlockers || preview.Diagnostics.Any(static item => item.IsFailure))
            return preview;

        var publication = EvaluateTargetPublication(
            project,
            analysis,
            completedHostMigrations ?? []);
        foreach (var diagnostic in publication.Diagnostics)
            builder.AddDiagnostic(diagnostic);
        if (!publication.Allowed)
            return builder.Build();
        PublishedSchemaHistoryStore.AddPublishMutations(builder, project, analysis.Schema);
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
        PipelineProject project,
        CompatibilityAnalysisOutcome analysis,
        IEnumerable<string> completedHostMigrations)
    {
        var schema = analysis.Schema
            ?? throw new InvalidOperationException("Publication requires a compiled schema.");
        var report = analysis.Report
            ?? throw new InvalidOperationException("Publication requires a compatibility report.");
        var evidence = ImmutableArray.CreateBuilder<TargetArtifactEvidence>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var generation = new SchemaCodeGenerator().Generate(schema.Descriptor);
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
            var registry = BuildGeneratedComponent(project, schema.Descriptor.SchemaHash, target, registryFiles, diagnostics);
            var codegen = BuildGeneratedComponent(project, schema.Descriptor.SchemaHash, target, codegenFiles, diagnostics);

            var bytesPath = WorkbookPipeline.DefaultOutputPath(project, targetText);
            var manifestPath = bytesPath + ".manifest.json";
            TargetArtifactComponentEvidence? bytesEvidence = null;
            TargetArtifactComponentEvidence? manifestEvidence = null;
            try
            {
                if (!File.Exists(bytesPath) || !File.Exists(manifestPath))
                    throw new InvalidDataException($"Converted target '{targetText}' is missing bytes or manifest at '{bytesPath}'.");
                var bytes = File.ReadAllBytes(bytesPath);
                var manifestText = File.ReadAllText(manifestPath);
                var manifest = ConvertedBytesManifest.Parse(manifestText);
                using var converted = ConvertedBytesReader.Read(bytes, manifestText).Snapshot;
                if (manifest.SchemaHash != schema.Descriptor.SchemaHash || manifest.ExportTarget != target)
                    throw new InvalidDataException($"Converted target '{targetText}' does not match the current schema identity.");
                bytesEvidence = new TargetArtifactComponentEvidence(
                    manifest.SchemaHash,
                    manifest.ExportTarget,
                    ContentFingerprint.FromBytes(bytes).Sha256);
                manifestEvidence = new TargetArtifactComponentEvidence(
                    manifest.SchemaHash,
                    manifest.ExportTarget,
                    ContentFingerprint.FromBytes(System.Text.Encoding.UTF8.GetBytes(manifestText)).Sha256);
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

    private static TargetArtifactComponentEvidence? BuildGeneratedComponent(
        PipelineProject project,
        ulong schemaHash,
        ExportTargetId target,
        IReadOnlyCollection<GeneratedSchemaArtifact> artifacts,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (artifacts.Count == 0)
            return null;
        using var stream = new MemoryStream();
        foreach (var artifact in artifacts.OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            var path = Path.Combine(project.Context.GeneratedDirectory, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                diagnostics.Add(new Diagnostic(
                    "compat.publish.generated-missing",
                    DiagnosticSeverity.Blocker,
                    SchemaPipeline.Relative(project, path),
                    "A generated target component is missing."));
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(artifact.RelativePath);
            stream.Write(BitConverter.GetBytes(pathBytes.Length));
            stream.Write(pathBytes);
            stream.Write(BitConverter.GetBytes(bytes.Length));
            stream.Write(bytes);
        }
        return new TargetArtifactComponentEvidence(
            schemaHash,
            target,
            ContentFingerprint.FromBytes(stream.ToArray()).Sha256);
    }

    private (CompatibilityDataFacts Facts, ImmutableArray<string> Paths, ImmutableArray<Diagnostic> Diagnostics)
        BuildWorkbookFacts(PipelineProject project, CompiledProjectSchema schema)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var paths = SchemaPipeline.ResolveWorkbooks(project).ToImmutableArray();
        var workbookFacts = ImmutableArray.CreateBuilder<WorkbookCompatibilityFacts>();
        var nonEmpty = ImmutableHashSet.CreateBuilder<int>();
        var expectedGlobCount = project.Context.Project.Workbooks.Length;
        var resolvedGlobCount = 0;
        foreach (var glob in project.Context.Project.Workbooks)
        {
            var normalized = glob.Replace('/', Path.DirectorySeparatorChar);
            var directory = project.Context.ResolvePath(Path.GetDirectoryName(normalized) ?? ".");
            var pattern = Path.GetFileName(normalized);
            if (Directory.Exists(directory)
                && Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any())
                resolvedGlobCount++;
        }

        foreach (var path in paths)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var workbook = XlsxWorkbookCodec.Read(bytes);
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

        var complete = resolvedGlobCount == expectedGlobCount
                       && paths.Length != 0
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
