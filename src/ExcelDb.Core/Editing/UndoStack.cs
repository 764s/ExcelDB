using System.Collections.Generic;
using Google.Protobuf;

namespace ExcelDb.Editing
{
    /// <summary>One row's absolute before/after snapshots. Null before = created, null after = deleted.</summary>
    public readonly struct RowChange
    {
        public readonly RowId Row;
        public readonly IMessage? Before;
        public readonly IMessage? After;

        public RowChange(RowId row, IMessage? before, IMessage? after)
        {
            Row = row;
            Before = before;
            After = after;
        }
    }

    public sealed class ChangeSet
    {
        public string Label { get; }
        public IReadOnlyList<RowChange> Changes { get; }

        public ChangeSet(string label, IReadOnlyList<RowChange> changes)
        {
            Label = label;
            Changes = changes;
        }

        public bool Touches(TableId table)
        {
            foreach (var change in Changes)
                if (change.Row.Table == table)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Global bounded undo/redo stack. Entries hold absolute snapshots, so pruning
    /// arbitrary entries (external reload of a table) keeps the remainder composable.
    /// </summary>
    sealed class UndoStack
    {
        readonly List<ChangeSet> _undo = new List<ChangeSet>();
        readonly List<ChangeSet> _redo = new List<ChangeSet>();
        readonly int _depth;

        public UndoStack(int depth) => _depth = depth;

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public int UndoCount => _undo.Count;
        public int RedoCount => _redo.Count;

        public void Push(ChangeSet entry)
        {
            _undo.Add(entry);
            _redo.Clear();
            if (_undo.Count > _depth)
                _undo.RemoveAt(0);
        }

        public ChangeSet? PopUndo()
        {
            if (_undo.Count == 0)
                return null;
            var entry = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(entry);
            return entry;
        }

        public ChangeSet? PopRedo()
        {
            if (_redo.Count == 0)
                return null;
            var entry = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(entry);
            return entry;
        }

        /// <summary>Drops every entry touching the given table from both stacks (external reload rule).</summary>
        public int Prune(TableId table)
        {
            int removed = _undo.RemoveAll(e => e.Touches(table));
            removed += _redo.RemoveAll(e => e.Touches(table));
            return removed;
        }
    }
}
