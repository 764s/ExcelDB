using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Model;

namespace ExcelDb.Workbooks.Formatting;

internal static class CanonicalJson
{
    public static bool TryNormalize(string json, out string canonical, out string? error)
    {
        try
        {
            var node = JsonNode.Parse(json);
            canonical = Serialize(node);
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            canonical = string.Empty;
            error = $"Invalid JSON value: {exception.Message}";
            return false;
        }
    }

    public static string Serialize(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false,
                   SkipValidation = false,
               }))
        {
            WriteNode(writer, node);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonObject value:
                writer.WriteStartObject();
                foreach (var property in value.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteNode(writer, property.Value);
                }

                writer.WriteEndObject();
                return;
            case JsonArray value:
                writer.WriteStartArray();
                foreach (var item in value)
                    WriteNode(writer, item);
                writer.WriteEndArray();
                return;
            default:
                node.WriteTo(writer);
                return;
        }
    }
}

internal static class CellScalarValueCodec
{
    public static bool TryParse(
        string physicalText,
        string typeName,
        CanonicalFieldShape shape,
        CanonicalSchemaDescriptor schema,
        out JsonNode? value,
        out string? error)
    {
        value = null;
        if (!DelimitedCellText.TryDecodeAtom(physicalText, out var decoded, out error))
            return false;

        if (string.Equals(decoded, WorkbookProtocol.ExplicitNullToken, StringComparison.Ordinal))
            return true;

        var scalarType = typeName.Split('.').Last().ToLowerInvariant();
        if (scalarType == "string" && string.Equals(decoded, "%7E", StringComparison.OrdinalIgnoreCase))
            decoded = WorkbookProtocol.ExplicitNullToken;

        if (shape is CanonicalFieldShape.Enum or CanonicalFieldShape.RepeatedEnum)
        {
            var enumType = schema.Enums.FirstOrDefault(item =>
                string.Equals(item.FullName, typeName, StringComparison.Ordinal)
                || string.Equals(item.Name, typeName, StringComparison.Ordinal));
            if (enumType is null)
            {
                error = $"Enum '{typeName}' is absent from the schema.";
                return false;
            }

            if (enumType.Values.Any(item => string.Equals(item.Name, decoded, StringComparison.Ordinal)))
            {
                value = JsonValue.Create(decoded);
                return true;
            }

            if (int.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enumNumber)
                && enumType.Values.Any(item => item.Number == enumNumber))
            {
                value = JsonValue.Create(enumNumber);
                return true;
            }

            error = $"'{decoded}' is not a member of enum '{typeName}'.";
            return false;
        }

        switch (scalarType)
        {
            case "string":
            case "bytes":
                value = JsonValue.Create(decoded);
                return true;
            case "bool":
            case "boolean":
                if (bool.TryParse(decoded, out var boolean))
                {
                    value = JsonValue.Create(boolean);
                    return true;
                }

                if (decoded is "0" or "1")
                {
                    value = JsonValue.Create(decoded == "1");
                    return true;
                }

                break;
            case "int32":
            case "sint32":
            case "sfixed32":
                if (int.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int32))
                {
                    value = JsonValue.Create(int32);
                    return true;
                }

                break;
            case "int64":
            case "sint64":
            case "sfixed64":
                if (long.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int64))
                {
                    value = JsonValue.Create(int64);
                    return true;
                }

                break;
            case "uint32":
            case "fixed32":
                if (uint.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uint32))
                {
                    value = JsonValue.Create(uint32);
                    return true;
                }

                break;
            case "uint64":
            case "fixed64":
                if (ulong.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uint64))
                {
                    value = JsonValue.Create(uint64);
                    return true;
                }

                break;
            case "float":
            case "double":
                if (double.TryParse(decoded, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating)
                    && double.IsFinite(floating))
                {
                    value = JsonValue.Create(floating);
                    return true;
                }

                break;
            default:
                // Project value types remain strings unless a custom codec owns their grammar.
                value = JsonValue.Create(decoded);
                return true;
        }

        error = $"'{decoded}' is not a valid {typeName}.";
        return false;
    }

    public static bool TryWrite(
        JsonNode? value,
        IEnumerable<string> reservedSeparators,
        out string physicalText,
        out string? error)
    {
        error = null;
        if (value is null)
        {
            physicalText = WorkbookProtocol.ExplicitNullToken;
            return true;
        }

        string atom;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            atom = string.Equals(text, WorkbookProtocol.ExplicitNullToken, StringComparison.Ordinal)
                ? "%7E"
                : text;
        }
        else if (value is JsonValue)
        {
            atom = value.ToJsonString();
        }
        else
        {
            atom = CanonicalJson.Serialize(value);
        }

        physicalText = DelimitedCellText.EncodeAtom(atom, reservedSeparators);
        return true;
    }
}

internal static class DelimitedCellText
{
    public static bool TrySplit(
        string text,
        string separator,
        out List<string> segments,
        out string? error)
    {
        segments = [];
        error = null;
        if (string.IsNullOrEmpty(separator))
        {
            error = "CellFormat separator must not be empty.";
            return false;
        }

        var start = 0;
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                quoted = !quoted;
                continue;
            }

            if (!quoted && text.AsSpan(index).StartsWith(separator, StringComparison.Ordinal))
            {
                segments.Add(text[start..index]);
                index += separator.Length - 1;
                start = index + 1;
            }
        }

        if (quoted)
        {
            error = "Cell text contains an unterminated quoted segment.";
            return false;
        }

        segments.Add(text[start..]);
        return true;
    }

    public static bool TryDecodeAtom(string text, out string value, out string? error)
    {
        error = null;
        if (!text.Contains('"', StringComparison.Ordinal))
        {
            value = text;
            return true;
        }

        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            value = string.Empty;
            error = "A quoted cell segment must be enclosed by one pair of double quotes.";
            return false;
        }

        var builder = new StringBuilder(text.Length - 2);
        for (var index = 1; index < text.Length - 1; index++)
        {
            if (text[index] != '"')
            {
                builder.Append(text[index]);
                continue;
            }

            if (index + 1 < text.Length - 1 && text[index + 1] == '"')
            {
                builder.Append('"');
                index++;
                continue;
            }

            value = string.Empty;
            error = "A quote inside a quoted cell segment must be doubled.";
            return false;
        }

        value = builder.ToString();
        return true;
    }

    public static string EncodeAtom(string value, IEnumerable<string> separators)
    {
        // Empty unquoted text means a missing segment, so an actual empty string must be quoted.
        var mustQuote = value.Length == 0
                        || value.Contains('"', StringComparison.Ordinal)
                        || value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
                        || separators.Any(separator => value.Contains(separator, StringComparison.Ordinal));
        return mustQuote ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }
}
