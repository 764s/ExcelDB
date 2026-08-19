using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExcelDb.Editor.Tests;

public sealed class UnityPackageGateTests
{
    private static readonly string[] EditorBusinessAssemblies =
    [
        "ExcelDB.Authoring.dll",
        "ExcelDB.Authoring.Workbooks.dll",
        "ExcelDB.Editor.Model.dll",
        "ExcelDB.Schema.Model.dll",
        "ExcelDB.Workbooks.dll",
    ];

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
        Assert.True(asmdef.RootElement.GetProperty("overrideReferences").GetBoolean());
        Assert.Equal(
            EditorBusinessAssemblies,
            asmdef.RootElement.GetProperty("precompiledReferences").EnumerateArray().Select(item => item.GetString()!).ToArray());
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
        Assert.DoesNotMatch(
            new Regex(
                @"^\s*(?:(?:public|internal|private|protected|sealed|abstract|static|partial)\s+)*record(?:\s+(?:class|struct))?\s+[A-Za-z_]",
                RegexOptions.CultureInvariant | RegexOptions.Multiline),
            combined);
        Assert.DoesNotContain("EditorPrefs", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(" required ", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("ExcelDb.Project.json", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Project json", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildPlan(string operation, bool", combined, StringComparison.Ordinal);

        foreach (var requiredProjection in new[]
                 {
                     "InitializeProject", "SearchRowReferences", "InspectWorkbook", "RecentReports",
                     "Conflicts", "GuardDirty", "GetClientFreshness", "BuildPlan",
                     "ReadProjectSettingsMetadata", "PendingChanges", "PrepareNextPlaySource", "SwitchPlaySource",
                 })
            Assert.Contains(requiredProjection, combined, StringComparison.Ordinal);
        foreach (var trigger in new[] { "play", "quit", "unmount" })
            Assert.Contains(trigger, combined, StringComparison.Ordinal);
        foreach (var reloadInvariant in new[]
                 {
                     "CompilationPipeline.compilationStarted += OnCompilationStarted;",
                     "AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;",
                     "EditorApplication.LockReloadAssemblies();",
                     "EditorApplication.UnlockReloadAssemblies();",
                     "SynchronizeReloadLock",
                 })
            Assert.Contains(reloadInvariant, combined, StringComparison.Ordinal);

        var menus = File.ReadAllText(Path.Combine(editor, "ExcelDbMenus.cs"));
        foreach (var menu in new[]
                 {
                     "ExcelDB/Browser", "ExcelDB/Refresh", "ExcelDB/Save All", "ExcelDB/Regenerate…",
                     "ExcelDB/Normalize…", "ExcelDB/Data Prepare…", "ExcelDB/Convert",
                     "ExcelDB/Open Report", "ExcelDB/Mount Workbook…", "ExcelDB/Settings…",
                 })
            Assert.Contains("\"" + menu + "\"", menus, StringComparison.Ordinal);
        Assert.DoesNotContain("server", menus, StringComparison.OrdinalIgnoreCase);

        var settings = File.ReadAllText(Path.Combine(editor, "ExcelDbSettings.cs"));
        foreach (var field in new[]
                 {
                     "Schema Directory",
                     "Excel Directory",
                     "Generated C# Directory",
                     "Generated Bytes Directory",
                 })
            Assert.Contains("\"" + field + "\"", settings, StringComparison.Ordinal);
        foreach (var retired in new[]
                 {
                     "Generated Directory",
                     "Workbooks",
                     "Default Client Bytes",
                     "Cache Directory",
                     "values.Length != 5",
                 })
            Assert.DoesNotContain(retired, settings, StringComparison.Ordinal);

        var windows = File.ReadAllText(Path.Combine(editor, "ExcelDbWindows.cs"));
        Assert.DoesNotContain("_purge", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("_rekey", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Purge\"", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Rekey\"", windows, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedInspectorIsWiredThroughPropertyGuiUnityUndoAndAuthoringModeGate()
    {
        var root = FindRepositoryRoot();
        var editor = Path.Combine(root, "src", "ExcelDB.Editor.Unity", "Editor");
        var windows = File.ReadAllText(Path.Combine(editor, "ExcelDbWindows.cs"));
        var inspector = File.ReadAllText(Path.Combine(editor, "ExcelDbSerializedInspector.cs"));
        var authoring = File.ReadAllText(Path.Combine(editor, "ExcelDbAuthoringProperties.cs"));
        var propertyGui = File.ReadAllText(Path.Combine(editor, "ExcelDbPropertyGUI.cs"));
        var bridge = File.ReadAllText(Path.Combine(editor, "UnityEditorBridge.cs"));

        foreach (var browserWire in new[]
                 {
                     "ExcelDbSerializedInspectorSession.TryCreate(",
                     "ExcelDbPropertyGUI.Draw(",
                     "typed.ApplyModifiedProperties(",
                     "typed.PerformUndo();",
                     "typed.PerformRedo();",
                     "typed.Save()",
                     "typed.Revert();",
                     "ExcelDbEditor.AssetDatabase.IsAuthoringEnabled",
                 })
            Assert.Contains(browserWire, windows, StringComparison.Ordinal);

        foreach (var sessionWire in new[]
                 {
                     "host as IExcelDbUnityPropertyBridge",
                     "factory.Create(binding, residentAsset)",
                     "var undo = new ExcelDbUnityUndoBridge();",
                     "_undo.ApplyModifiedProperties(SerializedObject, label)",
                     "SerializedObject.MarkSaved();",
                     "SerializedObject.Update();",
                 })
            Assert.Contains(sessionWire, inspector, StringComparison.Ordinal);

        foreach (var undoWire in new[]
                 {
                     "EnsureAuthoringEnabled();",
                     "public sealed class ExcelDbUnityUndoBridge : IDisposable",
                     "Undo.undoRedoPerformed += OnUnityUndoRedo;",
                     "_history.TryMoveTo(_state.Token)",
                     "ExcelDbAuthoringPropertyFactory.MarkDirty",
                 })
            Assert.Contains(undoWire, authoring, StringComparison.Ordinal);

        Assert.Contains("public static bool Draw(", propertyGui, StringComparison.Ordinal);
        Assert.Contains("EditorPropertyKind.Reference", propertyGui, StringComparison.Ordinal);
        Assert.Contains("DrawList(", propertyGui, StringComparison.Ordinal);
        Assert.Contains("constraint.AllowedTargetTables", propertyGui, StringComparison.Ordinal);
        Assert.Contains("constraint.ResolveTarget(value)", propertyGui, StringComparison.Ordinal);
        Assert.Contains("ExcelDbRowReferencePicker.Show(", propertyGui, StringComparison.Ordinal);
        Assert.Contains("ExcelDbRowReferenceDrag.HandleDrop(dropArea, (UnityEditorBrowserRow row) =>", propertyGui, StringComparison.Ordinal);
        Assert.Contains("interface IExcelDbUnityPropertyBridge", bridge, StringComparison.Ordinal);
        Assert.Contains("readonly struct UnityEditorBrowserRow", bridge, StringComparison.Ordinal);
        Assert.Contains("public string Table { get; }", bridge, StringComparison.Ordinal);
        Assert.Contains("public string Guid { get; }", bridge, StringComparison.Ordinal);
        Assert.Contains("public string Key { get; }", bridge, StringComparison.Ordinal);
        Assert.Contains("TryResolveEditorAsset(", bridge, StringComparison.Ordinal);
        Assert.Contains("ExcelDbEditor.AssetDatabase.IsAuthoringEnabled", inspector, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProducesCompleteInstallableUpmDirectory()
    {
        var root = FindRepositoryRoot();
        var source = Path.Combine(root, "src", "ExcelDB.Editor.Unity");
        var artifact = Path.Combine(root, "artifacts", "upm", "com.exceldb.editor");
        var pluginRoot = Path.Combine(artifact, "Editor", "Plugins");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException("Could not determine the active test configuration.");

        var project = File.ReadAllText(Path.Combine(source, "ExcelDB.Editor.Unity.csproj"));
        var packagedByBuild = Regex.Matches(
                project,
                @"<UnityEditorBusinessAssembly Include=""\$\(TargetDir\)([^""]+\.dll)""\s*/>",
                RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.Equal(EditorBusinessAssemblies, packagedByBuild);

        Assert.True(File.Exists(Path.Combine(artifact, "package.json")), "The Editor.Unity build must materialize the UPM package directory.");
        Assert.True(File.Exists(Path.Combine(artifact, "Editor", "ExcelDb.Editor.Unity.asmdef")));
        Assert.True(Directory.Exists(pluginRoot), "The Editor.Unity build must materialize its Editor/Plugins directory.");
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

        var packagedFiles = Directory.EnumerateFiles(pluginRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(EditorBusinessAssemblies.Order(StringComparer.Ordinal).ToArray(), packagedFiles);
        foreach (var assembly in EditorBusinessAssemblies)
        {
            var projectName = Path.GetFileNameWithoutExtension(assembly);
            var netStandardOutput = Path.Combine(
                root,
                "src",
                projectName,
                "bin",
                configuration,
                "netstandard2.1",
                assembly);
            Assert.True(
                File.Exists(netStandardOutput),
                $"Build {projectName} for netstandard2.1/{configuration} before validating the Editor UPM package.");
            Assert.Equal(
                File.ReadAllBytes(netStandardOutput),
                File.ReadAllBytes(Path.Combine(pluginRoot, assembly)));
        }
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
