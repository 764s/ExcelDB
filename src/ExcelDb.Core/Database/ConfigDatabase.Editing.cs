using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using ExcelDb.Editing;
using ExcelDb.Loading;

namespace ExcelDb.Database
{
    public sealed partial class ConfigDatabase
    {
        internal TableStore StoreOfInternal(TableId table) => StoreOf(table);

        public bool CanUndo => _undo.CanUndo;
        public bool CanRedo => _undo.CanRedo;

        public IEditTransaction BeginEdit(string label)
        {
            if (!_packs.Any(p => p.Writable))
                HandleUnsupported("BeginEdit", "no writable pack is mounted");
            // Under silent policy the transaction proceeds; per-row enforcement drops changes.
            return new EditTransaction(this, label);
        }

        internal void CommitTransaction(
            string label,
            List<RowId> draftOrder,
            Dictionary<RowId, IMessage> drafts,
            HashSet<RowId> created,
            HashSet<RowId> ignored,
            HashSet<RowId> deleted)
        {
            // ------------------------------------------------ collect effective changes
            var edits = new List<RowId>();
            var creations = new List<RowId>();
            foreach (var id in draftOrder)
            {
                if (ignored.Contains(id))
                    continue;
                if (created.Contains(id))
                {
                    creations.Add(id);
                }
                else
                {
                    var resident = LoadAsset(id);
                    if (!resident.Equals(drafts[id]))
                        edits.Add(id);
                }
            }

            if (edits.Count == 0 && creations.Count == 0 && deleted.Count == 0)
                return;

            ValidateKeys(edits, creations, drafts, deleted);

            // ------------------------------------------------ apply
            var changes = new List<RowChange>(edits.Count + creations.Count + deleted.Count);

            foreach (var id in deleted)
            {
                var store = StoreOf(id.Table);
                var resident = store.Rows[id.Id];
                changes.Add(new RowChange(id, MessageOps.Clone(resident), null));
                store.UnindexKey(resident, id.Id);
                store.Rows.Remove(id.Id);
                _graph.RemoveRow(id);
                store.Dirty = true;
            }

            foreach (var id in edits)
            {
                var store = StoreOf(id.Table);
                var resident = store.Rows[id.Id];
                var before = MessageOps.Clone(resident);
                store.UnindexKey(resident, id.Id);
                MessageOps.CopyInto(resident, drafts[id]);   // holders keep the same instance
                store.IndexKey(resident, id.Id);
                _graph.RebuildRow(id, resident);
                store.Dirty = true;
                changes.Add(new RowChange(id, before, MessageOps.Clone(resident)));
            }

            foreach (var id in creations)
            {
                var store = StoreOf(id.Table);
                var resident = MessageOps.Clone(drafts[id]);
                store.Rows.Add(id.Id, resident);
                store.IndexKey(resident, id.Id);
                store.ReserveId(id.Id);
                _graph.RebuildRow(id, resident);
                store.Dirty = true;
                changes.Add(new RowChange(id, null, MessageOps.Clone(resident)));
            }

            var changeSet = new ChangeSet(label, changes);
            _undo.Push(changeSet);
            RaiseRowsChanged(changeSet);
        }

        void ValidateKeys(
            List<RowId> edits, List<RowId> creations,
            Dictionary<RowId, IMessage> drafts, HashSet<RowId> deleted)
        {
            var touchedTables = edits.Select(e => e.Table.Number)
                .Concat(creations.Select(c => c.Table.Number))
                .Concat(deleted.Select(d => d.Table.Number))
                .Distinct();

            foreach (var tableNumber in touchedTables)
            {
                var table = new TableId(tableNumber);
                var store = StoreOf(table);

                // Final key -> id view of this table after the transaction.
                var finalKeys = new Dictionary<RowKey, int>(store.KeyIndex);
                foreach (var id in deleted)
                    if (id.Table == table)
                        finalKeys.Remove(store.KeyOf(store.Rows[id.Id]));
                foreach (var id in edits)
                    if (id.Table == table)
                        finalKeys.Remove(store.KeyOf(store.Rows[id.Id]));

                foreach (var id in edits.Concat(creations))
                {
                    if (id.Table != table)
                        continue;
                    var key = store.KeyOf(drafts[id]);
                    if (finalKeys.TryGetValue(key, out var holder))
                        throw new DataValidationException(
                            $"{store.Schema.Name}: key ({key}) of row #{id.Id} conflicts with #{holder}.");
                    finalKeys.Add(key, id.Id);
                }
            }
        }

        // -------------------------------------------------------------------
        // Undo / redo
        // -------------------------------------------------------------------

        public bool Undo()
        {
            var entry = _undo.PopUndo();
            if (entry == null)
                return false;

            var inverse = new List<RowChange>(entry.Changes.Count);
            for (int i = entry.Changes.Count - 1; i >= 0; i--)
            {
                var change = entry.Changes[i];
                ApplySnapshot(change.Row, change.Before);
                inverse.Add(new RowChange(change.Row, change.After, change.Before));
            }
            RaiseRowsChanged(new ChangeSet($"Undo: {entry.Label}", inverse));
            return true;
        }

        public bool Redo()
        {
            var entry = _undo.PopRedo();
            if (entry == null)
                return false;

            var forward = new List<RowChange>(entry.Changes.Count);
            foreach (var change in entry.Changes)
            {
                ApplySnapshot(change.Row, change.After);
                forward.Add(change);
            }
            RaiseRowsChanged(new ChangeSet($"Redo: {entry.Label}", forward));
            return true;
        }

        /// <summary>Forces a row to an absolute snapshot; null snapshot = row absent.</summary>
        void ApplySnapshot(RowId id, IMessage? snapshot)
        {
            var store = StoreOf(id.Table);
            store.Rows.TryGetValue(id.Id, out var resident);

            if (snapshot == null)
            {
                if (resident != null)
                {
                    store.UnindexKey(resident, id.Id);
                    store.Rows.Remove(id.Id);
                    _graph.RemoveRow(id);
                }
            }
            else if (resident != null)
            {
                store.UnindexKey(resident, id.Id);
                MessageOps.CopyInto(resident, snapshot);
                store.IndexKey(resident, id.Id);
                _graph.RebuildRow(id, resident);
            }
            else
            {
                // Row is re-created (undo of delete); instance identity is not preserved across delete.
                var recreated = MessageOps.Clone(snapshot);
                store.Rows.Add(id.Id, recreated);
                store.IndexKey(recreated, id.Id);
                store.ReserveId(id.Id);
                _graph.RebuildRow(id, recreated);
            }
            store.Dirty = true;
        }

        // -------------------------------------------------------------------
        // Save
        // -------------------------------------------------------------------

        public void SaveAssets()
        {
            foreach (var store in _stores.Values)
            {
                if (!store.Dirty)
                    continue;
                if (!store.Pack.Writable)
                {
                    HandleUnsupported("SaveAssets", $"pack '{store.Pack.Name}' is read-only");
                    continue;   // stays dirty: the data is still unsaved
                }

                var rows = new List<TableRecord>();
                foreach (var id in store.IdsInOrder())
                    rows.Add(new TableRecord(id, MessageOps.Clone(store.Rows[id])));
                store.Pack.Loader.Write(store.Schema.Id, new TableContent(rows));
                store.Dirty = false;
            }
        }
    }
}
