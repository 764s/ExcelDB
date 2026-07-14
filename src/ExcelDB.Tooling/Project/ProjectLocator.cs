namespace ExcelDb.Tooling.Project;

public static class ProjectLocator
{
    public static string? FindNearest(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ExcelDbProject.FileName);
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        return null;
    }
}
