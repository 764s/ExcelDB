using System;
using System.Collections.Generic;
using Google.Protobuf;
using ExcelDb.Database;

namespace ExcelDb.Editing
{
    public interface IEditTransaction : IDisposable
    {
        /// <summary>Mutable draft (clone) of an existing row. All writes go through drafts.</summary>
        T GetMutable<T>(RowId id) where T : class, IMessage;
        IMessage GetMutable(RowId id);

        /// <summary>Creates a new row in the table of <typeparamref name="T"/>; id is assigned immediately.</summary>
        (RowId Id, T Draft) Create<T>() where T : class, IMessage, new();
        (RowId Id, IMessage Draft) Create(TableId table);

        void Delete(RowId id);

        /// <summary>Applies the accumulated changes as one undo unit. Without Commit, Dispose discards everything.</summary>
        void Commit();
    }

    sealed class EditTransaction : IEditTransaction
    {
        readonly ConfigDatabase _db;
        readonly string _label;

        readonly Dictionary<RowId, IMessage> _drafts = new Dictionary<RowId, IMessage>();
        readonly List<RowId> _draftOrder = new List<RowId>();
        readonly HashSet<RowId> _created = new HashSet<RowId>();
        readonly HashSet<RowId> _ignored = new HashSet<RowId>();   // silent policy: detached drafts
        readonly HashSet<RowId> _deleted = new HashSet<RowId>();
        readonly Dictionary<int, int> _peekIds = new Dictionary<int, int>();
        bool _committed;

        internal EditTransaction(ConfigDatabase db, string label)
        {
            _db = db;
            _label = label;
        }

        public IMessage GetMutable(RowId id)
        {
            if (_deleted.Contains(id))
                throw new ExcelDbException($"{_db.Describe(id)} was deleted in this transaction.");
            if (_drafts.TryGetValue(id, out var existing))
                return existing;

            var resident = _db.LoadAsset(id);
            var draft = MessageOps.Clone(resident);

            var store = _db.StoreOfInternal(id.Table);
            if (!store.Pack.Writable && !_db.HandleUnsupported("Edit", $"pack '{store.Pack.Name}' is read-only"))
                _ignored.Add(id);   // fully usable draft; dropped at commit

            _drafts.Add(id, draft);
            _draftOrder.Add(id);
            return draft;
        }

        public T GetMutable<T>(RowId id) where T : class, IMessage =>
            GetMutable(id) as T ?? throw new ExcelDbException($"{_db.Describe(id)} is not of type {typeof(T).Name}.");

        public (RowId Id, IMessage Draft) Create(TableId table)
        {
            var store = _db.StoreOfInternal(table);
            var draft = store.Schema.Descriptor.Parser.ParseFrom(Google.Protobuf.ByteString.Empty);

            bool usable = store.Pack.Writable || _db.HandleUnsupported("Create", $"pack '{store.Pack.Name}' is read-only");
            int id;
            if (usable)
            {
                id = store.TakeNextId();
            }
            else
            {
                // Detached draft under silent policy: peek ids locally, consume nothing.
                _peekIds.TryGetValue(table.Number, out var offset);
                _peekIds[table.Number] = offset + 1;
                id = store.NextId + offset;
            }

            var rowId = new RowId(table, id);
            _drafts.Add(rowId, draft);
            _draftOrder.Add(rowId);
            _created.Add(rowId);
            if (!usable)
                _ignored.Add(rowId);
            return (rowId, draft);
        }

        public (RowId Id, T Draft) Create<T>() where T : class, IMessage, new()
        {
            var (id, draft) = Create(_db.Registry.Get<T>().Id);
            return (id, (T)draft);
        }

        public void Delete(RowId id)
        {
            if (_created.Contains(id))
            {
                _drafts.Remove(id);
                _draftOrder.Remove(id);
                _created.Remove(id);
                _ignored.Remove(id);
                return;
            }
            _db.LoadAsset(id);   // existence check
            var store = _db.StoreOfInternal(id.Table);
            if (!store.Pack.Writable && !_db.HandleUnsupported("Delete", $"pack '{store.Pack.Name}' is read-only"))
                return;
            _deleted.Add(id);
            _drafts.Remove(id);
            _draftOrder.Remove(id);
        }

        public void Commit()
        {
            if (_committed)
                throw new ExcelDbException("Transaction was already committed.");
            _committed = true;
            _db.CommitTransaction(_label, _draftOrder, _drafts, _created, _ignored, _deleted);
        }

        public void Dispose()
        {
            // Uncommitted transactions simply evaporate; drafts were private clones.
        }
    }
}
