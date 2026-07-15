using System.Collections.ObjectModel;

namespace ExcelDb.Schema.Compilation;

/// <summary>
/// A deterministic snapshot of the protocol-buffer inputs below one schema root.
/// </summary>
public sealed class SchemaSourceSet
{
    private SchemaSourceSet(
        string rootDirectory,
        IReadOnlyList<SchemaSourceFile> files,
        IReadOnlyList<SystemProtoMirrorFile> excludedSystemProtoMirrors)
    {
        RootDirectory = rootDirectory;
        Files = files;
        ExcludedSystemProtoMirrors = excludedSystemProtoMirrors;
    }

    /// <summary>The absolute schema root used as protoc's working directory.</summary>
    public string RootDirectory { get; }

    /// <summary>Discovered inputs, ordered by <see cref="SchemaSourceFile.LogicalPath"/> using ordinal comparison.</summary>
    public IReadOnlyList<SchemaSourceFile> Files { get; }

    /// <summary>
    /// Canonical executable-owned mirrors found below the schema root. They are
    /// reported for UI inspection but are never included in <see cref="Files"/>.
    /// </summary>
    public IReadOnlyList<SystemProtoMirrorFile> ExcludedSystemProtoMirrors { get; }

    /// <summary>
    /// Recursively discovers <c>*.proto</c> files. An empty directory produces an
    /// empty source set so the caller can apply operation-specific policy.
    /// </summary>
    public static SchemaSourceSet Discover(string schemaDirectory) =>
        Discover(schemaDirectory, PackageSystemProtoCatalog.Default);

