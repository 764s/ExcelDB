using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using ExcelDb.Editing;
using ExcelDb.Graph;
using ExcelDb.Loading;
using ExcelDb.Resources;
using ExcelDb.Schema;

namespace ExcelDb.Database
{
    public sealed class PackMounts
    {
        internal readonly List<Pack> Packs = new List<Pack>();
        internal readonly List<IExternalLoader> ExternalLoaders = new List<IExternalLoader>();

        public PackMounts Mount(string name, ITableLoader loader)
        {
            Packs.Add(new Pack(name, loader));
            return this;
        }

        public PackMounts MountExternal(IExternalLoader loader)
        {
            ExternalLoaders.Add(loader);
            return this;
        }
    }

    /// <summary>
    /// The single facade: reads, editing, undo/redo, resource scopes, clipboard,
    /// validation and hot reload. Externally one whole; internally partitioned
    /// into packs, each owned by one table loader. Single-threaded by contract (v1).
    /// </summary>
    public sealed partial class ConfigDatabase
    {
        internal readonly SchemaRegistry Registry;
        internal readonly DatabaseOptions Options;

        readonly Dictionary<int, TableStore> _stores = new Dictionary<int, TableStore>();
        readonly List<Pack> _packs = new List<Pack>();
        readonly DependencyGraph _graph = new DependencyGraph();
        readonly UndoStack _undo;
        readonly Dictionary<string, int> _ignored = new Dictionary<string, int>(StringComparer.Ordinal);

        internal ResourceManager Resources { get; }

        public event Action<ChangeSet>? OnRowsChanged;
        public event Action<TableId>? OnTableReloaded;
        public event Action<TableId, Exception>? OnReloadFailed;
        public event Action<string>? OnOperationIgnored;

        ConfigDatabase(SchemaRegistry registry, DatabaseOptions options, PackMounts mounts)
        {
            Registry = registry;
            Options = options;
            _undo = new UndoStack(options.UndoDepth);
            Resources = new ResourceManager(this, mounts.ExternalLoaders);

            foreach (var pack in mounts.Packs)
            {
                _packs.Add(pack);
                foreach (var table in pack.Loader.Tables)
                    ImportTable(pack, table);
                pack.Loader.TableChanged += table => ReloadTable(pack, table);
            }
        }

        public static ConfigDatabase Open(SchemaRegistry registry, Action<PackMounts> mount, DatabaseOptions? options = null)
        {
            var mounts = new PackMounts();
            mount(mounts);
            return new ConfigDatabase(registry, options ?? new DatabaseOptions(), mounts);
        }

        // -------------------------------------------------------------------
        // Import & hot reload
        // -------------------------------------------------------------------

        void ImportTable(Pack pack, TableId table)
        {
            var schema = Registry.Get(table);
            if (_stores.ContainsKey(table.Number))
                throw new SchemaException($"Table '{schema.Name}' is provided by more than one pack.");

            var store = new TableStore(schema, pack);
            var content = pack.Loader.Load(table);
            foreach (var record in content.Rows)
            {
                if (record.Id <= 0)
                    throw new DataValidationException($"{schema.Name}: row ids must be positive (got {record.Id}).");
                if (store.Rows.ContainsKey(record.Id))
                    throw new DataValidationException($"{schema.Name}#{record.Id}: duplicate row id.");

                // Residents are private clones so loader-side instances can never alias the store.
                var resident = MessageOps.Clone(record.Row);
                var key = store.KeyOf(resident);
                if (store.KeyIndex.ContainsKey(key))
                    throw new DataValidationException($"{schema.Name}#{record.Id}: duplicate key ({key}).");

                store.Rows.Add(record.Id, resident);
                store.KeyIndex.Add(key, record.Id);
                store.ReserveId(record.Id);
                _graph.RebuildRow(new RowId(table, record.Id), resident);
            }
            _stores.Add(table.Number, store);
        }

