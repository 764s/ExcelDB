using System.Collections.Generic;
using Google.Protobuf;

namespace ExcelDb.Graph
{
    public sealed class DependencyInfo
    {
        /// <summary>Reachable dependency rows, excluding the queried row itself.</summary>
        public IReadOnlyList<RowId> Rows { get; }
        /// <summary>External references of the queried row plus (when recursive) all reachable rows, deduplicated.</summary>
        public IReadOnlyList<ExternalKey> Externals { get; }

        public DependencyInfo(IReadOnlyList<RowId> rows, IReadOnlyList<ExternalKey> externals)
        {
            Rows = rows;
            Externals = externals;
        }
    }

    /// <summary>
    /// Forward and reverse row-reference edges plus per-row external references.
    /// Rows are memory-resident, so cycles only concern traversal (visited set), never lifetime.
    /// </summary>
    sealed class DependencyGraph
    {
        readonly Dictionary<RowId, List<RowId>> _forward = new Dictionary<RowId, List<RowId>>();
        readonly Dictionary<RowId, List<ExternalKey>> _externals = new Dictionary<RowId, List<ExternalKey>>();
        readonly Dictionary<RowId, HashSet<RowId>> _reverse = new Dictionary<RowId, HashSet<RowId>>();

        public void RebuildRow(RowId id, IMessage row)
        {
            RemoveRow(id);

            var forward = new List<RowId>();
            var externals = new List<ExternalKey>();
            MessageOps.WalkRowRefs(row, (_, reference) => forward.Add(new RowId(reference.Table, reference.Id)));
            MessageOps.WalkExternalRefs(row, (_, reference) => externals.Add(new ExternalKey(reference)));

            if (forward.Count > 0)
            {
                _forward[id] = forward;
                foreach (var target in forward)
                {
                    if (!_reverse.TryGetValue(target, out var inbound))
                        _reverse[target] = inbound = new HashSet<RowId>();
                    inbound.Add(id);
                }
            }
            if (externals.Count > 0)
                _externals[id] = externals;
        }

        public void RemoveRow(RowId id)
        {
            if (_forward.TryGetValue(id, out var forward))
            {
                foreach (var target in forward)
                    if (_reverse.TryGetValue(target, out var inbound))
                    {
                        inbound.Remove(id);
                        if (inbound.Count == 0)
                            _reverse.Remove(target);
                    }
                _forward.Remove(id);
            }
            _externals.Remove(id);
        }

        public DependencyInfo Collect(RowId root, bool recursive)
        {
            var rows = new List<RowId>();
            var externals = new List<ExternalKey>();
            var seenRows = new HashSet<RowId> { root };
            var seenExternals = new HashSet<ExternalKey>();

            AppendExternals(root, externals, seenExternals);

            if (_forward.TryGetValue(root, out var direct))
            {
                var stack = new Stack<RowId>();
                foreach (var target in direct)
                    if (seenRows.Add(target))
                        stack.Push(target);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    rows.Add(current);
                    AppendExternals(current, externals, seenExternals);

                    if (recursive && _forward.TryGetValue(current, out var next))
                        foreach (var target in next)
                            if (seenRows.Add(target))
                                stack.Push(target);
                }
            }

            return new DependencyInfo(rows, externals);
        }

        void AppendExternals(RowId id, List<ExternalKey> sink, HashSet<ExternalKey> seen)
        {
            if (_externals.TryGetValue(id, out var externals))
                foreach (var key in externals)
                    if (seen.Add(key))
                        sink.Add(key);
        }

        public IReadOnlyList<RowId> InboundReferences(RowId id)
        {
            if (!_reverse.TryGetValue(id, out var inbound) || inbound.Count == 0)
                return System.Array.Empty<RowId>();
            var result = new List<RowId>(inbound.Count);
            foreach (var source in inbound)
                result.Add(source);
            result.Sort((a, b) => a.Table.Number != b.Table.Number
                ? a.Table.Number.CompareTo(b.Table.Number)
                : a.Id.CompareTo(b.Id));
            return result;
        }
    }
}
