using System.Globalization;

namespace ExcelDb.Core.Identity;

/// <summary>A stable, non-derived 128-bit workbook row identity.</summary>
public readonly record struct RowGuid(Guid Value)
#if !NETSTANDARD2_1
    : ISpanFormattable
#endif
{
    public static RowGuid Empty => default;

    public bool IsEmpty => Value == Guid.Empty;

    public static RowGuid New() => new(Guid.NewGuid());

    public static RowGuid Parse(string text)
    {
        if (!TryParse(text, out var value))
            throw new FormatException("A row guid must be 32 lowercase hexadecimal characters and must not be zero.");
        return value;
    }

    public static bool TryParse(string? text, out RowGuid value)
    {
        value = default;
        if (text is null || text.Length != 32 || text.Any(static character =>
                !IsLowerHexCharacter(character)))
        {
            return false;
        }

        if (!Guid.TryParseExact(text, "N", out var parsed) || parsed == Guid.Empty)
            return false;

        value = new RowGuid(parsed);
        return true;
    }

    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);

    public string ToString(string? format, IFormatProvider? formatProvider) =>
        Value.ToString(string.IsNullOrEmpty(format) ? "N" : format, formatProvider ?? CultureInfo.InvariantCulture);

    public bool TryFormat(
        Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format,
        IFormatProvider? provider) =>
        Value.TryFormat(destination, out charsWritten, format.IsEmpty ? "N" : format);

    private static bool IsLowerHexCharacter(char character) =>
        (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f');
}
