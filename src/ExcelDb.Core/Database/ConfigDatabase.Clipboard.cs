using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using ExcelDb.Protocol;

namespace ExcelDb.Database
{
    public enum CopyDepth
    {
        /// <summary>Only the given rows; references pointing outside stay as-is.</summary>
        Shallow = 0,
        /// <summary>The rows plus their whole row-dependency subtree (cycle-safe).</summary>
        Subtree = 1,
    }

    /// <summary>
    /// Self-contained clipboard envelope: table numbers + original ids + serialized rows.
    /// Being bytes, it survives across database instances, processes and sessions.
    /// </summary>
    public sealed class ClipboardData
    {
        internal readonly struct Item
        {
            public readonly int Table;
            public readonly int OriginalId;
            public readonly byte[] Bytes;

            public Item(int table, int originalId, byte[] bytes)
            {
                Table = table;
                OriginalId = originalId;
                Bytes = bytes;
            }
        }

        internal readonly List<Item> Items = new List<Item>();

        public int Count => Items.Count;
    }

    public sealed partial class ConfigDatabase
    {
        public ClipboardData Copy(IReadOnlyList<RowId> rows, CopyDepth depth = CopyDepth.Shallow)
        {
            var all = new List<RowId>(rows);
            if (depth == CopyDepth.Subtree)
            {
                var seen = new HashSet<RowId>(rows);
                foreach (var root in rows)
                foreach (var dependency in GetDependencies(root, recursive: true).Rows)
                    if (seen.Add(dependency))
                        all.Add(dependency);
            }

            var data = new ClipboardData();
            foreach (var id in all)
            {
                var row = LoadAsset(id);   // throws for missing rows - copying garbage is a data error
                data.Items.Add(new ClipboardData.Item(id.Table.Number, id.Id, row.ToByteArray()));
            }
            return data;
        }

        /// <summary>
        /// Pastes rows back into their tables: fresh ids, internal references remapped
        /// (cycle-safe: remap is a flat table lookup), key conflicts resolved by suffixing
        /// the last string key segment, otherwise a data error. One undo unit.
        /// </summary>
        public IReadOnlyList<RowId> Paste(ClipboardData data)
        {
            using var tx = BeginEdit("Paste");

            // Pass 1: create drafts, build the old->new id map.
            var idMap = new Dictionary<RowId, RowId>();
            var drafts = new List<(RowId NewId, IMessage Draft)>();
            foreach (var item in data.Items)
            {
                var table = new TableId(item.Table);
                var schema = Registry.Get(table);
                var (newId, draft) = tx.Create(table);
                draft.MergeFrom(item.Bytes);
                idMap[new RowId(table, item.OriginalId)] = newId;
                drafts.Add((newId, draft));
                _ = schema;
            }

            // Pass 2: remap references among the pasted set; outward references stay.
            foreach (var (_, draft) in drafts)
                MessageOps.WalkRowRefs(draft, (_, reference) =>
                {
                    if (idMap.TryGetValue(new RowId(reference.Table, reference.Id), out var mapped))
                    {
                        reference.Table = mapped.Table.Number;
                        reference.Id = mapped.Id;
                    }
                });

            // Pass 3: resolve key conflicts.
            foreach (var (newId, draft) in drafts)
                EnsureUniqueKey(newId, draft, drafts);

            tx.Commit();
            return drafts.Select(d => d.NewId).ToArray();
        }

        void EnsureUniqueKey(RowId id, IMessage draft, List<(RowId NewId, IMessage Draft)> batch)
        {
            var store = StoreOf(id.Table);
            var schema = store.Schema;

            bool Taken(RowKey key)
            {
                if (store.KeyIndex.ContainsKey(key))
                    return true;
                foreach (var (otherId, otherDraft) in batch)
                    if (otherId != id && otherId.Table == id.Table && store.KeyOf(otherDraft).Equals(key))
                        return true;
                return false;
            }

            if (!Taken(store.KeyOf(draft)))
                return;

            var lastSegment = schema.KeyFields[schema.KeyFields.Count - 1];
            if (lastSegment.FieldType != Google.Protobuf.Reflection.FieldType.String)
                throw new DataValidationException(
                    $"{schema.Name}: pasted key ({store.KeyOf(draft)}) conflicts and the last key segment is not a string; " +
                    "rewrite keys before pasting.");

            var original = (string)lastSegment.Accessor.GetValue(draft);
            for (int suffix = 1; suffix < 10000; suffix++)
            {
                lastSegment.Accessor.SetValue(draft, $"{original}_copy{(suffix == 1 ? "" : suffix.ToString())}");
                if (!Taken(store.KeyOf(draft)))
                    return;
            }
            throw new DataValidationException($"{schema.Name}: could not derive a unique key for pasted row.");
        }

        public RowId Duplicate(RowId source)
        {
            var pasted = Paste(Copy(new[] { source }));
            return pasted[0];
        }
    }
}
