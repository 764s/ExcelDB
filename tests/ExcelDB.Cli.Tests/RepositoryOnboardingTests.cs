namespace ExcelDb.Cli.Tests;

public sealed class RepositoryOnboardingTests
{
    [Fact]
    public void ReadmeStartsWithADownloadableFiveMinuteUserJourney()
    {
        var root = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        var quickStart = readme.IndexOf("## 五分钟上手", StringComparison.Ordinal);
        var internals = readme.IndexOf("M1–M8", StringComparison.Ordinal);
        Assert.True(quickStart >= 0);
        Assert.True(internals < 0 || quickStart < internals);
        Assert.Contains("exceldb-win-x64.zip", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe init .", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe table create Hero", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe data prepare", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe check", readme, StringComparison.Ordinal);
        Assert.Contains("exceldb.exe convert", readme, StringComparison.Ordinal);
        Assert.Contains("Build\\config.bytes", readme, StringComparison.Ordinal);
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
