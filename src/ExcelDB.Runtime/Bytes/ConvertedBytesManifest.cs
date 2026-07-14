using System.Globalization;
using System.Text;
using System.Text.Json;
using ExcelDb.Core.Identity;

namespace ExcelDb.Runtime.Bytes;

public sealed record ConvertedBytesManifest(
    int FormatVersion,
    ulong SchemaHash,
    ExportTargetId ExportTarget,
    string SourceContentHash,
    int TableCount,
    int RowCount,
    string ToolVersion,
    string BytesSha256)
{
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("schemaHash", SchemaHash.ToString("x16", CultureInfo.InvariantCulture));
            writer.WriteString("exportTargetId", ExportTarget.Value);
            writer.WriteString("sourceContentHash", SourceContentHash);
            writer.WriteNumber("tableCount", TableCount);
            writer.WriteNumber("rowCount", RowCount);
            writer.WriteString("toolVersion", ToolVersion);
            writer.WriteString("bytesSha256", BytesSha256);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ConvertedBytesManifest Parse(string json)
    {
        RuntimeCompatibility.NotNullOrWhiteSpace(json, nameof(json));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A converted-bytes manifest must be a JSON object.");

        var formatVersion = root.GetProperty("formatVersion").GetInt32();
        var schemaText = root.GetProperty("schemaHash").GetString();
        if (schemaText is null
            || !ulong.TryParse(schemaText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var schemaHash))
        {
            throw new InvalidDataException("Manifest schemaHash must be a hexadecimal UInt64.");
        }

        var targetText = root.GetProperty("exportTargetId").GetString();
        if (!ExportTargetId.TryParse(targetText, out var target))
            throw new InvalidDataException("Manifest exportTargetId is invalid.");

        var sourceContentHash = RequireString(root, "sourceContentHash");
        var tableCount = root.GetProperty("tableCount").GetInt32();
        var rowCount = root.GetProperty("rowCount").GetInt32();
        var toolVersion = RequireString(root, "toolVersion");
        var bytesSha256 = RequireString(root, "bytesSha256");

        if (formatVersion <= 0 || tableCount < 0 || rowCount < 0 || bytesSha256.Length != 64)
            throw new InvalidDataException("Manifest contains an invalid version, count, or SHA-256 value.");

        return new ConvertedBytesManifest(
            formatVersion,
            schemaHash,
            target,
            sourceContentHash,
            tableCount,
            rowCount,
            toolVersion,
            bytesSha256);
    }

    private static string RequireString(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).GetString()
        ?? throw new InvalidDataException($"Manifest property '{propertyName}' must be a string.");
}

public sealed record ConvertedBytesPackage(
    byte[] Bytes,
    ConvertedBytesManifest Manifest,
    string ManifestJson);

public sealed record ConvertedBytesReadResult(
    SourceSnapshot Snapshot,
    ConvertedBytesManifest? Manifest);
