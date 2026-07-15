using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace ExcelDb.Schema.Compilation;

/// <summary>
/// The executable-owned, canonical set of protocol-buffer system imports. Project
/// mirrors are never an authority: compilers and repair planners consume this catalog.
/// </summary>
public interface ISystemProtoCatalog
{
    /// <summary>Canonical files ordered by logical path using ordinal comparison.</summary>
    IReadOnlyList<SystemProtoFile> Files { get; }

    /// <summary>A deterministic SHA-256 identity for all logical paths and file hashes.</summary>
    string CatalogHash { get; }

    /// <summary>Looks up an exact, case-sensitive protoc logical path.</summary>
    bool TryGetFile(
        string logicalPath,
        [NotNullWhen(true)] out SystemProtoFile? file);
}

/// <summary>An immutable canonical system proto owned by the executable.</summary>
public sealed class SystemProtoFile
{
    private readonly byte[] _canonicalBytes;

    public SystemProtoFile(string logicalPath, ReadOnlySpan<byte> canonicalBytes)
    {
        LogicalPath = SystemProtoCatalogPaths.RequireCanonicalLogicalPath(logicalPath);
        if (!SystemProtoCatalogPaths.IsReservedNamespace(LogicalPath))
        {
            throw new ArgumentException(
                $"System proto path is outside the reserved namespaces: '{LogicalPath}'.",
                nameof(logicalPath));
        }

        _canonicalBytes = canonicalBytes.ToArray();
        Sha256 = SystemProtoCatalogHash.Sha256(_canonicalBytes);
    }

    /// <summary>The protoc-facing path, always using forward slashes.</summary>
    public string LogicalPath { get; }

    /// <summary>The canonical bytes captured from the executable package.</summary>
    public ReadOnlyMemory<byte> CanonicalBytes => _canonicalBytes.ToArray();

    /// <summary>Lower-case SHA-256 of <see cref="CanonicalBytes"/>.</summary>
    public string Sha256 { get; }
}

/// <summary>
/// Loads the system catalog shipped in <c>schema-system</c> below a package root.
/// The resulting snapshot owns its bytes and no later disk changes can affect it.
/// </summary>
public sealed class PackageSystemProtoCatalog : ISystemProtoCatalog
{
    private const string SystemImportsRelativePath = "schema-system";

    private static readonly Lazy<PackageSystemProtoCatalog> DefaultCatalog =
        new(static () => new PackageSystemProtoCatalog(AppContext.BaseDirectory));

    private readonly IReadOnlyDictionary<string, SystemProtoFile> _byLogicalPath;

    public PackageSystemProtoCatalog()
        : this(AppContext.BaseDirectory)
    {
    }

    public PackageSystemProtoCatalog(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        if (!Path.IsPathFullyQualified(packageRoot))
        {
            throw new ArgumentException(
                "The system-proto package root must be absolute.",
                nameof(packageRoot));
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        var systemDirectory = Path.Combine(normalizedRoot, SystemImportsRelativePath);
        var files = LoadFiles(systemDirectory);
        if (files.Count == 0)
        {
            throw new InvalidDataException(
                $"System proto catalog is empty at package path '{SystemImportsRelativePath}'.");
        }

        RequireFile(files, "exceldb/options.proto");
        RequireFile(files, "google/protobuf/descriptor.proto");

        Files = new ReadOnlyCollection<SystemProtoFile>(files);
        _byLogicalPath = new ReadOnlyDictionary<string, SystemProtoFile>(
            files.ToDictionary(static file => file.LogicalPath, StringComparer.Ordinal));
        CatalogHash = SystemProtoCatalogHash.ComputeCatalogHash(files);
    }

    /// <summary>The package catalog rooted at <see cref="AppContext.BaseDirectory"/>.</summary>
    public static PackageSystemProtoCatalog Default => DefaultCatalog.Value;

    public IReadOnlyList<SystemProtoFile> Files { get; }

    public string CatalogHash { get; }

    public bool TryGetFile(
        string logicalPath,
        [NotNullWhen(true)] out SystemProtoFile? file)
    {
        ArgumentNullException.ThrowIfNull(logicalPath);
        return _byLogicalPath.TryGetValue(logicalPath, out file);
    }

    private static List<SystemProtoFile> LoadFiles(string systemDirectory)
    {
        if (!Directory.Exists(systemDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Embedded schema imports are missing at package path '{SystemImportsRelativePath}'.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(systemDirectory));
        var files = new List<SystemProtoFile>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(currentDirectory))
            {
                RejectReparsePoint(childDirectory, root);
                pendingDirectories.Push(RequireContainedPath(root, childDirectory));
            }

            foreach (var candidate in Directory.EnumerateFiles(currentDirectory, "*.proto"))
            {
                RejectReparsePoint(candidate, root);
                var fullPath = RequireContainedPath(root, candidate);
                var logicalPath = Path.GetRelativePath(root, fullPath)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
                    logicalPath = logicalPath.Replace(Path.AltDirectorySeparatorChar, '/');

                files.Add(new SystemProtoFile(logicalPath, File.ReadAllBytes(fullPath)));
            }
        }

        files.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.LogicalPath, right.LogicalPath));

        var duplicate = files
            .GroupBy(static file => file.LogicalPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"System proto catalog contains a case-insensitive path collision: '{duplicate.Key}'.");
        }

        return files;
    }

    private static void RequireFile(IEnumerable<SystemProtoFile> files, string logicalPath)
    {
        if (!files.Any(file => string.Equals(file.LogicalPath, logicalPath, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"System proto catalog is missing required file '{logicalPath}'.");
        }
    }

    private static string RequireContainedPath(string root, string candidate)
    {
        var fullPath = Path.GetFullPath(candidate);
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (SystemProtoCatalogPaths.EscapesRoot(relativePath))
        {
            throw new InvalidDataException(
                $"System proto package path escapes its root: '{relativePath}'.");
        }

        return fullPath;
    }

    private static void RejectReparsePoint(string path, string root)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            return;

        var relativePath = Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        throw new InvalidDataException(
            $"System proto package traversal through a reparse point is not allowed: '{relativePath}'.");
    }
}

/// <summary>Path rules shared by source discovery and system-mirror tooling.</summary>
public static class SystemProtoCatalogPaths
{
    public static bool IsReservedNamespace(string logicalPath)
    {
        ArgumentNullException.ThrowIfNull(logicalPath);
        return logicalPath.StartsWith("exceldb/", StringComparison.OrdinalIgnoreCase)
            || logicalPath.StartsWith("google/protobuf/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string RequireCanonicalLogicalPath(string logicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        if (logicalPath.IndexOf('\\') >= 0
            || Path.IsPathFullyQualified(logicalPath)
            || logicalPath.StartsWith("/", StringComparison.Ordinal)
            || logicalPath.EndsWith("/", StringComparison.Ordinal)
            || logicalPath.Split('/').Any(static segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException(
                $"System proto logical path is not canonical: '{logicalPath}'.",
                nameof(logicalPath));
        }

        return logicalPath;
    }

    internal static bool EscapesRoot(string relativePath) =>
        Path.IsPathFullyQualified(relativePath)
        || string.Equals(relativePath, "..", StringComparison.Ordinal)
        || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
}

internal static class SystemProtoCatalogHash
{
    public static string ComputeCatalogHash(IEnumerable<SystemProtoFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(static file => file.LogicalPath, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.LogicalPath));
            hash.AppendData([0]);
            hash.AppendData(Encoding.ASCII.GetBytes(file.Sha256));
            hash.AppendData([(byte)'\n']);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
