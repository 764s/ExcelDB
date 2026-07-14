using System.Buffers.Binary;

namespace ExcelDb.Runtime.Bytes;

internal static class ConvertedBytesLayout
{
    public static ReadOnlySpan<byte> Magic => "EXDBRT01"u8;

    public const int LegacyFormatVersion = 1;
    public const int FormatVersion = 2;
    public const int HeaderSize = 108;
    public const int IntegrityLength = 32;

    public const int TableEntrySize = 12;
    public const int StringIndexEntrySize = 8;
    public const int AssetEntrySize = 44;
    public const int LegacyFieldEntrySize = 12;
    public const int FieldEntrySize = 16;
    public const int KeyIndexEntrySize = 12;
    public const int GuidIndexEntrySize = 24;
    public const int ReferenceEntrySize = 8;

    public static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    public static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    public static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    public static int ReadInt32(ReadOnlySpan<byte> content, int offset)
    {
        EnsureRange(content, offset, sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(content.Slice(offset, sizeof(int)));
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> content, int offset)
    {
        EnsureRange(content, offset, sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(offset, sizeof(uint)));
    }

    public static ulong ReadUInt64(ReadOnlySpan<byte> content, int offset)
    {
        EnsureRange(content, offset, sizeof(ulong));
        return BinaryPrimitives.ReadUInt64LittleEndian(content.Slice(offset, sizeof(ulong)));
    }

    public static void EnsureRange(ReadOnlySpan<byte> content, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > content.Length - length)
            throw new InvalidDataException("Converted-bytes section lies outside the file.");
    }
}

internal readonly record struct Section(int Offset, int CountOrLength);
