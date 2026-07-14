using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using ExcelDb.Core.Identity;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Importing;

namespace ExcelDb.Workbooks.References;

/// <summary>
/// Canonical human-readable workbook projection of a RowRef. The stable identity is still
/// AssetIdentity; this token is resolved through the current table-scoped key index.
/// </summary>
public readonly record struct RowReferenceToken(int TableId, ImmutableArray<string> KeyComponents)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public override string ToString() => Format(TableId, KeyComponents);

    public static string Format(int tableId, IEnumerable<string> keyComponents)
    {
        if (tableId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableId), "A RowRef table id must be positive.");
        ArgumentNullException.ThrowIfNull(keyComponents);
        var components = keyComponents.ToImmutableArray();
        if (components.IsDefaultOrEmpty)
            throw new ArgumentException("A RowRef token must contain at least one key component.", nameof(keyComponents));
        if (components.Any(static component => string.IsNullOrEmpty(component)))
            throw new ArgumentException("A RowRef key component must not be null or empty.", nameof(keyComponents));

        return $"{tableId.ToString(CultureInfo.InvariantCulture)}:{string.Join('|', components.Select(EncodeComponent))}";
    }

    public static bool TryParse(string? text, out RowReferenceToken token) =>
        TryParse(text, out token, out _);

    public static bool TryParse(string? text, out RowReferenceToken token, out string? error)
    {
        token = default;
        if (string.IsNullOrEmpty(text))
        {
            error = "A RowRef token is empty.";
            return false;
        }

        var colon = text.IndexOf(':');
        if (colon <= 0
            || !int.TryParse(text.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var tableId)
            || tableId <= 0
            || !text.AsSpan(0, colon).SequenceEqual(tableId.ToString(CultureInfo.InvariantCulture).AsSpan()))
        {
            error = $"RowRef '{text}' must begin with a positive decimal table id followed by ':'.";
            return false;
        }

        var rawComponents = text[(colon + 1)..].Split('|');
        if (rawComponents.Length == 0)
        {
            error = $"RowRef '{text}' has no key components.";
            return false;
        }

        var components = ImmutableArray.CreateBuilder<string>(rawComponents.Length);
        foreach (var raw in rawComponents)
        {
            if (!TryDecodeComponent(raw, out var component, out error))
            {
                error = $"RowRef '{text}' contains an invalid key component: {error}";
                return false;
            }

            components.Add(component);
        }

        token = new RowReferenceToken(tableId, components.MoveToImmutable());
        error = null;
        return true;
    }

    private static string EncodeComponent(string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length);
        foreach (var valueByte in bytes)
        {
            if (IsUnreserved(valueByte))
            {
                builder.Append((char)valueByte);
            }
            else
            {
                builder.Append('%');
                builder.Append(valueByte.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static bool TryDecodeComponent(string encoded, out string value, out string? error)
    {
        if (encoded.Length == 0)
        {
            value = string.Empty;
            error = "key components must not be empty.";
            return false;
        }

        var bytes = new List<byte>(encoded.Length);
        for (var index = 0; index < encoded.Length; index++)
        {
            var character = encoded[index];
            if (character == '%')
            {
                if (index + 2 >= encoded.Length
                    || !TryHex(encoded[index + 1], out var high)
                    || !TryHex(encoded[index + 2], out var low))
                {
                    value = string.Empty;
                    error = "'%' must be followed by exactly two hexadecimal digits.";
                    return false;
                }

                bytes.Add((byte)((high << 4) | low));
                index += 2;
                continue;
            }

            if (character > 0x7f || !IsUnreserved((byte)character))
            {
                value = string.Empty;
                error = $"character '{character}' must be UTF-8 percent-encoded.";
                return false;
            }

            bytes.Add((byte)character);
        }

        try
        {
            value = StrictUtf8.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            value = string.Empty;
            error = "percent-encoded bytes are not valid UTF-8.";
            return false;
        }

        if (value.Any(static character => character == '\0' || char.IsControl(character)))
        {
            value = string.Empty;
            error = "decoded key components must not contain NUL or control characters.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsUnreserved(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z'
        or >= (byte)'a' and <= (byte)'z'
        or >= (byte)'0' and <= (byte)'9'
        or (byte)'-'
        or (byte)'_'
        or (byte)'.'
        or (byte)'~';

    private static bool TryHex(char value, out int result)
    {
        if (value is >= '0' and <= '9')
        {
            result = value - '0';
            return true;
        }

        if (value is >= 'A' and <= 'F')
        {
            result = value - 'A' + 10;
            return true;
        }

        if (value is >= 'a' and <= 'f')
        {
            result = value - 'a' + 10;
            return true;
        }

        result = 0;
        return false;
    }
}

/// <summary>Internal canonical composite-key representation used by workbook indexes.</summary>
public static class CanonicalKeyCodec
{
    public static string Format(IEnumerable<string> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return string.Join('|', components.Select(static component =>
        {
            ArgumentNullException.ThrowIfNull(component);
            return $"{component.Length.ToString(CultureInfo.InvariantCulture)}:{component}";
        }));
    }

    public static bool TryParse(string? text, out ImmutableArray<string> components)
    {
        components = [];
        if (string.IsNullOrEmpty(text))
            return false;

        var parsed = ImmutableArray.CreateBuilder<string>();
        var offset = 0;
        while (offset < text.Length)
        {
            var colon = text.IndexOf(':', offset);
            if (colon == offset
                || colon < 0
                || !int.TryParse(
                    text.AsSpan(offset, colon - offset),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var length)
                || length < 0
                || !text.AsSpan(offset, colon - offset)
                    .SequenceEqual(length.ToString(CultureInfo.InvariantCulture).AsSpan()))
            {
                return false;
            }

            var valueOffset = colon + 1;
            if (length > text.Length - valueOffset)
                return false;
            parsed.Add(text.Substring(valueOffset, length));
            offset = valueOffset + length;
            if (offset == text.Length)
                break;
            if (text[offset] != '|')
                return false;
            offset++;
            if (offset == text.Length)
                return false;
        }

        components = parsed.ToImmutable();
        return !components.IsDefaultOrEmpty;
    }
}

public readonly record struct RowReferenceResolution(
    bool Succeeded,
    AssetIdentity Identity,
    string? CanonicalToken,
    string? Error);

/// <summary>Resolves a workbook RowRef token against the current live, indexable row set.</summary>
public static class RowReferenceResolver
{
    public static RowReferenceResolution Resolve(
        CanonicalFieldDescriptor field,
        string tokenText,
        CanonicalSchemaDescriptor schema,
        IEnumerable<ImportedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(rows);
        if (!RowReferenceToken.TryParse(tokenText, out var token, out var parseError))
            return Failure(parseError!);

        var eligibleTables = schema.Tables
            .Where(static table => table.Kind == CanonicalTableKind.Asset)
            .Where(table => field.ReferenceTable is not null
                ? string.Equals(table.Name, field.ReferenceTable, StringComparison.Ordinal)
                    || string.Equals(table.FullName, field.ReferenceTable, StringComparison.Ordinal)
                : field.ReferenceGroup is not null
                    && table.Implements.Contains(field.ReferenceGroup, StringComparer.Ordinal))
            .ToArray();
        if (eligibleTables.Length == 0)
            return Failure($"Reference field '{field.PropertyPath}' has no live target tables.");
        if (!eligibleTables.Any(table => table.Id == token.TableId))
        {
            return Failure(
                $"RowRef table {token.TableId} is outside the declared target of field '{field.PropertyPath}'.");
        }

        var canonicalKey = CanonicalKeyCodec.Format(token.KeyComponents);
        var matches = rows
            .Where(row => row.IsIndexable
                && row.Identity is not null
                && row.TableId == token.TableId
                && string.Equals(row.Key, canonicalKey, StringComparison.Ordinal))
            .Select(static row => row.Identity!.Value)
            .Distinct()
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            1 => new RowReferenceResolution(true, matches[0], token.ToString(), null),
            0 => Failure($"RowRef '{token}' does not resolve through the current table-scoped key index."),
            _ => Failure($"RowRef '{token}' is ambiguous in the current table-scoped key index."),
        };
    }

    private static RowReferenceResolution Failure(string error) => new(false, default, null, error);
}
