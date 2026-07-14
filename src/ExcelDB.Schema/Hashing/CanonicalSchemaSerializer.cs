using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using ExcelDb.Schema.Descriptors;

namespace ExcelDb.Schema.Hashing;

public static class CanonicalSchemaSerializer
{
    private static readonly byte[] Magic = "ExcelDB.Schema.v1\0"u8.ToArray();

    public static byte[] Serialize(CanonicalSchemaDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        using var stream = new MemoryStream();
        stream.Write(Magic);

        WriteArray(stream, descriptor.Tables, WriteTable);
        WriteArray(stream, descriptor.RetiredTables, WriteRetiredTable);
        WriteArray(stream, descriptor.Enums, WriteEnum);
        return stream.ToArray();
    }

    public static ulong ComputeHash(CanonicalSchemaDescriptor descriptor) =>
        XxHash64.Hash(Serialize(descriptor));

    private static void WriteTable(Stream stream, CanonicalTableDescriptor table)
    {
        WriteInt32(stream, table.Id);
        WriteString(stream, table.Name);
        WriteString(stream, table.FullName);
        WriteInt32(stream, (int)table.Kind);
        WriteString(stream, table.SheetName);
        WriteStringArray(stream, table.Implements);
        WriteStringArray(stream, table.ValidatorIds);
        WriteStringArray(stream, table.ExportTargets);
        WriteRanges(stream, table.ReservedFieldNumbers);
        WriteArray(stream, table.Fields, WriteField);
        WriteArray(stream, table.KeyFieldIds, static (target, value) => WriteInt32(target, value));
    }

    private static void WriteRetiredTable(Stream stream, CanonicalRetiredTableDescriptor table)
    {
        WriteInt32(stream, table.Id);
        WriteString(stream, table.Name);
        WriteString(stream, table.FullName);
    }

    private static void WriteField(Stream stream, CanonicalFieldDescriptor field)
    {
        WriteInt32(stream, field.Id);
        WriteString(stream, field.Name);
        WriteString(stream, field.PropertyPath);
        WriteInt32(stream, (int)field.Shape);
        WriteString(stream, field.TypeName);
        WriteBoolean(stream, field.HasPresence);
        WriteNullableString(stream, field.OneOfGroup);
        WriteMap(stream, field.Map);
        WriteBoolean(stream, field.Required);
        WriteNullableString(stream, field.DefaultValue);
        WriteNullableDouble(stream, field.Minimum);
        WriteNullableDouble(stream, field.Maximum);
        WriteNullableString(stream, field.Regex);
        WriteBoolean(stream, field.Unique);
        WriteInt32(stream, field.KeyOrder);
        WriteNullableString(stream, field.ReferenceTable);
        WriteNullableString(stream, field.ReferenceGroup);
        WriteInt32(stream, (int)field.DeletePolicy);
        WriteInt32(stream, (int)field.ExpandMode);
        WriteBoolean(stream, field.Labels);
        WriteNullableString(stream, field.ChildTableSheetName);
        WriteCellFormat(stream, field.CellFormat);
        WriteExpression(stream, field.Expression);
        WriteWeighted(stream, field.Weighted);
        WriteStringArray(stream, field.ExportTargets);
        WriteRanges(stream, field.ReservedChildFieldNumbers);
        WriteArray(stream, field.Children, WriteField);
    }

    private static void WriteMap(Stream stream, CanonicalMapDescriptor? map)
    {
        WriteBoolean(stream, map is not null);
        if (map is null)
            return;

        WriteString(stream, map.KeyTypeName);
        WriteString(stream, map.ValueTypeName);
        WriteInt32(stream, (int)map.ValueShape);
        WriteNullableString(stream, map.KeyEnumType);
    }

    private static void WriteCellFormat(Stream stream, CanonicalCellFormat? format)
    {
        WriteBoolean(stream, format is not null);
        if (format is null)
            return;

        WriteString(stream, format.Kind);
        WriteStringArray(stream, format.Parameters);
        WriteArray(stream, format.Legacy, static (target, legacy) => WriteCellFormat(target, legacy));
    }

    private static void WriteExpression(Stream stream, CanonicalExpression? expression)
    {
        WriteBoolean(stream, expression is not null);
        if (expression is null)
            return;

        WriteString(stream, expression.SymbolsType);
        WriteString(stream, expression.ResultType);
    }

    private static void WriteWeighted(Stream stream, CanonicalWeighted? weighted)
    {
        WriteBoolean(stream, weighted is not null);
        if (weighted is null)
            return;

        WriteInt32(stream, weighted.WeightFieldId);
        WriteInt32(stream, weighted.ConditionFieldId);
    }

    private static void WriteEnum(Stream stream, CanonicalEnumDescriptor value)
    {
        WriteString(stream, value.Name);
        WriteString(stream, value.FullName);
        WriteRanges(stream, value.ReservedNumbers);
        WriteArray(stream, value.Values, static (target, item) =>
        {
            WriteInt32(target, item.Number);
            WriteString(target, item.Name);
        });
    }

    private static void WriteStringArray(Stream stream, ImmutableArray<string> values) =>
        WriteArray(stream, values, static (target, value) => WriteString(target, value));

    private static void WriteRanges(
        Stream stream,
        ImmutableArray<CanonicalReservedNumberRange> values) =>
        WriteArray(stream, values, static (target, value) =>
        {
            WriteInt64(target, value.Start);
            WriteInt64(target, value.EndExclusive);
        });

    private static void WriteArray<T>(Stream stream, ImmutableArray<T> values, Action<Stream, T> write)
    {
        WriteInt32(stream, values.Length);
        foreach (var value in values)
            write(stream, value);
    }

    private static void WriteNullableString(Stream stream, string? value)
    {
        WriteBoolean(stream, value is not null);
        if (value is not null)
            WriteString(stream, value);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteNullableDouble(Stream stream, double? value)
    {
        WriteBoolean(stream, value.HasValue);
        if (!value.HasValue)
            return;

        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value.Value));
        stream.Write(bytes);
    }

    private static void WriteBoolean(Stream stream, bool value) => stream.WriteByte(value ? (byte)1 : (byte)0);

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
