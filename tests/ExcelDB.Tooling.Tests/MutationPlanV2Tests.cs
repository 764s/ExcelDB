using System.Text;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;

namespace ExcelDb.Tooling.Tests;

public sealed class MutationPlanV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "exceldb-plan-v2-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CanonicalPlan_IsIndependentOfInsertionOrderAndCoversBothRootsAndCatalog()
    {
        var project = CasePath("canonical", "project");
        var generated = CasePath("canonical", "generated-csharp");

        var first = NewBuilder(project, generated, "catalog-a")
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/code.g.cs", Encoding.UTF8.GetBytes("code"))
            .WriteFile(PlanRootKind.Project, "Schema/table.proto", Encoding.UTF8.GetBytes("schema"))
            .Build();
        var second = NewBuilder(project, generated, "catalog-a")
            .WriteFile(PlanRootKind.Project, "Schema/table.proto", Encoding.UTF8.GetBytes("schema"))
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/code.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();
        var differentCatalog = NewBuilder(project, generated, "catalog-b")
            .WriteFile(PlanRootKind.Project, "Schema/table.proto", Encoding.UTF8.GetBytes("schema"))
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/code.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();
        var differentRoot = NewBuilder(project, CasePath("canonical", "other-generated"), "catalog-a")
            .WriteFile(PlanRootKind.Project, "Schema/table.proto", Encoding.UTF8.GetBytes("schema"))
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/code.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();

        Assert.Equal(MutationPlanCodec.Serialize(first), MutationPlanCodec.Serialize(second));
        Assert.NotEqual(first.PlanHash, differentCatalog.PlanHash);
        Assert.NotEqual(first.PlanHash, differentRoot.PlanHash);
        Assert.All(first.Observations, item => Assert.True(Enum.IsDefined(item.Root)));
        Assert.All(first.Mutations, item => Assert.True(Enum.IsDefined(item.Root)));
    }

    [Fact]
    public void FilteredInputSets_AreCanonicalAndUseProjectV2DiscoveryRules()
    {
        var project = CasePath("input-set-canonical", "project");
        var schema = Path.Combine(project, "Schema");
        var excel = Path.Combine(project, "Excel");
        Directory.CreateDirectory(Path.Combine(schema, "nested"));
        Directory.CreateDirectory(Path.Combine(schema, "exceldb"));
        Directory.CreateDirectory(Path.Combine(excel, "nested"));
        File.WriteAllText(Path.Combine(schema, "nested", "game.PROTO"), "syntax = \"proto3\";");
        File.WriteAllText(Path.Combine(schema, "exceldb", "options.proto"), "system");
        File.WriteAllText(Path.Combine(schema, "exceldb", "note.txt"), "owned tool state");
        File.WriteAllText(Path.Combine(excel, "root.xlsx"), "root");
        File.WriteAllText(Path.Combine(excel, "nested", "data.XLSX"), "nested");
        File.WriteAllText(Path.Combine(excel, "~$root.xlsx"), "temporary");
        File.WriteAllText(Path.Combine(excel, ".hidden.xlsx"), "tool");

        var first = NewBuilder(project, project, "catalog")
            .ObserveSystemProtoMirrorSet(schema)
            .ObserveExcelInputSet(excel)
            .ObserveSchemaInputSet(schema)
            .Build();
        var second = NewBuilder(project, project, "catalog")
            .ObserveSchemaInputSet(schema)
            .ObserveExcelInputSet(excel)
            .ObserveSystemProtoMirrorSet(schema)
            .Build();

        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.Equal(MutationPlanCodec.Serialize(first), MutationPlanCodec.Serialize(second));
        var schemaSet = first.InputSets.Single(item => item.Kind == InputSetKind.SchemaProto);
        Assert.Equal(["nested/game.PROTO"], schemaSet.Entries.Select(static item => item.RelativePath));
        var excelSet = first.InputSets.Single(item => item.Kind == InputSetKind.ExcelWorkbook);
        Assert.Equal(["nested/data.XLSX", "root.xlsx"], excelSet.Entries.Select(static item => item.RelativePath));
        var mirrorSet = first.InputSets.Single(item => item.Kind == InputSetKind.SystemProtoMirror);
        Assert.Equal(["exceldb/note.txt", "exceldb/options.proto"], mirrorSet.Entries.Select(static item => item.RelativePath));
        Assert.All(first.InputSets, item => Assert.Equal(64, item.Digest.Length));

        File.Delete(Path.Combine(schema, "exceldb", "options.proto"));
        File.Delete(Path.Combine(schema, "exceldb", "note.txt"));
        var withoutMirror = InputSetSnapshot.Capture(project, schema, InputSetKind.SchemaProto);
        File.WriteAllText(Path.Combine(schema, "exceldb", "options.proto"), "repaired canonical mirror");
        var repairedMirror = InputSetSnapshot.Capture(project, schema, InputSetKind.SchemaProto);
        Assert.Equal(schemaSet.Digest, withoutMirror.Digest);
        Assert.Equal(schemaSet.Digest, repairedMirror.Digest);
        Assert.Equal(schemaSet.Entries.ToArray(), withoutMirror.Entries.ToArray());
        Assert.Equal(schemaSet.Entries.ToArray(), repairedMirror.Entries.ToArray());
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("content")]
    public void Apply_InputSetMemberChangeIsStaleAndWritesNothing(string change)
    {
        var project = CasePath("input-set-stale", change, "project");
        var schema = Path.Combine(project, "Schema");
        Directory.CreateDirectory(schema);
        var original = Path.Combine(schema, "a.proto");
        File.WriteAllText(original, "original");
        var plan = NewBuilder(project, project, "catalog")
            .ObserveSchemaInputSet(schema)
            .WriteFile("out.txt", Encoding.UTF8.GetBytes("must-not-write"))
            .Build();

        switch (change)
        {
            case "add":
                File.WriteAllText(Path.Combine(schema, "b.proto"), "added");
                break;
            case "delete":
                File.Delete(original);
                break;
            case "rename":
                File.Move(original, Path.Combine(schema, "renamed.proto"));
                break;
            case "content":
                File.WriteAllText(original, "changed");
                break;
        }

        var report = new MutationPlanApplier().Apply(plan);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, item => item.Code == "plan.stale-input-set");
        Assert.False(File.Exists(Path.Combine(project, "out.txt")));
    }

    [Fact]
    public void Apply_RechecksInputSetAfterStagingAndRollsBackWithoutDomainWrites()
    {
        var project = CasePath("input-set-staging", "project");
        var schema = Path.Combine(project, "Schema");
        Directory.CreateDirectory(schema);
        File.WriteAllText(Path.Combine(schema, "a.proto"), "original");
        var plan = NewBuilder(project, project, "catalog")
            .ObserveSchemaInputSet(schema)
            .WriteFile("out.txt", Encoding.UTF8.GetBytes("must-not-write"))
            .Build();
        var applier = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.Staged)
                File.WriteAllText(Path.Combine(schema, "late.proto"), "late");
        });

        var report = applier.Apply(plan);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, item => item.Code == "plan.stale-input-set");
        Assert.False(File.Exists(Path.Combine(project, "out.txt")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
    }

    [Fact]
    public void Builder_ProjectsFrozenBaselineAndBlocksAnalyzeToPlanGap()
    {
        var project = CasePath("input-set-plan-gap", "project");
        var schema = Path.Combine(project, "Schema");
        Directory.CreateDirectory(schema);
        File.WriteAllText(Path.Combine(schema, "a.proto"), "original");
        var frozen = InputSetSnapshot.Capture(project, schema, InputSetKind.SchemaProto);
        File.WriteAllText(Path.Combine(schema, "late.proto"), "late");

        var plan = NewBuilder(project, project, "catalog")
            .ObserveSchemaInputSet(frozen)
            .WriteFile("out.txt", Encoding.UTF8.GetBytes("must-not-write"))
            .Build();
        var report = new MutationPlanApplier().Apply(plan);

        Assert.True(plan.HasBlockers);
        Assert.Contains(plan.Diagnostics, item => item.Code == "plan.stale-input-set");
        Assert.False(report.Succeeded);
        Assert.False(File.Exists(Path.Combine(project, "out.txt")));
    }

    [Fact]
    public void ApplyingRecovery_RollsBackMutationInsidePreimageInputSet()
    {
        var project = CasePath("input-set-recovery", "project");
        var schema = Path.Combine(project, "Schema");
        Directory.CreateDirectory(schema);
        var proto = Path.Combine(schema, "a.proto");
        File.WriteAllText(proto, "original");
        var plan = NewBuilder(project, project, "catalog")
            .ObserveSchemaInputSet(schema)
            .WriteFile(proto, Encoding.UTF8.GetBytes("partial"))
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.ItemApplied)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        Assert.Equal("partial", File.ReadAllText(proto));

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.True(report.Succeeded);
        Assert.Equal("original", File.ReadAllText(proto));
    }

    [Fact]
    public void FrozenV1_IsExplicitlyRejectedByCodecAndApplier()
    {
        var project = CasePath("v1", "project");
        var current = NewBuilder(project, project, "catalog").Build();
        var json = Encoding.UTF8.GetString(MutationPlanCodec.Serialize(current))
            .Replace("\"formatVersion\": 2", "\"formatVersion\": 1", StringComparison.Ordinal);

        var decode = Assert.Throws<InvalidDataException>(() => MutationPlanCodec.Deserialize(Encoding.UTF8.GetBytes(json)));
        var apply = Assert.Throws<InvalidDataException>(() => new MutationPlanApplier().Apply(current with { FormatVersion = 1 }));

        Assert.Contains("format 1 is frozen", decode.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("format 1 is frozen", apply.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WritesProjectAndExternalGeneratedCSharpRootsInOnePlan()
    {
        var project = CasePath("success", "project");
        var generated = CasePath("success", "generated 中文 with spaces");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, generated);
        var plan = NewBuilder(project, generated, "catalog")
            .WriteFile(PlanRootKind.Project, "Schema/table.proto", Encoding.UTF8.GetBytes("schema"))
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/client/table.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();

        var report = new MutationPlanApplier().Apply(plan);

        Assert.True(report.Succeeded);
        Assert.True(report.Applied);
        Assert.Equal("schema", File.ReadAllText(Path.Combine(project, "Schema", "table.proto")));
        Assert.Equal("code", File.ReadAllText(Path.Combine(generated, "Runtime", "client", "table.g.cs")));
        Assert.Contains(plan.Risks, item => item.Contains("External Generated C# root", StringComparison.Ordinal));
        Assert.Equal(2, report.Artifacts.Length);
    }

    [Fact]
    public void Apply_freezes_transaction_below_nearest_existing_ancestor_for_deep_missing_external_root()
    {
        var project = CasePath("deep-placement", "project");
        var existingAnchor = CasePath("deep-placement");
        var generated = Path.Combine(existingAnchor, "缺失 父目录", "更深 with spaces", "Generated CSharp");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, generated);
        var plan = NewBuilder(project, generated, "catalog")
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/client/table.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();

        var report = new MutationPlanApplier().Apply(plan);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal("code", File.ReadAllText(Path.Combine(generated, "Runtime", "client", "table.g.cs")));
        Assert.Empty(Directory.EnumerateDirectories(existingAnchor, ".exceldb-txn-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Apply_supports_external_generated_root_on_another_writable_volume_when_available()
    {
        var project = CasePath("cross-volume", "project");
        Directory.CreateDirectory(project);
        var projectVolume = Path.GetPathRoot(Path.GetFullPath(project));
        var alternate = DriveInfo.GetDrives().FirstOrDefault(drive =>
            drive.IsReady
            && !string.Equals(
                Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName),
                Path.TrimEndingDirectorySeparator(projectVolume!),
                StringComparison.OrdinalIgnoreCase));
        if (alternate is null)
            return;

        var externalAnchor = Path.Combine(
            alternate.RootDirectory.FullName,
            "exceldb-cross-volume-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            try
            {
                Directory.CreateDirectory(externalAnchor);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }
            var generated = Path.Combine(externalAnchor, "缺失 parent", "Generated CSharp");
            WriteProjectConfig(project, generated);
            var plan = NewBuilder(project, generated, "catalog")
                .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/table.g.cs", Encoding.UTF8.GetBytes("code"))
                .Build();

            var report = new MutationPlanApplier().Apply(plan);

            Assert.True(report.Succeeded, Format(report));
            Assert.Equal("code", File.ReadAllText(Path.Combine(generated, "Runtime", "table.g.cs")));
        }
        finally
        {
            if (Directory.Exists(externalAnchor))
            {
                foreach (var file in Directory.EnumerateFiles(externalAnchor, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(externalAnchor, recursive: true);
            }
        }
    }

    [Fact]
    public void Apply_ReplacesReadOnlyOwnedFile()
    {
        var project = CasePath("readonly", "project");
        Directory.CreateDirectory(project);
        var destination = Path.Combine(project, "owned.g.cs");
        File.WriteAllText(destination, "old");
        File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
        var plan = NewBuilder(project, project, "catalog")
            .WriteFile("owned.g.cs", Encoding.UTF8.GetBytes("new"))
            .Build();

        var report = new MutationPlanApplier().Apply(plan);

        Assert.True(report.Succeeded);
        Assert.Equal("new", File.ReadAllText(destination));
    }

    [Fact]
    public void CrossRootFailure_RollsBackAlreadyReplacedProjectFile()
    {
        var project = CasePath("rollback", "project");
        var generated = CasePath("rollback", "generated");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(generated);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, "first.txt");
        var generatedFile = Path.Combine(generated, "second.txt");
        File.WriteAllText(projectFile, "project-old");
        File.WriteAllText(generatedFile, "generated-old");
        var plan = NewBuilder(project, generated, "catalog")
            .WriteFile(PlanRootKind.Project, "first.txt", Encoding.UTF8.GetBytes("project-new"))
            .WriteFile(PlanRootKind.GeneratedCSharp, "second.txt", Encoding.UTF8.GetBytes("generated-new"))
            .Build();
        var applier = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.ItemApplied
                && checkpoint.Root == PlanRootKind.Project)
                throw new IOException("Simulated cross-root failure.");
        });

        var report = applier.Apply(plan);
        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, item => item.Code == "plan.apply-failed");
        Assert.Equal("project-old", File.ReadAllText(projectFile));
        Assert.Equal("generated-old", File.ReadAllText(generatedFile));
    }

    [Fact]
    public void RecoverPending_RestoresBothRootsFromDurableV2Journal()
    {
        var project = CasePath("recovery", "project");
        var generated = CasePath("recovery", "generated");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(generated);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, "project.txt");
        var generatedFile = Path.Combine(generated, "generated.txt");
        var projectOld = Encoding.UTF8.GetBytes("project-old");
        var generatedOld = Encoding.UTF8.GetBytes("generated-old");
        var projectNew = Encoding.UTF8.GetBytes("project-partial");
        var generatedNew = Encoding.UTF8.GetBytes("generated-partial");
        File.WriteAllBytes(projectFile, projectOld);
        File.WriteAllBytes(generatedFile, generatedOld);
        var frozenPlan = NewBuilder(project, generated, "catalog")
            .WriteFile(PlanRootKind.Project, "project.txt", projectNew)
            .WriteFile(PlanRootKind.GeneratedCSharp, "generated.txt", generatedNew)
            .Build();
        var applied = 0;
        var applier = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.ItemApplied && ++applied == 2)
                throw new SimulatedCrashException();
        });

        Assert.Throws<SimulatedCrashException>(() => applier.Apply(frozenPlan));
        Assert.Equal("project-partial", File.ReadAllText(projectFile));
        Assert.Equal("generated-partial", File.ReadAllText(generatedFile));

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.True(report.Succeeded);
        Assert.True(report.Applied);
        Assert.Equal("project-old", File.ReadAllText(projectFile));
        Assert.Equal("generated-old", File.ReadAllText(generatedFile));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(project)!, ".exceldb-txn-*"));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(generated)!, ".exceldb-txn-*"));
    }

    [Fact]
    public void Apply_recovers_pending_transaction_before_returning_a_blocked_plan()
    {
        var project = CasePath("blocked-recovery", "project");
        Directory.CreateDirectory(project);
        var generated = Path.Combine(project, "Generated", "CSharp");
        WriteProjectConfig(project, generated);
        var destination = Path.Combine(project, "project.txt");
        var originalBytes = Encoding.UTF8.GetBytes("original");
        var installedBytes = Encoding.UTF8.GetBytes("partial");
        File.WriteAllBytes(destination, originalBytes);
        var recoveryPlan = NewBuilder(project, generated, "catalog")
            .WriteFile("project.txt", installedBytes)
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.ItemApplied)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(recoveryPlan));
        Assert.Equal("partial", File.ReadAllText(destination));
        var blocked = NewBuilder(project, generated, "catalog")
            .AddDiagnostic(new Diagnostic(
                "test.blocker",
                DiagnosticSeverity.Blocker,
                ".",
                "The requested plan is intentionally blocked."))
            .Build();

        var report = new MutationPlanApplier().Apply(blocked);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "test.blocker");
        Assert.Equal("original", File.ReadAllText(destination));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(project)!, ".exceldb-txn-*"));
    }

    [Fact]
    public void Apply_BlocksReparsePointIntroducedAfterPreview()
    {
        var project = CasePath("reparse", "project");
        var outside = CasePath("reparse", "outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(project, "linked");
        var plan = NewBuilder(project, project, "catalog")
            .WriteFile("linked/escape.txt", Encoding.UTF8.GetBytes("must-not-escape"))
            .Build();

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }

        try
        {
            var report = new MutationPlanApplier().Apply(plan);
            Assert.False(report.Succeeded);
            Assert.Contains(report.Diagnostics, item => item.Code == "plan.path-unsafe");
            Assert.False(File.Exists(Path.Combine(outside, "escape.txt")));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(_root, recursive: true);
    }

    private MutationPlanBuilder NewBuilder(string project, string generated, string catalog)
    {
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var configHash = File.Exists(projectFile)
            ? ContentFingerprint.FromFile(projectFile).Sha256
            : "config-hash";
        var builder = new MutationPlanBuilder(
            "test",
            "test-tool",
            project,
            generated,
            configHash,
            catalog,
            schemaHash: 42);
        if (File.Exists(projectFile))
            builder.Observe(projectFile);
        return builder;
    }

    private string CasePath(params string[] parts)
    {
        var path = _root;
        foreach (var part in parts)
            path = Path.Combine(path, part);
        return path;
    }

    private static void WriteProjectConfig(string projectRoot, string generatedCSharpRoot)
    {
        var path = Path.Combine(projectRoot, ExcelDbProject.RelativeFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(
            path,
            ExcelDbProject.Default
                .WithGeneratedCSharpDirectory(Path.GetFullPath(generatedCSharpRoot))
                .ToCanonicalJson());
    }

    private static string Format(OperationReport report) =>
        string.Join(Environment.NewLine, report.Diagnostics.Select(static diagnostic => diagnostic.Message));

    private sealed class SimulatedCrashException : Exception;
}
