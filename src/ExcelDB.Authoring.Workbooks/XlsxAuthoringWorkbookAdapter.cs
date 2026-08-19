using System.Collections.Immutable;
using System.Text.Json;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Core.Values;
using ExcelDb.Runtime;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Authoring;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.Identity;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;
using ExcelDb.Workbooks.Transactions;
using ExcelDbEditor;

namespace ExcelDb.Authoring.Workbooks;

public sealed class XlsxAuthoringWorkbookAdapter : ITransactionalAuthoringWorkbookAdapter, IAuthoringConflictSource
{
    public const string ToolVersion = "authoring-workbooks-v1";

    private readonly object _gate = new();
    private readonly CanonicalSchemaDescriptor _schema;
    private readonly IReadOnlyDictionary<int, RuntimeTableBinding> _bindings;
    private readonly IReadOnlyDictionary<int, IAuthoringWorkbookTableCodec> _codecs;
    private readonly IReadOnlyDictionary<string, IAuthoringWorkbookTableCodec> _codecsByName;
    private readonly IWorkbookLocationOpener? _opener;
    private readonly CellFormatRegistry _cellFormats;
    private readonly WorkbookValidatorRegistry _validators;
    private readonly Dictionary<string, AdapterState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<AssetIdentity>> _sessionRevisionAhead = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ConflictId, ConflictKey> _lastConflictKeys = [];
    private readonly Dictionary<ConflictKey, ConflictResolutionAction> _conflictResolutions = [];
    private ImmutableArray<ExcelDbEditor.ConflictRecord> _lastConflicts = [];

    public XlsxAuthoringWorkbookAdapter(
        CanonicalSchemaDescriptor schema,
        RuntimeSchemaRegistry runtimeRegistry,
        IWorkbookLocationOpener? opener = null,
        CellFormatRegistry? cellFormats = null,
        WorkbookValidatorRegistry? validators = null)
        : this(schema, runtimeRegistry, CreateReflectionCodecs(schema, runtimeRegistry), opener, cellFormats, validators)
    {
    }

    public XlsxAuthoringWorkbookAdapter(
        CanonicalSchemaDescriptor schema,
        RuntimeSchemaRegistry runtimeRegistry,
        IEnumerable<IAuthoringWorkbookTableCodec> codecs,
        IWorkbookLocationOpener? opener = null,
        CellFormatRegistry? cellFormats = null,
        WorkbookValidatorRegistry? validators = null)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        Guard.NotNull(runtimeRegistry);
        Guard.NotNull(codecs);
        if (runtimeRegistry.ExpectedSchemaHash != schema.SchemaHash)
        {
            throw new ArgumentException(
                $"Runtime schema {runtimeRegistry.ExpectedSchemaHash:x16} does not match workbook schema {schema.SchemaHash:x16}.",
                nameof(runtimeRegistry));
        }

        _bindings = runtimeRegistry.Bindings.ToDictionary(static binding => binding.TableId);
        var codecArray = codecs.ToArray();
        _codecs = codecArray.ToDictionary(static codec => codec.TableId);
        _codecsByName = codecArray.ToDictionary(static codec => codec.TableName, StringComparer.Ordinal);
        _opener = opener;
        _cellFormats = cellFormats ?? new CellFormatRegistry();
        _validators = validators ?? new WorkbookValidatorRegistry();

