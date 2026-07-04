using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using ExcelDb.Loading;
using ExcelDb.Schema;

namespace ExcelDb.Database
{
    public sealed class Pack
    {
        public string Name { get; }
        public ITableLoader Loader { get; }
        public bool Writable => (Loader.Capabilities & LoaderCapabilities.Write) != 0;

        public Pack(string name, ITableLoader loader)
        {
            Name = name;
            Loader = loader;
        }
    }

    /// <summary>
    /// Runtime store of one table: resident message instances by id, key index,
    /// high-water id mark, dirty flag and owning pack.
    /// </summary>
    sealed class TableStore
    {
        public TableSchema Schema { get; }
        public Pack Pack { get; }

        public readonly Dictionary<int, IMessage> Rows = new Dictionary<int, IMessage>();
        public readonly Dictionary<RowKey, int> KeyIndex = new Dictionary<RowKey, int>();

        /// <summary>Next id to assign; never decreases, ids are never reused.</summary>
        public int NextId { get; private set; } = 1;

        public bool Dirty { get; set; }

        public TableStore(TableSchema schema, Pack pack)
        {
            Schema = schema;
            Pack = pack;
        }

        public void ReserveId(int existingId)
        {
            if (existingId >= NextId)
                NextId = existingId + 1;
        }

        public int TakeNextId() => NextId++;

        public RowKey KeyOf(IMessage row) => RowKey.Extract(row, Schema);

        public void IndexKey(IMessage row, int id) => KeyIndex[KeyOf(row)] = id;

        public void UnindexKey(IMessage row, int id)
        {
            var key = KeyOf(row);
            if (KeyIndex.TryGetValue(key, out var mapped) && mapped == id)
                KeyIndex.Remove(key);
        }

        public IReadOnlyList<int> IdsInOrder() => Rows.Keys.OrderBy(id => id).ToArray();
    }
}
