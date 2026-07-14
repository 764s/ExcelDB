using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Runtime;

namespace ExcelDbEditor;

/// <summary>
/// Host-owned authoring session. Activating a session scopes the Unity-shaped static facade
/// without introducing project-global configuration or a second persisted source of truth.
/// </summary>
public class AssetDatabaseSession : IDisposable
{
    private static readonly AsyncLocal<AssetDatabaseSession?> Ambient = new();
    private readonly Dictionary<string, WorkbookState> _workbooks = new(StringComparer.Ordinal);
    private readonly Dictionary<int, AuthoringTableRegistration> _tablesById = [];
    private readonly Dictionary<string, AuthoringTableRegistration> _tablesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<GUID, AssetEntry> _byGuid = [];
    private readonly Dictionary<object, AssetEntry> _byObject = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, AssetEntry> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<(int TableId, string Key), AssetEntry> _byTableKey = [];
    private readonly List<ConflictRecord> _conflicts = [];
    private readonly List<AssetDatabaseDiagnostic> _diagnostics = [];
    private readonly IAuthoringWorkbookAdapter _adapter;
    private readonly RuntimeMode _mode;
    private readonly bool _excelEnabled;
    private readonly bool _watcherEnabled;
    private readonly Dictionary<string, ImportAssetOptions> _pendingImports = new(StringComparer.Ordinal);
    private readonly HashSet<AssetEntry> _pendingSingleSaves = [];
    private AssetDatabaseSession? _previous;
    private int _editingDepth;
    private bool _saveAllPending;
    private bool _publishing;
    private bool _disposed;

