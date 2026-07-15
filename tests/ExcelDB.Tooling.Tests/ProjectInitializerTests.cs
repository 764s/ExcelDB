using System.Text;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;

namespace ExcelDb.Tooling.Tests;

public sealed class ProjectInitializerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exceldb-tooling-tests", Guid.NewGuid().ToString("N"));
    private readonly string _externalRoot = Path.Combine(Path.GetTempPath(), "exceldb-tooling-tests", $"generated 中文 {Guid.NewGuid():N}");

    [Fact]
    public void Init_CreatesCanonicalProjectV2AndIsIdempotent()
    {
        var initializer = new ProjectInitializer("test", "catalog-hash");

        var first = initializer.Plan(_root);
        var firstReport = new MutationPlanApplier().Apply(first);

        Assert.True(firstReport.Succeeded, string.Join(Environment.NewLine, firstReport.Diagnostics.Select(static item => $"{item.Code}: {item.Message}")));
        Assert.Equal(2, first.FormatVersion);
        Assert.Equal("catalog-hash", first.SystemCatalogHash);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "Generated", "CSharp")), first.GeneratedCSharpRoot);
        Assert.Equal(
            ExcelDbProject.Default.ToCanonicalJson(),
            File.ReadAllBytes(Path.Combine(_root, ExcelDbProject.FileName)));
        Assert.True(Directory.Exists(Path.Combine(_root, "Schema")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Excel")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Generated", "CSharp")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Generated", "Bytes")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Data")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Build")));
        Assert.False(File.Exists(Path.Combine(_root, ExcelDbProject.LegacyFileName)));

        foreach (var localState in new[] { "cache", "plans", "reports", "recovery", "published" })
            Assert.True(Directory.Exists(Path.Combine(_root, ".exceldb", localState)));
        Assert.Equal(
            "/cache/\n/plans/\n/reports/\n/recovery/\n",
            File.ReadAllText(Path.Combine(_root, ".exceldb", ".gitignore")).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(
            "/exceldb/\n/google/protobuf/\n",
            File.ReadAllText(Path.Combine(_root, "Schema", ".gitignore")).Replace("\r\n", "\n", StringComparison.Ordinal));

        var context = ExcelDbProject.Load(Path.Combine(_root, ExcelDbProject.FileName))
            .Resolve(Path.Combine(_root, ExcelDbProject.FileName));
        Assert.Equal(Path.GetFullPath(_root), context.RootDirectory);
        Assert.Equal(Path.Combine(_root, ".exceldb"), context.InternalDirectory);

        var second = initializer.Plan(_root);
        var secondReport = new MutationPlanApplier().Apply(second);

        Assert.Empty(second.Mutations);
        Assert.True(secondReport.Succeeded);
        Assert.False(secondReport.Applied);
    }

    [Fact]
    public void Init_WithExternalGeneratedCSharp_CreatesRealRootWithoutLocalPlaceholder()
    {
        var initializer = new ProjectInitializer("test");

        var plan = initializer.Plan(_root, _externalRoot);
        var report = new MutationPlanApplier().Apply(plan);

        Assert.True(report.Succeeded);
        Assert.Equal(Path.GetFullPath(_externalRoot), plan.GeneratedCSharpRoot);
        Assert.Contains(plan.Risks, risk => risk.Contains("External Generated C# root", StringComparison.Ordinal));
        Assert.True(Directory.Exists(_externalRoot));
        Assert.True(Directory.Exists(Path.Combine(_root, "Generated", "Bytes")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Generated", "CSharp")));
        var project = ExcelDbProject.Load(Path.Combine(_root, ExcelDbProject.FileName));
        Assert.Equal(Path.GetFullPath(_externalRoot), project.GeneratedCSharpDir);

        var second = initializer.Plan(_root, _externalRoot);
        Assert.Empty(second.Mutations);
    }

    [Fact]
    public void Plan_RejectsLegacyV1WithoutAnyMutation()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ExcelDbProject.LegacyFileName), "{}");

        var plan = new ProjectInitializer("test").Plan(_root);
        var report = new MutationPlanApplier().Apply(plan);

        Assert.Empty(plan.Mutations);
        Assert.Contains(plan.Diagnostics, item => item.Code == "project.legacy-v1" && item.IsBlocker);
        Assert.False(report.Succeeded);
        Assert.False(File.Exists(Path.Combine(_root, ExcelDbProject.FileName)));
        Assert.False(Directory.Exists(Path.Combine(_root, "Schema")));
    }

    [Fact]
    public void Plan_RejectsStaleTargetWithoutWriting()
    {
        Directory.CreateDirectory(_root);
        var initializer = new ProjectInitializer("test");
        var plan = initializer.Plan(_root);
        Directory.CreateDirectory(Path.Combine(_root, "Schema"));

        var report = new MutationPlanApplier().Apply(plan);

        Assert.Equal(ExcelDb.Core.Diagnostics.OperationExitCode.Blocker, report.ExitCode);
        Assert.False(File.Exists(Path.Combine(_root, ExcelDbProject.FileName)));
    }

    [Theory]
    [InlineData("{\"formatVersion\":2,\"schemaDir\":\"Schema\",\"excelDir\":\"Excel\",\"generatedCSharpDir\":\"Generated/CSharp\",\"generatedBytesDir\":\"Generated/Bytes\",\"extra\":true}")]
    [InlineData("{\"formatVersion\":2,\"schemaDir\":\"Schema\",\"excelDir\":\"Excel\",\"generatedCSharpDir\":\"Generated/CSharp\"}")]
    [InlineData("{\"formatVersion\":1,\"schemaDir\":\"Schema\",\"excelDir\":\"Excel\",\"generatedCSharpDir\":\"Generated/CSharp\",\"generatedBytesDir\":\"Generated/Bytes\"}")]
    [InlineData("{\"formatVersion\":2,\"formatVersion\":2,\"schemaDir\":\"Schema\",\"excelDir\":\"Excel\",\"generatedCSharpDir\":\"Generated/CSharp\",\"generatedBytesDir\":\"Generated/Bytes\"}")]
    public void ProjectLoader_RejectsNonV2Shapes(string json)
    {
        var path = CreateProjectFile(json);

        Assert.Throws<InvalidDataException>(() => ExcelDbProject.Load(path));
    }

    [Fact]
    public void ProjectLoader_RejectsLegacyPathWithExplicitMessage()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ExcelDbProject.LegacyFileName);
        File.WriteAllText(path, "{}");

        var exception = Assert.Throws<InvalidDataException>(() => ExcelDbProject.Load(path));

        Assert.Contains("Legacy", exception.Message, StringComparison.Ordinal);
        Assert.Contains("automatic migration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectContext_RejectsEscapesAndOverlappingArtifactRoots()
    {
        var absoluteExcel = Path.Combine(Path.GetTempPath(), "other-excel");
        var escaped = CreateProjectFile(ProjectJson(excelDir: absoluteExcel));
        Assert.Throws<InvalidDataException>(() => ExcelDbProject.Load(escaped));

        var traversal = CreateProjectFile(ProjectJson(schemaDir: "../Schema"));
        Assert.Throws<InvalidDataException>(() => ExcelDbProject.Load(traversal));

        var overlapping = CreateProjectFile(ProjectJson(generatedCSharpDir: "Schema/Generated"));
        var project = ExcelDbProject.Load(overlapping);
        Assert.Throws<InvalidDataException>(() => project.Resolve(overlapping));
    }

    [Fact]
    public void ProjectContext_DerivesInternalAndTargetPathsWithoutPerTargetConfiguration()
    {
        var projectFile = CreateProjectFile(ProjectJson());
        var context = ExcelDbProject.Load(projectFile).Resolve(projectFile);

        Assert.Equal(Path.Combine(_root, ".exceldb", "cache"), context.CacheDirectory);
        Assert.Equal(Path.Combine(_root, ".exceldb", "plans"), context.PlansDirectory);
        Assert.Equal(Path.Combine(_root, ".exceldb", "reports"), context.ReportsDirectory);
        Assert.Equal(Path.Combine(_root, ".exceldb", "recovery"), context.RecoveryDirectory);
        Assert.Equal(Path.Combine(_root, ".exceldb", "published"), context.PublishedDirectory);
        Assert.Equal(Path.Combine(_root, ".exceldb", "system-imports.json"), context.SystemImportsManifestPath);
        Assert.Equal(Path.Combine(_root, "Generated", "Bytes", "client", "config.bytes"), context.GetBytesOutputPath("client"));
        Assert.Equal(Path.Combine(_root, "Generated", "Bytes", "lite-client", "config.bytes.manifest.json"), context.GetBytesManifestPath("lite-client"));
        Assert.Throws<InvalidDataException>(() => context.GetBytesOutputPath("../server"));
        Assert.Throws<InvalidDataException>(() => context.GetBytesOutputPath("Server"));
        Assert.Throws<InvalidDataException>(() => context.GetBytesOutputPath("all/targets"));
    }

    [Fact]
    public void EnumerateExcelFiles_IsRecursiveAndExcludesTemporaryAndToolFiles()
    {
        var report = new MutationPlanApplier().Apply(new ProjectInitializer("test").Plan(_root));
        Assert.True(report.Succeeded);
        var excel = Path.Combine(_root, "Excel");
        var nested = Path.Combine(excel, "Nested");
        var toolDirectory = Path.Combine(excel, ".tool");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(toolDirectory);
        File.WriteAllText(Path.Combine(excel, "Root.XLSX"), "root");
        File.WriteAllText(Path.Combine(nested, "Data.xlsx"), "nested");
        File.WriteAllText(Path.Combine(excel, "~$Root.xlsx"), "temporary");
        File.WriteAllText(Path.Combine(excel, ".hidden.xlsx"), "tool");
        File.WriteAllText(Path.Combine(toolDirectory, "Tool.xlsx"), "tool");
        File.WriteAllText(Path.Combine(excel, "not-excel.txt"), "text");
        var hidden = Path.Combine(excel, "attribute-hidden.xlsx");
        File.WriteAllText(hidden, "hidden");
        if (OperatingSystem.IsWindows())
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var context = ExcelDbProject.Load(Path.Combine(_root, ExcelDbProject.FileName))
            .Resolve(Path.Combine(_root, ExcelDbProject.FileName));
        var files = context.EnumerateExcelFiles();
        var frozen = InputSetSnapshot.Capture(
            context.RootDirectory,
            context.ExcelDirectory,
            InputSetKind.ExcelWorkbook);

        Assert.Equal(
            new[] { Path.Combine(nested, "Data.xlsx"), Path.Combine(excel, "Root.XLSX") }.OrderBy(static path => path, StringComparer.Ordinal),
            files);
        Assert.Equal(
            files.Select(path => Path.GetRelativePath(excel, path).Replace('\\', '/')),
            frozen.Entries.Select(static entry => entry.RelativePath));
    }

    [Fact]
    public void ProjectLocator_ReportsNearestLegacyInsteadOfSilentlySkippingIt()
    {
        var nested = Path.Combine(_root, "one", "two");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(_root, ExcelDbProject.LegacyFileName), "{}");

        var location = ProjectLocator.LocateNearest(nested);

        Assert.Equal(ProjectLocationKind.LegacyV1, location.Kind);
        Assert.Null(ProjectLocator.FindNearest(nested));
        Assert.Equal(Path.Combine(_root, ExcelDbProject.LegacyFileName), ProjectLocator.FindNearestLegacy(nested));
    }

    [Fact]
    public void ProjectLocator_ReportsConflictedWhenV1AndV2ExistTogether()
    {
        var nested = Path.Combine(_root, "one", "two");
        Directory.CreateDirectory(nested);
        var projectFile = Path.Combine(_root, ExcelDbProject.RelativeFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
        File.WriteAllBytes(projectFile, ExcelDbProject.Default.ToCanonicalJson());
        File.WriteAllText(Path.Combine(_root, ExcelDbProject.LegacyFileName), "{}");

        var location = ProjectLocator.LocateNearest(nested);

        Assert.Equal(ProjectLocationKind.Conflicted, location.Kind);
        Assert.Null(ProjectLocator.FindNearest(nested));
        Assert.Null(ProjectLocator.FindNearestLegacy(nested));
        Assert.Throws<InvalidDataException>(() => ExcelDbProject.Load(projectFile));
    }

    [Fact]
    public void OwnedWindowsAttributes_AreRepairedIdempotently()
    {
        var initializer = new ProjectInitializer("test");
        Assert.True(new MutationPlanApplier().Apply(initializer.Plan(_root)).Succeeded);
        var optionsDirectory = Path.Combine(_root, "Schema", "exceldb");
        var googleDirectory = Path.Combine(_root, "Schema", "google");
        var protobufDirectory = Path.Combine(googleDirectory, "protobuf");
        Directory.CreateDirectory(optionsDirectory);
        Directory.CreateDirectory(protobufDirectory);
        var options = Path.Combine(optionsDirectory, "options.proto");
        var descriptor = Path.Combine(protobufDirectory, "descriptor.proto");
        File.WriteAllText(options, "options");
        File.WriteAllText(descriptor, "descriptor");
        var projectFile = Path.Combine(_root, ExcelDbProject.FileName);
        var context = ExcelDbProject.Load(projectFile).Resolve(projectFile);

        var first = initializer.RepairOwnedAttributes(
            context,
            ["exceldb/options.proto", "google/protobuf/descriptor.proto"]);
        var second = initializer.RepairOwnedAttributes(
            context,
            ["exceldb/options.proto", "google/protobuf/descriptor.proto"]);

        Assert.Empty(first);
        Assert.Empty(second);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.GetAttributes(context.InternalDirectory).HasFlag(FileAttributes.Hidden));
            Assert.True(File.GetAttributes(optionsDirectory).HasFlag(FileAttributes.Hidden));
            Assert.True(File.GetAttributes(googleDirectory).HasFlag(FileAttributes.Hidden));
            Assert.True(File.GetAttributes(protobufDirectory).HasFlag(FileAttributes.Hidden));
            Assert.True(File.GetAttributes(options).HasFlag(FileAttributes.ReadOnly));
            Assert.True(File.GetAttributes(descriptor).HasFlag(FileAttributes.ReadOnly));
        }
    }

    [Fact]
    public void SerializedPlan_DetectsTampering()
    {
        var plan = new ProjectInitializer("test").Plan(_root);
        var json = MutationPlanCodec.Serialize(plan);
        json[Array.IndexOf(json, (byte)'i')] = (byte)'x';

        Assert.ThrowsAny<Exception>(() => MutationPlanCodec.Deserialize(json));
    }

    private string CreateProjectFile(string json)
    {
        var internalDirectory = Path.Combine(_root, ".exceldb");
        Directory.CreateDirectory(internalDirectory);
        var path = Path.Combine(internalDirectory, "project.json");
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string ProjectJson(
        string schemaDir = "Schema",
        string excelDir = "Excel",
        string generatedCSharpDir = "Generated/CSharp",
        string generatedBytesDir = "Generated/Bytes") =>
        $$"""
        {
          "formatVersion": 2,
          "schemaDir": "{{JsonEscape(schemaDir)}}",
          "excelDir": "{{JsonEscape(excelDir)}}",
          "generatedCSharpDir": "{{JsonEscape(generatedCSharpDir)}}",
          "generatedBytesDir": "{{JsonEscape(generatedBytesDir)}}"
        }
        """;

    private static string JsonEscape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

    public void Dispose()
    {
        DeleteTree(_root);
        DeleteTree(_externalRoot);
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (IOException)
            {
                // Best-effort test cleanup.
            }
        }
        Directory.Delete(path, recursive: true);
    }
}