        void ReloadTable(Pack pack, TableId table)
        {
            if (!_stores.TryGetValue(table.Number, out var store) || store.Pack != pack)
                return;

            try
            {
                ApplyReload(store, table, pack.Loader.Load(table));
            }
            catch (Exception e)
            {
                // A bad file must never kill the session: keep old data, surface the failure.
                OnReloadFailed?.Invoke(table, e);
                CountIgnored("Reload");
            }
        }

        void ApplyReload(TableStore store, TableId table, TableContent content)
        {
            var schema = store.Schema;
            var incoming = new Dictionary<int, IMessage>();
            var keySeen = new HashSet<RowKey>();
            foreach (var record in content.Rows)
            {
                if (record.Id <= 0 || incoming.ContainsKey(record.Id))
                    throw new DataValidationException($"{schema.Name}: invalid or duplicate id {record.Id} in reload.");
                if (!keySeen.Add(RowKey.Extract(record.Row, schema)))
                    throw new DataValidationException($"{schema.Name}#{record.Id}: duplicate key in reload.");
                incoming.Add(record.Id, record.Row);
            }

            // Deletions first.
            foreach (var id in store.IdsInOrder())
                if (!incoming.ContainsKey(id))
                {
                    var resident = store.Rows[id];
                    store.UnindexKey(resident, id);
                    store.Rows.Remove(id);
                    _graph.RemoveRow(new RowId(table, id));
                }

            // Survivors are merged in place so holders keep the same instance; new rows inserted.
            store.KeyIndex.Clear();
            foreach (var pair in incoming)
            {
                if (store.Rows.TryGetValue(pair.Key, out var resident))
                    MessageOps.CopyInto(resident, pair.Value);
                else
                    store.Rows.Add(pair.Key, resident = MessageOps.Clone(pair.Value));

                store.KeyIndex[store.KeyOf(resident)] = pair.Key;
                store.ReserveId(pair.Key);
                _graph.RebuildRow(new RowId(table, pair.Key), resident);
            }

            store.Dirty = false;               // source is authoritative now
            _undo.Prune(table);                // established rule: drop entries touching the reloaded table
            OnTableReloaded?.Invoke(table);
        }

        // -------------------------------------------------------------------
        // Reads
        // -------------------------------------------------------------------

        internal TableStore StoreOf(TableId table) =>
            _stores.TryGetValue(table.Number, out var store)
                ? store
                : throw new SchemaException($"Table {table.Number} is not mounted.");

        internal bool TryStoreOf(TableId table, out TableStore store) => _stores.TryGetValue(table.Number, out store!);

        public IMessage LoadAsset(RowId id) =>
            TryLoadAsset(id, out var row) ? row : throw new RowNotFoundException(id, Describe(id));

        public T LoadAsset<T>(RowId id) where T : class, IMessage =>
            LoadAsset(id) as T
            ?? throw new ExcelDbException($"{Describe(id)} is not of type {typeof(T).Name}.");

        public T LoadAsset<T>(Protocol.RowRef reference) where T : class, IMessage =>
            LoadAsset<T>(new RowId(reference.Table, reference.Id));

        public bool TryLoadAsset(RowId id, out IMessage row)
        {
            row = null!;
            if (!_stores.TryGetValue(id.Table.Number, out var store))
                return false;
            if (!store.Rows.TryGetValue(id.Id, out var resident))
                return false;
            row = resident;
            return true;
        }

        public bool TryLoadAsset<T>(RowId id, out T row) where T : class, IMessage
        {
            row = (TryLoadAsset(id, out var untyped) ? untyped as T : null)!;
            return row != null;
        }

        public RowId Find<T>(params object?[] keyParts) => Find(Registry.Get<T>(), keyParts);

        public RowId Find(string tableName, params object?[] keyParts) => Find(Registry.Get(tableName), keyParts);

        RowId Find(TableSchema schema, object?[] keyParts)
        {
            var store = StoreOf(schema.Id);
            var key = RowKey.Of(keyParts);
            if (!store.KeyIndex.TryGetValue(key, out var id))
                throw new ExcelDbException($"{schema.Name}[{key}] not found.");
            return new RowId(schema.Id, id);
        }

