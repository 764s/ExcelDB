using System.Security.Cryptography;
using System.Buffers.Binary;

namespace ExcelDb.Runtime;

internal static class RuntimeCompatibility
{
    public static void NotNull<T>(T? value, string parameterName)
        where T : class
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
    }

    public static void NotNullOrWhiteSpace(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value must not be null or whitespace.", parameterName);
    }

    public static byte[] Sha256(ReadOnlySpan<byte> content)
    {
#if NETSTANDARD2_1
        using var algorithm = SHA256.Create();
        return algorithm.ComputeHash(content.ToArray());
#else
        return SHA256.HashData(content);
#endif
    }

    public static string HexLower(ReadOnlySpan<byte> content)
    {
        const string digits = "0123456789abcdef";
        var result = new char[checked(content.Length * 2)];
        for (var index = 0; index < content.Length; index++)
        {
            var value = content[index];
            result[index * 2] = digits[value >> 4];
            result[(index * 2) + 1] = digits[value & 0x0f];
        }
        return new string(result);
    }

    public static Guid ReadBigEndianGuid(ReadOnlySpan<byte> content)
    {
        if (content.Length < 16)
            throw new ArgumentException("A guid requires 16 bytes.", nameof(content));

        return new Guid(
            BinaryPrimitives.ReadInt32BigEndian(content),
            BinaryPrimitives.ReadInt16BigEndian(content.Slice(4)),
            BinaryPrimitives.ReadInt16BigEndian(content.Slice(6)),
            content[8],
            content[9],
            content[10],
            content[11],
            content[12],
            content[13],
            content[14],
            content[15]);
    }

    public static void WriteBigEndianGuid(Guid value, Span<byte> destination)
    {
        if (destination.Length < 16)
            throw new ArgumentException("A guid requires 16 bytes.", nameof(destination));

        var components = value.ToByteArray();
        BinaryPrimitives.WriteInt32BigEndian(
            destination,
            BinaryPrimitives.ReadInt32LittleEndian(components));
        BinaryPrimitives.WriteInt16BigEndian(
            destination.Slice(4),
            BinaryPrimitives.ReadInt16LittleEndian(components.AsSpan(4)));
        BinaryPrimitives.WriteInt16BigEndian(
            destination.Slice(6),
            BinaryPrimitives.ReadInt16LittleEndian(components.AsSpan(6)));
        components.AsSpan(8, 8).CopyTo(destination.Slice(8));
    }
}
