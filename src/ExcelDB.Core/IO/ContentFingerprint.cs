using System.Security.Cryptography;
using ExcelDb.Core.Compatibility;

namespace ExcelDb.Core.IO;

public readonly record struct ContentFingerprint(long Length, string Sha256)
{
    public static ContentFingerprint FromBytes(ReadOnlySpan<byte> content) =>
        new(content.Length, ComputeSha256(content));

    public static ContentFingerprint FromFile(string path)
    {
        Guard.NotNullOrWhiteSpace(path, nameof(path));
        using var stream = File.OpenRead(path);
        return new ContentFingerprint(stream.Length, ComputeSha256(stream));
    }

    public override string ToString() => $"{Length}:{Sha256}";

    private static string ComputeSha256(ReadOnlySpan<byte> content)
    {
#if NETSTANDARD2_1
        using var algorithm = SHA256.Create();
        return ToLowerHex(algorithm.ComputeHash(content.ToArray()));
#else
        return Convert.ToHexStringLower(SHA256.HashData(content));
#endif
    }

    private static string ComputeSha256(Stream content)
    {
#if NETSTANDARD2_1
        using var algorithm = SHA256.Create();
        return ToLowerHex(algorithm.ComputeHash(content));
#else
        return Convert.ToHexStringLower(SHA256.HashData(content));
#endif
    }

#if NETSTANDARD2_1
    private static string ToLowerHex(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "0123456789abcdef";
        var characters = new char[checked(bytes.Length * 2)];
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            characters[index * 2] = alphabet[value >> 4];
            characters[(index * 2) + 1] = alphabet[value & 0x0f];
        }

        return new string(characters);
    }
#endif
}
