using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Core.Values;
using ExcelDb.Runtime;
using ExcelDb.Runtime.Bytes;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Generation;
using ExcelDb.Tooling.Plans;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.Identity;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;
using ExcelDb.Workbooks.References;

namespace ExcelDb.Pipeline;

public sealed class WorkbookPipeline
{
    private readonly string _toolVersion;
    private readonly SchemaPipeline _schemas;
    private readonly CellFormatRegistry _cellFormats;
    private readonly WorkbookValidatorRegistry _validators;

    public WorkbookPipeline(
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

    public static string DefaultOutputPath(PipelineProject project, string target)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!ExportTargetId.TryParse(target, out _))
            throw new ArgumentException($"Invalid export target '{target}'.", nameof(target));
        var client = project.Context.BytesOutputPath;
        if (string.Equals(target, "client", StringComparison.Ordinal))
            return client;
        var directory = Path.GetDirectoryName(client) ?? project.Context.RootDirectory;
        var extension = Path.GetExtension(client);
        var stem = Path.GetFileNameWithoutExtension(client);
        return Path.Combine(directory, $"{stem}.{target}{extension}");
    }

    public async Task<MutationPlan> GenerateAsync(
        PipelineProject project,
        string? workbookPath = null,
        bool purge = false,
        bool rekey = false,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<Diagnostic>();
        var schema = await _schemas.CompileAsync(project, diagnostics, cancellationToken).ConfigureAwait(false);
        var builder = _schemas.CreatePlanBuilder(project, "generate", schema?.Descriptor.SchemaHash ?? 0);
        SchemaPipeline.ObserveSchemaInputs(builder, project);
        if (schema is null || !CheckGeneratedFresh(project, schema.Descriptor, diagnostics))
        {
            foreach (var diagnostic in diagnostics)
                builder.AddDiagnostic(diagnostic);
            return builder.Build();
        }

        var workbooks = SchemaPipeline.ResolveWorkbooks(project, workbookPath).ToArray();
        if (workbookPath is not null && workbooks.Length == 1 && !File.Exists(workbooks[0]))
        {
            var definition = new WorkbookDefinition(
                DeterministicGuid(workbooks[0]),
                schema.Descriptor.SchemaHash,
                DateTimeOffset.UnixEpoch,
                schema.Descriptor.Tables
                    .Where(static table => table.Kind == CanonicalTableKind.Asset)
                    .OrderBy(static table => table.Id)
                    .Select(WorkbookLayout.CreateTable)
                    .ToImmutableArray(),
                ChildTables: WorkbookLayout.CreateChildTables(schema.Descriptor));
            SchemaPipeline.WriteIfChanged(builder, workbooks[0], XlsxWorkbookCodec.Write(definition));
        }
        else
        {
            var projections = new List<WorkbookProjectionCandidate>();
            foreach (var workbook in workbooks)
            {
                try
                {
                    var sourceBytes = File.ReadAllBytes(workbook);
                    var inspection = XlsxWorkbookCodec.Inspect(sourceBytes, schema.Descriptor);
                    var physical = inspection.Workbook;
                    if (inspection.HasDrift)
                    {
                        builder.AddRisk(
                            $"projection-repair={SchemaPipeline.Relative(project, workbook)}:" +
                            string.Join(',', inspection.RepairPlan!.Repairs));
                    }
                    var import = WorkbookImporter.Import(
                        workbook,
                        sourceBytes,
                        physical,
                        schema.Descriptor,
                        cellFormats: _cellFormats,
                        validators: _validators);
                    // During an explicit rekey the persisted key projection is the old
                    // human-token domain that references still use.  The projected copy
                    // recomputes the new key from canonical business cells.  Normal
                    // generate operations continue to distrust/rebuild that projection.
                    var source = rekey
                        ? XlsxWorkbookCodec.Read(sourceBytes)
                        : WorkbookIdentityProjection.BindCanonicalKeys(physical, import);
                    var projected = ProjectWorkbookDefinition(source, schema.Descriptor, purge, rekey);
                    projections.Add(new WorkbookProjectionCandidate(workbook, sourceBytes, source, projected));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException)
                {
                    diagnostics.Add(new Diagnostic("workbook.generate", DiagnosticSeverity.Blocker, SchemaPipeline.Relative(project, workbook), exception.Message));
                }
            }

            if (!diagnostics.Any(static diagnostic => diagnostic.IsBlocker))
            {
                IReadOnlyDictionary<(int TableId, string OldKey), string>? rekeyMap = null;
                if (rekey)
                    rekeyMap = BuildRekeyMap(projections);
                foreach (var projection in projections)
                {
                    try
                    {
                        var projected = rekeyMap is null
                            ? projection.Projected
                            : RewriteReferenceTokens(projection.Projected, schema.Descriptor, rekeyMap);
                        var output = XlsxWorkbookCodec.Project(projection.SourceBytes, projected, purgeUnownedCells: purge);
                        if (!projection.SourceBytes.AsSpan().SequenceEqual(output))
                            SchemaPipeline.WriteIfChanged(builder, projection.Path, output);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException)
                    {
                        diagnostics.Add(new Diagnostic("workbook.generate", DiagnosticSeverity.Blocker, SchemaPipeline.Relative(project, projection.Path), exception.Message));
                    }
                }
            }
        }

        foreach (var diagnostic in diagnostics)
            builder.AddDiagnostic(diagnostic);
        if (purge)
            builder.AddRisk("purge-unmanaged-structure");
        if (rekey)
            builder.AddRisk("recompute-business-keys-and-reference-tokens");
        return builder.Build();
    }

    public async Task<MutationPlan> NormalizeAsync(
        PipelineProject project,
        string? workbookPath = null,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<Diagnostic>();
        var schema = await _schemas.CompileAsync(project, diagnostics, cancellationToken).ConfigureAwait(false);
        var builder = _schemas.CreatePlanBuilder(project, "normalize", schema?.Descriptor.SchemaHash ?? 0);
        SchemaPipeline.ObserveSchemaInputs(builder, project);
        if (schema is null || !CheckGeneratedFresh(project, schema.Descriptor, diagnostics))
        {
            foreach (var diagnostic in diagnostics)
                builder.AddDiagnostic(diagnostic);
            return builder.Build();
        }

        foreach (var workbook in SchemaPipeline.ResolveWorkbooks(project, workbookPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(workbook);
                var inspection = XlsxWorkbookCodec.Inspect(bytes, schema.Descriptor);
                foreach (var diagnostic in inspection.Diagnostics)
                    diagnostics.Add(diagnostic);
                if (inspection.Diagnostics.Any(static item => item.IsFailure))
                    continue;
                var definition = inspection.Workbook;
                var patches = BuildCanonicalPatches(definition, schema.Descriptor);
                if (patches.Length != 0)
                {
                    var normalized = XlsxWorkbookCodec.PatchCells(bytes, patches);
                    SchemaPipeline.WriteIfChanged(builder, workbook, normalized);
                    builder.AddRisk($"legacy-cells={patches.Length}:{SchemaPipeline.Relative(project, workbook)}");
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                diagnostics.Add(new Diagnostic("workbook.normalize", DiagnosticSeverity.Blocker, SchemaPipeline.Relative(project, workbook), exception.Message));
            }
        }

        foreach (var diagnostic in diagnostics)
            builder.AddDiagnostic(diagnostic);
        return builder.Build();
    }

    public async Task<MutationPlan> DataPrepareAsync(
        PipelineProject project,
        string? workbookPath = null,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<Diagnostic>();
        var schema = await _schemas.CompileAsync(project, diagnostics, cancellationToken).ConfigureAwait(false);
        var builder = _schemas.CreatePlanBuilder(project, "data-prepare", schema?.Descriptor.SchemaHash ?? 0);
        SchemaPipeline.ObserveSchemaInputs(builder, project);
        if (schema is null || !CheckGeneratedFresh(project, schema.Descriptor, diagnostics))
        {
            foreach (var diagnostic in diagnostics)
                builder.AddDiagnostic(diagnostic);
            return builder.Build();
        }

        var sources = ReadSources(project, workbookPath, schema.Descriptor, diagnostics);
        if (diagnostics.Any(static item => item.IsFailure))
        {
            foreach (var diagnostic in diagnostics)
                builder.AddDiagnostic(diagnostic);
            return builder.Build();
        }
        // A pending row receives a fresh opaque identity.  Determinism begins at the
        // frozen MutationPlan boundary; identity must never be derived from a key,
        // path, workbook fingerprint, or schema hash.
        var scan = new ProjectIdentityScanner().Scan(sources);
        diagnostics.AddRange(scan.Diagnostics);
        if (!scan.HasBlockers)
        {
            foreach (var source in sources)
            {
                var selected = scan.Observations.Where(observation =>
                    ReferenceEquals(observation.Source, source)
                    && observation.Classification == IdentityClassification.PendingNew).ToArray();
                if (selected.Length == 0)
                    continue;
                var patches = BuildIdentityPatches(source, selected);
                var updated = XlsxWorkbookCodec.PatchCells(File.ReadAllBytes(source.Path), patches);
                SchemaPipeline.WriteIfChanged(builder, source.Path, updated);
                builder.AddRisk($"pending-identities={selected.Length}:{SchemaPipeline.Relative(project, source.Path)}");
            }
        }

        foreach (var diagnostic in diagnostics)
            builder.AddDiagnostic(diagnostic);
        return builder.Build();
    }

    public async Task<WorkbookCheckOutcome> CheckAsync(
        PipelineProject project,
        string? workbookPath = null,
        bool pendingIsBlocker = false,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<Diagnostic>();
        var schema = await _schemas.CompileAsync(project, diagnostics, cancellationToken).ConfigureAwait(false);
        if (schema is null)
            return BuildCheck(project, diagnostics, null, [], 0);
        CheckGeneratedFresh(project, schema.Descriptor, diagnostics);
        var sources = ReadSources(project, workbookPath, schema.Descriptor, diagnostics);
        if (sources.Count == 0 && schema.Descriptor.Tables.Any(static table => table.Kind == CanonicalTableKind.Asset))
            diagnostics.Add(new Diagnostic("workbook.missing", DiagnosticSeverity.Error, project.Context.Project.Workbooks[0], "No workbook matches the Project configuration."));

        var scan = new ProjectIdentityScanner().Scan(sources);
        diagnostics.AddRange(scan.Diagnostics);
        var pending = scan.Observations.Count(static observation => observation.Classification == IdentityClassification.PendingNew);
        if (pending != 0)
        {
            diagnostics.Add(new Diagnostic(
                "identity.pending",
                pendingIsBlocker ? DiagnosticSeverity.Blocker : DiagnosticSeverity.Error,
                "workbooks",
                $"{pending} row(s) have no __guid; run data prepare explicitly."));
        }

        var importedRows = new List<ImportedRow>();
        var domainEntries = new List<WorkbookImportDomainEntry>();
        foreach (var source in sources)
        {
            var imported = WorkbookImporter.Import(
                source.Path,
                File.ReadAllBytes(source.Path),
                source.Workbook,
                schema.Descriptor,
                cellFormats: _cellFormats,
                validators: _validators);
            diagnostics.AddRange(imported.Diagnostics);
            importedRows.AddRange(imported.Rows);
            domainEntries.Add(new WorkbookImportDomainEntry(source.Path, imported));
        }
        diagnostics.AddRange(WorkbookImportDomain.Validate(domainEntries));
        ValidateReferences(schema.Descriptor, importedRows, diagnostics);

        return BuildCheck(project, diagnostics, schema, sources.Select(static source => source.Path), pending);
    }

    public async Task<OperationReport> ConvertAsync(
        PipelineProject project,
        string target,
        string outputPath,
        string? workbookPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!ExportTargetId.TryParse(target, out var exportTarget))
            throw new ArgumentException($"Invalid export target '{target}'.", nameof(target));
        var check = await CheckAsync(project, workbookPath, pendingIsBlocker: true, cancellationToken).ConfigureAwait(false);
        if (check.Schema is null || !check.Report.Succeeded)
            return check.Report with { Operation = "convert" };
        var schema = check.Schema.Descriptor;
        if (!schema.Tables.Any(table => table.ExportTargets.Contains(target, StringComparer.Ordinal)))
        {
            return new OperationReport(
                "convert",
                _toolVersion,
                false,
                [new Diagnostic("target.unknown", DiagnosticSeverity.Blocker, target, "Export target is not declared by the current schema.")],
                []);
        }

        var diagnostics = new List<Diagnostic>();
        var records = new List<RuntimeAssetRecord>();
        var imports = new List<WorkbookImportResult>();
        var importDomainEntries = new List<WorkbookImportDomainEntry>();
        foreach (var path in check.WorkbookPaths)
        {
            var bytes = File.ReadAllBytes(path);
            var inspection = XlsxWorkbookCodec.Inspect(bytes, schema);
            diagnostics.AddRange(inspection.Diagnostics);
            if (inspection.Diagnostics.Any(static item => item.IsFailure))
                continue;
            var workbook = inspection.Workbook;
            var imported = WorkbookImporter.Import(path, bytes, workbook, schema, cellFormats: _cellFormats, validators: _validators);
            diagnostics.AddRange(imported.Diagnostics);
            imports.Add(imported);
            importDomainEntries.Add(new WorkbookImportDomainEntry(path, imported));
        }

        var allRows = imports.SelectMany(static import => import.Rows).ToArray();
        diagnostics.AddRange(WorkbookImportDomain.Validate(importDomainEntries));
        var targetSchema = schema with
        {
            Tables = schema.Tables
                .Where(table => table.ExportTargets.Contains(target, StringComparer.Ordinal))
                .ToImmutableArray(),
        };
        foreach (var imported in imports)
        {
            foreach (var row in imported.Rows.Where(static row => row.IsIndexable && row.Identity is not null))
            {
                var table = schema.Tables.Single(item => item.Id == row.TableId);
                if (!table.ExportTargets.Contains(target, StringComparer.Ordinal))
                    continue;
                var fields = ImmutableArray.CreateBuilder<RuntimeFieldValue>();
                var dependencies = new HashSet<AssetIdentity>();
                foreach (var field in Flatten(table.Fields)
                             .Where(field => field.ExportTargets.Contains(target, StringComparer.Ordinal))
                             .OrderBy(static field => FieldPathKey(field), StringComparer.Ordinal))
                {
                    if (!row.Values.TryGetValue(field.PropertyPath, out var value)
                        || value.State is not (CanonicalValueState.Value or CanonicalValueState.Defaulted))
                        continue;

                    if (IsRowReference(field))
                    {
                        var resolution = RowReferenceResolver.Resolve(field, value.Text!, targetSchema, allRows);
                        if (!resolution.Succeeded)
                        {
                            diagnostics.Add(new Diagnostic(
                                "ref.unresolved",
                                DiagnosticSeverity.Blocker,
                                $"{row.Location}:{field.PropertyPath}",
                                resolution.Error!));
                            continue;
                        }

                        dependencies.Add(resolution.Identity);
                        fields.Add(new RuntimeFieldValue(
                            EffectiveFieldPath(field),
                            Encoding.UTF8.GetBytes(resolution.Identity.ToString())));
                        continue;
                    }

                    fields.Add(new RuntimeFieldValue(EffectiveFieldPath(field), Encoding.UTF8.GetBytes(value.Text!)));
                }

                records.Add(new RuntimeAssetRecord(
                    row.Identity!.Value,
                    row.Key ?? string.Empty,
                    fields,
                    dependencies.OrderBy(static item => item.TableId)
                        .ThenBy(static item => item.RowGuid.ToString(), StringComparer.Ordinal),
                    $"{table.Name}/{row.Key}"));
            }
        }

        if (diagnostics.Any(static item => item.IsFailure))
            return new OperationReport("convert", _toolVersion, false, diagnostics.ToImmutableArray(), []);

        var contentHash = ComputeTargetContentHash(schema.SchemaHash, exportTarget, records);
        using var snapshot = new SourceSnapshot(schema.SchemaHash, exportTarget, contentHash[..16], contentHash, records);
        ConvertedBytesPackage package;
        try
        {
            package = ConvertedBytesWriter.Build(snapshot, _toolVersion);
        }
        catch (InvalidDataException exception)
        {
            return new OperationReport("convert", _toolVersion, false, [new Diagnostic("convert.invalid", DiagnosticSeverity.Blocker, outputPath, exception.Message)], []);
        }

        var fullOutput = project.Context.ResolvePath(outputPath);
        var manifestPath = fullOutput + ".manifest.json";
        var builder = _schemas.CreatePlanBuilder(project, "convert", schema.SchemaHash);
        SchemaPipeline.ObserveSchemaInputs(builder, project);
        foreach (var path in check.WorkbookPaths)
            builder.Observe(path);
        builder.WriteFile(fullOutput, package.Bytes);
        builder.WriteFile(manifestPath, Encoding.UTF8.GetBytes(package.ManifestJson + "\n"));
        builder.AddRisk($"export-target={target}");
        var plan = builder.Build();
        var report = new MutationPlanApplier().Apply(plan);
        return report with
        {
            Diagnostics = report.Diagnostics.Add(new Diagnostic("convert.target", DiagnosticSeverity.Info, target, $"Converted target '{target}', source {contentHash}.")),
        };
    }

    private static string ComputeTargetContentHash(
        ulong schemaHash,
        ExportTargetId target,
        IEnumerable<RuntimeAssetRecord> records)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true);
        writer.Write(schemaHash);
        WriteHashString(writer, target.Value);
        foreach (var record in records
                     .OrderBy(static item => item.Identity.TableId)
                     .ThenBy(static item => item.Identity.RowGuid.ToString(), StringComparer.Ordinal))
        {
            writer.Write(record.Identity.TableId);
            WriteHashString(writer, record.Identity.RowGuid.ToString());
            WriteHashString(writer, record.Key);
            WriteHashString(writer, record.Path);
            writer.Write(record.Fields.Length);
            foreach (var field in record.Fields.OrderBy(static item => string.Join('.', item.FieldIdPath), StringComparer.Ordinal))
            {
                writer.Write(field.FieldIdPath.Length);
                foreach (var fieldNumber in field.FieldIdPath)
                    writer.Write(fieldNumber);
                writer.Write(field.Data.Length);
                writer.Write(field.Data.Span);
            }
            writer.Write(record.Dependencies.Length);
            foreach (var dependency in record.Dependencies
                         .OrderBy(static item => item.TableId)
                         .ThenBy(static item => item.RowGuid.ToString(), StringComparer.Ordinal))
            {
                writer.Write(dependency.TableId);
                WriteHashString(writer, dependency.RowGuid.ToString());
            }
        }
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void WriteHashString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    public WorkbookDiffReport Diff(string basePath, string targetPath)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            var left = XlsxWorkbookCodec.Read(File.ReadAllBytes(basePath));
            var right = XlsxWorkbookCodec.Read(File.ReadAllBytes(targetPath));
            var entries = DiffRows(left, right);
            return new WorkbookDiffReport(1, Path.GetFullPath(basePath), Path.GetFullPath(targetPath), entries, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic("diff.invalid", DiagnosticSeverity.Error, targetPath, exception.Message));
            return new WorkbookDiffReport(1, Path.GetFullPath(basePath), Path.GetFullPath(targetPath), [], diagnostics.ToImmutable());
        }
    }

    private static WorkbookCheckOutcome BuildCheck(
        PipelineProject project,
        IEnumerable<Diagnostic> diagnostics,
        CompiledProjectSchema? schema,
        IEnumerable<string> workbooks,
        int pending)
    {
        var ordered = diagnostics.OrderByDescending(static item => item.Severity).ThenBy(static item => item.Code, StringComparer.Ordinal).ThenBy(static item => item.Location, StringComparer.Ordinal).ToImmutableArray();
        var artifacts = workbooks.Select(path => new ArtifactRecord("workbook", path, File.Exists(path) ? ContentFingerprint.FromFile(path).Sha256 : null)).ToImmutableArray();
        return new WorkbookCheckOutcome(new OperationReport("check", "pipeline-v1", false, ordered, artifacts), schema, workbooks.ToImmutableArray(), pending);
    }

    private List<WorkbookSource> ReadSources(
        PipelineProject project,
        string? workbookPath,
        CanonicalSchemaDescriptor schema,
        ICollection<Diagnostic> diagnostics)
    {
        var sources = new List<WorkbookSource>();
        foreach (var path in SchemaPipeline.ResolveWorkbooks(project, workbookPath))
        {
            if (!File.Exists(path))
            {
                diagnostics.Add(new Diagnostic("workbook.missing", DiagnosticSeverity.Error, SchemaPipeline.Relative(project, path), "Workbook does not exist."));
                continue;
            }
            try
            {
                var bytes = File.ReadAllBytes(path);
                var inspection = XlsxWorkbookCodec.Inspect(bytes, schema);
                foreach (var diagnostic in inspection.Diagnostics)
                    diagnostics.Add(diagnostic);
                var physical = inspection.Workbook;
                var imported = WorkbookImporter.Import(path, bytes, physical, schema, cellFormats: _cellFormats, validators: _validators);
                var identityBound = WorkbookIdentityProjection.BindCanonicalKeys(physical, imported);
                sources.Add(new WorkbookSource(path, identityBound, ContentFingerprint.FromBytes(bytes)));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                diagnostics.Add(new Diagnostic("workbook.invalid", DiagnosticSeverity.Error, SchemaPipeline.Relative(project, path), exception.Message));
            }
        }
        return sources;
    }

    private static bool CheckGeneratedFresh(PipelineProject project, CanonicalSchemaDescriptor schema, ICollection<Diagnostic> diagnostics)
    {
        var generation = new SchemaCodeGenerator().Generate(schema);
        var ok = true;
        foreach (var artifact in generation.Artifacts)
        {
            var path = Path.Combine(project.Context.GeneratedDirectory, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var expected = Encoding.UTF8.GetBytes(artifact.Content);
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
                continue;
            diagnostics.Add(new Diagnostic("codegen.stale", DiagnosticSeverity.Error, SchemaPipeline.Relative(project, path), "Generated C# is missing or stale; run schema build."));
            ok = false;
        }
        var manifestPath = Path.Combine(project.Context.GeneratedDirectory, "codegen.manifest.json");
        var manifest = Encoding.UTF8.GetBytes(generation.Manifest.Json + "\n");
        if (!File.Exists(manifestPath) || !File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(manifest))
        {
            diagnostics.Add(new Diagnostic("codegen.stale", DiagnosticSeverity.Error, SchemaPipeline.Relative(project, manifestPath), "Codegen manifest is missing or stale; run schema build."));
            ok = false;
        }
        return ok;
    }

    private WorkbookDefinition ProjectWorkbookDefinition(
        WorkbookDefinition current,
        CanonicalSchemaDescriptor schema,
        bool purge,
        bool rekey)
    {
        var projectedTables = new List<WorkbookTable>();
        foreach (var table in current.Tables)
        {
            var tableSchema = schema.Tables.SingleOrDefault(item => item.Id == table.TableId && item.Kind == CanonicalTableKind.Asset);
            if (tableSchema is null)
            {
                if (!purge)
                    projectedTables.Add(table);
                continue;
            }
            var layout = WorkbookLayout.CreateTable(tableSchema);
            var oldProperties = table.Columns.ToDictionary(static column => column.FieldPath, static column => column.PropertyPath, StringComparer.Ordinal);
            var rows = table.Rows.Select(row =>
            {
                var cells = ImmutableDictionary.CreateBuilder<string, WorkbookCell>(StringComparer.Ordinal);
                foreach (var column in layout.Columns)
                {
                    if (row.Cells.TryGetValue(column.PropertyPath, out var value))
                        cells[column.PropertyPath] = value;
                    else if (oldProperties.TryGetValue(column.FieldPath, out var old) && row.Cells.TryGetValue(old, out value))
                        cells[column.PropertyPath] = value;
                }
                var projectedCells = cells.ToImmutable();
                var key = rekey
                    ? BuildCanonicalKey(schema, tableSchema, projectedCells)
                    : row.Key;
                return row with { Cells = projectedCells, Key = key };
            }).ToImmutableArray();
            projectedTables.Add(table with { ProtoName = tableSchema.Name, SheetName = tableSchema.SheetName, Columns = layout.Columns, Rows = rows });
        }

        var present = projectedTables.Select(static table => table.TableId).ToHashSet();
        projectedTables.AddRange(schema.Tables
            .Where(static table => table.Kind == CanonicalTableKind.Asset)
            .Where(table => !present.Contains(table.Id))
            .OrderBy(static table => table.Id)
            .Select(WorkbookLayout.CreateTable));
        var currentChildren = current.EffectiveChildTables.ToDictionary(
            static table => $"{table.OwnerTableId}:{string.Join('.', table.OwnerFieldIdPath)}",
            StringComparer.Ordinal);
        var projectedChildren = new List<WorkbookChildTable>();
        foreach (var layout in WorkbookLayout.CreateChildTables(schema))
        {
            var identity = $"{layout.OwnerTableId}:{string.Join('.', layout.OwnerFieldIdPath)}";
            if (!currentChildren.TryGetValue(identity, out var old))
            {
                projectedChildren.Add(layout);
                continue;
            }
            var oldProperties = old.Columns.ToDictionary(
                static column => column.FieldPath,
                static column => column.PropertyPath,
                StringComparer.Ordinal);
            var rows = old.Rows.Select(row =>
            {
                var cells = ImmutableDictionary.CreateBuilder<string, WorkbookCell>(StringComparer.Ordinal);
                foreach (var column in layout.Columns)
                {
                    if (row.Cells.TryGetValue(column.PropertyPath, out var value))
                        cells[column.PropertyPath] = value;
                    else if (oldProperties.TryGetValue(column.FieldPath, out var previous)
                             && row.Cells.TryGetValue(previous, out value))
                        cells[column.PropertyPath] = value;
                }
                return row with { Cells = cells.ToImmutable() };
            }).ToImmutableArray();
            projectedChildren.Add(layout with { SheetName = layout.SheetName, Rows = rows });
            currentChildren.Remove(identity);
        }
        if (!purge)
            projectedChildren.AddRange(currentChildren.Values);
        return current with
        {
            SchemaHash = schema.SchemaHash,
            Tables = projectedTables.OrderBy(static table => table.TableId).ToImmutableArray(),
            ChildTables = projectedChildren
                .OrderBy(static table => table.OwnerTableId)
                .ThenBy(static table => string.Join('.', table.OwnerFieldIdPath), StringComparer.Ordinal)
                .ToImmutableArray(),
        };
    }

    private string BuildCanonicalKey(
        CanonicalSchemaDescriptor schema,
        CanonicalTableDescriptor table,
        ImmutableDictionary<string, WorkbookCell> cells)
    {
        var keyFields = Flatten(table.Fields)
            .Where(static field => field.KeyOrder > 0)
            .OrderBy(static field => field.KeyOrder)
            .ToArray();
        if (keyFields.Length == 0)
            throw new InvalidDataException($"Table '{table.FullName}' has no key fields.");

        var components = new string[keyFields.Length];
        for (var index = 0; index < keyFields.Length; index++)
        {
            var field = keyFields[index];
            cells.TryGetValue(field.PropertyPath, out var cell);
            var parsed = CanonicalCellParser.Parse(cell, field, schema, _cellFormats);
            if (parsed.Error is not null
                || parsed.Value.State is not (CanonicalValueState.Value or CanonicalValueState.Defaulted)
                || string.IsNullOrEmpty(parsed.Value.Text))
            {
                throw new InvalidDataException(
                    $"Cannot rekey table '{table.FullName}': key field '{field.PropertyPath}' is missing or invalid ({parsed.Error ?? parsed.Value.State.ToString()}).");
            }

            components[index] = parsed.Value.Text;
        }

        return CanonicalKeyCodec.Format(components);
    }

    private static IReadOnlyDictionary<(int TableId, string OldKey), string> BuildRekeyMap(
        IEnumerable<WorkbookProjectionCandidate> projections)
    {
        var map = new Dictionary<(int TableId, string OldKey), string>();
        var newKeys = new HashSet<(int TableId, string NewKey)>();
        foreach (var projection in projections.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            var projectedTables = projection.Projected.Tables.ToDictionary(static table => table.TableId);
            foreach (var sourceTable in projection.Source.Tables.OrderBy(static table => table.TableId))
            {
                if (!projectedTables.TryGetValue(sourceTable.TableId, out var projectedTable))
                    continue;
                var projectedByRow = projectedTable.Rows.ToDictionary(
                    row => row.SourceRowNumber ?? projectedTable.DataStartRow,
                    static row => row);
                foreach (var sourceRow in sourceTable.Rows)
                {
                    var rowNumber = sourceRow.SourceRowNumber ?? sourceTable.DataStartRow;
                    if (!projectedByRow.TryGetValue(rowNumber, out var projectedRow))
                        continue;
                    if (string.IsNullOrEmpty(sourceRow.Key) || string.IsNullOrEmpty(projectedRow.Key))
                        throw new InvalidDataException($"Cannot rekey {sourceTable.SheetName}!{rowNumber}: the old or new key is unavailable.");
                    var oldIdentity = (sourceTable.TableId, sourceRow.Key);
                    if (map.TryGetValue(oldIdentity, out var existing)
                        && !string.Equals(existing, projectedRow.Key, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"Old key '{sourceRow.Key}' in table {sourceTable.TableId} is ambiguous.");
                    }
                    map[oldIdentity] = projectedRow.Key;
                    if (!newKeys.Add((sourceTable.TableId, projectedRow.Key)))
                        throw new InvalidDataException($"Rekey produces duplicate key '{projectedRow.Key}' in table {sourceTable.TableId}.");
                }
            }
        }
        return map;
    }

    private static WorkbookDefinition RewriteReferenceTokens(
        WorkbookDefinition workbook,
        CanonicalSchemaDescriptor schema,
        IReadOnlyDictionary<(int TableId, string OldKey), string> rekeyMap)
    {
        var tableSchemas = schema.Tables.ToDictionary(static table => table.Id);
        var rekeyedTableIds = rekeyMap.Keys.Select(static key => key.TableId).ToHashSet();
        var tables = workbook.Tables.Select(table =>
        {
            if (!tableSchemas.TryGetValue(table.TableId, out var tableSchema))
                return table;
            var referenceFields = Flatten(tableSchema.Fields)
                .Where(IsRowReference)
                .ToDictionary(static field => field.PropertyPath, StringComparer.Ordinal);
            if (referenceFields.Count == 0)
                return table;
            var rows = table.Rows.Select(row =>
            {
                var cells = row.Cells.ToBuilder();
                foreach (var pair in referenceFields)
                {
                    if (!cells.TryGetValue(pair.Key, out var cell)
                        || cell.Formula is not null
                        || string.IsNullOrEmpty(cell.Text))
                        continue;
                    if (!RowReferenceToken.TryParse(cell.Text, out var token, out var error))
                        throw new InvalidDataException($"Cannot rekey RowRef '{cell.Text}' at {table.SheetName}:{pair.Key}: {error}");
                    var oldKey = CanonicalKeyCodec.Format(token.KeyComponents);
                    if (!rekeyMap.TryGetValue((token.TableId, oldKey), out var newKey))
                    {
                        if (rekeyedTableIds.Contains(token.TableId))
                        {
                            throw new InvalidDataException(
                                $"Cannot rekey RowRef '{cell.Text}' at {table.SheetName}:{pair.Key}: the referenced old key is absent or ambiguous.");
                        }
                        continue;
                    }
                    if (!CanonicalKeyCodec.TryParse(newKey, out var newComponents))
                        throw new InvalidDataException($"Rekey produced an invalid canonical key '{newKey}'.");
                    cells[pair.Key] = new WorkbookCell(RowReferenceToken.Format(token.TableId, newComponents));
                }
                return row with { Cells = cells.ToImmutable() };
            }).ToImmutableArray();
            return table with { Rows = rows };
        }).ToImmutableArray();
        return workbook with { Tables = tables };
    }

    private static CellPatch[] BuildIdentityPatches(
        WorkbookSource source,
        IReadOnlyCollection<IdentityObservation> selected)
    {
        var keyProjectionRows = new Dictionary<(int TableId, int SourceRow), int>();
        var projectionRow = 2;
        foreach (var table in source.Workbook.Tables.OrderBy(static table => table.TableId))
        {
            foreach (var row in table.Rows)
            {
                keyProjectionRows[(table.TableId, row.SourceRowNumber ?? table.DataStartRow)] = projectionRow++;
            }
        }

        var patches = new List<CellPatch>(selected.Count * 2);
        foreach (var observation in selected
                     .OrderBy(static item => item.Table.TableId)
                     .ThenBy(static item => item.Location.RowNumber))
        {
            var guid = observation.CandidateGuid
                ?? throw new InvalidOperationException($"Pending row {observation.Location} has no candidate guid.");
            patches.Add(new CellPatch(
                observation.Table.SheetName,
                observation.Location.RowNumber,
                observation.Table.Columns.Length + 1,
                new WorkbookCell(guid.ToString())));
            if (keyProjectionRows.TryGetValue(
                    (observation.Table.TableId, observation.Location.RowNumber),
                    out var keyProjectionRow))
            {
                patches.Add(new CellPatch(
                    WorkbookProtocol.KeySheetName,
                    keyProjectionRow,
                    2,
                    new WorkbookCell(guid.ToString())));
            }
        }

        return patches.ToArray();
    }

    private ImmutableArray<CellPatch> BuildCanonicalPatches(WorkbookDefinition workbook, CanonicalSchemaDescriptor schema)
    {
        var patches = ImmutableArray.CreateBuilder<CellPatch>();
        foreach (var table in workbook.Tables)
        {
            var tableSchema = schema.Tables.SingleOrDefault(item => item.Id == table.TableId);
            if (tableSchema is null)
                continue;
            var fields = Flatten(tableSchema.Fields).ToDictionary(static field => field.PropertyPath, StringComparer.Ordinal);
            for (var rowIndex = 0; rowIndex < table.Rows.Length; rowIndex++)
            {
                var row = table.Rows[rowIndex];
                var sourceRow = row.SourceRowNumber ?? table.DataStartRow + rowIndex;
                for (var columnIndex = 0; columnIndex < table.Columns.Length; columnIndex++)
                {
                    var column = table.Columns[columnIndex];
                    if (!fields.TryGetValue(column.PropertyPath, out var field)
                        || !row.Cells.TryGetValue(column.PropertyPath, out var cell)
                        || cell.Formula is not null
                        || cell.Text is null)
                        continue;
                    var parsed = CanonicalCellParser.Parse(cell, field, schema, _cellFormats);
                    if (parsed.Error is null
                        && parsed.Value.State is CanonicalValueState.Value or CanonicalValueState.Defaulted
                        && !string.Equals(parsed.CanonicalPhysicalText ?? parsed.Value.Text, cell.Text, StringComparison.Ordinal))
                    {
                        patches.Add(new CellPatch(
                            table.SheetName,
                            sourceRow,
                            columnIndex + 1,
                            new WorkbookCell(parsed.CanonicalPhysicalText ?? parsed.Value.Text)));
                    }
                }
            }
        }
        return patches.ToImmutable();
    }

    private static void ValidateReferences(
        CanonicalSchemaDescriptor schema,
        IReadOnlyCollection<ImportedRow> rows,
        ICollection<Diagnostic> diagnostics)
    {
        var tables = schema.Tables.ToDictionary(static table => table.Id);
        foreach (var row in rows.Where(static row => row.IsIndexable))
        {
            if (!tables.TryGetValue(row.TableId, out var table))
                continue;
            foreach (var field in Flatten(table.Fields).Where(IsRowReference))
            {
                if (!row.Values.TryGetValue(field.PropertyPath, out var value)
                    || value.State is not (CanonicalValueState.Value or CanonicalValueState.Defaulted))
                    continue;
                var resolution = RowReferenceResolver.Resolve(field, value.Text!, schema, rows);
                if (!resolution.Succeeded)
                {
                    diagnostics.Add(new Diagnostic(
                        "ref.unresolved",
                        DiagnosticSeverity.Blocker,
                        $"{row.Location}:{field.PropertyPath}",
                        resolution.Error!));
                }
            }
        }
    }

    private static bool IsRowReference(CanonicalFieldDescriptor field) =>
        field.Shape == CanonicalFieldShape.Message
        && string.Equals(field.TypeName, "exceldb.RowRef", StringComparison.Ordinal);

    private static ImmutableArray<int> EffectiveFieldPath(CanonicalFieldDescriptor field) =>
        field.FieldIdPath.IsDefaultOrEmpty ? [field.Id] : field.FieldIdPath;

    private static string FieldPathKey(CanonicalFieldDescriptor field) =>
        string.Join('.', EffectiveFieldPath(field));

    private static IEnumerable<CanonicalFieldDescriptor> Flatten(IEnumerable<CanonicalFieldDescriptor> fields)
    {
        foreach (var field in fields)
        {
            yield return field;
            foreach (var child in Flatten(field.Children))
                yield return child;
        }
    }

    private static ImmutableArray<WorkbookDiffEntry> DiffRows(WorkbookDefinition left, WorkbookDefinition right)
    {
        var entries = new List<WorkbookDiffEntry>();
        var leftRows = Rows(left);
        var rightRows = Rows(right);
        foreach (var identity in leftRows.Keys.Union(rightRows.Keys).Order(StringComparer.Ordinal))
        {
            var hasLeft = leftRows.TryGetValue(identity, out var before);
            var hasRight = rightRows.TryGetValue(identity, out var after);
            if (!hasLeft)
            {
                entries.Add(new WorkbookDiffEntry("added", after!.TableId, after.Sheet, identity, null, null, after.Row.Key));
                continue;
            }
            if (!hasRight)
            {
                entries.Add(new WorkbookDiffEntry("removed", before!.TableId, before.Sheet, identity, null, before.Row.Key, null));
                continue;
            }
            if (!string.Equals(before!.Row.Key, after!.Row.Key, StringComparison.Ordinal))
                entries.Add(new WorkbookDiffEntry("renamed", after.TableId, after.Sheet, identity, "$key", before.Row.Key, after.Row.Key));
            if (before.TableId != after.TableId || !string.Equals(before.Sheet, after.Sheet, StringComparison.Ordinal))
                entries.Add(new WorkbookDiffEntry("moved", after.TableId, after.Sheet, identity, "$location", $"{before.TableId}/{before.Sheet}", $"{after.TableId}/{after.Sheet}"));
            foreach (var property in before.Row.Cells.Keys.Union(after.Row.Cells.Keys).Order(StringComparer.Ordinal))
            {
                before.Row.Cells.TryGetValue(property, out var oldCell);
                after.Row.Cells.TryGetValue(property, out var newCell);
                var oldText = oldCell?.ComparisonText;
                var newText = newCell?.ComparisonText;
                if (!string.Equals(oldText, newText, StringComparison.Ordinal))
                    entries.Add(new WorkbookDiffEntry("modified", after.TableId, after.Sheet, identity, property, oldText, newText));
            }
        }
        return entries.OrderBy(static item => item.TableId).ThenBy(static item => item.Identity, StringComparer.Ordinal).ThenBy(static item => item.PropertyPath, StringComparer.Ordinal).ThenBy(static item => item.Kind, StringComparer.Ordinal).ToImmutableArray();
    }

    private static Dictionary<string, RowAt> Rows(WorkbookDefinition workbook)
    {
        var result = new Dictionary<string, RowAt>(StringComparer.Ordinal);
        foreach (var table in workbook.Tables)
        {
            for (var index = 0; index < table.Rows.Length; index++)
            {
                var row = table.Rows[index];
                var identity = row.RowGuid?.ToString() ?? $"pending:{table.TableId}:{row.SourceRowNumber ?? table.DataStartRow + index}";
                result.TryAdd(identity, new RowAt(table.TableId, table.SheetName, row));
            }
        }
        return result;
    }

    private static Guid DeterministicGuid(string value) => DeterministicRowGuid(value).Value;

    private static RowGuid DeterministicRowGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16];
        if (bytes.All(static item => item == 0))
            bytes[0] = 1;
        return new RowGuid(new Guid(bytes));
    }

    private sealed record WorkbookProjectionCandidate(
        string Path,
        byte[] SourceBytes,
        WorkbookDefinition Source,
        WorkbookDefinition Projected);

    private sealed record RowAt(int TableId, string Sheet, WorkbookRow Row);
}
