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
using ExcelDb.Tooling.Project;
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

public sealed record PublishedSchemaHistoryEntry(
    int Sequence,
    ulong SchemaHash,
    string Revision,
    string DescriptorFile,
    string DescriptorContentHash);

public sealed record PublishedSchemaHistoryLoadResult(
    PublishedSchemaSnapshot? Latest,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<PublishedSchemaHistoryEntry> Entries = default)
{
    public bool IsValid => !Diagnostics.Any(static item => item.IsFailure);
}

/// <summary>
/// Version-controlled published schema authority. It intentionally lives below
/// .exceldb/published and outside cache, so deleting local state cannot erase numeric
/// identity/tombstone history.
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
    public const string IndexFileName = "published.json";

    public static string DirectoryPath(PipelineProject project) =>
        project.Context.PublishedDirectory;

    public static string IndexPath(PipelineProject project) =>
        Path.Combine(DirectoryPath(project), IndexFileName);

    public static PublishedSchemaHistoryLoadResult LoadLatest(PipelineProject project) =>
        LoadLatest(project, evidenceBuilder: null);

    internal static PublishedSchemaHistoryLoadResult LoadLatest(
        PipelineProject project,
        MutationPlanBuilder? evidenceBuilder)
    {
        ArgumentNullException.ThrowIfNull(project);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var directory = DirectoryPath(project);
        var indexPath = IndexPath(project);
        string[] snapshots;
        try
        {
            ProjectPathSafety.EnsureContainedPathIsPlain(
                project.Context.InternalDirectory,
                directory,
                "Published schema history",
                targetMustBeDirectory: true);
            evidenceBuilder?.Observe(indexPath);
            snapshots = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "descriptor-*.pb", SearchOption.TopDirectoryOnly).ToArray()
                : [];
            foreach (var snapshot in snapshots)
                ProjectPathSafety.EnsurePlainFile(snapshot, "Published schema descriptor");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            diagnostics.Add(Blocker("compat.history-path-unsafe", Relative(project, directory), exception.Message));
            return new PublishedSchemaHistoryLoadResult(null, diagnostics.ToImmutable(), []);
        }
        if (!File.Exists(indexPath))
        {
            if (snapshots.Length != 0)
            {
                diagnostics.Add(Blocker(
                    "compat.history-index-missing",
                    Relative(project, indexPath),
                    "Published descriptor snapshots exist but the authoritative history index is missing."));
            }
            return new PublishedSchemaHistoryLoadResult(null, diagnostics.ToImmutable(), []);
        }

        try
        {
            ProjectPathSafety.EnsurePlainFile(indexPath, "Published schema history index");
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

            var loadedDescriptors = new Dictionary<string, PublishedSchemaSnapshot>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var expectedFile = $"descriptor-{entry.SchemaHash.ToString("x16", CultureInfo.InvariantCulture)}.pb";
                if (!string.Equals(entry.DescriptorFile, expectedFile, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Published descriptor file '{entry.DescriptorFile}' is not the canonical path '{expectedFile}'.");
                }

                if (!loadedDescriptors.TryGetValue(entry.DescriptorFile, out var loaded))
                {
                    loaded = LoadDescriptor(project, directory, entry, evidenceBuilder);
                    loadedDescriptors.Add(entry.DescriptorFile, loaded);
                }
                else if (loaded.SchemaHash != entry.SchemaHash
                         || !string.Equals(
                             loaded.DescriptorContentHash,
                             entry.DescriptorContentHash,
                             StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Published descriptor '{entry.DescriptorFile}' has conflicting history entries.");
                }
            }

            var indexedFiles = loadedDescriptors.Keys.ToHashSet(StringComparer.Ordinal);
            var discoveredFiles = snapshots
                .Select(Path.GetFileName)
                .Where(static file => file is not null)
                .Select(static file => file!)
                .ToHashSet(StringComparer.Ordinal);
            if (!indexedFiles.SetEquals(discoveredFiles))
            {
                var unexpected = discoveredFiles.Except(indexedFiles, StringComparer.Ordinal).Order(StringComparer.Ordinal);
                var missing = indexedFiles.Except(discoveredFiles, StringComparer.Ordinal).Order(StringComparer.Ordinal);
                throw new InvalidDataException(
                    "Published descriptor files do not exactly match the authoritative history index" +
                    $"; unexpected=[{string.Join(',', unexpected)}], missing=[{string.Join(',', missing)}].");
            }

            var latest = entries[^1];
            var latestDescriptor = loadedDescriptors[latest.DescriptorFile] with
            {
                Sequence = latest.Sequence,
                Revision = latest.Revision,
            };
            return new PublishedSchemaHistoryLoadResult(
                latestDescriptor,
                diagnostics.ToImmutable(),
                entries.ToImmutableArray());
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or InvalidProtocolBufferException
                                           or JsonException
                                           or KeyNotFoundException
                                           or FormatException
                                           or OverflowException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            diagnostics.Add(Blocker(
                "compat.history-invalid",
                Relative(project, indexPath),
                exception.Message));
            return new PublishedSchemaHistoryLoadResult(null, diagnostics.ToImmutable(), []);
        }
    }

    private static PublishedSchemaSnapshot LoadDescriptor(
        PipelineProject project,
        string directory,
        PublishedSchemaHistoryEntry entry,
        MutationPlanBuilder? evidenceBuilder)
    {
        var descriptorPath = Path.GetFullPath(Path.Combine(directory, entry.DescriptorFile));
        var directoryPath = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(descriptorPath), directoryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Published descriptor '{entry.DescriptorFile}' escapes the history directory.");
        }

        evidenceBuilder?.Observe(descriptorPath);
        if (!File.Exists(descriptorPath))
            throw new InvalidDataException($"Published descriptor '{entry.DescriptorFile}' is missing.");
        ProjectPathSafety.EnsurePlainFile(descriptorPath, "Published schema descriptor");
        var bytes = File.ReadAllBytes(descriptorPath);
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(contentHash, entry.DescriptorContentHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Published descriptor '{entry.DescriptorFile}' content hash does not match the history index.");
        }

        var descriptorSet = DescriptorParser.ParseFrom(bytes);
        var compilation = new SchemaCompiler().CompileDescriptorSet(descriptorSet);
        if (!compilation.Succeeded || compilation.Descriptor is null)
        {
            throw new InvalidDataException(
                $"Published descriptor '{entry.DescriptorFile}' no longer compiles canonically.");
        }

        var recomputedHash = CanonicalSchemaSerializer.ComputeHash(compilation.Descriptor);
        if (compilation.Descriptor.SchemaHash != entry.SchemaHash
            || recomputedHash != entry.SchemaHash)
        {
            throw new InvalidDataException(
                $"Published descriptor '{entry.DescriptorFile}' canonical hash does not match the history index: " +
                $"index={entry.SchemaHash:x16}, compiled={compilation.Descriptor.SchemaHash:x16}, recomputed={recomputedHash:x16}.");
        }

        return new PublishedSchemaSnapshot(
            entry.Sequence,
            entry.SchemaHash,
            entry.Revision,
            entry.DescriptorContentHash,
            descriptorPath,
            compilation.Descriptor,
            descriptorSet);
    }

    public static void AddPublishMutations(
        MutationPlanBuilder builder,
        PipelineProject project,
        CompiledProjectSchema current,
        PublishedSchemaHistoryLoadResult? observedHistory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(current);
        var load = observedHistory ?? LoadLatest(project, builder);
        if (!load.IsValid)
            throw new InvalidOperationException(string.Join(Environment.NewLine, load.Diagnostics.Select(static item => item.Message)));
        var latest = load.Latest;
        if (latest?.SchemaHash == current.Descriptor.SchemaHash)
            return;

        var directory = DirectoryPath(project);
        var descriptorFile = $"descriptor-{current.Descriptor.SchemaHash.ToString("x16", CultureInfo.InvariantCulture)}.pb";
        var descriptorPath = Path.Combine(directory, descriptorFile);
        var descriptorHash = Convert.ToHexStringLower(SHA256.HashData(current.DescriptorBytes));
        var entries = (load.Entries.IsDefault ? [] : load.Entries).ToList();
        entries.Add(new PublishedSchemaHistoryEntry(
            entries.Count + 1,
            current.Descriptor.SchemaHash,
            $"schema-{current.Descriptor.SchemaHash.ToString("x16", CultureInfo.InvariantCulture)}",
            descriptorFile,
            descriptorHash));

        builder.Observe(descriptorPath);
        if (File.Exists(descriptorPath))
        {
            var existingHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(descriptorPath)));
            if (!string.Equals(existingHash, descriptorHash, StringComparison.Ordinal))
                throw new InvalidDataException($"Published descriptor path collision at '{descriptorPath}'.");
        }
        else
        {
            builder.WriteFile(descriptorPath, current.DescriptorBytes);
        }
        var indexPath = IndexPath(project);
        builder.Observe(indexPath);
        builder.WriteFile(indexPath, SerializeIndex(entries));
    }

    private static PublishedSchemaHistoryEntry ReadEntry(JsonElement element)
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
        return new PublishedSchemaHistoryEntry(sequence, schemaHash, revision, descriptorFile, descriptorContentHash.ToLowerInvariant());
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Published schema history property '{name}' is missing.");
    }

    private static byte[] SerializeIndex(IEnumerable<PublishedSchemaHistoryEntry> entries)
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

}
