using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Schema.Compilation;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;

namespace ExcelDb.Pipeline;

public enum SystemProtoMirrorStatus
{
    Current = 0,
    Missing = 1,
    Outdated = 2,
    Conflicted = 3,
}

public enum SystemProtoMirrorMutationKind
{
    WriteFile = 0,
    DeleteFile = 1,
}

public sealed record SystemProtoMirrorMutation(
    SystemProtoMirrorMutationKind Kind,
    string AbsolutePath,
    byte[]? Content = null);

public sealed record SystemProtoMirrorAnalysis(
    SystemProtoMirrorStatus Status,
    ImmutableArray<SystemProtoMirrorMutation> Mutations,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableDictionary<string, string> TrustedStaleFiles,
    InputSetObservation? ReservedInputSet = null,
    PathObservation? ManifestObservation = null)
{
    public bool HasBlockers => Diagnostics.Any(static diagnostic => diagnostic.IsBlocker);
}

/// <summary>
/// Validates and plans the disposable IDE mirror of the executable-owned system
/// proto catalog. The compiler never consumes these disk copies.
/// </summary>
public sealed class SystemProtoMirror
{
    private const int FormatVersion = 1;
    private static readonly Regex ImportStatementPattern = new(
        "^\\s*import\\s+(?:(?:public|weak)\\s+)?[\"'](?<path>[^\"'\\r\\n]+)[\"']\\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private readonly Action? _afterSnapshot;

    public SystemProtoMirror()
    {
    }

    internal SystemProtoMirror(Action afterSnapshot)
    {
        _afterSnapshot = afterSnapshot ?? throw new ArgumentNullException(nameof(afterSnapshot));
    }

    public SystemProtoMirrorAnalysis Analyze(
        ProjectContext project,
        ISystemProtoCatalog catalog,
        bool explicitRepair)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(catalog);

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var mutations = ImmutableArray.CreateBuilder<SystemProtoMirrorMutation>();
        var trustedStaleFiles = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var ownershipNeedsRewrite = false;
        var snapshot = CaptureSnapshot(project, diagnostics);
        if (snapshot is null)
        {
            return Build(
                project,
                SystemProtoMirrorStatus.Conflicted,
                mutations,
                diagnostics,
                trustedStaleFiles,
                null);
        }
        _afterSnapshot?.Invoke();
        var manifest = ReadManifest(snapshot.Manifest, project.SystemImportsManifestPath, diagnostics);
        var diskFiles = snapshot.ReservedFiles;
        var catalogFiles = catalog.Files.ToDictionary(static file => file.LogicalPath, StringComparer.Ordinal);

        foreach (var disk in diskFiles)
        {
            if (!catalogFiles.ContainsKey(disk.Key)
                && (manifest is null || !manifest.Files.ContainsKey(disk.Key)))
            {
                diagnostics.Add(Blocker(
                    "system-import.unknown-reserved",
                    disk.Key,
                    AppendImportChains(project, catalog, disk.Key, "The reserved system import path is not owned by this executable catalog.")));
            }
        }

        if (manifest is null)
        {
            if (diskFiles.Count != 0)
            {
                diagnostics.Add(Blocker(
                    "system-import.ownership-missing",
                    Relative(project, project.SystemImportsManifestPath),
                    "Reserved system proto files exist without .exceldb/system-imports.json ownership. Remove them or reinitialize explicitly; ExcelDB will not silently claim project files."));
                return Build(project, SystemProtoMirrorStatus.Conflicted, mutations, diagnostics, trustedStaleFiles, snapshot);
            }

            AddCompleteCatalogWrites(project, catalog, mutations);
            mutations.Add(new SystemProtoMirrorMutation(
                SystemProtoMirrorMutationKind.WriteFile,
                project.SystemImportsManifestPath,
                SerializeManifest(catalog)));
            diagnostics.Add(new Diagnostic(
                "system-import.missing",
                DiagnosticSeverity.Warning,
                project.Project.SchemaDir,
                "The IDE system proto mirror is missing and can be repaired from the executable catalog."));
            return Build(project, SystemProtoMirrorStatus.Missing, mutations, diagnostics, trustedStaleFiles, snapshot);
        }

        var status = string.Equals(manifest.CatalogHash, catalog.CatalogHash, StringComparison.Ordinal)
            ? SystemProtoMirrorStatus.Current
            : SystemProtoMirrorStatus.Outdated;

        foreach (var owned in manifest.Files.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (catalogFiles.ContainsKey(owned.Key))
                continue;
            if (!diskFiles.TryGetValue(owned.Key, out var removedFile))
                continue;
            var actual = removedFile.Sha256;
            if (!string.Equals(actual, owned.Value, StringComparison.Ordinal))
            {
                diagnostics.Add(Blocker(
                    "system-import.modified-retired",
                    owned.Key,
                    AppendImportChains(project, catalog, owned.Key, "A mirror file retired from the current catalog was modified after ExcelDB last owned it; it will not be deleted.")));
                status = SystemProtoMirrorStatus.Conflicted;
                continue;
            }
            trustedStaleFiles[owned.Key] = actual;
            mutations.Add(new SystemProtoMirrorMutation(SystemProtoMirrorMutationKind.DeleteFile, removedFile.AbsolutePath));
            status = SystemProtoMirrorStatus.Outdated;
        }

        foreach (var canonical in catalog.Files)
        {
            var destination = ResolveMirrorPath(project, canonical.LogicalPath);
            if (!diskFiles.TryGetValue(canonical.LogicalPath, out var existingFile))
            {
                mutations.Add(new SystemProtoMirrorMutation(
                    SystemProtoMirrorMutationKind.WriteFile,
                    destination,
                    canonical.CanonicalBytes.ToArray()));
                status = status == SystemProtoMirrorStatus.Current
                    ? SystemProtoMirrorStatus.Missing
                    : status;
                continue;
            }

            var actualHash = existingFile.Sha256;
            if (string.Equals(actualHash, canonical.Sha256, StringComparison.Ordinal))
            {
                if (!manifest.Files.TryGetValue(canonical.LogicalPath, out var canonicalRecordedHash))
                {
                    diagnostics.Add(Blocker(
                        "system-import.unregistered",
                        canonical.LogicalPath,
                        AppendImportChains(project, catalog, canonical.LogicalPath, "A canonical-looking reserved file is not recorded as tool owned; ExcelDB will not silently claim it.")));
                    status = SystemProtoMirrorStatus.Conflicted;
                }
                else if (!string.Equals(canonicalRecordedHash, actualHash, StringComparison.Ordinal))
                {
                    if (!explicitRepair)
                    {
                        diagnostics.Add(Blocker(
                            "system-import.ownership-mismatch",
                            canonical.LogicalPath,
                            AppendImportChains(project, catalog, canonical.LogicalPath, $"The canonical mirror content does not match its recorded ownership hash (recorded {canonicalRecordedHash}, actual {actualHash}). Run the explicit Proto dependency repair after reviewing .exceldb/system-imports.json.")));
                        status = SystemProtoMirrorStatus.Conflicted;
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "system-import.ownership-repair",
                            DiagnosticSeverity.Warning,
                            canonical.LogicalPath,
                            "The explicitly confirmed repair will restore the ownership record for this canonical mirror."));
                        ownershipNeedsRewrite = true;
                        status = SystemProtoMirrorStatus.Outdated;
                    }
                }
                continue;
            }

