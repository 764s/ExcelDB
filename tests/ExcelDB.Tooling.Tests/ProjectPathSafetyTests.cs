using ExcelDb.Tooling.Project;
using Xunit;

namespace ExcelDb.Tooling.Tests;

public sealed class ProjectPathSafetyTests
{
    [Fact]
    public void ProjectLoader_rejects_an_internal_directory_reparse_point_when_supported()
    {
        var container = TestContainer();
        var projectRoot = Path.Combine(container, "project");
        var outside = Path.Combine(container, "outside");
        var link = Path.Combine(projectRoot, ".exceldb");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "project.json"), ExcelDbProject.Default.ToCanonicalJson());
        try
        {
            if (!TryCreateDirectoryLink(link, outside))
                return;

            var exception = Assert.Throws<InvalidDataException>(() =>
                ExcelDbProject.Load(Path.Combine(link, "project.json")));

            Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.GetAttributes(Path.Combine(outside, "project.json")).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            DeleteLinkIfPresent(link);
            DeleteTree(container);
        }
    }

    [Fact]
    public void ProjectContext_rejects_a_contained_artifact_ancestor_reparse_point_when_supported()
    {
        var container = TestContainer();
        var projectRoot = Path.Combine(container, "project");
        var internalDirectory = Path.Combine(projectRoot, ".exceldb");
        var outside = Path.Combine(container, "outside");
        var link = Path.Combine(projectRoot, "Artifacts");
        Directory.CreateDirectory(internalDirectory);
        Directory.CreateDirectory(Path.Combine(outside, "Schema"));
        var project = ExcelDbProject.Default with { SchemaDir = "Artifacts/Schema" };
        var projectFile = Path.Combine(internalDirectory, "project.json");
        File.WriteAllBytes(projectFile, project.ToCanonicalJson());
        try
        {
            if (!TryCreateDirectoryLink(link, outside))
                return;

            var loaded = ExcelDbProject.Load(projectFile);
            var exception = Assert.Throws<InvalidDataException>(() => loaded.Resolve(projectFile));

            Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteLinkIfPresent(link);
            DeleteTree(container);
        }
    }

    [Fact]
    public void ProjectLoader_rejects_a_configuration_file_symlink_when_supported()
    {
        var container = TestContainer();
        var internalDirectory = Path.Combine(container, "project", ".exceldb");
        var outsideFile = Path.Combine(container, "outside-project.json");
        var projectFile = Path.Combine(internalDirectory, "project.json");
        Directory.CreateDirectory(internalDirectory);
        File.WriteAllBytes(outsideFile, ExcelDbProject.Default.ToCanonicalJson());
        try
        {
            try
            {
                File.CreateSymbolicLink(projectFile, outsideFile);
            }
            catch (Exception creationException) when (creationException is UnauthorizedAccessException
                                               or PlatformNotSupportedException
                                               or IOException)
            {
                return;
            }

            var failure = Assert.Throws<InvalidDataException>(() => ExcelDbProject.Load(projectFile));

            Assert.Contains("reparse point", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(projectFile)
                && File.GetAttributes(projectFile).HasFlag(FileAttributes.ReparsePoint))
            {
                File.Delete(projectFile);
            }
            DeleteTree(container);
        }
    }

    private static string TestContainer()
    {
        var path = Path.Combine(Path.GetTempPath(), "exceldb-project-path-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                           or PlatformNotSupportedException
                                           or IOException)
        {
            return false;
        }
    }

    private static void DeleteLinkIfPresent(string link)
    {
        if (Directory.Exists(link)
            && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(link);
        }
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(entry, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
