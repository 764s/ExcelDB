using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace ExcelDb.Workbooks.Compatibility;

internal static class Guard
{
    public static void NotNull(
        [NotNull] object? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
#if NETSTANDARD2_1
        if (value is null)
            throw new ArgumentNullException(parameterName);
#else
        ArgumentNullException.ThrowIfNull(value, parameterName);
#endif
    }

    public static void NotNullOrWhiteSpace(
        [NotNull] string? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
#if NETSTANDARD2_1
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value cannot be null, empty, or whitespace.", parameterName);
#else
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
#endif
    }

    public static void NotNullOrEmpty(
        [NotNull] string? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
#if NETSTANDARD2_1
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("The value cannot be null or empty.", parameterName);
#else
        ArgumentException.ThrowIfNullOrEmpty(value, parameterName);
#endif
    }

    public static void NotDisposed(bool condition, object instance)
    {
#if NETSTANDARD2_1
        if (condition)
            throw new ObjectDisposedException(instance.GetType().FullName);
#else
        ObjectDisposedException.ThrowIf(condition, instance);
#endif
    }
}

internal static class HashUtility
{
    public static string Sha256Upper(byte[] bytes)
    {
#if NETSTANDARD2_1
        using var algorithm = SHA256.Create();
        return ToUpperHex(algorithm.ComputeHash(bytes));
#else
        return Convert.ToHexString(SHA256.HashData(bytes));
#endif
    }

    public static string ToUpperHex(ReadOnlySpan<byte> bytes)
    {
#if NETSTANDARD2_1
        const string alphabet = "0123456789ABCDEF";
        var characters = new char[checked(bytes.Length * 2)];
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            characters[index * 2] = alphabet[value >> 4];
            characters[(index * 2) + 1] = alphabet[value & 0x0f];
        }

        return new string(characters);
#else
        return Convert.ToHexString(bytes);
#endif
    }
}

internal static class PlatformCompatibility
{
    public static void MoveOverwrite(string source, string destination)
    {
#if NETSTANDARD2_1
        if (File.Exists(destination))
            File.Replace(source, destination, destinationBackupFileName: null);
        else
            File.Move(source, destination);
#else
        File.Move(source, destination, overwrite: true);
#endif
    }

    public static bool IsAsciiLetter(char value) =>
        (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
}
