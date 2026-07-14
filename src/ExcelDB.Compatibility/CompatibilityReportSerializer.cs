using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExcelDb.Compatibility;

public static class CompatibilityReportSerializer
{
    public static byte[] SerializeUtf8(CompatibilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", report.FormatVersion);
            if (report.PreviousSchemaHash.HasValue)
                writer.WriteString("previousSchemaHash", report.PreviousSchemaHash.Value.ToString("x16"));
            else
                writer.WriteNull("previousSchemaHash");
            writer.WriteString("currentSchemaHash", report.CurrentSchemaHash.ToString("x16"));
            writer.WriteBoolean("historyAvailable", report.HistoryAvailable);
            writer.WriteString("maximumSeverity", report.MaximumSeverity.ToString());
            writer.WriteStartArray("entries");
            foreach (var entry in report.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", entry.Kind.ToString());
                writer.WriteString("severity", entry.Severity.ToString());
                WriteLocation(writer, entry.Location);
                writer.WriteString("message", entry.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("diagnostics");
            foreach (var diagnostic in report.Diagnostics
                         .OrderBy(static item => item.Code, StringComparer.Ordinal)
                         .ThenBy(static item => item.Location, StringComparer.Ordinal)
                         .ThenBy(static item => item.Message, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("code", diagnostic.Code);
                writer.WriteString("severity", diagnostic.Severity.ToString());
                writer.WriteString("location", diagnostic.Location);
                writer.WriteString("message", diagnostic.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static string Serialize(CompatibilityReport report) =>
        Encoding.UTF8.GetString(SerializeUtf8(report));

    public static string ComputeHash(CompatibilityReport report) =>
        Convert.ToHexString(SHA256.HashData(SerializeUtf8(report))).ToLowerInvariant();

    private static void WriteLocation(Utf8JsonWriter writer, CompatibilityLocation location)
    {
        writer.WriteStartObject("location");
        if (location.TableId.HasValue) writer.WriteNumber("tableId", location.TableId.Value); else writer.WriteNull("tableId");
        writer.WriteStartArray("fieldIdPath");
        foreach (var fieldId in location.FieldIdPath)
            writer.WriteNumberValue(fieldId);
        writer.WriteEndArray();
        WriteNullable(writer, "enumName", location.EnumName);
        if (location.EnumNumber.HasValue) writer.WriteNumber("enumNumber", location.EnumNumber.Value); else writer.WriteNull("enumNumber");
        WriteNullable(writer, "exportTarget", location.ExportTarget);
        WriteNullable(writer, "previousPath", location.PreviousPath);
        WriteNullable(writer, "currentPath", location.CurrentPath);
        WriteNullable(writer, "workbookId", location.WorkbookId);
        WriteNullable(writer, "cell", location.Cell);
        writer.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }
}
