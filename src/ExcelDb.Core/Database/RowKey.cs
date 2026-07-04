using System;
using System.Text;
using Google.Protobuf;
using ExcelDb.Schema;

namespace ExcelDb.Database
{
    /// <summary>
    /// Business key of a row: ordered segment values. Boxed representation - used at
    /// lookup boundaries only; hot paths flow RowId. Typed per-table key structs are
    /// a later codegen-plugin concern.
    /// </summary>
    public readonly struct RowKey : IEquatable<RowKey>
    {
        readonly object?[] _parts;
        readonly int _hash;

        RowKey(object?[] normalizedParts)
        {
            _parts = normalizedParts;
            int hash = 17;
            foreach (var part in normalizedParts)
                hash = hash * 31 + (part?.GetHashCode() ?? 0);
            _hash = hash;
        }

        public static RowKey Of(params object?[] parts)
        {
            var normalized = new object?[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                normalized[i] = Normalize(parts[i]);
            return new RowKey(normalized);
        }

        public static RowKey Extract(IMessage row, TableSchema schema)
        {
            var parts = new object?[schema.KeyFields.Count];
            for (int i = 0; i < schema.KeyFields.Count; i++)
                parts[i] = Normalize(schema.KeyFields[i].Accessor.GetValue(row));
            return new RowKey(parts);
        }

        /// <summary>Integral widths and enums are normalized so callers can pass literals loosely.</summary>
        static object? Normalize(object? value) => value switch
        {
            null => null,
            Enum e => Convert.ToInt64(e),
            sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value),
            _ => value,
        };

        public int Length => _parts?.Length ?? 0;

        public bool Equals(RowKey other)
        {
            var a = _parts ?? Array.Empty<object?>();
            var b = other._parts ?? Array.Empty<object?>();
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (!Equals(a[i], b[i]))
                    return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is RowKey k && Equals(k);
        public override int GetHashCode() => _hash;

        public override string ToString()
        {
            if (_parts == null || _parts.Length == 0)
                return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < _parts.Length; i++)
            {
                if (i > 0)
                    sb.Append('|');
                sb.Append(_parts[i]);
            }
            return sb.ToString();
        }
    }
}
