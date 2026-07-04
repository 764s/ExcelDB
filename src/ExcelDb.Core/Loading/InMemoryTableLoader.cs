using System;
using System.Collections.Generic;

namespace ExcelDb.Loading
{
    /// <summary>
    /// Table source backed by plain dictionaries. Primary loader for unit tests and
    /// programmatic scenarios; also the reference implementation of loader semantics.
    /// </summary>
    public sealed class InMemoryTableLoader : ITableLoader
    {
        readonly Dictionary<int, TableContent> _tables = new Dictionary<int, TableContent>();
        readonly List<(TableId Table, TableContent Content)> _writes = new List<(TableId, TableContent)>();
        readonly HashSet<int> _tableIds = new HashSet<int>();
        readonly List<TableId> _tableList = new List<TableId>();

        public bool IsWritable { get; set; } = true;

        /// <summary>Write log for test assertions.</summary>
        public IReadOnlyList<(TableId Table, TableContent Content)> Writes => _writes;

        public IReadOnlyCollection<TableId> Tables => _tableList;

        public LoaderCapabilities Capabilities =>
            (IsWritable ? LoaderCapabilities.Write : LoaderCapabilities.None) | LoaderCapabilities.Watch;

        public event Action<TableId>? TableChanged;

        public InMemoryTableLoader Add(TableId table, TableContent content)
        {
            _tables[table.Number] = content;
            if (_tableIds.Add(table.Number))
                _tableList.Add(table);
            return this;
        }

        /// <summary>Swaps table content and raises the hot-reload signal.</summary>
        public void Replace(TableId table, TableContent content)
        {
            _tables[table.Number] = content;
            if (_tableIds.Add(table.Number))
                _tableList.Add(table);
            TableChanged?.Invoke(table);
        }

        public TableContent Load(TableId table) =>
            _tables.TryGetValue(table.Number, out var content)
                ? content
                : throw new ExcelDbException($"In-memory loader has no table {table.Number}.");

        public void Write(TableId table, TableContent content)
        {
            if (!IsWritable)
                throw new ExcelDbException("Loader is read-only.");
            _tables[table.Number] = content;
            _writes.Add((table, content));
        }
    }
}
