namespace ExcelDb.Tooling.Project;

/// <summary>
/// Rejects symbolic-link, junction, and reparse-point aliases before project-owned code reads,
/// writes, or deletes through a configured path.
/// </summary>
public static class ProjectPathSafety
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static void EnsureProjectRootAndInternalDirectoryArePlain(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        EnsureDeclaredRootIsPlain(root, "Project root");
        EnsureContainedPathIsPlain(
            root,
            Path.Combine(root, ".exceldb"),
            "Project internal directory",
            targetMustBeDirectory: true);
    }

    public static void EnsureDeclaredRootIsPlain(string declaredRoot, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(declaredRoot));
        var filesystemRoot = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException($"{purpose} has no filesystem root: '{declaredRoot}'.");
        var current = filesystemRoot;
        var segments = Path.GetRelativePath(filesystemRoot, fullPath).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!File.Exists(current) && !Directory.Exists(current))
                return;
            EnsureEntryIsPlain(current, purpose, mustBeDirectory: true);
        }
    }

    public static void EnsureContainedPathIsPlain(
        string declaredRoot,
        string candidate,
        string purpose,
        bool targetMustBeDirectory = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(declaredRoot));
        var fullPath = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison))
        {
            throw new InvalidDataException($"{purpose} escapes its declared root: '{candidate}'.");
        }

        if (File.Exists(root) || Directory.Exists(root))
            EnsureEntryIsPlain(root, purpose, mustBeDirectory: true);
        else
            return;
        if (relative == ".")
            return;

        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            if (!File.Exists(current) && !Directory.Exists(current))
                return;
            EnsureEntryIsPlain(
                current,
                purpose,
                mustBeDirectory: index != segments.Length - 1 || targetMustBeDirectory);
        }
    }

    public static void EnsurePlainFile(string path, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException($"{purpose} has no parent directory: '{path}'.");
        EnsureDeclaredRootIsPlain(parent, $"{purpose} parent");
        if (!File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new InvalidDataException($"{purpose} is not a regular file: '{fullPath}'.");
        EnsureEntryIsPlain(fullPath, purpose, mustBeDirectory: false);
    }

    private static void EnsureEntryIsPlain(string path, string purpose, bool mustBeDirectory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{purpose} crosses a symbolic link, junction, or reparse point: '{path}'.");
        }
        if (mustBeDirectory && (attributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException($"{purpose} requires a directory but found a file: '{path}'.");
    }
}
