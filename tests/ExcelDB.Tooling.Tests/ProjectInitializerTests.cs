using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;
using System.Text.Json;

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

    [Fact]
    public void DurablePlanRecoveryRestoresOldSetAfterPartialProcessTermination()
    {
        Directory.CreateDirectory(_root);
        var existing = Path.Combine(_root, "existing.txt");
        var created = Path.Combine(_root, "created.txt");
        File.WriteAllText(existing, "new-partial");
        File.WriteAllText(created, "new-file-partial");
        var transaction = Path.Combine(Path.GetDirectoryName(_root)!, ".exceldb-txn-" + Guid.NewGuid().ToString("N"));
        var backupDirectory = Path.Combine(transaction, "backup");
        var stagedDirectory = Path.Combine(transaction, "staged");
        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(stagedDirectory);
        var backup = Path.Combine(backupDirectory, "0");
        File.WriteAllText(backup, "old-complete");
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        File.WriteAllText(Path.Combine(transaction, "journal.json"), JsonSerializer.Serialize(new
        {
            ProjectRoot = _root,
            PlanHash = "test-plan",
            Phase = "applying",
            Items = new object[]
            {
                new { Index = 0, Destination = existing, StagePath = Path.Combine(stagedDirectory, "0"), BackupPath = backup, HadOriginal = true, Applied = true },
                new { Index = 1, Destination = created, StagePath = Path.Combine(stagedDirectory, "1"), BackupPath = Path.Combine(backupDirectory, "1"), HadOriginal = false, Applied = true },
            },
            CreatedDirectories = Array.Empty<string>(),
        }, options));

        var report = MutationPlanApplier.RecoverPending(_root);

        Assert.True(report.Succeeded);
        Assert.True(report.Applied);
        Assert.Equal("old-complete", File.ReadAllText(existing));
        Assert.False(File.Exists(created));
        Assert.False(Directory.Exists(transaction));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