            if (!manifest.Files.TryGetValue(canonical.LogicalPath, out var recordedHash))
            {
                diagnostics.Add(Blocker(
                    "system-import.unregistered",
                    canonical.LogicalPath,
                    AppendImportChains(project, catalog, canonical.LogicalPath, "A reserved file is not recorded as tool owned.")));
                status = SystemProtoMirrorStatus.Conflicted;
                continue;
            }

            var changedSinceOwned = !string.Equals(actualHash, recordedHash, StringComparison.Ordinal);
            if (changedSinceOwned && !explicitRepair)
            {
                diagnostics.Add(Blocker(
                    "system-import.modified",
                    canonical.LogicalPath,
                    AppendImportChains(project, catalog, canonical.LogicalPath, $"The IDE mirror differs from both the owned hash and executable catalog (expected {canonical.Sha256}, actual {actualHash}). Run the explicit Proto dependency repair after reviewing the file.")));
                status = SystemProtoMirrorStatus.Conflicted;
                continue;
            }

            if (!changedSinceOwned)
                trustedStaleFiles[canonical.LogicalPath] = actualHash;

            mutations.Add(new SystemProtoMirrorMutation(
                SystemProtoMirrorMutationKind.WriteFile,
                destination,
                canonical.CanonicalBytes.ToArray()));
            diagnostics.Add(new Diagnostic(
                changedSinceOwned ? "system-import.explicit-repair" : "system-import.outdated",
                DiagnosticSeverity.Warning,
                canonical.LogicalPath,
                changedSinceOwned
                    ? "The explicitly confirmed repair will restore this modified IDE mirror from the executable catalog."
                    : "The IDE mirror will be updated to the executable catalog."));
            status = changedSinceOwned ? SystemProtoMirrorStatus.Conflicted : SystemProtoMirrorStatus.Outdated;
        }

        if (!diagnostics.Any(static diagnostic => diagnostic.IsBlocker)
            && (mutations.Count != 0
                || ownershipNeedsRewrite
                || !string.Equals(manifest.CatalogHash, catalog.CatalogHash, StringComparison.Ordinal)
                || manifest.Files.Count != catalog.Files.Count))
        {
            mutations.Add(new SystemProtoMirrorMutation(
                SystemProtoMirrorMutationKind.WriteFile,
                project.SystemImportsManifestPath,
                SerializeManifest(catalog)));
        }

        if (diagnostics.Any(static diagnostic => diagnostic.IsBlocker))
            status = SystemProtoMirrorStatus.Conflicted;
        return Build(project, status, mutations, diagnostics, trustedStaleFiles, snapshot);
    }

    private static MirrorSnapshot? CaptureSnapshot(
        ProjectContext project,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        FrozenManifest manifest;
        try
        {
            manifest = CaptureManifest(project);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException
                                           or InvalidDataException)
        {
            diagnostics.Add(Blocker(
                "system-import.manifest-invalid",
                project.SystemImportsManifestPath,
                exception.Message));
            return null;
        }

        try
        {
            var paths = EnumerateReservedFiles(project, diagnostics);
            var files = ImmutableDictionary.CreateBuilder<string, FrozenReservedFile>(StringComparer.Ordinal);
            var entries = ImmutableArray.CreateBuilder<InputSetEntry>();
            foreach (var pair in paths.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                ProjectPathSafety.EnsurePlainFile(pair.Value, $"System proto mirror '{pair.Key}'");
                var bytes = File.ReadAllBytes(pair.Value);
                var hash = Sha256(bytes);
                files.Add(pair.Key, new FrozenReservedFile(pair.Value, bytes, hash));
                entries.Add(new InputSetEntry(pair.Key, ObservedPathKind.File, bytes.LongLength, hash));
            }

            var relativeSchemaRoot = Relative(project, project.SchemaDirectory);
            var reservedInputSet = Directory.Exists(project.SchemaDirectory)
                ? InputSetSnapshot.Create(
                    project.RootDirectory,
                    relativeSchemaRoot,
                    InputSetKind.SystemProtoMirror,
                    ObservedPathKind.Directory,
                    0,
                    null,
                    entries,
                    PlanRootKind.Project)
                : InputSetSnapshot.Capture(
                    project.RootDirectory,
                    relativeSchemaRoot,
                    InputSetKind.SystemProtoMirror,
                    PlanRootKind.Project);
            return new MirrorSnapshot(manifest, files.ToImmutable(), reservedInputSet);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException
                                           or InvalidDataException)
        {
            diagnostics.Add(Blocker(
                "system-import.snapshot-invalid",
                project.Project.SchemaDir,
                $"The system proto mirror could not be frozen safely: {exception.Message}"));
            return null;
        }
    }

    private static FrozenManifest CaptureManifest(ProjectContext project)
    {
        var path = project.SystemImportsManifestPath;
        var relative = Relative(project, path);
        if (File.Exists(path))
        {
            ProjectPathSafety.EnsurePlainFile(path, "System-import ownership manifest");
            var bytes = File.ReadAllBytes(path);
            return new FrozenManifest(
                new PathObservation(
                    relative,
                    ObservedPathKind.File,
                    bytes.LongLength,
                    Sha256(bytes),
                    PlanRootKind.Project),
                bytes);
        }
        return new FrozenManifest(
            new PathObservation(
                relative,
                Directory.Exists(path) ? ObservedPathKind.Directory : ObservedPathKind.Missing,
                Root: PlanRootKind.Project),
            null);
    }

    private static OwnershipManifest? ReadManifest(
        FrozenManifest manifest,
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (manifest.Observation.Kind == ObservedPathKind.Missing)
            return null;
        try
        {
            if (manifest.Observation.Kind != ObservedPathKind.File || manifest.Content is null)
                throw new InvalidDataException("System-import ownership manifest path is not a plain file.");
            using var document = JsonDocument.Parse(manifest.Content, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.GetProperty("formatVersion").GetInt32() != FormatVersion)
            {
                throw new InvalidDataException("Unsupported system-import ownership manifest format.");
            }
            var catalogHash = RequireHash(root.GetProperty("catalogHash").GetString(), "catalogHash");
            var files = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var element in root.GetProperty("files").EnumerateArray())
            {
                var logicalPath = element.GetProperty("path").GetString()
                    ?? throw new InvalidDataException("System-import ownership path is missing.");
                _ = SystemProtoCatalogPaths.IsReservedNamespace(logicalPath)
                    ? true
                    : throw new InvalidDataException($"Ownership path '{logicalPath}' is outside a reserved namespace.");
                if (logicalPath.IndexOf('\\') >= 0
                    || logicalPath.Split('/').Any(static segment => segment.Length == 0 || segment is "." or ".."))
                {
                    throw new InvalidDataException($"Ownership path '{logicalPath}' is not canonical.");
                }
                if (!files.TryAdd(logicalPath, RequireHash(element.GetProperty("sha256").GetString(), logicalPath)))
                    throw new InvalidDataException($"Duplicate system-import ownership path '{logicalPath}'.");
            }
            return new OwnershipManifest(catalogHash, files.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException
                                           or InvalidDataException
                                           or JsonException
                                           or KeyNotFoundException
                                           or InvalidOperationException)
        {
            diagnostics.Add(Blocker("system-import.manifest-invalid", path, exception.Message));
            return null;
        }
    }

    private static Dictionary<string, string> EnumerateReservedFiles(
        ProjectContext project,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(project.SchemaDirectory)
            && (File.GetAttributes(project.SchemaDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            diagnostics.Add(Blocker(
                "system-import.schema-reparse",
                project.Project.SchemaDir,
                "Schema must not be a symbolic link, junction, or other reparse point."));
            return result;
        }
        foreach (var reservedRoot in new[] { "exceldb", "google/protobuf" })
        {
            var directory = Path.Combine(
                project.SchemaDirectory,
                reservedRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory))
                continue;
            try
            {
                var pending = new Stack<string>();
                pending.Push(directory);
                while (pending.Count != 0)
                {
                    var current = pending.Pop();
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException($"Reserved mirror directory '{Path.GetRelativePath(project.SchemaDirectory, current)}' is a reparse point.");
                    foreach (var child in Directory.EnumerateDirectories(current))
                        pending.Push(child);
                    foreach (var path in Directory.EnumerateFiles(current))
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException($"Reserved mirror file '{path}' is a reparse point.");
                        var relative = Path.GetRelativePath(project.SchemaDirectory, path).Replace('\\', '/');
                        result.Add(relative, Path.GetFullPath(path));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException)
            {
                diagnostics.Add(Blocker("system-import.enumerate", reservedRoot, exception.Message));
            }
        }
        return result;
    }

    private static void AddCompleteCatalogWrites(
        ProjectContext project,
        ISystemProtoCatalog catalog,
        ImmutableArray<SystemProtoMirrorMutation>.Builder mutations)
    {
        foreach (var file in catalog.Files)
        {
            mutations.Add(new SystemProtoMirrorMutation(
                SystemProtoMirrorMutationKind.WriteFile,
                ResolveMirrorPath(project, file.LogicalPath),
                file.CanonicalBytes.ToArray()));
        }
    }

    private static byte[] SerializeManifest(ISystemProtoCatalog catalog)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("catalogHash", catalog.CatalogHash);
            writer.WriteStartArray("files");
            foreach (var file in catalog.Files.OrderBy(static item => item.LogicalPath, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.LogicalPath);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return [.. stream.ToArray(), (byte)'\n'];
    }

    private static SystemProtoMirrorAnalysis Build(
        ProjectContext project,
        SystemProtoMirrorStatus status,
        ImmutableArray<SystemProtoMirrorMutation>.Builder mutations,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableDictionary<string, string>.Builder trustedStaleFiles,
        MirrorSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            try
            {
                var manifestNow = CaptureManifest(project);
                if (!InputSetSnapshot.Matches(project.RootDirectory, snapshot.ReservedInputSet)
                    || manifestNow.Observation != snapshot.Manifest.Observation)
                {
                    diagnostics.Add(Blocker(
                        "system-import.changed-during-analysis",
                        project.Project.SchemaDir,
                        "The system proto mirror or its ownership manifest changed while it was being analyzed; create a fresh plan."));
                    mutations.Clear();
                    status = SystemProtoMirrorStatus.Conflicted;
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException
                                               or NotSupportedException
                                               or InvalidDataException)
            {
                diagnostics.Add(Blocker(
                    "system-import.changed-during-analysis",
                    project.Project.SchemaDir,
                    $"The system proto mirror could not be revalidated after analysis: {exception.Message}"));
                mutations.Clear();
                status = SystemProtoMirrorStatus.Conflicted;
            }
        }

        return new(
            status,
            mutations.OrderBy(static mutation => mutation.AbsolutePath, StringComparer.Ordinal).ToImmutableArray(),
            diagnostics.OrderBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Location, StringComparer.Ordinal)
                .ToImmutableArray(),
            trustedStaleFiles.ToImmutable(),
            snapshot?.ReservedInputSet,
            snapshot?.Manifest.Observation);
    }

    private static string ResolveMirrorPath(ProjectContext project, string logicalPath)
    {
        var target = Path.GetFullPath(
            logicalPath.Replace('/', Path.DirectorySeparatorChar),
            project.SchemaDirectory);
        var relative = Path.GetRelativePath(project.SchemaDirectory, target);
        if (Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"System import path '{logicalPath}' escapes schemaDir.");
        }
        return target;
    }

    private static string Relative(ProjectContext project, string path) =>
        Path.GetRelativePath(project.RootDirectory, path).Replace('\\', '/');

    private static string AppendImportChains(
        ProjectContext project,
        ISystemProtoCatalog catalog,
        string target,
        string message)
    {
        try
        {
            if (!Directory.Exists(project.SchemaDirectory)
                || (File.GetAttributes(project.SchemaDirectory) & FileAttributes.ReparsePoint) != 0)
                return message;
            var importsBySource = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var systemProto in catalog.Files)
                importsBySource[systemProto.LogicalPath] = ParseImports(systemProto.CanonicalBytes.Span);
            var pending = new Stack<string>();
            pending.Push(project.SchemaDirectory);
            while (pending.Count != 0)
            {
                var directory = pending.Pop();
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        return message;
                    var logicalDirectory = Path.GetRelativePath(project.SchemaDirectory, child).Replace('\\', '/') + "/";
                    if (!SystemProtoCatalogPaths.IsReservedNamespace(logicalDirectory))
                        pending.Push(child);
                }
                foreach (var file in Directory.EnumerateFiles(directory, "*.proto", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        return message;
                    var logicalPath = Path.GetRelativePath(project.SchemaDirectory, file).Replace('\\', '/');
                    if (!SystemProtoCatalogPaths.IsReservedNamespace(logicalPath))
                        importsBySource[logicalPath] = ParseImports(File.ReadAllBytes(file));
                }
            }

            var chains = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var source in importsBySource.Keys.Order(StringComparer.Ordinal))
            {
                var chain = new List<string>();
                if (TryFindImportChain(source, target, importsBySource, new HashSet<string>(StringComparer.Ordinal), chain))
                    chains.Add($"Import chain: {string.Join(" -> ", chain)}");
            }
            return chains.Count == 0 ? message : $"{message}\n{string.Join('\n', chains)}";
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or DecoderFallbackException)
        {
            return message;
        }
    }

    private static bool TryFindImportChain(
        string source,
        string target,
        IReadOnlyDictionary<string, IReadOnlyList<string>> importsBySource,
        ISet<string> visiting,
        ICollection<string> result)
    {
        if (!visiting.Add(source) || !importsBySource.TryGetValue(source, out var imports))
            return false;
        try
        {
            foreach (var imported in imports.Order(StringComparer.Ordinal))
            {
                if (string.Equals(imported, target, StringComparison.Ordinal))
                {
                    result.Add(source);
                    result.Add(target);
                    return true;
                }
                var child = new List<string>();
                if (!TryFindImportChain(imported, target, importsBySource, visiting, child))
                    continue;
                result.Add(source);
                foreach (var item in child)
                    result.Add(item);
                return true;
            }
            return false;
        }
        finally
        {
            visiting.Remove(source);
        }
    }

    private static IReadOnlyList<string> ParseImports(ReadOnlySpan<byte> bytes)
    {
        var source = StripProtoComments(Encoding.UTF8.GetString(bytes));
        return ImportStatementPattern.Matches(source)
            .Select(static match => match.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string StripProtoComments(string source)
    {
        var result = new StringBuilder(source.Length);
        var inString = false;
        var delimiter = '\0';
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (inString)
            {
                result.Append(character);
                if (character == '\\' && index + 1 < source.Length)
                    result.Append(source[++index]);
                else if (character == delimiter)
                    inString = false;
                continue;
            }
            if (character is '\"' or '\'')
            {
                inString = true;
                delimiter = character;
                result.Append(character);
                continue;
            }
            if (character == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index += 2;
                while (index < source.Length && source[index] is not ('\r' or '\n'))
                    index++;
                if (index < source.Length)
                    result.Append(source[index]);
                continue;
            }
            if (character == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/'))
                    index++;
                index++;
                continue;
            }
            result.Append(character);
        }
        return result.ToString();
    }

    private static string RequireHash(string? value, string name)
    {
        if (value is null
            || value.Length != 64
            || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"System-import ownership hash '{name}' is invalid.");
        }
        return value.ToLowerInvariant();
    }

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static Diagnostic Blocker(string code, string location, string message) =>
        new(code, DiagnosticSeverity.Blocker, location, message);

    private sealed record OwnershipManifest(
        string CatalogHash,
        ImmutableDictionary<string, string> Files);

    private sealed record FrozenManifest(PathObservation Observation, byte[]? Content);

    private sealed record FrozenReservedFile(string AbsolutePath, byte[] Content, string Sha256);

    private sealed record MirrorSnapshot(
        FrozenManifest Manifest,
        ImmutableDictionary<string, FrozenReservedFile> ReservedFiles,
        InputSetObservation ReservedInputSet);
}
