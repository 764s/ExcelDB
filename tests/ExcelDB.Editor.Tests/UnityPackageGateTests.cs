using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExcelDb.Editor.Tests;

public sealed class UnityPackageGateTests
{
    [Fact]
    public void EditorUpmManifestAndAssemblyDefinitionAreSelfConsistent()
    {
        var root = FindRepositoryRoot();
        var source = Path.Combine(root, "src", "ExcelDB.Editor.Unity");
        using var package = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source, "package.json")));
        var manifest = package.RootElement;

        Assert.Equal("com.exceldb.editor", manifest.GetProperty("name").GetString());
        Assert.Equal("2022.3", manifest.GetProperty("unity").GetString());
        Assert.Equal("0.1.0", manifest.GetProperty("dependencies").GetProperty("com.exceldb.runtime").GetString());
        Assert.Single(manifest.GetProperty("dependencies").EnumerateObject());

        using var asmdef = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source, "Editor", "ExcelDb.Editor.Unity.asmdef")));
        Assert.Equal("ExcelDb.Editor.Unity", asmdef.RootElement.GetProperty("name").GetString());
        Assert.Equal(["Editor"], asmdef.RootElement.GetProperty("includePlatforms").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Empty(asmdef.RootElement.GetProperty("references").EnumerateArray());
    }

    [Fact]
    public void UnitySourcesStayInsideEditorAndWithinUnity2022LanguageSurface()
    {
        var root = FindRepositoryRoot();
        var editor = Path.Combine(root, "src", "ExcelDB.Editor.Unity", "Editor");
        var sources = Directory.EnumerateFiles(editor, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(sources);
        var combined = string.Join("\n", sources.Select(File.ReadAllText));

        Assert.All(sources, path => Assert.StartsWith("#if UNITY_EDITOR", File.ReadAllText(path).TrimStart('\uFEFF', '\r', '\n', ' ', '\t')));
        Assert.DoesNotMatch(new Regex(@"\bnamespace\s+[A-Za-z0-9_.]+\s*;", RegexOptions.CultureInvariant), combined);
        Assert.DoesNotMatch(new Regex(@"\brecord\b", RegexOptions.CultureInvariant), combined);
        Assert.DoesNotContain("EditorPrefs", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(" required ", combined, StringComparison.Ordinal);

        foreach (var requiredProjection in new[]
                 {
                     "InitializeProject", "SearchRowReferences", "InspectWorkbook", "RecentReports",
                     "Conflicts", "GuardDirty", "GetClientFreshness", "BuildPlan",
                     "ReadProjectSettingsMetadata", "PendingChanges", "PrepareNextPlaySource", "SwitchPlaySource",
                 })
            Assert.Contains(requiredProjection, combined, StringComparison.Ordinal);
        foreach (var trigger in new[] { "assembly-reload", "play", "quit", "unmount" })
            Assert.Contains(trigger, combined, StringComparison.Ordinal);

        var menus = File.ReadAllText(Path.Combine(editor, "ExcelDbMenus.cs"));
        foreach (var menu in new[]
                 {
                     "ExcelDB/Browser", "ExcelDB/Refresh", "ExcelDB/Save All", "ExcelDB/Generate…",
                     "ExcelDB/Normalize…", "ExcelDB/Data Prepare…", "ExcelDB/Convert",
                     "ExcelDB/Open Report", "ExcelDB/Mount Workbook…", "ExcelDB/Settings…",
                 })
            Assert.Contains("\"" + menu + "\"", menus, StringComparison.Ordinal);
        Assert.DoesNotContain("server", menus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildProducesCompleteInstallableUpmDirectory()
    {
        var root = FindRepositoryRoot();
        var source = Path.Combine(root, "src", "ExcelDB.Editor.Unity");
        var artifact = Path.Combine(root, "artifacts", "upm", "com.exceldb.editor");

        Assert.True(File.Exists(Path.Combine(artifact, "package.json")), "The Editor.Unity build must materialize the UPM package directory.");
        Assert.True(File.Exists(Path.Combine(artifact, "Editor", "ExcelDb.Editor.Unity.asmdef")));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(source, "package.json")),
            File.ReadAllBytes(Path.Combine(artifact, "package.json")));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(source, "Editor", "ExcelDb.Editor.Unity.asmdef")),
            File.ReadAllBytes(Path.Combine(artifact, "Editor", "ExcelDb.Editor.Unity.asmdef")));
        var expected = Directory.EnumerateFiles(Path.Combine(source, "Editor"), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Path.Combine(source, "Editor"), path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actual = Directory.EnumerateFiles(Path.Combine(artifact, "Editor"), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Path.Combine(artifact, "Editor"), path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
        Assert.All(expected, relative => Assert.Equal(
            File.ReadAllBytes(Path.Combine(source, "Editor", relative.Replace('/', Path.DirectorySeparatorChar))),
            File.ReadAllBytes(Path.Combine(artifact, "Editor", relative.Replace('/', Path.DirectorySeparatorChar)))));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ExcelDB.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate ExcelDB.slnx from the test output directory.");
    }
}
