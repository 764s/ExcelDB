using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Protocol;
using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Hashing;
using ExcelDb.Tooling.Plans;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ExcelDb.Pipeline;

public sealed record PublishedSchemaSnapshot(
    int Sequence,
    ulong SchemaHash,
    string Revision,
    string DescriptorContentHash,
    string DescriptorPath,
    CanonicalSchemaDescriptor Descriptor,
    FileDescriptorSet DescriptorSet);

public sealed record PublishedSchemaHistoryLoadResult(
    PublishedSchemaSnapshot? Latest,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(static item => item.IsFailure);
}

/// <summary>
/// Version-controlled published schema authority.  It intentionally lives below SchemaDir,
/// outside CacheDir, so deleting local caches cannot erase numeric identity/tombstone history.
/// </summary>
public static class PublishedSchemaHistoryStore
{
    private static readonly MessageParser<FileDescriptorSet> DescriptorParser =
        FileDescriptorSet.Parser.WithExtensionRegistry(new ExtensionRegistry
        {
            OptionsExtensions.Table,
            OptionsExtensions.Field,
            OptionsExtensions.EnumValue,
            OptionsExtensions.Defaults,
        });

    public const int FormatVersion = 1;
    public const string DirectoryName = ".exceldb-history";
    public const string IndexFileName = "published.json";

    public static string DirectoryPath(PipelineProject project) =>
        Path.Combine(project.Context.SchemaDirectory, DirectoryName);

    public static string IndexPath(PipelineProject project) =>
        Path.Combine(DirectoryPath(project), IndexFileName);

