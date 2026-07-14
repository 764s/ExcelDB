using System.Collections;
using ExcelDb.Core.Identity;

namespace ExcelDb.Runtime;

public enum ChangeKind : byte
{
    Removed = 0,
    Added = 1,
    Moved = 2,
    Renamed = 3,
    Recreated = 4,
    PropertyChanged = 5,
    DependencyChanged = 6,
}

public readonly record struct ChangeEvent(
    ChangeKind Kind,
    AssetKey Key,
    AssetIdentity AssetIdentity,
    Type RuntimeType);

public readonly struct ChangeEventList : IReadOnlyList<ChangeEvent>
{
    private readonly ChangeEvent[]? _items;
    private readonly int _count;

    internal ChangeEventList(ChangeEvent[] items, int count)
    {
        _items = items;
        _count = count;
    }

    internal ChangeEventList(System.Collections.Immutable.ImmutableArray<ChangeEvent> items)
    {
        _items = items.IsDefaultOrEmpty ? Array.Empty<ChangeEvent>() : items.ToArray();
        _count = items.IsDefault ? 0 : items.Length;
    }

    public int Count => _items is null ? 0 : _count;

    public ChangeEvent this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _items![index];
        }
    }

    public Enumerator GetEnumerator() => new(_items, Count);

    IEnumerator<ChangeEvent> IEnumerable<ChangeEvent>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public System.Collections.Immutable.ImmutableArray<ChangeEvent> ToImmutableArray()
    {
        if (_items is null || _count == 0)
            return [];
        return System.Collections.Immutable.ImmutableArray.Create(_items, 0, _count);
    }

    public struct Enumerator : IEnumerator<ChangeEvent>
    {
        private readonly ChangeEvent[]? _items;
        private readonly int _count;
        private int _index;

        internal Enumerator(ChangeEvent[]? items, int count)
        {
            _items = items;
            _count = count;
            _index = -1;
        }

        public readonly ChangeEvent Current => _items![_index];

        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            var next = _index + 1;
            if (next >= _count)
                return false;
            _index = next;
            return true;
        }

        public void Reset() => _index = -1;

        public readonly void Dispose()
        {
        }
    }
}

public readonly record struct ChangeSet(uint Version, ChangeEventList Events);
