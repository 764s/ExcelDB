using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;
using ExcelDb.Tooling.Project;

namespace ExcelDb.Pipeline;

/// <summary>
/// Persists machine-local operation evidence for Project Hub navigation. Reports
/// are diagnostic state, never inputs to schema, Excel, generated C#, or bytes.
/// </summary>
internal static class OperationReportStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static OperationReport TryPersist(ProjectContext context, OperationReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            if (Directory.Exists(context.ReportsDirectory)
                && (File.GetAttributes(context.ReportsDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The reports directory is a symbolic link, junction, or reparse point.");
            }

            Directory.CreateDirectory(context.ReportsDirectory);
            var operation = string.Concat(report.Operation.Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '-'));
            var fileName = $"{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture)}-{operation}-{Guid.NewGuid():N}.json";
            var path = Path.Combine(context.ReportsDirectory, fileName);
            var persisted = report with
            {
                Artifacts = report.Artifacts.Add(new ArtifactRecord("operation-report", path)),
            };
            AtomicFile.WriteAllBytes(path, [.. JsonSerializer.SerializeToUtf8Bytes(persisted, JsonOptions), (byte)'\n']);
            return persisted;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or JsonException)
        {
            return report with
            {
                Diagnostics = report.Diagnostics.Add(new Diagnostic(
                    "report.persist-warning",
                    DiagnosticSeverity.Warning,
                    context.ReportsDirectory,
                    $"The operation completed, but its local diagnostic report could not be saved: {exception.Message}")),
            };
        }
    }
}
