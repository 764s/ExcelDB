using System;

namespace ExcelDb
{
    public class ExcelDbException : Exception
    {
        public ExcelDbException(string message) : base(message) { }
        public ExcelDbException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Schema is malformed: missing table number, bad key declaration, unresolvable constraint...</summary>
    public sealed class SchemaException : ExcelDbException
    {
        public SchemaException(string message) : base(message) { }
    }

    /// <summary>
    /// Bad data: key conflicts, duplicate ids, invalid clipboard content.
    /// Always thrown regardless of the unsupported-operation policy.
    /// </summary>
    public sealed class DataValidationException : ExcelDbException
    {
        public DataValidationException(string message) : base(message) { }
    }

    public sealed class RowNotFoundException : ExcelDbException
    {
        public RowId Row { get; }

        public RowNotFoundException(RowId row, string describe)
            : base($"Row {describe} does not exist.") => Row = row;
    }

    /// <summary>Capability gap surfaced under <see cref="UnsupportedPolicy.Throw"/>.</summary>
    public sealed class UnsupportedOperationException : ExcelDbException
    {
        public UnsupportedOperationException(string message) : base(message) { }
    }
}
