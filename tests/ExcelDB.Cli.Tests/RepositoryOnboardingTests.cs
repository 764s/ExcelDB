namespace ExcelDb.Cli.Tests;

public sealed class RepositoryOnboardingTests
{
    [Fact]
    public void ReadmeStartsWithDownloadAndTheTwoSupportedLaunchPaths()
    {
        var root = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        var quickStart = readme.IndexOf("## 下载后直接开始", StringComparison.Ordinal);
        var internals = readme.IndexOf("M1–M8", StringComparison.Ordinal);
        Assert.True(quickStart >= 0);
        Assert.True(internals < 0 || quickStart < internals);
        Assert.Contains("exceldb-win-x64.zip", readme, StringComparison.Ordinal);
        Assert.Contains("双击 `exceldb.exe`", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe init .", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe table create Hero", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe data prepare", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe check", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe convert", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe convert --target server", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe project inspect", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe project configure", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe project repair-imports", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe project clean-cache", readme, StringComparison.Ordinal);
        Assert.Contains("Schema", readme, StringComparison.Ordinal);
        Assert.Contains("Excel", readme, StringComparison.Ordinal);
        Assert.Contains("Generated\\CSharp", readme, StringComparison.Ordinal);
        Assert.Contains("Generated\\Bytes\\client\\config.bytes", readme, StringComparison.Ordinal);
        Assert.Contains(".exceldb\\project.json", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Data\\game.xlsx", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("Build\\config.bytes", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionTagsBuildTheExactDownloadNamedByTheReadme()
    {
        var root = FindRepositoryRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "release.yml");
        Assert.True(File.Exists(workflowPath));
        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("tags:", workflow, StringComparison.Ordinal);
        Assert.Contains("- \"v*\"", workflow, StringComparison.Ordinal);
        Assert.Contains("PublishProfile=win-x64", workflow, StringComparison.Ordinal);
        Assert.Contains("Release smoke", workflow, StringComparison.Ordinal);
        Assert.Contains("$files.Count -ne 1", workflow, StringComparison.Ordinal);
        Assert.Contains("$env:PATH = \"\"", workflow, StringComparison.Ordinal);
        Assert.Contains("$env:DOTNET_ROOT_X64 = \"Z:\\missing-dotnet\"", workflow, StringComparison.Ordinal);
        Assert.Contains("$LASTEXITCODE -ne 3", workflow, StringComparison.Ordinal);
        Assert.Contains("samples/init-with-portable-exceldb.bat", workflow, StringComparison.Ordinal);
        Assert.Contains("samples/init-with-installed-exceldb.bat", workflow, StringComparison.Ordinal);
        Assert.Contains("portable BAT 项目 含空格", workflow, StringComparison.Ordinal);
        Assert.Contains("installed BAT 项目 含空格", workflow, StringComparison.Ordinal);
        Assert.Contains("portable 外部 CSharp 中文 空格", workflow, StringComparison.Ordinal);
        Assert.Contains("installed 外部 CSharp 中文 空格", workflow, StringComparison.Ordinal);
        Assert.Contains("Assert-ProjectV2", workflow, StringComparison.Ordinal);
        Assert.Contains("$propertyNames.Count -ne $expectedProperties.Count", workflow, StringComparison.Ordinal);
        Assert.Contains("misleading local Generated/CSharp copy", workflow, StringComparison.Ordinal);
        Assert.Contains("$env:PATH = $publish", workflow, StringComparison.Ordinal);
        Assert.Contains("@(\"init\", $project)", workflow, StringComparison.Ordinal);
        Assert.Contains("id:string:key", workflow, StringComparison.Ordinal);
        Assert.Contains("@(\"table\", \"edit\"", workflow, StringComparison.Ordinal);
        Assert.Contains("@(\"generate\")", workflow, StringComparison.Ordinal);
        Assert.Contains("@(\"check\")", workflow, StringComparison.Ordinal);
        Assert.Contains("@(\"convert\", \"--target\", \"client\")", workflow, StringComparison.Ordinal);
        Assert.Contains("@(\"convert\", \"--target\", \"server\")", workflow, StringComparison.Ordinal);
        Assert.Contains("@(\"schema\", \"publish\")", workflow, StringComparison.Ordinal);
        Assert.Contains("@(\"project\", \"repair-imports\")", workflow, StringComparison.Ordinal);
        Assert.Contains("Generated/Bytes/server/config.bytes", workflow, StringComparison.Ordinal);
        Assert.Contains("Schema/exceldb/options.proto", workflow, StringComparison.Ordinal);
        Assert.Contains("canonical 11-file Google catalog", workflow, StringComparison.Ordinal);
        Assert.Contains("Legacy Project v1 init must be rejected", workflow, StringComparison.Ordinal);
        Assert.Contains("Read-only project inspect unexpectedly repaired", workflow, StringComparison.Ordinal);
        Assert.Contains("ExcelDB release smoke outbound block", workflow, StringComparison.Ordinal);
        Assert.Contains("New-NetFirewallRule", workflow, StringComparison.Ordinal);
        Assert.Contains(".exceldb/published/published.json", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/exceldb-win-x64.zip", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release create", workflow, StringComparison.Ordinal);
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
