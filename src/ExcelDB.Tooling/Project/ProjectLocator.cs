namespace ExcelDb.Tooling.Project;

public enum ProjectLocationKind : byte
{
    None = 0,
    ProjectV2 = 1,
    LegacyV1 = 2,
    Conflicted = 3,
}

public sealed record ProjectLocation(ProjectLocationKind Kind, string? ProjectFilePath, string RootDirectory);

public static class ProjectLocator
{
    public static string? FindNearest(string startDirectory)
    {
        var location = LocateNearest(startDirectory);
        return location.Kind == ProjectLocationKind.ProjectV2 ? location.ProjectFilePath : null;
    }

    public static string? FindNearestLegacy(string startDirectory)
    {
        var location = LocateNearest(startDirectory);
        return location.Kind == ProjectLocationKind.LegacyV1 ? location.ProjectFilePath : null;
    }

    public static ProjectLocation LocateNearest(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ExcelDbProject.FileName);
            var legacy = Path.Combine(current.FullName, ExcelDbProject.LegacyFileName);
            if (File.Exists(candidate) && File.Exists(legacy))
                return new ProjectLocation(ProjectLocationKind.Conflicted, candidate, current.FullName);
            if (File.Exists(candidate))
                return new ProjectLocation(ProjectLocationKind.ProjectV2, candidate, current.FullName);

            if (File.Exists(legacy))
                return new ProjectLocation(ProjectLocationKind.LegacyV1, legacy, current.FullName);
            current = current.Parent;
        }

        return new ProjectLocation(ProjectLocationKind.None, null, Path.GetFullPath(startDirectory));
    }
}
