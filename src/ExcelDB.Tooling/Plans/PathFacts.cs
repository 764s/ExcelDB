using System.Security.Cryptography;

namespace ExcelDb.Tooling.Plans;

internal static class PathFacts
{
    public static PathObservation Observe(string projectRoot, string relativePath)
    {
        var fullPath = ResolveContained(projectRoot, relativePath);
        if (File.Exists(fullPath))
        {
            using var stream = File.OpenRead(fullPath);
            return new PathObservation(
                Normalize(relativePath),
                ObservedPathKind.File,
                stream.Length,
                Convert.ToHexStringLower(SHA256.HashData(stream)));
        }

        return new PathObservation(
            Normalize(relativePath),
            Directory.Exists(fullPath) ? ObservedPathKind.Directory : ObservedPathKind.Missing);
    }

    public static bool Matches(string projectRoot, PathObservation observation) =>
        Observe(projectRoot, observation.RelativePath) == observation;

    public static string ResolveContained(string projectRoot, string relativePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var fullPath = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), root);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Mutation path escapes the project root: '{relativePath}'.");
        }

        return fullPath;
    }

    public static string Normalize(string relativePath)
    {
        if (relativePath == ".")
            return relativePath;
        return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
