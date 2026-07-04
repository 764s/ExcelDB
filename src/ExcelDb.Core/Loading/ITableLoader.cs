using System;
using System.Collections.Generic;
using Google.Protobuf;

namespace ExcelDb.Loading
{
    public readonly struct TableRecord
    {
        public readonly int Id;
        public readonly IMessage Row;

        public TableRecord(int id, IMessage row)
        {
            Id = id;
            Row = row;
        }
    }

    public sealed class TableContent
    {
        public List<TableRecord> Rows { get; }

        public TableContent(List<TableRecord> rows) => Rows = rows;
        public TableContent() : this(new List<TableRecord>()) { }
    }

    [Flags]
    public enum LoaderCapabilities
    {
        None = 0,
        Write = 1,
        Watch = 2,
    }

    /// <summary>
    /// Source of table data. Physical format (xlsx, baked bytes, in-memory) is an
    /// implementation detail invisible above this interface. v1 is single-threaded: sync API.
    /// </summary>
    public interface ITableLoader
    {
        IReadOnlyCollection<TableId> Tables { get; }
        LoaderCapabilities Capabilities { get; }

        TableContent Load(TableId table);
        void Write(TableId table, TableContent content);

        /// <summary>Hot reload signal; only raised by loaders with the Watch capability.</summary>
        event Action<TableId>? TableChanged;
    }
}