        foreach (var table in schema.Tables.Where(static table => table.Kind == CanonicalTableKind.Asset))
        {
            if (!_bindings.TryGetValue(table.Id, out var binding))
                throw new ArgumentException($"Runtime registry has no binding for table {table.Id}/{table.Name}.", nameof(runtimeRegistry));
            if (!_codecs.TryGetValue(table.Id, out var codec))
                throw new ArgumentException($"No workbook codec is registered for table {table.Id}/{table.Name}.", nameof(codecs));
            if (!string.Equals(codec.TableName, table.Name, StringComparison.Ordinal)
                || codec.AssetType != binding.RuntimeType)
            {
                throw new ArgumentException(
                    $"Workbook codec {codec.TableId}/{codec.TableName}/{codec.AssetType.FullName} does not match schema/runtime binding.",
                    nameof(codecs));
            }
        }
    }

    public ImmutableArray<ExcelDbEditor.ConflictRecord> LastConflicts
    {
        get
        {
            lock (_gate)
                return _lastConflicts;
        }
    }

    public ImmutableArray<ExcelDbEditor.ConflictRecord> Conflicts => LastConflicts;

    public bool ResolveConflict(ConflictId id, ConflictResolutionAction action)
    {
        lock (_gate)
        {
            var index = -1;
            for (var candidate = 0; candidate < _lastConflicts.Length; candidate++)
            {
                if (_lastConflicts[candidate].Id != id)
                    continue;
                index = candidate;
                break;
            }
            if (index < 0)
                return false;
            if (!_lastConflictKeys.TryGetValue(id, out var key))
                return false;
            _conflictResolutions[key] = action;
            _lastConflictKeys.Remove(id);
            _lastConflicts = _lastConflicts.RemoveAt(index);
            return true;
        }
    }

    public AuthoringWorkbookImport Import(string workbookPath, bool forceUpdate)
    {
        lock (_gate)
        {
            var path = NormalizePath(workbookPath);
            try
            {
                var recovery = AuthoringStartupRecovery.RecoverWorkbookDirectoryBeforeImport(path);
                if (!recovery.Succeeded)
                {
                    return new AuthoringWorkbookImport(
                        path,
                        [],
                        recovery with { Operation = "authoring-import" });
                }
                if (!forceUpdate
                    && _states.TryGetValue(path, out var cached)
                    && File.Exists(path)
                    && ContentFingerprint.FromFile(path) == cached.SourceFingerprint)
                {
                    return cached.LastImport;
                }

                var bytes = File.ReadAllBytes(path);
                var previous = _states.GetValueOrDefault(path);
                var built = BuildState(
                    path,
                    bytes,
                    previous?.Imported.Snapshot,
                    clientFingerprint: ContentFingerprint.FromBytes(bytes).ToString());
                if (!built.Import.Report.Succeeded || built.State is null)
                    return built.Import;
                _states[path] = built.State;
                _sessionRevisionAhead[path] = [];
                _lastConflicts = [];
                return built.Import;
            }
            catch (Exception exception) when (IsWorkbookException(exception))
            {
                return FailedImport(path, "EXAW0001", exception.Message);
            }
        }
    }

    public OperationReport Save(AuthoringSaveRequest request)
    {
        Guard.NotNull(request);
        return Save([request]);
    }

    public OperationReport Save(ImmutableArray<AuthoringSaveRequest> requests)
    {
        if (requests.IsDefault)
            throw new ArgumentException("Save request array must be initialized.", nameof(requests));
        lock (_gate)
        {
            _lastConflicts = [];
            _lastConflictKeys.Clear();
            if (requests.IsEmpty)
                return OperationReport.Success("authoring-save", ToolVersion, applied: false);
            var duplicatePath = requests
                .GroupBy(static request => NormalizePath(request.WorkbookPath), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(static group => group.Count() > 1);
            if (duplicatePath is not null)
                return Failure("EXAW0103", duplicatePath.Key, "A multi-workbook save repeats a workbook target.");

            try
            {
                var plannedDeletes = requests
                    .SelectMany(static request => request.Items.Where(static item => item.Deleted))
                    .Where(static item => !item.Guid.Empty())
                    .Select(static item => new AssetIdentity(item.TableId, item.Guid.Value))
                    .ToImmutableHashSet();
                var preparations = ImmutableArray.CreateBuilder<SavePreparation>();
                foreach (var request in requests.OrderBy(static request => NormalizePath(request.WorkbookPath), StringComparer.Ordinal))
                {
                    var result = PrepareSave(request, plannedDeletes);
                    if (!result.Report.Succeeded)
                        return result.Report;
                    if (result.Preparation is not null)
                        preparations.Add(result.Preparation);
                }

                if (preparations.Count == 0)
                    return OperationReport.Success("authoring-save", ToolVersion, applied: false);

                var preparationArray = preparations.ToImmutable();
                var combinedPlanHash = string.Join(
                    "+",
                preparationArray
                    .Select(static item => item.WritePlan.PlanHash)
                    .OrderBy(static planHash => planHash, StringComparer.Ordinal));
                var transaction = MultiWorkbookTransaction.Commit(
                    preparationArray.Select(static item => item.PreparedWrite));
                if (!transaction.Report.Succeeded)
                {
                    return transaction.Report with
                    {
                        Operation = "authoring-save",
                        PlanHash = combinedPlanHash,
                    };
                }

                foreach (var preparation in preparationArray)
                {
                    ApplyMergedValuesToSubmittedAssets(
                        preparation.Request.Items,
                        preparation.MergedRows,
                        preparation.CurrentWorkbook);
                }

                foreach (var preparation in preparationArray)
                    _states.Remove(preparation.Path);
                var artifacts = ImmutableArray.CreateBuilder<ArtifactRecord>();
                foreach (var preparation in preparationArray)
                {
                    var finalBytes = File.ReadAllBytes(preparation.Path);
                    var rebuilt = BuildState(
                        preparation.Path,
                        finalBytes,
                        preparation.Theirs.Snapshot,
                        preparation.State.ClientFingerprint);
                    if (rebuilt.State is null)
                    {
                        return rebuilt.Import.Report with
                        {
                            Operation = "authoring-save",
                            Applied = true,
                            PlanHash = combinedPlanHash,
                        };
                    }

                    _states[preparation.Path] = rebuilt.State;
                    var revisionAhead = _sessionRevisionAhead.GetValueOrDefault(preparation.Path) ?? [];
                    foreach (var pair in preparation.CommitChanges)
                    {
                        if (pair.Value.Kind == DraftChangeKind.Added)
                            revisionAhead.Add(pair.Key);
                        else if (pair.Value.Kind == DraftChangeKind.Deleted)
                            revisionAhead.Remove(pair.Key);
                    }

                    _sessionRevisionAhead[preparation.Path] = revisionAhead;
                    artifacts.Add(new ArtifactRecord(
                        "workbook",
                        preparation.Path,
                        rebuilt.State.SourceFingerprint.Sha256));
                    foreach (var conflictKey in preparation.AppliedResolutionKeys)
                        _conflictResolutions.Remove(conflictKey);
                }

                return new OperationReport(
                    "authoring-save",
                    ToolVersion,
                    true,
                    [],
                    artifacts.ToImmutable(),
                    combinedPlanHash);
            }
            catch (Exception exception) when (IsWorkbookException(exception))
            {
                return Failure("EXAW0199", ".", exception.Message);
            }
        }
    }

    private SavePreparationResult PrepareSave(
        AuthoringSaveRequest request,
        ImmutableHashSet<AssetIdentity> plannedDeletes)
    {
        var path = NormalizePath(request.WorkbookPath);
        if (!_states.TryGetValue(path, out var state))
        {
            var imported = Import(path, forceUpdate: true);
            if (!imported.Report.Succeeded || !_states.TryGetValue(path, out state))
                return new SavePreparationResult(imported.Report with { Operation = "authoring-save" }, null);
        }

        if (request.ExpectedFingerprint is not null
            && !string.Equals(request.ExpectedFingerprint, state.ClientFingerprint, StringComparison.Ordinal))
        {
            return FailedPreparation(
                "EXAW0100",
                path,
                "Save request was created from a different mounted workbook generation.");
        }

        var duplicateItem = request.Items
            .GroupBy(static item => item.Guid)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateItem is not null)
            return FailedPreparation("EXAW0101", duplicateItem.Key.ToString(), "Save request repeats an asset GUID.");
        if (request.Items.IsDefaultOrEmpty)
            return new SavePreparationResult(OperationReport.Success("authoring-save", ToolVersion, applied: false), null);

        var currentBytes = File.ReadAllBytes(path);
        var currentWorkbook = XlsxWorkbookCodec.Read(currentBytes);
        var theirs = WorkbookImporter.Import(
            path,
            currentBytes,
            currentWorkbook,
            _schema,
            state.Imported.Snapshot,
            _cellFormats);
        if (theirs.Diagnostics.Any(static diagnostic => diagnostic.IsFailure))
        {
            return new SavePreparationResult(
                new OperationReport("authoring-save", ToolVersion, false, theirs.Diagnostics, []),
                null);
        }

        state.Drafts.Discard();
        var capturedRows = new Dictionary<AssetIdentity, SnapshotRow>();
        var preparationDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var item in request.Items)
            PrepareDraft(state, theirs, item, capturedRows, preparationDiagnostics, plannedDeletes);
        if (preparationDiagnostics.Any(static diagnostic => diagnostic.IsFailure))
        {
            return new SavePreparationResult(
                new OperationReport("authoring-save", ToolVersion, false, preparationDiagnostics.ToImmutable(), []),
                null);
        }

        var mine = state.Drafts.Changes.ToBuilder();
        foreach (var pair in capturedRows)
        {
            if (mine.TryGetValue(pair.Key, out var draft) && draft.Row is not null)
                mine[pair.Key] = draft with { Row = pair.Value };
        }

        var merge = ThreeWayMerge.Merge(state.Imported.Snapshot, theirs.Snapshot, mine);
        var appliedResolutionKeys = ImmutableHashSet<ConflictKey>.Empty;
        if (!merge.CanCommit)
        {
            merge = ApplyConflictResolutions(
                merge,
                state.Imported.Snapshot,
                theirs.Snapshot,
                mine,
                out appliedResolutionKeys);
        }
        if (!merge.CanCommit)
        {
            var publicConflicts = ImmutableArray.CreateBuilder<ExcelDbEditor.ConflictRecord>();
            foreach (var conflict in merge.Conflicts)
            {
                var id = ConflictId.New();
                _lastConflictKeys.Add(id, new ConflictKey(conflict.Identity, conflict.Path));
                publicConflicts.Add(new ExcelDbEditor.ConflictRecord(
                    id,
                    new GUID(conflict.Identity.RowGuid),
                    conflict.Path,
                    conflict.BaseValue,
                    conflict.MineValue,
                    conflict.TheirsValue));
            }

            _lastConflicts = publicConflicts.ToImmutable();
            return new SavePreparationResult(
                new OperationReport(
                    "authoring-save",
                    ToolVersion,
                    false,
                    merge.Conflicts.Select(static conflict => new Diagnostic(
                        "EXAW0102",
                        DiagnosticSeverity.Blocker,
                        $"{conflict.Identity}/{conflict.Path}",
                        $"Three-way merge conflict: base={conflict.BaseValue}, mine={conflict.MineValue}, theirs={conflict.TheirsValue}."))
                        .ToImmutableArray(),
                    []),
                null);
        }

        var commitChanges = BuildCommitChanges(request.Items, theirs.Snapshot, merge.Rows);
        if (commitChanges.Count == 0)
            return new SavePreparationResult(OperationReport.Success("authoring-save", ToolVersion, applied: false), null);
        var closure = new RuntimeDependencyImpactClosureProvider(
            _states.Values.SelectMany(static adapterState => adapterState.RuntimeRecords.Values));
        var writePlan = WorkbookWritePlan.Create(
            path,
            currentWorkbook,
            theirs.Snapshot,
            commitChanges,
            closure);
        if (!writePlan.CanApply)
        {
            return new SavePreparationResult(
                new OperationReport(
                    "authoring-save",
                    ToolVersion,
                    false,
                    writePlan.Diagnostics,
                    [],
                    writePlan.PlanHash),
                null);
        }

        var preparation = new SavePreparation(
            path,
            request,
            state,
            currentWorkbook,
            theirs,
            merge.Rows,
            commitChanges,
            writePlan,
            WorkbookWriteService.Prepare(writePlan),
            appliedResolutionKeys);
        return new SavePreparationResult(
            OperationReport.Success("authoring-save-preflight", ToolVersion, applied: false),
            preparation);
    }

    private static SavePreparationResult FailedPreparation(string code, string location, string message) =>
        new(Failure(code, location, message), null);

    public bool Open(string workbookPath, string tableName, GUID guid)
    {
        if (_opener is null || guid.Empty())
            return false;
        lock (_gate)
        {
            var path = NormalizePath(workbookPath);
            if (!_states.TryGetValue(path, out var state)
                || !_codecsByName.TryGetValue(tableName, out var codec))
            {
                return false;
            }

            var table = state.Workbook.Tables.SingleOrDefault(item => item.TableId == codec.TableId);
            var row = table?.Rows.SingleOrDefault(item => item.RowGuid == guid.Value);
            return table is not null
                && row is not null
                && _opener.Open(path, table.SheetName, row.SourceRowNumber ?? table.DataStartRow);
        }
    }

    private BuildStateResult BuildState(
        string path,
        byte[] bytes,
        ImportSnapshot? previousSnapshot,
        string clientFingerprint)
    {
        var fingerprint = ContentFingerprint.FromBytes(bytes);
        var workbook = XlsxWorkbookCodec.Read(bytes);
        var imported = WorkbookImporter.Import(
            path,
            bytes,
            workbook,
            _schema,
            previousSnapshot,
            _cellFormats,
            _validators);
        workbook = WorkbookIdentityProjection.BindCanonicalKeys(workbook, imported, _schema);
        var source = new WorkbookSource(path, workbook, fingerprint);
        var domainSources = _states
            .Where(pair => !string.Equals(pair.Key, path, StringComparison.OrdinalIgnoreCase))
            .Select(static pair => new WorkbookSource(
                pair.Key,
                pair.Value.Workbook,
                pair.Value.SourceFingerprint))
            .Append(source);
        var identityScan = new ProjectIdentityScanner().Scan(
            domainSources,
            previousSnapshot?.ToKnownIdentities());
        var domainDiagnostics = WorkbookImportDomain.Validate(
            _states
                .Where(pair => !string.Equals(pair.Key, path, StringComparison.OrdinalIgnoreCase))
                .Select(static pair => new WorkbookImportDomainEntry(pair.Key, pair.Value.Imported))
                .Append(new WorkbookImportDomainEntry(path, imported)));
        var diagnostics = imported.Diagnostics
            .Concat(identityScan.Diagnostics)
            .Concat(domainDiagnostics)
            .Distinct()
            .ToImmutableArray();
        var assets = ImmutableArray.CreateBuilder<AuthoringImportedAsset>();
        var runtimeRecords = ImmutableDictionary.CreateBuilder<AssetIdentity, RuntimeAssetRecord>();
        if (!diagnostics.Any(static diagnostic => diagnostic.IsFailure))
        {
            foreach (var row in imported.Rows.Where(static row => row.IsIndexable && row.Identity is not null))
            {
                try
                {
                    var codec = RequireCodec(row.TableId);
                    var binding = _bindings[row.TableId];
                    var record = codec.CreateRuntimeRecord(row);
                    ValidateRuntimeRecord(row, record);
                    var asset = binding.Create();
                    binding.ResetToDefaults(asset);
                    binding.Apply(asset, record);
                    if (!codec.AssetType.IsInstanceOfType(asset))
                        throw new InvalidDataException($"Runtime binding returned the wrong type for table {row.TableId}.");
                    runtimeRecords.Add(record.Identity, record);
                    assets.Add(new AuthoringImportedAsset(
                        new GUID(record.Identity.RowGuid),
                        codec.TableName,
                        record.Key,
                        asset,
                        row.Revision));
                }
                catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
                {
                    diagnostics = diagnostics.Add(new Diagnostic(
                        "EXAW0002",
                        DiagnosticSeverity.Error,
                        row.Location,
                        exception.Message));
                    break;
                }
            }
        }

        var report = new OperationReport(
            "authoring-import",
            ToolVersion,
            false,
            diagnostics,
            [new ArtifactRecord("workbook", path, fingerprint.Sha256)]);
        var authoringImport = new AuthoringWorkbookImport(
            path,
            report.Succeeded ? assets.ToImmutable() : [],
            report,
            fingerprint.ToString(),
            workbook.Tables
                .Where(static table => !table.IsRetiredPreserved)
                .Select(static table => table.ProtoName)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static key => key, StringComparer.Ordinal)
                .ToImmutableArray());
        if (!report.Succeeded)
            return new BuildStateResult(authoringImport, null);
        var state = new AdapterState(
            path,
            workbook,
            imported,
            new AuthoringDraftStore(imported.Snapshot),
            fingerprint,
            clientFingerprint,
            runtimeRecords.ToImmutable(),
            authoringImport);
        return new BuildStateResult(authoringImport, state);
    }

    private void PrepareDraft(
        AdapterState state,
        WorkbookImportResult theirs,
        AuthoringSaveItem item,
        Dictionary<AssetIdentity, SnapshotRow> capturedRows,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableHashSet<AssetIdentity> plannedDeletes)
    {
        if (item.Guid.Empty())
        {
            diagnostics.Add(Blocker("EXAW0110", item.TableName, "Save item has an empty GUID."));
            return;
        }

        if (!_codecs.TryGetValue(item.TableId, out var codec)
            || !string.Equals(codec.TableName, item.TableName, StringComparison.Ordinal))
        {
            diagnostics.Add(Blocker("EXAW0111", item.TableName, "Save item refers to an unregistered table codec."));
            return;
        }

        var identity = new AssetIdentity(item.TableId, item.Guid.Value);
        state.Imported.Snapshot.Rows.TryGetValue(identity, out var baseline);
        theirs.Snapshot.Rows.TryGetValue(identity, out var theirRow);
        var revisionAhead = _sessionRevisionAhead.GetValueOrDefault(state.Path)?.Contains(identity) == true;
        var expectedEditorRevision = baseline is null
            ? 0
            : revisionAhead && baseline.Revision != uint.MaxValue
                ? baseline.Revision + 1
                : baseline.Revision;
        if (baseline is not null && item.Revision != expectedEditorRevision)
        {
            diagnostics.Add(Blocker(
                "EXAW0112",
                identity.ToString(),
                $"Editor revision {item.Revision} does not match mounted editor revision {expectedEditorRevision}."));
            return;
        }

        if (baseline is null && item.Revision != 0)
        {
            diagnostics.Add(Blocker("EXAW0113", identity.ToString(), "A new asset must start at revision 0."));
            return;
        }

        if (item.Deleted)
        {
            if (baseline is null)
            {
                diagnostics.Add(Blocker("EXAW0114", identity.ToString(), "Cannot delete an asset absent from the mounted snapshot."));
                return;
            }

            state.Drafts.Delete(identity);
            return;
        }

        if (item.Asset is null || !codec.AssetType.IsInstanceOfType(item.Asset))
        {
            diagnostics.Add(Blocker("EXAW0115", identity.ToString(), $"Save item asset is not a {codec.AssetType.FullName}."));
            return;
        }

        if (baseline is not null && theirRow is null)
        {
            diagnostics.Add(Blocker("EXAW0116", identity.ToString(), "The workbook row was deleted externally while the editor retained it."));
            return;
        }

        if (baseline is null && IsGuidOwnedElsewhere(identity, state.Path, plannedDeletes))
        {
            diagnostics.Add(Blocker("EXAW0117", identity.ToString(), "The new row GUID is already owned elsewhere in the project domain."));
            return;
        }

        SnapshotRow captured;
        try
        {
            captured = codec.Capture(item, baseline);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(Blocker("EXAW0118", identity.ToString(), exception.Message));
            return;
        }

        if (captured.Identity != identity
            || captured.Revision != item.Revision)
        {
            diagnostics.Add(Blocker("EXAW0119", identity.ToString(), "Table codec changed save identity or revision."));
            return;
        }

        if (baseline is not null && captured.Revision != baseline.Revision)
            captured = captured with { Revision = baseline.Revision };

        capturedRows[identity] = captured;
        if (baseline is null)
        {
            state.Drafts.Add(captured);
            return;
        }

        var changed = false;
        foreach (var pair in captured.Values)
        {
            if (!baseline.Values.TryGetValue(pair.Key, out var previous) || previous != pair.Value)
            {
                state.Drafts.Edit(identity, pair.Key, pair.Value);
                changed = true;
            }
        }

        if (!string.Equals(baseline.Key, captured.Key, StringComparison.Ordinal))
        {
            state.Drafts.Rename(identity, captured.Key ?? item.Key);
            changed = true;
        }

        if (!changed)
            state.Drafts.Rename(identity, captured.Key ?? item.Key); // explicit save/touch still advances __rev once
    }

    private static ImmutableDictionary<AssetIdentity, DraftChange> BuildCommitChanges(
        ImmutableArray<AuthoringSaveItem> items,
        ImportSnapshot theirs,
        ImmutableDictionary<AssetIdentity, SnapshotRow> merged)
    {
        var changes = ImmutableDictionary.CreateBuilder<AssetIdentity, DraftChange>();
        foreach (var item in items)
        {
            if (item.Guid.Empty())
                continue;
            var identity = new AssetIdentity(item.TableId, item.Guid.Value);
            theirs.Rows.TryGetValue(identity, out var before);
            merged.TryGetValue(identity, out var after);
            if (item.Deleted || after is null)
            {
                if (before is not null)
                    changes[identity] = new DraftChange(DraftChangeKind.Deleted, null);
                continue;
            }

            var kind = before is null
                ? DraftChangeKind.Added
                : !string.Equals(before.Key, after.Key, StringComparison.Ordinal)
                    ? DraftChangeKind.Renamed
                    : DraftChangeKind.Modified;
            changes[identity] = new DraftChange(kind, after);
        }

        return changes.ToImmutable();
    }

    private MergeResult ApplyConflictResolutions(
        MergeResult merge,
        ImportSnapshot @base,
        ImportSnapshot theirs,
        IReadOnlyDictionary<AssetIdentity, DraftChange> mine,
        out ImmutableHashSet<ConflictKey> appliedKeys)
    {
        var rows = merge.Rows.ToBuilder();
        var unresolved = ImmutableArray.CreateBuilder<MergeConflict>();
        var applied = ImmutableHashSet.CreateBuilder<ConflictKey>();
        foreach (var conflict in merge.Conflicts)
        {
            var key = new ConflictKey(conflict.Identity, conflict.Path);
            if (!_conflictResolutions.TryGetValue(key, out var resolution))
            {
                unresolved.Add(conflict);
                continue;
            }

            @base.Rows.TryGetValue(conflict.Identity, out var baseRow);
            theirs.Rows.TryGetValue(conflict.Identity, out var theirRow);
            mine.TryGetValue(conflict.Identity, out var draft);
            var myRow = draft?.Row;
            var selected = resolution switch
            {
                ConflictResolutionAction.KeepEditorValue => myRow,
                ConflictResolutionAction.TakeBase => baseRow,
                _ => theirRow,
            };
            if (conflict.Kind is MergeConflictKind.DeleteModify or MergeConflictKind.ConcurrentAdd)
            {
                if (selected is null)
                    rows.Remove(conflict.Identity);
                else
                    rows[conflict.Identity] = selected;
            }
            else
            {
                var row = rows.GetValueOrDefault(conflict.Identity)
                    ?? selected
                    ?? theirRow
                    ?? myRow
                    ?? baseRow;
                if (row is null)
                {
                    rows.Remove(conflict.Identity);
                }
                else if (conflict.Kind == MergeConflictKind.Key)
                {
                    rows[conflict.Identity] = row with { Key = selected?.Key };
                }
                else
                {
                    var value = selected?.Values.GetValueOrDefault(conflict.Path, CanonicalValue.Missing)
                        ?? CanonicalValue.Missing;
                    rows[conflict.Identity] = row with
                    {
                        Values = row.Values.SetItem(conflict.Path, value),
                    };
                }
            }

            applied.Add(key);
        }

        appliedKeys = applied.ToImmutable();
        return new MergeResult(rows.ToImmutable(), unresolved.ToImmutable());
    }

    private void ApplyMergedValuesToSubmittedAssets(
        ImmutableArray<AuthoringSaveItem> items,
        ImmutableDictionary<AssetIdentity, SnapshotRow> merged,
        WorkbookDefinition workbook)
    {
        foreach (var item in items.Where(static item => !item.Deleted && item.Asset is not null))
        {
            var identity = new AssetIdentity(item.TableId, item.Guid.Value);
            if (!merged.TryGetValue(identity, out var row))
                continue;
            var table = workbook.Tables.Single(table => table.TableId == item.TableId);
            var imported = new ImportedRow(
                identity,
                item.TableId,
                table.SheetName,
                0,
                row.Revision,
                row.Key,
                row.Values,
                row.RawCells,
                true,
                ImportChangeKind.Modified);
            var record = RequireCodec(item.TableId).CreateRuntimeRecord(imported);
            _bindings[item.TableId].Apply(item.Asset!, record);
        }
    }

    private bool IsGuidOwnedElsewhere(
        AssetIdentity candidate,
        string targetPath,
        ImmutableHashSet<AssetIdentity> plannedDeletes) =>
        _states.Any(pair =>
            !string.Equals(pair.Key, targetPath, StringComparison.OrdinalIgnoreCase)
            && pair.Value.Imported.Snapshot.Rows.Keys.Any(identity =>
                identity.RowGuid == candidate.RowGuid
                && !plannedDeletes.Contains(identity)));

    private IAuthoringWorkbookTableCodec RequireCodec(int tableId) =>
        _codecs.TryGetValue(tableId, out var codec)
            ? codec
            : throw new InvalidDataException($"No workbook codec is registered for table {tableId}.");

    private static IEnumerable<IAuthoringWorkbookTableCodec> CreateReflectionCodecs(
        CanonicalSchemaDescriptor schema,
        RuntimeSchemaRegistry runtimeRegistry)
    {
        Guard.NotNull(schema);
        Guard.NotNull(runtimeRegistry);
        var bindings = runtimeRegistry.Bindings.ToDictionary(static binding => binding.TableId);
        foreach (var table in schema.Tables.Where(static table => table.Kind == CanonicalTableKind.Asset))
        {
            if (!bindings.TryGetValue(table.Id, out var binding))
                throw new ArgumentException($"Runtime registry has no binding for table {table.Id}/{table.Name}.", nameof(runtimeRegistry));
            var codecType = typeof(ReflectionAuthoringWorkbookTableCodec<>).MakeGenericType(binding.RuntimeType);
            yield return (IAuthoringWorkbookTableCodec)(Activator.CreateInstance(codecType, table, null)
                ?? throw new InvalidOperationException($"Could not create reflection workbook codec for {binding.RuntimeType.FullName}."));
        }
    }

    private static void ValidateRuntimeRecord(ImportedRow row, RuntimeAssetRecord record)
    {
        if (record.Identity != row.Identity
            || record.TableId != row.TableId
            || string.IsNullOrEmpty(record.Key))
        {
            throw new InvalidDataException("Table codec changed imported identity/table or emitted an empty authoring key.");
        }

        if (record.Fields.Select(static field => field.FieldNumber).Distinct().Count() != record.Fields.Length)
            throw new InvalidDataException("Table codec emitted duplicate runtime field numbers.");
    }

    private static Diagnostic Blocker(string code, string location, string message) =>
        new(code, DiagnosticSeverity.Blocker, location, message);

    private static OperationReport Failure(string code, string location, string message) =>
        new(
            "authoring-save",
            ToolVersion,
            false,
            [Blocker(code, location, message)],
            []);

    private static AuthoringWorkbookImport FailedImport(string path, string code, string message)
    {
        var report = new OperationReport(
            "authoring-import",
            ToolVersion,
            false,
            [new Diagnostic(code, DiagnosticSeverity.Error, path, message)],
            []);
        return new AuthoringWorkbookImport(path, [], report);
    }

    private static string NormalizePath(string path)
    {
        Guard.NotNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static bool IsWorkbookException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or OverflowException
            or JsonException;

    private sealed record BuildStateResult(AuthoringWorkbookImport Import, AdapterState? State);

    private sealed record SavePreparationResult(OperationReport Report, SavePreparation? Preparation);

    private sealed record SavePreparation(
        string Path,
        AuthoringSaveRequest Request,
        AdapterState State,
        WorkbookDefinition CurrentWorkbook,
        WorkbookImportResult Theirs,
        ImmutableDictionary<AssetIdentity, SnapshotRow> MergedRows,
        ImmutableDictionary<AssetIdentity, DraftChange> CommitChanges,
        WorkbookWritePlan WritePlan,
        PreparedWorkbookWrite PreparedWrite,
        ImmutableHashSet<ConflictKey> AppliedResolutionKeys);

    private readonly record struct ConflictKey(AssetIdentity Identity, string Path);

    private sealed record AdapterState(
        string Path,
        WorkbookDefinition Workbook,
        WorkbookImportResult Imported,
        AuthoringDraftStore Drafts,
        ContentFingerprint SourceFingerprint,
        string ClientFingerprint,
        ImmutableDictionary<AssetIdentity, RuntimeAssetRecord> RuntimeRecords,
        AuthoringWorkbookImport LastImport);
}

