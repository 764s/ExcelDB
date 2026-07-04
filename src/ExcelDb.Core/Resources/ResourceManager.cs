using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ExcelDb.Database;
using ExcelDb.Loading;

namespace ExcelDb.Resources
{
    /// <summary>
    /// Framework-side cache + refcount + in-flight dedupe over host external loaders.
    /// External loaders stay stateless pure IO. Assets form a flat refcount table -
    /// rows are memory-resident, so cycles in the row graph never threaten unloading.
    /// </summary>
    internal sealed class ResourceManager
    {
        sealed class Entry
        {
            public Task<object?> Loading = Task.FromResult<object?>(null);
            public int RefCount;
            public IExternalLoader? Loader;
        }

        readonly ConfigDatabase _db;
        readonly Dictionary<string, IExternalLoader> _loaders = new Dictionary<string, IExternalLoader>(StringComparer.Ordinal);
        readonly Dictionary<ExternalKey, Entry> _entries = new Dictionary<ExternalKey, Entry>();

        public ResourceManager(ConfigDatabase db, IEnumerable<IExternalLoader> loaders)
        {
            _db = db;
            foreach (var loader in loaders)
                _loaders[loader.Scheme] = loader;
        }

        public Task<object?> Acquire(ExternalKey key)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.RefCount++;
                return entry.Loading;
            }

            entry = new Entry { RefCount = 1 };
            if (_loaders.TryGetValue(key.Scheme, out var loader))
            {
                entry.Loader = loader;
                entry.Loading = loader.LoadAsync(key).AsTask();
            }
            else
            {
                _db.HandleUnsupported("LoadExternal", $"no loader mounted for scheme '{key.Scheme}'");
                // Silent policy: a fully usable handle resolving to null.
            }
            _entries.Add(key, entry);
            return entry.Loading;
        }

        public void Release(ExternalKey key)
        {
            if (!_entries.TryGetValue(key, out var entry))
                return;
            if (--entry.RefCount > 0)
                return;

            _entries.Remove(key);
            if (entry.Loader != null && entry.Loading.IsCompletedSuccessfully && entry.Loading.Result is { } resource)
                entry.Loader.Unload(key, resource);
        }

        public int RefCountOf(ExternalKey key) => _entries.TryGetValue(key, out var e) ? e.RefCount : 0;
    }

    /// <summary>
    /// Batch lifetime for acquired externals. Same key acquired twice in one scope
    /// counts once; Dispose returns the whole batch.
    /// </summary>
    public sealed class ResourceScope : IDisposable
    {
        readonly ConfigDatabase _db;
        readonly Dictionary<ExternalKey, Task<object?>> _held = new Dictionary<ExternalKey, Task<object?>>();
        bool _disposed;

        public string Label { get; }

        internal ResourceScope(ConfigDatabase db, string label)
        {
            _db = db;
            Label = label;
        }

        public Task<object?> Acquire(Protocol.ExternalRef reference) => Acquire(new ExternalKey(reference));

        public Task<object?> Acquire(ExternalKey key)
        {
            CheckAlive();
            if (_held.TryGetValue(key, out var existing))
                return existing;
            var task = _db.Resources.Acquire(key);
            _held.Add(key, task);
            return task;
        }

        /// <summary>Acquires every external referenced by the row (and, when recursive, its whole dependency subtree).</summary>
        public ResourceScope PreloadRow(RowId id, bool recursive = true)
        {
            CheckAlive();
            foreach (var key in _db.GetDependencies(id, recursive).Externals)
                Acquire(key);
            return this;
        }

        public Task WaitAll()
        {
            CheckAlive();
            var tasks = new Task[_held.Count];
            int i = 0;
            foreach (var task in _held.Values)
                tasks[i++] = task;
            return Task.WhenAll(tasks);
        }

        /// <summary>Resolved resource lookup; the consumer names the concrete type at this boundary.</summary>
        public T? Get<T>(Protocol.ExternalRef reference) where T : class => Get<T>(new ExternalKey(reference));

        public T? Get<T>(ExternalKey key) where T : class
        {
            CheckAlive();
            if (!_held.TryGetValue(key, out var task))
                throw new ExcelDbException($"Scope '{Label}' did not acquire {key}.");
            if (!task.IsCompleted)
                throw new ExcelDbException($"{key} is still loading; await WaitAll() first.");
            return task.Result as T;
        }

        public int Count => _held.Count;

        void CheckAlive()
        {
            if (_disposed)
                throw new ObjectDisposedException($"ResourceScope '{Label}'");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var key in _held.Keys)
                _db.Resources.Release(key);
            _held.Clear();
        }
    }
}