    /// <summary>
    /// Discovers business sources while validating any physical files in the
    /// executable-owned import namespaces against one explicit catalog.
    /// </summary>
    public static SchemaSourceSet Discover(
        string schemaDirectory,
        ISystemProtoCatalog systemProtoCatalog) =>
        Discover(schemaDirectory, systemProtoCatalog, new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// Discovers business sources while also allowing explicitly ownership-proven
    /// stale mirror bytes to be excluded long enough for a repair plan to replace
    /// them. The stale bytes are never passed to protoc.
    /// </summary>
    public static SchemaSourceSet Discover(
        string schemaDirectory,
        ISystemProtoCatalog systemProtoCatalog,
        IReadOnlyDictionary<string, string> trustedStaleSystemProtoHashes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaDirectory);
        ArgumentNullException.ThrowIfNull(systemProtoCatalog);
        ArgumentNullException.ThrowIfNull(trustedStaleSystemProtoHashes);

        var rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(schemaDirectory));
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"Schema root does not exist: '{rootDirectory}'.");
        }
        RejectReparsePoint(rootDirectory, rootDirectory);

        var discovered = new List<SchemaSourceFile>();
        var excludedSystemProtoMirrors = new List<SystemProtoMirrorFile>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootDirectory);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();

            foreach (var childDirectory in Directory.EnumerateDirectories(currentDirectory))
            {
                var fullPath = RequireContainedPath(rootDirectory, childDirectory);
                RejectReparsePoint(fullPath, rootDirectory);
                pendingDirectories.Push(fullPath);
            }

            foreach (var candidate in Directory.EnumerateFiles(currentDirectory))
            {
                if (!string.Equals(Path.GetExtension(candidate), ".proto", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fullPath = RequireContainedPath(rootDirectory, candidate);
                RejectReparsePoint(fullPath, rootDirectory);

                var relativePath = Path.GetRelativePath(rootDirectory, fullPath);
                if (EscapesRoot(relativePath))
                {
                    throw new InvalidDataException(
                        $"Schema source escapes the schema root: '{relativePath}'.");
                }

                var logicalPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
                {
                    logicalPath = logicalPath.Replace(Path.AltDirectorySeparatorChar, '/');
                }

                var content = File.ReadAllBytes(fullPath);
                if (SystemProtoCatalogPaths.IsReservedNamespace(logicalPath))
                {
                    ValidateAndExcludeSystemMirror(
                        logicalPath,
                        fullPath,
                        content,
                        systemProtoCatalog,
                        trustedStaleSystemProtoHashes,
                        excludedSystemProtoMirrors);
                    continue;
                }

                discovered.Add(new SchemaSourceFile(logicalPath, fullPath, content));
            }
        }

        discovered.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.LogicalPath, right.LogicalPath));
        excludedSystemProtoMirrors.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.LogicalPath, right.LogicalPath));

        return new SchemaSourceSet(
            rootDirectory,
            new ReadOnlyCollection<SchemaSourceFile>(discovered),
            new ReadOnlyCollection<SystemProtoMirrorFile>(excludedSystemProtoMirrors));
    }

    private static string RequireContainedPath(string rootDirectory, string candidate)
    {
        var fullPath = Path.GetFullPath(candidate);
        var relativePath = Path.GetRelativePath(rootDirectory, fullPath);
        if (EscapesRoot(relativePath))
        {
            throw new InvalidDataException(
                $"Schema input path escapes the schema root: '{relativePath}'.");
        }

        return fullPath;
    }

    private static bool EscapesRoot(string relativePath)
    {
        return Path.IsPathFullyQualified(relativePath)
            || string.Equals(relativePath, "..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void RejectReparsePoint(string path, string rootDirectory)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
        {
            return;
        }

        var relativePath = Path.GetRelativePath(rootDirectory, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        throw new InvalidDataException(
            $"Schema input traversal through a reparse point is not allowed: '{relativePath}'.");
    }

    private static void ValidateAndExcludeSystemMirror(
        string logicalPath,
        string fullPath,
        byte[] content,
        ISystemProtoCatalog systemProtoCatalog,
        IReadOnlyDictionary<string, string> trustedStaleSystemProtoHashes,
        ICollection<SystemProtoMirrorFile> excludedSystemProtoMirrors)
    {
        var actualSha256 = SystemProtoCatalogHash.Sha256(content);
        if (!systemProtoCatalog.TryGetFile(logicalPath, out var catalogFile))
        {
            if (IsTrustedStale(logicalPath, actualSha256, trustedStaleSystemProtoHashes))
            {
                excludedSystemProtoMirrors.Add(new SystemProtoMirrorFile(logicalPath, fullPath, actualSha256));
                return;
            }
            throw SystemProtoMirrorException.UnknownReservedPath(logicalPath, actualSha256);
        }

        if (!content.AsSpan().SequenceEqual(catalogFile.CanonicalBytes.Span))
        {
            if (IsTrustedStale(logicalPath, actualSha256, trustedStaleSystemProtoHashes))
            {
                excludedSystemProtoMirrors.Add(new SystemProtoMirrorFile(logicalPath, fullPath, actualSha256));
                return;
            }
            throw SystemProtoMirrorException.Modified(
                logicalPath,
                catalogFile.Sha256,
                actualSha256);
        }

        excludedSystemProtoMirrors.Add(new SystemProtoMirrorFile(
            logicalPath,
            fullPath,
            actualSha256));
    }

    private static bool IsTrustedStale(
        string logicalPath,
        string actualSha256,
        IReadOnlyDictionary<string, string> trustedStaleSystemProtoHashes) =>
        trustedStaleSystemProtoHashes.TryGetValue(logicalPath, out var trustedHash)
        && string.Equals(trustedHash, actualSha256, StringComparison.Ordinal);
}

/// <summary>A physical schema input and its stable protoc-facing path.</summary>
public sealed class SchemaSourceFile
{
    private readonly string _sha256;

    internal SchemaSourceFile(string logicalPath, string fullPath, byte[] content)
    {
        LogicalPath = logicalPath;
        FullPath = fullPath;
        Content = content;
        _sha256 = SystemProtoCatalogHash.Sha256(content);
    }

    /// <summary>The schema-root-relative path with '/' separators.</summary>
    public string LogicalPath { get; }

    /// <summary>The absolute physical path discovered below the schema root.</summary>
    public string FullPath { get; }

    /// <summary>Length of the immutable bytes captured during discovery.</summary>
    public long Length => Content.Length;

    /// <summary>Lower-case SHA-256 of the immutable bytes captured during discovery.</summary>
    public string Sha256 => _sha256;

    /// <summary>The immutable bytes captured during discovery.</summary>
    internal ReadOnlyMemory<byte> Content { get; }
}

/// <summary>A canonical project mirror excluded from all business schema inputs.</summary>
public sealed record SystemProtoMirrorFile(
    string LogicalPath,
    string FullPath,
    string Sha256);

public enum SystemProtoMirrorProblem
{
    Modified,
    UnknownReservedPath,
}

/// <summary>
/// A blocker raised when a project file occupies an executable-owned import path
/// but cannot be proven identical to the canonical catalog entry.
/// </summary>
public sealed class SystemProtoMirrorException : IOException
{
    private SystemProtoMirrorException(
        SystemProtoMirrorProblem problem,
        string logicalPath,
        string? expectedSha256,
        string actualSha256,
        string message)
        : base(message)
    {
        Problem = problem;
        LogicalPath = logicalPath;
        ExpectedSha256 = expectedSha256;
        ActualSha256 = actualSha256;
    }

    public SystemProtoMirrorProblem Problem { get; }

    public string LogicalPath { get; }

    public string? ExpectedSha256 { get; }

    public string ActualSha256 { get; }

    internal static SystemProtoMirrorException Modified(
        string logicalPath,
        string expectedSha256,
        string actualSha256) =>
        new(
            SystemProtoMirrorProblem.Modified,
            logicalPath,
            expectedSha256,
            actualSha256,
            $"Reserved system proto mirror '{logicalPath}' was modified "
            + $"(expected SHA-256 {expectedSha256}, actual {actualSha256}). "
            + "Explicitly repair the executable-owned mirror before writing schema structure.");

    internal static SystemProtoMirrorException UnknownReservedPath(
        string logicalPath,
        string actualSha256) =>
        new(
            SystemProtoMirrorProblem.UnknownReservedPath,
            logicalPath,
            null,
            actualSha256,
            $"Schema file '{logicalPath}' occupies a reserved system import namespace "
            + "but is not present in the executable system proto catalog.");
}