    public AssetDatabaseSession(
        IAuthoringWorkbookAdapter adapter,
        RuntimeMode mode = RuntimeMode.EditorAuthoring,
        bool enableExcelDataSource = false,
        bool enableWatcher = false)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        _mode = mode;
        _excelEnabled = mode == RuntimeMode.EditorAuthoring
            || (mode is RuntimeMode.EditorPlayDebug or RuntimeMode.Development && enableExcelDataSource);
        _watcherEnabled = mode == RuntimeMode.EditorAuthoring
            || (mode is RuntimeMode.EditorPlayDebug or RuntimeMode.Development && enableWatcher);
    }

    internal static AssetDatabaseSession Current => Ambient.Value ?? EmptyAssetDatabaseSession.Instance;

    public IReadOnlyList<AssetDatabaseDiagnostic> Diagnostics => _diagnostics;

    public bool IsDirty => _byGuid.Values.Any(static entry => entry.Dirty || entry.Deleted);

    public RuntimeMode Mode => _mode;

    public int AssetEditingDepth => _editingDepth;

    public AssetDatabaseSession RegisterTable(AuthoringTableRegistration registration)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(registration);
        if (!_tablesById.TryAdd(registration.TableId, registration)
            || !_tablesByName.TryAdd(registration.TableName, registration))
        {
            throw new InvalidOperationException($"Duplicate authoring table '{registration.TableName}'/{registration.TableId}.");
        }

        return this;
    }

    public IDisposable Activate()
    {
        ThrowIfDisposed();
        if (_previous is not null || ReferenceEquals(Ambient.Value, this))
            throw new InvalidOperationException("This authoring session is already active.");
        _previous = Ambient.Value;
        Ambient.Value = this;
        return new Activation(this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_editingDepth != 0)
        {
            AddDiagnostic(
                "assetdb.editing_unbalanced",
                DiagnosticSeverity.Warning,
                ".",
                $"Asset editing scope was disposed with depth {_editingDepth}; queued watcher/writeback work was not published.");
        }
        if (ReferenceEquals(Ambient.Value, this))
            Ambient.Value = _previous;
        _previous = null;
        _disposed = true;
    }

    internal event Action<ImportReport>? WorkbookImported;

    internal void MountWorkbook(string path)
    {
        ThrowIfDisposed();
        EnsureExcelAllowed();
        EnsureNotPublishing();
        path = NormalizePath(path);
        if (_workbooks.ContainsKey(path))
            return;
        _workbooks.Add(path, new WorkbookState(path));
        ImportAsset(path, ImportAssetOptions.ForceUpdate);
    }

    internal void UnmountWorkbook(string path)
    {
        ThrowIfDisposed();
        EnsureNotPublishing();
        path = NormalizePath(path);
        if (!_workbooks.TryGetValue(path, out var workbook))
        {
            AddDiagnostic("EXAD0001", DiagnosticSeverity.Warning, path, "Workbook is not mounted.");
            return;
        }

        if (workbook.Entries.Any(static entry => entry.Dirty || entry.Deleted))
        {
            AddDiagnostic("EXAD0002", DiagnosticSeverity.Error, path, "Workbook has unsaved authoring changes.");
            return;
        }

        foreach (var entry in workbook.Entries.ToArray())
            RemoveIndexes(entry);
        _pendingImports.Remove(path);
        _workbooks.Remove(path);
    }

    internal void Refresh(ImportAssetOptions options)
    {
        ThrowIfDisposed();
        EnsureExcelAllowed();
        EnsureNotPublishing();
        foreach (var path in _workbooks.Keys.Order(StringComparer.Ordinal).ToArray())
        {
            if (_editingDepth != 0)
                QueueImport(path, options);
            else
                ImportAssetCore(path, options);
        }
    }

    internal void ImportAsset(string path, ImportAssetOptions options)
    {
        ThrowIfDisposed();
        EnsureExcelAllowed();
        EnsureNotPublishing();
        path = NormalizePath(path);
        if (_editingDepth != 0)
        {
            QueueImport(path, options);
            return;
        }

        ImportAssetCore(path, options);
    }

    /// <summary>
    /// Host watcher seam. A file-system callback only queues a hint; parsing and publication run
    /// later through the same serialized import path used by Refresh and MountWorkbook.
    /// </summary>
    public void NotifyWorkbookChanged(string path)
    {
        ThrowIfDisposed();
        if (!_watcherEnabled)
            throw new InvalidOperationException($"Workbook watcher is disabled in runtime mode {_mode}.");
        path = NormalizePath(path);
        if (!_workbooks.ContainsKey(path))
        {
            AddDiagnostic("EXAD0007", DiagnosticSeverity.Warning, path, "Watcher signaled an unmounted workbook.");
            return;
        }

        QueueImport(path, ImportAssetOptions.Default);
    }

    /// <summary>Drains watcher hints at a host-owned stable authoring publish point.</summary>
    public bool ProcessPendingChanges()
    {
        ThrowIfDisposed();
        EnsureNotPublishing();
        if (_editingDepth != 0 || _pendingImports.Count == 0)
            return false;
        DrainPendingImports();
        return true;
    }

    private void ImportAssetCore(string path, ImportAssetOptions options)
    {
        if (!_workbooks.TryGetValue(path, out var workbook))
        {
            AddDiagnostic("EXAD0003", DiagnosticSeverity.Error, path, "Only a mounted workbook can be imported.");
            return;
        }

        if (workbook.Entries.Any(static entry => entry.Dirty || entry.Deleted))
        {
            AddDiagnostic("EXAD0004", DiagnosticSeverity.Error, path, "Import would overwrite unsaved authoring changes.");
            return;
        }

        var result = _adapter.Import(path, (options & ImportAssetOptions.ForceUpdate) != 0);
        if (!result.Report.Succeeded)
        {
            PublishImport(result.Report, path);
            return;
        }

        var previousEntries = workbook.Entries.ToArray();
        var previousByGuid = previousEntries.ToDictionary(static entry => entry.Guid);
        var candidates = new List<(AssetEntry Entry, object ImportedAsset, bool Reused)>();
        var candidateGuids = new HashSet<GUID>();
        var candidatePaths = new HashSet<string>(StringComparer.Ordinal);
        var candidateKeys = new HashSet<(int TableId, string Key)>();
        var candidateObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var importDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var imported in result.Assets)
        {
            if (!_tablesByName.TryGetValue(imported.TableName, out var table)
                || !table.AssetType.IsInstanceOfType(imported.Asset)
                || imported.Guid.Empty())
            {
                importDiagnostics.Add(new Diagnostic(
                    "EXAD0005",
                    DiagnosticSeverity.Error,
                    path,
                    $"Imported row '{imported.TableName}/{imported.Key}' is not registered or has an invalid identity/type."));
                continue;
            }

            var reused = previousByGuid.TryGetValue(imported.Guid, out var previous)
                && previous.Table.TableId == table.TableId
                && table.AssetType.IsInstanceOfType(previous.Asset);
            var resident = reused ? previous!.Asset : imported.Asset;
            var entry = new AssetEntry(workbook, table, imported.Guid, imported.Key, resident, imported.Revision, persisted: true);
            var duplicate = !candidateGuids.Add(entry.Guid)
                || !candidatePaths.Add(entry.Path)
                || !candidateKeys.Add((entry.Table.TableId, entry.Key))
                || !candidateObjects.Add(entry.Asset)
                || (_byGuid.TryGetValue(entry.Guid, out var guidOwner) && !ReferenceEquals(guidOwner.Workbook, workbook))
                || (_byPath.TryGetValue(entry.Path, out var pathOwner) && !ReferenceEquals(pathOwner.Workbook, workbook))
                || (_byTableKey.TryGetValue((entry.Table.TableId, entry.Key), out var keyOwner) && !ReferenceEquals(keyOwner.Workbook, workbook))
                || (_byObject.TryGetValue(entry.Asset, out var objectOwner) && !ReferenceEquals(objectOwner.Workbook, workbook));
            if (duplicate)
            {
                importDiagnostics.Add(new Diagnostic(
                    "EXAD0006",
                    DiagnosticSeverity.Blocker,
                    path,
                    $"Imported row '{entry.Table.TableName}/{entry.Key}' conflicts with an existing project-domain GUID, key, path, or resident instance."));
                continue;
            }

            candidates.Add((entry, imported.Asset, reused));
        }

        if (importDiagnostics.Any(static diagnostic => diagnostic.IsFailure))
        {
            foreach (var diagnostic in importDiagnostics)
                _diagnostics.Add(new AssetDatabaseDiagnostic(DateTimeOffset.UtcNow, diagnostic));
            PublishImport(
                new OperationReport("authoring-import", "assetdatabase-v1", false, importDiagnostics.ToImmutable(), []),
                path);
            return;
        }

        foreach (var candidate in candidates.Where(static candidate => candidate.Reused))
            candidate.Entry.Table.ApplyImported(candidate.Entry.Asset, candidate.ImportedAsset);
        foreach (var entry in previousEntries)
            RemoveIndexes(entry);
        workbook.Entries.Clear();
        workbook.Fingerprint = result.Fingerprint;
        workbook.AvailableTables = result.AvailableTables.IsDefault
            ? null
            : result.AvailableTables.ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!TryAddIndexes(candidate.Entry, out var error))
                throw new InvalidOperationException($"Validated import index insertion failed: {error}");
            workbook.Entries.Add(candidate.Entry);
        }

        PublishImport(result.Report, path);
    }

    internal T? Load<T>(string path) where T : class => Load(path, typeof(T)) as T;

    internal object? Load(string path, Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        path = NormalizePath(path);
        return _byPath.TryGetValue(path, out var entry) && !entry.Deleted && type.IsInstanceOfType(entry.Asset)
            ? entry.Asset
            : null;
    }

    internal object[] LoadAll(string path)
    {
        path = NormalizePath(path);
        if (_byPath.TryGetValue(path, out var row) && !row.Deleted)
            return [row.Asset];
        if (_workbooks.TryGetValue(path, out var workbook))
            return workbook.Entries.Where(static entry => !entry.Deleted).OrderBy(static entry => entry.Table.TableId).ThenBy(static entry => entry.Order).Select(static entry => entry.Asset).ToArray();

        var tablePrefix = path + "/";
        return _byPath.Values
            .Where(entry => !entry.Deleted && entry.Path.StartsWith(tablePrefix, StringComparison.Ordinal))
            .OrderBy(static entry => entry.Table.TableId)
            .ThenBy(static entry => entry.Order)
            .Select(static entry => entry.Asset)
            .ToArray();
    }

    internal Type? GetMainType(string path) =>
        _byPath.TryGetValue(NormalizePath(path), out var entry) && !entry.Deleted ? entry.Table.AssetType : null;

    internal string[] Find(string filter, string[]? folders)
    {
        filter ??= string.Empty;
        var tokens = filter.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var nameTerms = tokens.Where(static token => !token.Contains(':')).ToArray();
        var typeTerms = Values(tokens, "t:");
        var labelTerms = Values(tokens, "l:");
        var referenceTerms = Values(tokens, "ref:");
        var normalizedFolders = folders?.Select(NormalizePath).ToArray() ?? [];

        return _byGuid.Values
            .Where(static entry => !entry.Deleted)
            .Where(entry => normalizedFolders.Length == 0 || normalizedFolders.Any(folder => IsAtOrBelow(entry.Path, folder)))
            .Where(entry => nameTerms.All(term => entry.Key.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Where(entry => typeTerms.Length == 0 || typeTerms.Any(term => entry.Table.TypeAliases.Contains(term, StringComparer.Ordinal)))
            .Where(entry => labelTerms.Length == 0 || labelTerms.Any(term => Labels(entry).Contains(term, StringComparer.Ordinal)))
            .Where(entry => referenceTerms.Length == 0 || referenceTerms.Any(term => References(entry).Any(guid => ReferenceMatches(guid, term))))
            .OrderBy(static entry => entry.Table.TableId)
            .ThenBy(static entry => entry.Order)
            .Select(static entry => entry.Guid.ToString())
            .ToArray();
    }

    internal bool Contains(object obj) => obj is not null && _byObject.TryGetValue(obj, out var entry) && !entry.Deleted;

    internal string GetPath(object obj) => obj is not null && _byObject.TryGetValue(obj, out var entry) && !entry.Deleted ? entry.Path : string.Empty;

    internal bool IsValidFolder(string path)
    {
        path = NormalizePath(path);
        if (_workbooks.TryGetValue(path, out _))
            return true;
        return _workbooks.Values.Any(workbook =>
        {
            if (!path.StartsWith(workbook.Path + "/", StringComparison.Ordinal))
                return false;
            var tableName = path[(workbook.Path.Length + 1)..];
            return !tableName.Contains('/')
                && _tablesByName.ContainsKey(tableName)
                && workbook.HasTable(tableName);
        });
    }

    internal GUID GuidFromPath(string path) => _byPath.TryGetValue(NormalizePath(path), out var entry) && !entry.Deleted ? entry.Guid : default;

    internal string PathFromGuid(GUID guid) => !guid.Empty() && _byGuid.TryGetValue(guid, out var entry) && !entry.Deleted ? entry.Path : string.Empty;

    internal void Create(object asset, string path)
    {
        ArgumentNullException.ThrowIfNull(asset);
        EnsureWriteAllowed();
        path = NormalizePath(path);
        if (_byPath.ContainsKey(path))
        {
            AddDiagnostic("EXAD0100", DiagnosticSeverity.Error, path, "An asset already exists at this path.");
            return;
        }

        if (_byObject.ContainsKey(asset))
        {
            AddDiagnostic("EXAD0101", DiagnosticSeverity.Error, path, "The object is already persisted.");
            return;
        }

        if (!TryParseRowPath(path, out var workbook, out var table, out var key, out var error)
            || !table.AssetType.IsInstanceOfType(asset))
        {
            AddDiagnostic("EXAD0102", DiagnosticSeverity.Error, path, error ?? "The object type is not registered for the target table.");
            return;
        }
        if (_byTableKey.ContainsKey((table.TableId, key)))
        {
            AddDiagnostic("EXAD0103", DiagnosticSeverity.Error, path, "An asset with this table key already exists in another mounted workbook.");
            return;
        }

        table.SetKey(asset, key);
        var entry = new AssetEntry(workbook, table, new GUID(RowGuid.New()), key, asset, 0, persisted: false) { Dirty = true };
        if (!TryAddIndexes(entry, out error))
        {
            AddDiagnostic("EXAD0103", DiagnosticSeverity.Error, path, error);
            return;
        }

        workbook.Entries.Add(entry);
        PublishEditingIfReady();
    }

    internal bool Delete(string path)
    {
        EnsureWriteAllowed();
        path = NormalizePath(path);
        if (!_byPath.TryGetValue(path, out var entry) || entry.Deleted)
        {
            AddDiagnostic("EXAD0104", DiagnosticSeverity.Error, path, "Asset does not exist.");
            return false;
        }

        var deleteSet = new HashSet<AssetEntry> { entry };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var owner in LiveEntries())
            {
                if (deleteSet.Contains(owner))
                    continue;
                if (ReferenceBindings(owner).Any(reference =>
                        reference.DeletePolicy == AuthoringReferenceDeletePolicy.Cascade
                        && TryGetLiveEntry(reference.Target, out var target)
                        && deleteSet.Contains(target)))
                {
                    changed |= deleteSet.Add(owner);
                }
            }
        }

        var incoming = LiveEntries()
            .Where(owner => !deleteSet.Contains(owner))
            .SelectMany(owner => ReferenceBindings(owner).Select(reference => (Owner: owner, Reference: reference)))
            .Where(edge => TryGetLiveEntry(edge.Reference.Target, out var target) && deleteSet.Contains(target))
            .ToArray();
        var blockers = incoming
            .Where(static edge => edge.Reference.DeletePolicy == AuthoringReferenceDeletePolicy.Block)
            .ToArray();
        if (blockers.Length != 0)
        {
            AddDiagnostic(
                "EXAD0104",
                DiagnosticSeverity.Error,
                path,
                $"Asset deletion is blocked by: {string.Join(", ", blockers.Select(static edge => $"{edge.Owner.Path}:{edge.Reference.PropertyPath}").Order(StringComparer.Ordinal))}.");
            return false;
        }

        var invalidSetNull = incoming
            .Where(static edge => edge.Reference.DeletePolicy == AuthoringReferenceDeletePolicy.SetNull && edge.Reference.Clear is null)
            .ToArray();
        if (invalidSetNull.Length != 0)
        {
            AddDiagnostic(
                "EXAD0106",
                DiagnosticSeverity.Blocker,
                path,
                $"SET_NULL references have no clear accessor: {string.Join(", ", invalidSetNull.Select(static edge => $"{edge.Owner.Path}:{edge.Reference.PropertyPath}").Order(StringComparer.Ordinal))}.");
            return false;
        }

        foreach (var edge in incoming.Where(static edge => edge.Reference.DeletePolicy == AuthoringReferenceDeletePolicy.SetNull))
        {
            edge.Reference.Clear!();
            edge.Owner.Dirty = true;
        }

        LinkImpact(deleteSet.Concat(incoming.Select(static edge => edge.Owner)));

        foreach (var deleted in deleteSet.OrderBy(static item => item.Table.TableId).ThenBy(static item => item.Order))
        {
            if (deleted.PersistedWorkbook is null)
            {
                deleted.Workbook.Entries.Remove(deleted);
                RemoveIndexes(deleted);
                continue;
            }

            deleted.Deleted = true;
            deleted.Dirty = true;
            _byPath.Remove(deleted.Path);
            _byTableKey.Remove((deleted.Table.TableId, deleted.Key));
        }

        PublishEditingIfReady();
        return true;
    }

    internal string Rename(string path, string newName)
    {
        EnsureWriteAllowed();
        path = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(newName))
        {
            AddDiagnostic("EXAD0107", DiagnosticSeverity.Error, path, "A new key is required.");
            return "A new key is required.";
        }
        if (!_byPath.TryGetValue(path, out var entry) || entry.Deleted)
            return MoveFailure(path, "Asset does not exist.");
        return Move(path, $"{entry.Workbook.Path}/{entry.Table.TableName}/{newName}");
    }

    internal string Move(string oldPath, string newPath)
    {
        EnsureWriteAllowed();
        oldPath = NormalizePath(oldPath);
        newPath = NormalizePath(newPath);
        if (!_byPath.TryGetValue(oldPath, out var entry) || entry.Deleted)
            return MoveFailure(oldPath, "Source asset does not exist.");
        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
            return string.Empty;
        if (_byPath.ContainsKey(newPath))
            return MoveFailure(newPath, "Destination asset already exists.");
        if (!TryParseRowPath(newPath, out var workbook, out var table, out var key, out var error))
            return MoveFailure(newPath, error!);
        if (table.TableId != entry.Table.TableId)
            return MoveFailure(newPath, "Moving an asset to another table is not supported.");
        if (_byTableKey.TryGetValue((entry.Table.TableId, key), out var keyOwner) && !ReferenceEquals(keyOwner, entry))
            return MoveFailure(newPath, "Destination key already exists in the project table domain.");

        _byPath.Remove(entry.Path);
        _byTableKey.Remove((entry.Table.TableId, entry.Key));
        entry.Workbook.Entries.Remove(entry);
        entry.Workbook = workbook;
        entry.Key = key;
        entry.Table.SetKey(entry.Asset, key);
        entry.Dirty = true;
        workbook.Entries.Add(entry);
        _byPath.Add(entry.Path, entry);
        _byTableKey.Add((entry.Table.TableId, entry.Key), entry);
        RewriteIncomingReferences(entry);
        PublishEditingIfReady();
        return string.Empty;
    }

    private string MoveFailure(string location, string message)
    {
        AddDiagnostic("EXAD0108", DiagnosticSeverity.Error, location, message);
        return message;
    }

    internal bool Copy(string path, string newPath)
    {
        EnsureWriteAllowed();
        path = NormalizePath(path);
        newPath = NormalizePath(newPath);
        if (!_byPath.TryGetValue(path, out var source) || source.Deleted || _byPath.ContainsKey(newPath))
        {
            AddDiagnostic("EXAD0109", DiagnosticSeverity.Error, newPath, "Copy source is missing or the destination already exists.");
            return false;
        }
        var clone = source.Table.Clone(source.Asset);
        Create(clone, newPath);
        return _byObject.ContainsKey(clone);
    }

    internal string GenerateUniquePath(string path)
    {
        path = NormalizePath(path);
        if (!TryParseRowPath(path, out _, out var table, out var key, out _))
            return path;
        if (!_byTableKey.ContainsKey((table.TableId, key)))
            return path;
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{path}_{suffix}";
            if (!_byTableKey.ContainsKey((table.TableId, key + "_" + suffix)))
                return candidate;
        }

        throw new InvalidOperationException("No unique asset path is available.");
    }

    internal void StartEditing()
    {
        EnsureWriteAllowed();
        checked { _editingDepth++; }
    }

    internal void StopEditing()
    {
        EnsureWriteAllowed();
        if (_editingDepth == 0)
        {
            AddDiagnostic("EXAD0105", DiagnosticSeverity.Warning, ".", "StopAssetEditing was called without a matching StartAssetEditing.");
            return;
        }

        _editingDepth--;
        if (_editingDepth != 0)
            return;

        if (_saveAllPending)
        {
            _saveAllPending = false;
            _pendingSingleSaves.Clear();
            SaveEntries(_byGuid.Values.Where(static entry => entry.Dirty || entry.Deleted));
        }
        else if (_pendingSingleSaves.Count != 0)
        {
            var pending = _pendingSingleSaves.ToArray();
            _pendingSingleSaves.Clear();
            SaveEntries(pending.SelectMany(ExpandImpactClosure));
        }

        DrainPendingImports();
        PublishEditingIfReady();
    }

    internal void SaveAll()
    {
        EnsureWriteAllowed();
        if (_editingDepth != 0)
        {
            _saveAllPending = true;
            return;
        }

        SaveEntries(_byGuid.Values.Where(static entry => entry.Dirty || entry.Deleted));
    }

    internal void SaveOne(object obj)
    {
        EnsureWriteAllowed();
        if (obj is not null && _byObject.TryGetValue(obj, out var entry))
        {
            entry.Dirty = true;
            if (_editingDepth != 0)
                _pendingSingleSaves.Add(entry);
            else
                SaveEntries(ExpandImpactClosure(entry));
        }
        else
        {
            AddDiagnostic("EXAD0200", DiagnosticSeverity.Error, ".", "Object is not a persisted ExcelDB asset.");
        }
    }

    internal void SaveOne(GUID guid)
    {
        EnsureWriteAllowed();
        if (_byGuid.TryGetValue(guid, out var entry))
        {
            entry.Dirty = true;
            if (_editingDepth != 0)
                _pendingSingleSaves.Add(entry);
            else
                SaveEntries(ExpandImpactClosure(entry));
        }
        else
        {
            AddDiagnostic("EXAD0201", DiagnosticSeverity.Error, guid.ToString(), "GUID is not a persisted ExcelDB asset.");
        }
    }

    internal string[] Dependencies(string path, bool recursive)
    {
        path = NormalizePath(path);
        if (!_byPath.TryGetValue(path, out var root) || root.Deleted)
            return [];
        var result = new HashSet<GUID>();
        if (recursive)
            result.Add(root.Guid);
        var queue = new Queue<GUID>(References(root));
        while (queue.TryDequeue(out var guid))
        {
            if (!result.Add(guid) || !recursive || !_byGuid.TryGetValue(guid, out var entry) || entry.Deleted)
                continue;
            foreach (var dependency in References(entry))
                queue.Enqueue(dependency);
        }

        return result
            .Select(guid => _byGuid.GetValueOrDefault(guid))
            .Where(static entry => entry is not null && !entry.Deleted)
            .OrderBy(static entry => entry!.Table.TableId)
            .ThenBy(static entry => entry!.Order)
            .Select(static entry => entry!.Path)
            .ToArray();
    }

    internal string[] GetLabels(object obj) => obj is not null && _byObject.TryGetValue(obj, out var entry) ? Labels(entry).ToArray() : [];

    internal void SetLabels(object obj, string[] labels)
    {
        EnsureWriteAllowed();
        ArgumentNullException.ThrowIfNull(labels);
        if (obj is null || !_byObject.TryGetValue(obj, out var entry) || entry.Deleted || entry.Table.SetLabels is null)
        {
            AddDiagnostic("EXAD0202", DiagnosticSeverity.Error, ".", "This table has no writable labels field.");
            return;
        }

        var canonical = labels.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        entry.Table.SetLabels(entry.Asset, canonical);
        entry.Dirty = true;
        PublishEditingIfReady();
    }

    internal bool Open(object target) => target is not null
        && _byObject.TryGetValue(target, out var entry)
        && !entry.Deleted
        && _adapter.Open(entry.Workbook.Path, entry.Table.TableName, entry.Guid);

    internal RuntimeQueryStatus GetConflicts(Span<ConflictRecord> buffer, out int count)
    {
        count = _conflicts.Count;
        var copy = Math.Min(count, buffer.Length);
        for (var index = 0; index < copy; index++)
            buffer[index] = _conflicts[index];
        return count > buffer.Length ? RuntimeQueryStatus.Truncated : RuntimeQueryStatus.Success;
    }

    internal bool ResolveConflict(ConflictId id, ConflictResolutionAction action)
    {
        if (!Enum.IsDefined(action))
        {
            AddDiagnostic("EXAD0300", DiagnosticSeverity.Error, id.Value.ToString("N"), "Unknown conflict resolution action.");
            return false;
        }
        var index = _conflicts.FindIndex(conflict => conflict.Id == id);
        if (index < 0)
            return false;
        if (_adapter is IAuthoringConflictSource source && !source.ResolveConflict(id, action))
            return false;
        _conflicts.RemoveAt(index);
        return true;
    }

    public void AddConflict(ConflictRecord conflict)
    {
        if (conflict.Id.IsEmpty)
            throw new ArgumentException("Conflict id must be non-empty.", nameof(conflict));
        _conflicts.Add(conflict);
    }

    private void SaveEntries(IEnumerable<AssetEntry> entries)
    {
        if (_conflicts.Count != 0)
        {
            AddDiagnostic(
                "EXAD0203",
                DiagnosticSeverity.Blocker,
                ".",
                "SaveAssets is blocked until all authoring conflicts are resolved.");
            return;
        }

        var selected = entries
            .Where(static entry => entry.Dirty || entry.Deleted)
            .Distinct()
            .OrderBy(static entry => entry.Table.TableId)
            .ThenBy(static entry => entry.Order)
            .ToArray();
        if (selected.Length == 0)
            return;

        var itemsByWorkbook = new Dictionary<WorkbookState, List<AuthoringSaveItem>>();
        foreach (var entry in selected)
        {
            if (entry.PersistedWorkbook is null)
            {
                if (!entry.Deleted)
                    AddSaveItem(entry.Workbook, ToSaveItem(entry, deleted: false, revision: 0));
                continue;
            }

            if (entry.Deleted)
            {
                AddSaveItem(entry.PersistedWorkbook, ToSaveItem(entry, deleted: true, entry.Revision));
                continue;
            }

            if (!ReferenceEquals(entry.PersistedWorkbook, entry.Workbook))
            {
                AddSaveItem(entry.PersistedWorkbook, ToSaveItem(entry, deleted: true, entry.Revision));
                AddSaveItem(entry.Workbook, ToSaveItem(entry, deleted: false, revision: 0));
                continue;
            }

            AddSaveItem(entry.Workbook, ToSaveItem(entry, deleted: false, entry.Revision));
        }

        if (itemsByWorkbook.Count == 0)
        {
            FinalizeSuccessfulSave(selected);
            return;
        }

        var requests = itemsByWorkbook
            .OrderBy(static pair => pair.Key.Path, StringComparer.Ordinal)
            .Select(static pair => new AuthoringSaveRequest(
                pair.Key.Path,
                pair.Value
                    .OrderBy(static item => item.TableId)
                    .ThenBy(static item => item.Guid.ToString(), StringComparer.Ordinal)
                    .ToImmutableArray(),
                pair.Key.Fingerprint))
            .ToImmutableArray();

        OperationReport report;
        if (requests.Length == 1)
        {
            report = _adapter.Save(requests[0]);
        }
        else if (_adapter is ITransactionalAuthoringWorkbookAdapter transactional)
        {
            report = transactional.Save(requests);
        }
        else
        {
            report = new OperationReport(
                "authoring-save",
                "assetdatabase-v1",
                false,
                [new Diagnostic(
                    "EXAD0204",
                    DiagnosticSeverity.Blocker,
                    ".",
                    "This save spans multiple workbooks, but the configured adapter has no recoverable multi-workbook transaction capability.")],
                []);
        }

        if (!report.Succeeded)
        {
            foreach (var diagnostic in report.Diagnostics)
                _diagnostics.Add(new AssetDatabaseDiagnostic(DateTimeOffset.UtcNow, diagnostic));
            SynchronizeAdapterConflicts();
            return;
        }

        FinalizeSuccessfulSave(selected);
        DrainPendingImports();

        void AddSaveItem(WorkbookState workbook, AuthoringSaveItem item)
        {
            if (!itemsByWorkbook.TryGetValue(workbook, out var list))
            {
                list = [];
                itemsByWorkbook.Add(workbook, list);
            }

            list.Add(item);
        }
    }

    private static AuthoringSaveItem ToSaveItem(AssetEntry entry, bool deleted, uint revision) =>
        new(
            entry.Guid,
            entry.Table.TableId,
            entry.Table.TableName,
            entry.Key,
            deleted ? null : entry.Asset,
            deleted,
            revision);

    private void FinalizeSuccessfulSave(IEnumerable<AssetEntry> entries)
    {
        var saved = entries.ToArray();
        foreach (var entry in saved)
        {
            if (entry.Deleted)
            {
                entry.Workbook.Entries.Remove(entry);
                RemoveIndexes(entry);
                continue;
            }

            entry.Revision = ReferenceEquals(entry.PersistedWorkbook, entry.Workbook)
                ? checked(entry.Revision + 1)
                : 1;
            entry.PersistedWorkbook = entry.Workbook;
            entry.PersistedKey = entry.Key;
            entry.Dirty = false;
        }

        foreach (var entry in saved)
        {
            foreach (var neighbor in entry.ImpactClosure.ToArray())
                neighbor.ImpactClosure.Remove(entry);
            entry.ImpactClosure.Clear();
        }
    }

    private void SynchronizeAdapterConflicts()
    {
        if (_adapter is not IAuthoringConflictSource source)
            return;
        _conflicts.Clear();
        _conflicts.AddRange(source.Conflicts);
    }

    private void RewriteIncomingReferences(AssetEntry target)
    {
        var projection = new AuthoringReferenceTarget(
            target.Guid,
            target.Table.TableId,
            target.Key,
            target.Path);
        var affected = new List<AssetEntry> { target };
        foreach (var owner in LiveEntries())
        {
            var touched = false;
            foreach (var reference in ReferenceBindings(owner).Where(reference => reference.Target == target.Guid))
            {
                reference.RewriteTarget?.Invoke(projection);
                touched = true;
            }

            if (touched && !ReferenceEquals(owner, target))
            {
                owner.Dirty = true;
                affected.Add(owner);
            }
        }


        LinkImpact(affected);
    }

    private static void LinkImpact(IEnumerable<AssetEntry> entries)
    {
        var closure = entries.Distinct().ToArray();
        foreach (var left in closure)
        {
            foreach (var right in closure)
            {
                if (!ReferenceEquals(left, right))
                    left.ImpactClosure.Add(right);
            }
        }
    }

    private static IEnumerable<AssetEntry> ExpandImpactClosure(AssetEntry root)
    {
        var result = new HashSet<AssetEntry> { root };
        var queue = new Queue<AssetEntry>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var current))
        {
            foreach (var neighbor in current.ImpactClosure)
            {
                if (result.Add(neighbor))
                    queue.Enqueue(neighbor);
            }
        }

        return result.Where(static entry => entry.Dirty || entry.Deleted);
    }

    private IEnumerable<AssetEntry> LiveEntries() => _byGuid.Values.Where(static entry => !entry.Deleted);

    private IEnumerable<AuthoringReference> ReferenceBindings(AssetEntry entry)
    {
        if (entry.Table.GetReferences is not null)
            return entry.Table.GetReferences(entry.Asset) ?? [];
        return (entry.Table.GetDependencies?.Invoke(entry.Asset) ?? [])
            .Select(static guid => new AuthoringReference(guid, string.Empty));
    }

    private bool TryGetLiveEntry(GUID guid, out AssetEntry entry)
    {
        if (_byGuid.TryGetValue(guid, out entry!) && !entry.Deleted)
            return true;
        entry = null!;
        return false;
    }

    private void QueueImport(string path, ImportAssetOptions options)
    {
        if (_pendingImports.TryGetValue(path, out var pending))
            _pendingImports[path] = pending | options;
        else
            _pendingImports.Add(path, options);
    }

    private void DrainPendingImports()
    {
        if (_editingDepth != 0 || _publishing || _pendingImports.Count == 0)
            return;
        var pending = _pendingImports.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToArray();
        foreach (var pair in pending)
        {
            if (_workbooks.TryGetValue(pair.Key, out var workbook)
                && workbook.Entries.Any(static entry => entry.Dirty || entry.Deleted))
            {
                continue;
            }

            _pendingImports.Remove(pair.Key);
            ImportAssetCore(pair.Key, pair.Value);
        }
    }

    private bool TryParseRowPath(string path, out WorkbookState workbook, out AuthoringTableRegistration table, out string key, out string? error)
    {
        foreach (var candidate in _workbooks.Values.OrderByDescending(static item => item.Path.Length))
        {
            var prefix = candidate.Path + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var remainder = path[prefix.Length..];
            var separator = remainder.IndexOf('/');
            if (separator <= 0 || separator == remainder.Length - 1)
                break;
            var tableName = remainder[..separator];
            if (!_tablesByName.TryGetValue(tableName, out table!))
                break;
            if (!candidate.HasTable(tableName))
            {
                workbook = null!;
                table = null!;
                key = string.Empty;
                error = $"Mounted workbook '{candidate.Path}' has no generated sheet for table '{tableName}'.";
                return false;
            }
            workbook = candidate;
            key = remainder[(separator + 1)..];
            error = null;
            return true;
        }

        workbook = null!;
        table = null!;
        key = string.Empty;
        error = "Path must address a registered table below a mounted workbook.";
        return false;
    }

    private bool TryAddIndexes(AssetEntry entry, out string error)
    {
        if (_byGuid.ContainsKey(entry.Guid))
        {
            error = $"Duplicate row GUID {entry.Guid}.";
            return false;
        }
        if (_byPath.ContainsKey(entry.Path))
        {
            error = $"Duplicate asset path '{entry.Path}'.";
            return false;
        }
        if (_byObject.ContainsKey(entry.Asset))
        {
            error = "The same object instance occurs more than once.";
            return false;
        }
        if (_byTableKey.ContainsKey((entry.Table.TableId, entry.Key)))
        {
            error = $"Duplicate project-domain key '{entry.Table.TableName}/{entry.Key}'.";
            return false;
        }
        _byGuid.Add(entry.Guid, entry);
        _byPath.Add(entry.Path, entry);
        _byObject.Add(entry.Asset, entry);
        _byTableKey.Add((entry.Table.TableId, entry.Key), entry);
        error = string.Empty;
        return true;
    }

    private void RemoveIndexes(AssetEntry entry)
    {
        _byGuid.Remove(entry.Guid);
        _byPath.Remove(entry.Path);
        _byObject.Remove(entry.Asset);
        _byTableKey.Remove((entry.Table.TableId, entry.Key));
    }

    private IEnumerable<string> Labels(AssetEntry entry) => entry.Table.GetLabels?.Invoke(entry.Asset) ?? [];

    private IEnumerable<GUID> References(AssetEntry entry) => ReferenceBindings(entry).Select(static reference => reference.Target);

    private bool ReferenceMatches(GUID guid, string term)
    {
        var targetPath = PathFromGuid(guid);
        return targetPath.Length != 0 && (string.Equals(targetPath, NormalizePath(term), StringComparison.Ordinal) || targetPath.EndsWith('/' + term, StringComparison.Ordinal));
    }

    private void PublishImport(OperationReport report, string path)
    {
        if (_publishing)
            throw new InvalidOperationException("Authoring publication is not reentrant.");
        _publishing = true;
        try
        {
            WorkbookImported?.Invoke(new ImportReport(path, report));
        }
        finally
        {
            _publishing = false;
        }

    }

    private void PublishEditingIfReady()
    {
        // Indexes are already updated synchronously. This seam deliberately only coalesces host refresh work.
        if (_editingDepth == 0)
            Thread.MemoryBarrier();
    }

    private void AddDiagnostic(string code, DiagnosticSeverity severity, string location, string message) =>
        _diagnostics.Add(new AssetDatabaseDiagnostic(DateTimeOffset.UtcNow, new Diagnostic(code, severity, location, message)));

    private static string[] Values(string[] tokens, string prefix) => tokens.Where(token => token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(token => token[prefix.Length..]).Where(static value => value.Length != 0).ToArray();

    private static bool IsAtOrBelow(string path, string folder) => string.Equals(path, folder, StringComparison.Ordinal) || path.StartsWith(folder + "/", StringComparison.Ordinal);

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.Replace('\\', '/').TrimEnd('/');
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void EnsureNotPublishing()
    {
        if (_publishing)
            throw new InvalidOperationException("Authoring state mutations are not allowed during workbookImported publication.");
    }

    private void EnsureExcelAllowed()
    {
        if (!_excelEnabled)
            throw new InvalidOperationException($"Excel authoring is disabled in runtime mode {_mode}; EditorPlayDebug/Development require explicit opt-in and Release always rejects it.");
    }

    private void EnsureWriteAllowed()
    {
        ThrowIfDisposed();
        EnsureNotPublishing();
        if (_mode is RuntimeMode.Development or RuntimeMode.Release)
            throw new InvalidOperationException($"Workbook writeback is disabled in runtime mode {_mode}.");
    }

    private sealed class WorkbookState(string path)
    {
        public string Path { get; } = path;
        public string? Fingerprint { get; set; }
        public HashSet<string>? AvailableTables { get; set; }
        public List<AssetEntry> Entries { get; } = [];

        public bool HasTable(string tableName) => AvailableTables?.Contains(tableName) ?? true;
    }

    private sealed class AssetEntry(
        WorkbookState workbook,
        AuthoringTableRegistration table,
        GUID guid,
        string key,
        object asset,
        uint revision,
        bool persisted)
    {
        private static long _nextOrder;
        public WorkbookState Workbook { get; set; } = workbook;
        public AuthoringTableRegistration Table { get; } = table;
        public GUID Guid { get; } = guid;
        public string Key { get; set; } = key;
        public object Asset { get; } = asset;
        public uint Revision { get; set; } = revision;
        public WorkbookState? PersistedWorkbook { get; set; } = persisted ? workbook : null;
        public string? PersistedKey { get; set; } = persisted ? key : null;
        public bool Dirty { get; set; }
        public bool Deleted { get; set; }
        public HashSet<AssetEntry> ImpactClosure { get; } = [];
        public long Order { get; } = Interlocked.Increment(ref _nextOrder);
        public string Path => $"{Workbook.Path}/{Table.TableName}/{Key}";
    }

    private sealed class Activation(AssetDatabaseSession owner) : IDisposable
    {
        private AssetDatabaseSession? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null)
                return;
            if (!ReferenceEquals(Ambient.Value, current))
                throw new InvalidOperationException("Authoring session activation scopes must be disposed in LIFO order.");
            Ambient.Value = current._previous;
            current._previous = null;
        }
    }
}

internal sealed class EmptyAssetDatabaseSession : AssetDatabaseSession
{
    public static EmptyAssetDatabaseSession Instance { get; } = new();
    private EmptyAssetDatabaseSession() : base(new EmptyAdapter()) { }

    private sealed class EmptyAdapter : IAuthoringWorkbookAdapter
    {
        public AuthoringWorkbookImport Import(string workbookPath, bool forceUpdate) => new(workbookPath, [], Failure("import", workbookPath), null);
        public OperationReport Save(AuthoringSaveRequest request) => Failure("save", request.WorkbookPath);
        public bool Open(string workbookPath, string tableName, GUID guid) => false;
        private static OperationReport Failure(string operation, string path) => new(operation, "0", false, [new Diagnostic("EXAD0000", DiagnosticSeverity.Error, path, "No AssetDatabaseSession is active.")], []);
    }
}
