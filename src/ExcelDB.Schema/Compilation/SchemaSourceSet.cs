using System.Collections.ObjectModel;

namespace ExcelDb.Schema.Compilation;

/// <summary>
/// A deterministic snapshot of the protocol-buffer inputs below one schema root.
/// </summary>
public sealed class SchemaSourceSet
{
    private SchemaSourceSet(string rootDirectory, IReadOnlyList<SchemaSourceFile> files)
    {
        RootDirectory = rootDirectory;
        Files = files;
    }

    /// <summary>The absolute schema root used as protoc's working directory.</summary>
    public string RootDirectory { get; }

    /// <summary>Discovered inputs, ordered by <see cref="SchemaSourceFile.LogicalPath"/> using ordinal comparison.</summary>
    public IReadOnlyList<SchemaSourceFile> Files { get; }

    /// <summary>
    /// Recursively discovers <c>*.proto</c> files. An empty directory produces an
    /// empty source set so the caller can apply operation-specific policy.
    /// </summary>
    public static SchemaSourceSet Discover(string schemaDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaDirectory);

        var rootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(schemaDirectory));
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"Schema root does not exist: '{rootDirectory}'.");
        }

        var discovered = new List<SchemaSourceFile>();
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

                RejectReservedSystemPath(logicalPath);
                discovered.Add(new SchemaSourceFile(logicalPath, fullPath, File.ReadAllBytes(fullPath)));
            }
        }

        discovered.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.LogicalPath, right.LogicalPath));

        return new SchemaSourceSet(
            rootDirectory,
            new ReadOnlyCollection<SchemaSourceFile>(discovered));
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

    private static void RejectReservedSystemPath(string logicalPath)
    {
        if (string.Equals(logicalPath, "exceldb/options.proto", StringComparison.OrdinalIgnoreCase)
            || logicalPath.StartsWith("google/protobuf/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Schema source path is reserved for package-local system imports: '{logicalPath}'.");
        }
    }
}

/// <summary>A physical schema input and its stable protoc-facing path.</summary>
public sealed class SchemaSourceFile
{
    internal SchemaSourceFile(string logicalPath, string fullPath, byte[] content)
    {
        LogicalPath = logicalPath;
        FullPath = fullPath;
        Content = content;
    }

    /// <summary>The schema-root-relative path with '/' separators.</summary>
    public string LogicalPath { get; }

    /// <summary>The absolute physical path discovered below the schema root.</summary>
    public string FullPath { get; }

    /// <summary>The immutable bytes captured during discovery.</summary>
    internal ReadOnlyMemory<byte> Content { get; }
}
