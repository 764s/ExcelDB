using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;

namespace ExcelDb.Tooling.Tests;

public sealed class ProjectInitializerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exceldb-tooling-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Init_CreatesCanonicalProjectAndIsIdempotent()
    {
        var initializer = new ProjectInitializer("test");
        var first = initializer.Plan(_root);

        var firstReport = new MutationPlanApplier().Apply(first);

        Assert.True(firstReport.Succeeded);
        Assert.True(File.Exists(Path.Combine(_root, ExcelDbProject.FileName)));
        Assert.True(Directory.Exists(Path.Combine(_root, "Schema")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Generated")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Data")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Build")));
        Assert.True(Directory.Exists(Path.Combine(_root, ".exceldb")));

        var second = initializer.Plan(_root);
        var secondReport = new MutationPlanApplier().Apply(second);

        Assert.Empty(second.Mutations);
        Assert.True(secondReport.Succeeded);
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

    [Fact]
    public void ProjectLoader_RejectsUnknownKeys()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ExcelDbProject.FileName);
        File.WriteAllText(path, "{\"generatedDir\":\"Generated\",\"workbooks\":[\"Data/*.xlsx\"],\"bytesOutput\":\"Build/config.bytes\",\"extra\":true}");

        Assert.Throws<InvalidDataException>(() => ExcelDbProject.Load(path));
    }

    [Fact]
    public void SerializedPlan_DetectsTampering()
    {
        var plan = new ProjectInitializer("test").Plan(_root);
        var json = MutationPlanCodec.Serialize(plan);
        json[Array.IndexOf(json, (byte)'i')] = (byte)'x';

        Assert.ThrowsAny<Exception>(() => MutationPlanCodec.Deserialize(json));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
