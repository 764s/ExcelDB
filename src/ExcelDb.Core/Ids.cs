using System;

namespace ExcelDb
{
    /// <summary>Stable table number as declared in schema options. Human name lives in the registry.</summary>
    public readonly struct TableId : IEquatable<TableId>
    {
        public readonly int Number;

        public TableId(int number) => Number = number;

        public bool Equals(TableId other) => Number == other.Number;
        public override bool Equals(object? obj) => obj is TableId t && Equals(t);
        public override int GetHashCode() => Number;
        public override string ToString() => Number.ToString();

        public static bool operator ==(TableId a, TableId b) => a.Number == b.Number;
        public static bool operator !=(TableId a, TableId b) => a.Number != b.Number;
    }

    /// <summary>
    /// Identity of a row: (stable table number, per-table auto-increment id).
    /// The single currency flowing through cells, graph, clipboard and APIs.
    /// </summary>
    public readonly struct RowId : IEquatable<RowId>
    {
        public readonly TableId Table;
        public readonly int Id;

        public RowId(TableId table, int id)
        {
            Table = table;
            Id = id;
        }

        public RowId(int tableNumber, int id) : this(new TableId(tableNumber), id) { }

        public bool Equals(RowId other) => Table == other.Table && Id == other.Id;
        public override bool Equals(object? obj) => obj is RowId r && Equals(r);
        public override int GetHashCode() => (Table.Number * 397) ^ Id;
        public override string ToString() => $"{Table.Number}#{Id}";

        public static bool operator ==(RowId a, RowId b) => a.Equals(b);
        public static bool operator !=(RowId a, RowId b) => !a.Equals(b);
    }

    /// <summary>Value key of an undifferentiated external resource reference.</summary>
    public readonly struct ExternalKey : IEquatable<ExternalKey>
    {
        public readonly string Scheme;
        public readonly string Id;

        public ExternalKey(string scheme, string id)
        {
            Scheme = scheme ?? string.Empty;
            Id = id ?? string.Empty;
        }

        public ExternalKey(Protocol.ExternalRef reference) : this(reference.Scheme, reference.Id) { }

        public bool IsEmpty => Scheme.Length == 0 && Id.Length == 0;

        public Protocol.ExternalRef ToRef() => new Protocol.ExternalRef { Scheme = Scheme, Id = Id };

        public bool Equals(ExternalKey other) =>
            string.Equals(Scheme, other.Scheme, StringComparison.Ordinal) &&
            string.Equals(Id, other.Id, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ExternalKey k && Equals(k);
        public override int GetHashCode() => (Scheme.GetHashCode() * 397) ^ Id.GetHashCode();
        public override string ToString() => $"{Scheme}:{Id}";
    }
}