    public static PublishedSchemaHistoryLoadResult LoadLatest(PipelineProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var directory = DirectoryPath(project);
        var indexPath = IndexPath(project);
        var snapshots = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "descriptor-*.pb", SearchOption.TopDirectoryOnly).ToArray()
            : [];
        if (!File.Exists(indexPath))
        {
            if (snapshots.Length != 0)
            {
                diagnostics.Add(Blocker(
                    "compat.history-index-missing",
                    Relative(project, indexPath),
                    "Published descriptor snapshots exist but the authoritative history index is missing."));
            }
            return new PublishedSchemaHistoryLoadResult(null, diagnostics.ToImmutable());
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(indexPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.GetProperty("formatVersion").GetInt32() != FormatVersion)
            {
                throw new InvalidDataException("Unsupported published schema history format.");
            }
            var entries = root.GetProperty("entries").EnumerateArray().Select(ReadEntry).ToArray();
            if (entries.Length == 0)
                throw new InvalidDataException("Published schema history contains no entries.");
            for (var index = 0; index < entries.Length; index++)
            {
                if (entries[index].Sequence != index + 1)
                    throw new InvalidDataException("Published schema history sequence is not contiguous.");
                if (index != 0 && entries[index - 1].SchemaHash == entries[index].SchemaHash)
                    throw new InvalidDataException("Published schema history repeats an adjacent schema hash.");
            }

            var latest = entries[^1];
            var descriptorPath = Path.GetFullPath(Path.Combine(directory, latest.DescriptorFile));
            if (!descriptorPath.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(descriptorPath))
            {
                throw new InvalidDataException($"Published descriptor '{latest.DescriptorFile}' is missing or escapes the history directory.");
            }
            var bytes = File.ReadAllBytes(descriptorPath);
            var contentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(contentHash, latest.DescriptorContentHash, StringComparison.Ordinal))
                throw new InvalidDataException("Published descriptor content hash does not match the history index.");
            var descriptorSet = DescriptorParser.ParseFrom(bytes);
            var compilation = new SchemaCompiler().CompileDescriptorSet(descriptorSet);
            if (!compilation.Succeeded || compilation.Descriptor is null)
                throw new InvalidDataException("Published descriptor set no longer compiles canonically.");
            var recomputedHash = CanonicalSchemaSerializer.ComputeHash(compilation.Descriptor);
            if (compilation.Descriptor.SchemaHash != latest.SchemaHash
                || recomputedHash != latest.SchemaHash)
            {
                throw new InvalidDataException(
                    $"Published descriptor canonical hash does not match the history index: index={latest.SchemaHash:x16}, compiled={compilation.Descriptor.SchemaHash:x16}, recomputed={recomputedHash:x16}.");
            }
            return new PublishedSchemaHistoryLoadResult(
                new PublishedSchemaSnapshot(
                    latest.Sequence,
                    latest.SchemaHash,
                    latest.Revision,
                    latest.DescriptorContentHash,
                    descriptorPath,
                    compilation.Descriptor,
                    descriptorSet),
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException
                                           or InvalidDataException
                                           or InvalidProtocolBufferException
                                           or JsonException
                                           or KeyNotFoundException
                                           or FormatException
                                           or OverflowException)
        {
            diagnostics.Add(Blocker(
                "compat.history-invalid",
                Relative(project, indexPath),
                exception.Message));
            return new PublishedSchemaHistoryLoadResult(null, diagnostics.ToImmutable());
        }
    }

    public static void AddPublishMutations(
        MutationPlanBuilder builder,
        PipelineProject project,
        CompiledProjectSchema current)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(current);
        var load = LoadLatest(project);
        if (!load.IsValid)
            throw new InvalidOperationException(string.Join(Environment.NewLine, load.Diagnostics.Select(static item => item.Message)));
        var latest = load.Latest;
        if (latest?.SchemaHash == current.Descriptor.SchemaHash)
            return;

        var directory = DirectoryPath(project);
        var descriptorFile = $"descriptor-{current.Descriptor.SchemaHash.ToString("x16", CultureInfo.InvariantCulture)}.pb";
        var descriptorPath = Path.Combine(directory, descriptorFile);
        var descriptorHash = Convert.ToHexStringLower(SHA256.HashData(current.DescriptorBytes));
        var entries = ReadEntriesOrEmpty(project).ToList();
        entries.Add(new HistoryEntry(
            entries.Count + 1,
            current.Descriptor.SchemaHash,
            $"schema-{current.Descriptor.SchemaHash.ToString("x16", CultureInfo.InvariantCulture)}",
            descriptorFile,
            descriptorHash));

        if (File.Exists(descriptorPath))
        {
            builder.Observe(descriptorPath);
            var existingHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(descriptorPath)));
            if (!string.Equals(existingHash, descriptorHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Published descriptor path collision at '{descriptorPath}'.");
        }
        else
        {
            builder.Observe(descriptorPath);
            builder.WriteFile(descriptorPath, current.DescriptorBytes);
        }
        var indexPath = IndexPath(project);
        builder.Observe(indexPath);
        builder.WriteFile(indexPath, SerializeIndex(entries));
    }

    private static IReadOnlyList<HistoryEntry> ReadEntriesOrEmpty(PipelineProject project)
    {
        var indexPath = IndexPath(project);
        if (!File.Exists(indexPath))
            return [];
        using var document = JsonDocument.Parse(File.ReadAllBytes(indexPath));
        return document.RootElement.GetProperty("entries").EnumerateArray().Select(ReadEntry).ToArray();
    }

    private static HistoryEntry ReadEntry(JsonElement element)
    {
        var hashText = element.GetProperty("schemaHash").GetString();
        if (!ulong.TryParse(hashText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var schemaHash)
            || schemaHash == 0)
            throw new InvalidDataException($"Invalid published schema hash '{hashText}'.");
        var sequence = element.GetProperty("sequence").GetInt32();
        var revision = RequiredString(element, "revision");
        var descriptorFile = RequiredString(element, "descriptorFile");
        var descriptorContentHash = RequiredString(element, "descriptorContentHash");
        if (sequence <= 0 || descriptorContentHash.Length != 64
            || descriptorContentHash.Any(static character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Published schema history entry is malformed.");
        return new HistoryEntry(sequence, schemaHash, revision, descriptorFile, descriptorContentHash.ToLowerInvariant());
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Published schema history property '{name}' is missing.");
    }

    private static byte[] SerializeIndex(IEnumerable<HistoryEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteStartArray("entries");
            foreach (var entry in entries.OrderBy(static item => item.Sequence))
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", entry.Sequence);
                writer.WriteString("schemaHash", entry.SchemaHash.ToString("x16", CultureInfo.InvariantCulture));
                writer.WriteString("revision", entry.Revision);
                writer.WriteString("descriptorFile", entry.DescriptorFile);
                writer.WriteString("descriptorContentHash", entry.DescriptorContentHash);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return [.. stream.ToArray(), (byte)'\n'];
    }

    private static string Relative(PipelineProject project, string path) =>
        Path.GetRelativePath(project.Context.RootDirectory, path).Replace('\\', '/');

    private static Diagnostic Blocker(string code, string location, string message) =>
        new(code, DiagnosticSeverity.Blocker, location, message);

    private sealed record HistoryEntry(
        int Sequence,
        ulong SchemaHash,
        string Revision,
        string DescriptorFile,
        string DescriptorContentHash);
}