internal sealed class RuntimeDependencyImpactClosureProvider : IImpactClosureProvider
{
    private readonly IReadOnlyDictionary<AssetIdentity, ImmutableHashSet<AssetIdentity>> _neighbors;

    public RuntimeDependencyImpactClosureProvider(IEnumerable<RuntimeAssetRecord> records)
    {
        Guard.NotNull(records);
        var graph = new Dictionary<AssetIdentity, HashSet<AssetIdentity>>();
        foreach (var record in records)
        {
            graph.TryAdd(record.Identity, []);
            foreach (var dependency in record.Dependencies)
            {
                graph.TryAdd(dependency, []);
                graph[record.Identity].Add(dependency);
                graph[dependency].Add(record.Identity);
            }
        }

        _neighbors = graph.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToImmutableHashSet());
    }

    public ImmutableHashSet<AssetIdentity> Expand(
        IEnumerable<AssetIdentity> seeds,
        ImportSnapshot snapshot)
    {
        Guard.NotNull(seeds);
        Guard.NotNull(snapshot);
        var result = seeds.ToHashSet();
        var queue = new Queue<AssetIdentity>(result);
        while (queue.TryDequeue(out var identity))
        {
            if (!_neighbors.TryGetValue(identity, out var neighbors))
                continue;
            foreach (var neighbor in neighbors)
            {
                if (result.Add(neighbor))
                    queue.Enqueue(neighbor);
            }
        }

        return result.ToImmutableHashSet();
    }
}