        public bool TryFind<T>(out RowId id, params object?[] keyParts)
        {
            var schema = Registry.Get<T>();
            var store = StoreOf(schema.Id);
            if (store.KeyIndex.TryGetValue(RowKey.Of(keyParts), out var rowId))
            {
                id = new RowId(schema.Id, rowId);
                return true;
            }
            id = default;
            return false;
        }

        public IReadOnlyList<RowId> FindAssets<T>() => FindAssets(Registry.Get<T>().Id);

        public IReadOnlyList<RowId> FindAssets(TableId table)
        {
            var store = StoreOf(table);
            return store.IdsInOrder().Select(id => new RowId(table, id)).ToArray();
        }

        /// <summary>Human form: "Table#id (key)". Framework outputs must use this, never raw numbers.</summary>
        public string Describe(RowId id)
        {
            if (!Registry.TryGet(id.Table, out var schema))
                return $"?{id.Table.Number}#{id.Id}";
            if (!_stores.TryGetValue(id.Table.Number, out var store) || !store.Rows.TryGetValue(id.Id, out var row))
                return $"{schema.Name}#{id.Id} <missing>";
            return $"{schema.Name}#{id.Id} ({RowKey.Extract(row, schema)})";
        }

        // -------------------------------------------------------------------
        // Dependencies
        // -------------------------------------------------------------------

        public DependencyInfo GetDependencies(RowId id, bool recursive = false)
        {
            if (!TryLoadAsset(id, out _))
                throw new RowNotFoundException(id, Describe(id));
            return _graph.Collect(id, recursive);
        }

        public IReadOnlyList<RowId> GetInboundReferences(RowId id) => _graph.InboundReferences(id);

        // -------------------------------------------------------------------
        // Resource scopes
        // -------------------------------------------------------------------

        public ResourceScope CreateScope(string label) => new ResourceScope(this, label);

        // -------------------------------------------------------------------
        // Policy & diagnostics
        // -------------------------------------------------------------------

        internal void CountIgnored(string operation)
        {
            _ignored.TryGetValue(operation, out var count);
            _ignored[operation] = count + 1;
            OnOperationIgnored?.Invoke(operation);
        }

        /// <summary>Returns true when the caller should proceed; false = silently ignore (already counted).</summary>
        internal bool HandleUnsupported(string operation, string reason)
        {
            if (Options.UnsupportedOperation == UnsupportedPolicy.Throw)
                throw new UnsupportedOperationException($"{operation}: {reason}");
            CountIgnored(operation);
            return false;
        }

        public DatabaseInfo Info => new DatabaseInfo(
            _packs.Select(p => new PackInfo(
                p.Name, p.Writable,
                _stores.Values.Count(s => s.Pack == p),
                _stores.Values.Where(s => s.Pack == p).Sum(s => s.Rows.Count))).ToArray(),
            new Dictionary<string, int>(_ignored),
            _undo.UndoCount, _undo.RedoCount,
            _stores.Values.Where(s => s.Dirty).Select(s => s.Schema.Name).ToArray());

        internal void RaiseRowsChanged(ChangeSet changes) => OnRowsChanged?.Invoke(changes);
    }

    public sealed class PackInfo
    {
        public string Name { get; }
        public bool Writable { get; }
        public int TableCount { get; }
        public int RowCount { get; }

        public PackInfo(string name, bool writable, int tableCount, int rowCount)
        {
            Name = name;
            Writable = writable;
            TableCount = tableCount;
            RowCount = rowCount;
        }
    }

    public sealed class DatabaseInfo
    {
        public IReadOnlyList<PackInfo> Packs { get; }
        public IReadOnlyDictionary<string, int> IgnoredOperations { get; }
        public int UndoCount { get; }
        public int RedoCount { get; }
        public IReadOnlyList<string> DirtyTables { get; }

        public DatabaseInfo(
            IReadOnlyList<PackInfo> packs, IReadOnlyDictionary<string, int> ignoredOperations,
            int undoCount, int redoCount, IReadOnlyList<string> dirtyTables)
        {
            Packs = packs;
            IgnoredOperations = ignoredOperations;
            UndoCount = undoCount;
            RedoCount = redoCount;
            DirtyTables = dirtyTables;
        }
    }
}
