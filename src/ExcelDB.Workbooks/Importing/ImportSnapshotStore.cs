using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Core.Values;
using ExcelDb.Workbooks.Model;

namespace ExcelDb.Workbooks.Importing;

public sealed record ImportSnapshotLoadResult(
    ImportSnapshot? Snapshot,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsDegraded => Snapshot is null;
}

/// <summary>Deterministic, local-only persistence for the rebuildable M6 merge base.</summary>
public static class ImportSnapshotStore
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Save(string path, ImportSnapshot snapshot)
    {
        Guard.NotNullOrWhiteSpace(path);
        Guard.NotNull(snapshot);
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(ToDocument(snapshot), JsonOptions));
    }

    public static ImportSnapshotLoadResult Load(
        string path,
        string expectedWorkbookPath,
        ulong expectedSchemaHash)
    {
        Guard.NotNullOrWhiteSpace(path);
        Guard.NotNullOrWhiteSpace(expectedWorkbookPath);
        if (!File.Exists(path))
        {
            return Degraded(
                "EXWB2200",
                path,
                "Import snapshot is missing; a clean resident may rebuild its base from the current workbook, while dirty state requires explicit recovery.");
        }

        try
        {
            var document = JsonSerializer.Deserialize<SnapshotDocument>(File.ReadAllBytes(path), JsonOptions)
                ?? throw new InvalidDataException("Import snapshot is empty.");
            if (document.Version != FormatVersion)
                throw new InvalidDataException($"Unsupported import snapshot format {document.Version}.");
            if (!SamePath(document.WorkbookPath, expectedWorkbookPath)
                || !ulong.TryParse(
                    document.SchemaHash,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var schemaHash)
                || schemaHash != expectedSchemaHash)
            {
                return Degraded(
                    "EXWB2202",
                    path,
                    "Import snapshot belongs to a different workbook or schema and was not used as merge evidence.");
            }

            return new ImportSnapshotLoadResult(FromDocument(document, schemaHash), []);
        }
        catch (Exception exception) when (exception is JsonException
                                           or IOException
                                           or InvalidDataException
                                           or ArgumentException
                                           or FormatException)
        {
            return Degraded(
                "EXWB2201",
                path,
                $"Import snapshot is corrupt and was not used as merge evidence: {exception.Message}");
        }
    }

    private static SnapshotDocument ToDocument(ImportSnapshot snapshot) =>
        new(
            FormatVersion,
            snapshot.WorkbookPath,
            snapshot.WorkbookFingerprint.Length,
            snapshot.WorkbookFingerprint.Sha256,
            snapshot.SchemaHash.ToString("x16", CultureInfo.InvariantCulture),
            snapshot.Rows.Values
                .OrderBy(static row => row.Identity.TableId)
                .ThenBy(static row => row.Identity.RowGuid.ToString(), StringComparer.Ordinal)
                .Select(static row => new SnapshotRowDocument(
                    row.Identity.TableId,
                    row.Identity.RowGuid.ToString(),
                    row.Key,
                    row.Revision,
                    Values(row.Values),
                    Values(row.RawValues),
                    row.RawCells
                        .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                        .Select(static pair => new CellDocument(
                            pair.Key,
                            pair.Value.Text,
                            pair.Value.Formula))
                        .ToArray()))
                .ToArray());

    private static ImportSnapshot FromDocument(SnapshotDocument document, ulong schemaHash)
    {
        var rows = ImmutableDictionary.CreateBuilder<AssetIdentity, SnapshotRow>();
        foreach (var item in document.Rows ?? [])
        {
            var identity = new AssetIdentity(item.TableId, RowGuid.Parse(item.RowGuid));
            var values = ReadValues(item.Values);
            var rawValues = ReadValues(item.RawValues);
            var rawCells = (item.RawCells ?? [])
                .ToImmutableDictionary(
                    static cell => cell.Path,
                    static cell => new WorkbookCell(cell.Text, cell.Formula),
                    StringComparer.Ordinal);
            rows.Add(identity, new SnapshotRow(identity, item.Key, item.Revision, values, rawCells)
            {
                RawValues = rawValues,
            });
        }

        return new ImportSnapshot(
            document.WorkbookPath,
            new ContentFingerprint(document.FingerprintLength, document.FingerprintSha256),
            schemaHash,
            rows.ToImmutable());
    }

    private static ValueDocument[] Values(ImmutableDictionary<string, CanonicalValue> values) =>
        values.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new ValueDocument(
                pair.Key,
                pair.Value.State,
                pair.Value.Text,
                pair.Value.RawText))
            .ToArray();

    private static ImmutableDictionary<string, CanonicalValue> ReadValues(ValueDocument[]? values) =>
        (values ?? []).ToImmutableDictionary(
            static value => value.Path,
            static value => value.State switch
            {
                CanonicalValueState.Missing => CanonicalValue.Missing,
                CanonicalValueState.Defaulted => CanonicalValue.FromDefault(
                    value.Text ?? throw new InvalidDataException("Defaulted snapshot value has no text.")),
                CanonicalValueState.Null => CanonicalValue.Null,
                CanonicalValueState.Value => CanonicalValue.FromValue(
                    value.Text ?? throw new InvalidDataException("Snapshot value has no canonical text.")),
                CanonicalValueState.Invalid => CanonicalValue.Invalid(
                    value.RawText ?? throw new InvalidDataException("Invalid snapshot value has no raw text.")),
                _ => throw new InvalidDataException($"Unknown canonical snapshot state {value.State}."),
            },
            StringComparer.Ordinal);

    private static ImportSnapshotLoadResult Degraded(string code, string path, string message) =>
        new(null, [new Diagnostic(code, DiagnosticSeverity.Warning, path, message)]);

    private static bool SamePath(string left, string right) =>
        string.Equals(
            left.Replace('\\', '/'),
            right.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private sealed record SnapshotDocument(
        int Version,
        string WorkbookPath,
        long FingerprintLength,
        string FingerprintSha256,
        string SchemaHash,
        SnapshotRowDocument[] Rows);

    private sealed record SnapshotRowDocument(
        int TableId,
        string RowGuid,
        string? Key,
        uint Revision,
        ValueDocument[] Values,
        ValueDocument[] RawValues,
        CellDocument[] RawCells);

    private sealed record ValueDocument(
        string Path,
        CanonicalValueState State,
        string? Text,
        string? RawText);

    private sealed record CellDocument(string Path, string? Text, string? Formula);
}
