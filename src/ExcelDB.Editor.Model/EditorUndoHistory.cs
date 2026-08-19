using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ExcelDb.Editor.Model
{
    /// <summary>
    /// Stable application identity plus a live revision reader for one resident authoring POCO.
    /// The revision must change whenever an external source replaces or edits the resident value.
    /// </summary>
    public sealed class EditorEditTarget
    {
        private readonly Func<string> _readRevision;
        private EditorObjectBinding? _binding;

        public EditorEditTarget(string identity, object value, Func<string> readRevision)
        {
            if (string.IsNullOrWhiteSpace(identity))
                throw new ArgumentException("A stable target identity is required.", nameof(identity));
            Identity = identity;
            Value = value ?? throw new ArgumentNullException(nameof(value));
            _readRevision = readRevision ?? throw new ArgumentNullException(nameof(readRevision));
            ExpectedRevision = ReadRevision();
        }

        public string Identity { get; }

        public object Value { get; }

        public string Revision => ReadRevision();

        internal string ExpectedRevision { get; private set; }

        internal long MutationVersion { get; set; }

        internal bool IsRevisionCurrent => string.Equals(ExpectedRevision, ReadRevision(), StringComparison.Ordinal);

        internal bool RefreshExpectedRevision()
        {
            var current = ReadRevision();
            var changed = !string.Equals(ExpectedRevision, current, StringComparison.Ordinal);
            ExpectedRevision = current;
            return changed;
        }

        internal void AcceptCurrentRevision() => ExpectedRevision = ReadRevision();

        internal void AttachBinding(EditorObjectBinding binding)
        {
            if (binding == null)
                throw new ArgumentNullException(nameof(binding));
            if (_binding != null && !ReferenceEquals(_binding, binding))
                throw new InvalidOperationException(
                    "Edit target '" + Identity + "' is already attached to another editor object binding.");
            _binding = binding;
        }

        private string ReadRevision()
        {
            var revision = _readRevision();
            if (revision == null)
                throw new InvalidOperationException("Revision reader for '" + Identity + "' returned null.");
            return revision;
        }
    }

    public enum EditorHistoryStatus
    {
        Applied = 0,
        Empty = 1,
        RevisionConflict = 2,
        StateConflict = 3,
        InvalidToken = 4,
    }

    /// <summary>
    /// Serializable linear history coordinate. Generation changes whenever pruning, truncation or branching
    /// makes an older coordinate ambiguous.
    /// </summary>
    public readonly struct EditorHistoryToken : IEquatable<EditorHistoryToken>
    {
        public EditorHistoryToken(long generation, long position)
        {
            if (generation <= 0)
                throw new ArgumentOutOfRangeException(nameof(generation));
            if (position < 0)
                throw new ArgumentOutOfRangeException(nameof(position));
            Generation = generation;
            Position = position;
        }

        public long Generation { get; }

        public long Position { get; }

        public bool Equals(EditorHistoryToken other)
        {
            return Generation == other.Generation && Position == other.Position;
        }

        public override bool Equals(object? obj) => obj is EditorHistoryToken other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (Generation.GetHashCode() * 397) ^ Position.GetHashCode();
            }
        }

        public override string ToString() => Generation + ":" + Position;
    }

    public sealed class EditorHistoryResult
    {
        internal EditorHistoryResult(
            EditorHistoryStatus status,
            string? label,
            IEnumerable<string>? conflictingTargets = null,
            IEnumerable<EditorEditTarget>? appliedTargets = null)
        {
            Status = status;
            Label = label;
            ConflictingTargets = Array.AsReadOnly(
                (conflictingTargets ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToArray());
            AppliedTargets = Array.AsReadOnly(
                (appliedTargets ?? Array.Empty<EditorEditTarget>())
                .GroupBy(static item => item.Identity, StringComparer.Ordinal)
                .Select(static group => group.First())
                .OrderBy(static item => item.Identity, StringComparer.Ordinal)
                .ToArray());
        }

        public EditorHistoryStatus Status { get; }

        public string? Label { get; }

        public IReadOnlyList<string> ConflictingTargets { get; }

        public IReadOnlyList<EditorEditTarget> AppliedTargets { get; }

        public bool Succeeded => Status == EditorHistoryStatus.Applied;
    }

    public enum EditorHistoryChangeKind
    {
        Recorded = 0,
        Undo = 1,
        Redo = 2,
        Pruned = 3,
        Cleared = 4,
    }

    public sealed class EditorHistoryChangedEventArgs : EventArgs
    {
        internal EditorHistoryChangedEventArgs(
            EditorHistoryChangeKind kind,
            EditorHistoryToken token,
            IEnumerable<string> targetIdentities)
        {
            Kind = kind;
            Token = token;
            TargetIdentities = Array.AsReadOnly(
                targetIdentities.Distinct(StringComparer.Ordinal).OrderBy(static item => item, StringComparer.Ordinal).ToArray());
        }

        public EditorHistoryChangeKind Kind { get; }

        public EditorHistoryToken Token { get; }

        public IReadOnlyList<string> TargetIdentities { get; }
    }

    /// <summary>
    /// Bounded absolute-snapshot undo/redo. Entries can be pruned independently after external reload.
    /// </summary>
    public sealed class EditorUndoHistory
    {
        private readonly int _depth;
        private readonly List<EditorChangeSet> _undo = new List<EditorChangeSet>();
        private readonly List<EditorChangeSet> _redo = new List<EditorChangeSet>();
        private EditorChangeGroup? _activeGroup;
        private long _generation = 1;

        public EditorUndoHistory(int depth = 100)
        {
            if (depth <= 0)
                throw new ArgumentOutOfRangeException(nameof(depth));
            _depth = depth;
        }

        public event EventHandler<EditorHistoryChangedEventArgs>? Changed;

        public int UndoCount => _undo.Count;

        public int RedoCount => _redo.Count;

        public bool CanUndo => _undo.Count != 0;

        public bool CanRedo => _redo.Count != 0;

        public long Generation => _generation;

        public long Position => _undo.Count;

        public string? UndoLabel => _undo.Count == 0 ? null : _undo[_undo.Count - 1].Label;

        public string? RedoLabel => _redo.Count == 0 ? null : _redo[_redo.Count - 1].Label;

        public EditorHistoryToken CaptureToken() => new EditorHistoryToken(_generation, _undo.Count);

        public IDisposable BeginGroup(string label)
        {
            ValidateLabel(label);
            if (_activeGroup != null)
                throw new InvalidOperationException("Nested editor undo groups are not supported.");
            var group = new EditorChangeGroup(label);
            _activeGroup = group;
            return new GroupScope(this, group);
        }

        public EditorHistoryResult TryUndo()
        {
            EnsureNoActiveGroup();
            if (_undo.Count == 0)
                return new EditorHistoryResult(EditorHistoryStatus.Empty, null);

            var entry = _undo[_undo.Count - 1];
            var preflight = Preflight(entry, undo: true);
            if (preflight != null)
                return preflight;

            Apply(entry, undo: true);
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(entry);
            Notify(EditorHistoryChangeKind.Undo, entry.Changes.Select(static change => change.Target.Identity));
            return new EditorHistoryResult(
                EditorHistoryStatus.Applied,
                entry.Label,
                appliedTargets: entry.Changes.Select(static change => change.Target));
        }

        public EditorHistoryResult TryRedo()
        {
            EnsureNoActiveGroup();
            if (_redo.Count == 0)
                return new EditorHistoryResult(EditorHistoryStatus.Empty, null);

            var entry = _redo[_redo.Count - 1];
            var preflight = Preflight(entry, undo: false);
            if (preflight != null)
                return preflight;

            Apply(entry, undo: false);
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(entry);
            Notify(EditorHistoryChangeKind.Redo, entry.Changes.Select(static change => change.Target.Identity));
            return new EditorHistoryResult(
                EditorHistoryStatus.Applied,
                entry.Label,
                appliedTargets: entry.Changes.Select(static change => change.Target));
        }

        /// <summary>
        /// Moves along the current linear history to a previously captured coordinate. Invalidated branch,
        /// prune and bounded-truncation tokens are rejected before changing resident state.
        /// </summary>
        public EditorHistoryResult TryMoveTo(EditorHistoryToken token)
        {
            EnsureNoActiveGroup();
            var total = _undo.Count + _redo.Count;
            if (token.Generation != _generation || token.Position > total)
                return new EditorHistoryResult(EditorHistoryStatus.InvalidToken, null);
            if (token.Position == _undo.Count)
                return new EditorHistoryResult(EditorHistoryStatus.Applied, null);

            var route = (token.Position < _undo.Count
                ? _undo.Skip((int)token.Position)
                : _redo.Skip(_redo.Count - (int)(token.Position - _undo.Count))).ToArray();
            var revisionConflicts = route
                .SelectMany(static entry => entry.Changes)
                .Where(static change => !change.Target.IsRevisionCurrent)
                .Select(static change => change.Target.Identity)
                .ToArray();
            if (revisionConflicts.Length != 0)
                return new EditorHistoryResult(EditorHistoryStatus.RevisionConflict, null, revisionConflicts);

            var start = _undo.Count;
            EditorHistoryResult result = new EditorHistoryResult(EditorHistoryStatus.Applied, null);
            while (_undo.Count > token.Position)
            {
                result = TryUndo();
                if (!result.Succeeded)
                    break;
            }
            while (result.Succeeded && _undo.Count < token.Position)
            {
                result = TryRedo();
                if (!result.Succeeded)
                    break;
            }
            if (result.Succeeded)
                return new EditorHistoryResult(
                    EditorHistoryStatus.Applied,
                    result.Label,
                    appliedTargets: route.SelectMany(static entry => entry.Changes).Select(static change => change.Target));

            while (_undo.Count < start)
            {
                var rollback = TryRedo();
                if (!rollback.Succeeded)
                    throw new InvalidOperationException("Failed to restore history after a rejected move.");
            }
            while (_undo.Count > start)
            {
                var rollback = TryUndo();
                if (!rollback.Succeeded)
                    throw new InvalidOperationException("Failed to restore history after a rejected move.");
            }
            return result;
        }

        public EditorHistoryResult TryRestore(EditorHistoryToken token) => TryMoveTo(token);

        /// <summary>Drops all history touching an externally reloaded stable identity.</summary>
        public int Prune(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity))
                throw new ArgumentException("A stable target identity is required.", nameof(identity));
            EnsureNoActiveGroup();
            var removed = _undo.RemoveAll(item => item.Touches(identity));
            removed += _redo.RemoveAll(item => item.Touches(identity));
            if (removed != 0)
            {
                AdvanceGeneration();
                Notify(EditorHistoryChangeKind.Pruned, new[] { identity });
            }
            return removed;
        }

        public void Clear()
        {
            EnsureNoActiveGroup();
            if (_undo.Count == 0 && _redo.Count == 0)
                return;
            _undo.Clear();
            _redo.Clear();
            AdvanceGeneration();
            Notify(EditorHistoryChangeKind.Cleared, Array.Empty<string>());
        }

        internal void Record(string label, IEnumerable<EditorSnapshotChange> changes)
        {
            ValidateLabel(label);
            if (changes == null)
                throw new ArgumentNullException(nameof(changes));
            var array = changes.ToArray();
            if (array.Length == 0)
                return;

            if (_activeGroup != null)
            {
                _activeGroup.Add(array);
                return;
            }

            Push(EditorChangeSet.Create(label, array));
        }

        private void CompleteGroup(EditorChangeGroup group)
        {
            if (!ReferenceEquals(_activeGroup, group))
                return;
            _activeGroup = null;
            if (group.Changes.Count != 0)
                Push(EditorChangeSet.Create(group.Label, group.Changes));
        }

        private void Push(EditorChangeSet entry)
        {
            if (_redo.Count != 0)
                AdvanceGeneration();
            _undo.Add(entry);
            _redo.Clear();
            if (_undo.Count > _depth)
            {
                _undo.RemoveAt(0);
                AdvanceGeneration();
            }
            Notify(EditorHistoryChangeKind.Recorded, entry.Changes.Select(static change => change.Target.Identity));
        }

        private void AdvanceGeneration()
        {
            _generation = checked(_generation + 1);
        }

        private void Notify(EditorHistoryChangeKind kind, IEnumerable<string> targetIdentities)
        {
            var handler = Changed;
            if (handler == null)
                return;
            var args = new EditorHistoryChangedEventArgs(kind, CaptureToken(), targetIdentities);
            foreach (EventHandler<EditorHistoryChangedEventArgs> subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber(this, args);
                }
                catch
                {
                    // History state must not be rolled back or corrupted by an observer-only callback.
                }
            }
        }

        private static EditorHistoryResult? Preflight(EditorChangeSet entry, bool undo)
        {
            var revisionConflicts = entry.Changes
                .Where(static change => !change.Target.IsRevisionCurrent)
                .Select(static change => change.Target.Identity)
                .ToArray();
            if (revisionConflicts.Length != 0)
                return new EditorHistoryResult(EditorHistoryStatus.RevisionConflict, entry.Label, revisionConflicts);

            var stateConflicts = new List<string>();
            foreach (var change in entry.Changes)
            {
                var expectedVersion = undo ? change.AfterVersion : change.BeforeVersion;
                var expectedSnapshot = undo ? change.After : change.Before;
                if (change.Target.MutationVersion != expectedVersion
                    || !change.Binding.TargetEqualsSnapshot(change.Target.Value, expectedSnapshot))
                    stateConflicts.Add(change.Target.Identity);
            }
            return stateConflicts.Count == 0
                ? null
                : new EditorHistoryResult(EditorHistoryStatus.StateConflict, entry.Label, stateConflicts);
        }

        private static void Apply(EditorChangeSet entry, bool undo)
        {
            var applied = new List<EditorSnapshotChange>();
            try
            {
                var changes = undo ? entry.Changes.Reverse() : entry.Changes;
                foreach (var change in changes)
                {
                    change.Binding.ApplySnapshot(change.Target.Value, undo ? change.Before : change.After);
                    change.Target.MutationVersion = undo ? change.BeforeVersion : change.AfterVersion;
                    applied.Add(change);
                }
            }
            catch (Exception applyException)
            {
                var rollbackExceptions = new List<Exception>();
                for (var index = applied.Count - 1; index >= 0; index--)
                {
                    var change = applied[index];
                    try
                    {
                        change.Binding.ApplySnapshot(change.Target.Value, undo ? change.After : change.Before);
                        change.Target.MutationVersion = undo ? change.AfterVersion : change.BeforeVersion;
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackExceptions.Add(rollbackException);
                    }
                }
                if (rollbackExceptions.Count != 0)
                    throw new EditorAtomicApplyException(
                        undo ? "Editor undo" : "Editor redo",
                        applyException,
                        rollbackExceptions);
                throw;
            }
        }

        private static void ValidateLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("An undo label is required.", nameof(label));
        }

        private void EnsureNoActiveGroup()
        {
            if (_activeGroup != null)
                throw new InvalidOperationException("Close the active editor undo group before changing history.");
        }

        private sealed class GroupScope : IDisposable
        {
            private EditorUndoHistory? _owner;
            private readonly EditorChangeGroup _group;

            public GroupScope(EditorUndoHistory owner, EditorChangeGroup group)
            {
                _owner = owner;
                _group = group;
            }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null;
                if (owner != null)
                    owner.CompleteGroup(_group);
            }
        }
    }

    internal sealed class EditorSnapshotChange
    {
        public EditorSnapshotChange(
            EditorEditTarget target,
            EditorObjectBinding binding,
            object?[] before,
            object?[] after,
            long beforeVersion,
            long afterVersion)
        {
            Target = target;
            Binding = binding;
            Before = binding.CloneSnapshot(before);
            After = binding.CloneSnapshot(after);
            BeforeVersion = beforeVersion;
            AfterVersion = afterVersion;
        }

        public EditorEditTarget Target { get; }

        public EditorObjectBinding Binding { get; }

        public object?[] Before { get; }

        public object?[] After { get; }

        public long BeforeVersion { get; }

        public long AfterVersion { get; }
    }

    internal sealed class EditorChangeSet
    {
        private EditorChangeSet(string label, IReadOnlyList<EditorSnapshotChange> changes)
        {
            Label = label;
            Changes = changes;
        }

        public string Label { get; }

        public IReadOnlyList<EditorSnapshotChange> Changes { get; }

        public bool Touches(string identity)
        {
            return Changes.Any(change => string.Equals(change.Target.Identity, identity, StringComparison.Ordinal));
        }

        public static EditorChangeSet Create(string label, IEnumerable<EditorSnapshotChange> source)
        {
            var merged = new Dictionary<EditorEditTarget, EditorSnapshotChange>();
            var order = new List<EditorEditTarget>();
            foreach (var change in source)
            {
                EditorSnapshotChange? previous;
                if (merged.TryGetValue(change.Target, out previous))
                {
                    if (!ReferenceEquals(previous.Binding, change.Binding))
                        throw new InvalidOperationException("One target cannot use different editor bindings in an undo group.");
                    merged[change.Target] = new EditorSnapshotChange(
                        change.Target,
                        change.Binding,
                        previous.Before,
                        change.After,
                        previous.BeforeVersion,
                        change.AfterVersion);
                }
                else
                {
                    order.Add(change.Target);
                    merged.Add(change.Target, change);
                }
            }

            return new EditorChangeSet(
                label,
                new ReadOnlyCollection<EditorSnapshotChange>(order.Select(target => merged[target]).ToArray()));
        }
    }

    internal sealed class EditorChangeGroup
    {
        private readonly List<EditorSnapshotChange> _changes = new List<EditorSnapshotChange>();

        public EditorChangeGroup(string label)
        {
            Label = label;
        }

        public string Label { get; }

        public IReadOnlyList<EditorSnapshotChange> Changes => _changes;

        public void Add(IEnumerable<EditorSnapshotChange> changes) => _changes.AddRange(changes);
    }
}
