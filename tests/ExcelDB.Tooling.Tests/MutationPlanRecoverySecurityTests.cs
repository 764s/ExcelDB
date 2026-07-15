using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExcelDb.Core.IO;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;

namespace ExcelDb.Tooling.Tests;

public sealed class MutationPlanRecoverySecurityTests : IDisposable
{
    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "exceldb-plan-security-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Tampered_generated_root_and_plan_hash_never_touch_victim()
    {
        var fixture = CreateFixture("tampered-root", hadOriginal: true);
        var victim = CasePath("victim");
        Directory.CreateDirectory(victim);
        var victimFile = Path.Combine(victim, "owned.txt");
        File.WriteAllText(victimFile, "keep");
        fixture.Journal.GeneratedCSharpRoot = victim;
        fixture.Journal.PlanHash = new string('0', 64);
        WriteJournal(fixture);

        var report = MutationPlanApplier.RecoverPending(fixture.ProjectRoot);

        Assert.False(report.Succeeded);
        Assert.Equal("keep", File.ReadAllText(victimFile));
        Assert.True(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void Missing_or_modified_backup_preserves_installed_destination_and_journal()
    {
        var missing = CreateAppliedCrash("missing-backup", hadOriginal: true);
        File.Delete(missing.BackupPath);

        var missingReport = MutationPlanApplier.RecoverPending(missing.ProjectRoot);

        Assert.False(missingReport.Succeeded);
        Assert.Equal(missing.NewBytes, File.ReadAllBytes(missing.Destination));
        Assert.True(File.Exists(missing.JournalPath));

        var modified = CreateAppliedCrash("modified-backup", hadOriginal: true);
        File.WriteAllText(modified.BackupPath, "not-the-original");

        var modifiedReport = MutationPlanApplier.RecoverPending(modified.ProjectRoot);

        Assert.False(modifiedReport.Succeeded);
        Assert.Equal(modified.NewBytes, File.ReadAllBytes(modified.Destination));
        Assert.True(File.Exists(modified.JournalPath));
    }

    [Fact]
    public void Unknown_new_destination_is_not_deleted()
    {
        var fixture = CreateAppliedCrash("unknown-destination", hadOriginal: false);
        var unknown = Encoding.UTF8.GetBytes("third-party-content");
        File.WriteAllBytes(fixture.Destination, unknown);

        var report = MutationPlanApplier.RecoverPending(fixture.ProjectRoot);

        Assert.False(report.Succeeded);
        Assert.Equal(unknown, File.ReadAllBytes(fixture.Destination));
        Assert.True(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void Transaction_root_file_collision_blocks_recovery_without_discarding_domain_evidence()
    {
        var fixture = CreateAppliedCrash("transaction-root-file", hadOriginal: true);
        Directory.Delete(fixture.TransactionRoot, recursive: true);
        File.WriteAllText(fixture.TransactionRoot, "collision");

        var report = MutationPlanApplier.RecoverPending(fixture.ProjectRoot);

        Assert.False(report.Succeeded);
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(fixture.Destination));
        Assert.Equal("collision", File.ReadAllText(fixture.TransactionRoot));
        Assert.True(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void Missing_applying_transaction_root_blocks_recovery_and_preserves_partial_domain()
    {
        var fixture = CreateAppliedCrash("transaction-root-missing", hadOriginal: true);
        Directory.Delete(fixture.TransactionRoot, recursive: true);

        var report = MutationPlanApplier.RecoverPending(fixture.ProjectRoot);

        Assert.False(report.Succeeded);
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(fixture.Destination));
        Assert.True(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void Project_journal_phase_downgrade_cannot_override_root_local_applying_progress()
    {
        var fixture = CreateAppliedCrash("phase-downgrade", hadOriginal: true);
        var journal = JsonNode.Parse(File.ReadAllBytes(fixture.JournalPath))!.AsObject();
        journal["phase"] = "prepared";
        journal["items"]!.AsArray()[0]!["state"] = "pending";
        WriteCanonicalNode(fixture.JournalPath, journal);

        var report = MutationPlanApplier.RecoverPending(fixture.ProjectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(fixture.Destination));
        Assert.False(File.Exists(fixture.BackupPath));
        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public void Project_root_seal_cannot_authorize_created_directory_in_unsealed_external_root()
    {
        var project = CasePath("per-root-seal", "project");
        var currentGenerated = Path.Combine(project, "Generated", "CSharp");
        var victimRoot = CasePath("per-root-seal", "victim-generated");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, currentGenerated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var candidate = ExcelDbProject.Default
            .WithGeneratedCSharpDirectory(Path.GetFullPath(victimRoot))
            .ToCanonicalJson();
        var plan = new MutationPlanBuilder(
                "forged-candidate",
                "tests",
                project,
                victimRoot,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                0)
            .Observe(projectFile)
            .WriteFile(projectFile, candidate)
            .CreateDirectory(PlanRootKind.GeneratedCSharp, ".")
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.Staged)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        var journalPath = Path.Combine(journalDirectory, "journal.json");
        var transactionId = Guid.ParseExact(Path.GetFileName(journalDirectory), "N");
        var generatedTransaction = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(victimRoot))!,
            $".exceldb-txn-{transactionId:N}-generatedcsharp");
        Assert.True(File.Exists(Path.Combine(generatedTransaction, "seal.json")));
        Directory.Delete(generatedTransaction, recursive: true);
        Directory.CreateDirectory(victimRoot);
        var journal = JsonNode.Parse(File.ReadAllBytes(journalPath))!.AsObject();
        journal["phase"] = "applying";
        journal["createdDirectories"]!.AsArray()
            .Single(item => item!["root"]!.GetValue<int>() == (int)PlanRootKind.GeneratedCSharp)!["state"] = "created";
        WriteCanonicalNode(journalPath, journal);

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.False(report.Succeeded);
        Assert.True(Directory.Exists(victimRoot));
        Assert.True(File.Exists(journalPath));
    }

    [Fact]
    public void Project_journal_cannot_promote_staged_root_and_delete_competing_same_hash_file()
    {
        var project = CasePath("phase-promotion", "project");
        var generated = CasePath("phase-promotion", "generated");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var replacement = Encoding.UTF8.GetBytes("same-content");
        var plan = new MutationPlanBuilder(
                "phase-promotion",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile(PlanRootKind.GeneratedCSharp, "victim.g.cs", replacement)
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.Staged)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        var journalPath = Path.Combine(journalDirectory, "journal.json");
        var transactionId = Guid.ParseExact(Path.GetFileName(journalDirectory), "N");
        var transactionRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(generated))!,
            $".exceldb-txn-{transactionId:N}-generatedcsharp");
        var staged = Assert.Single(Directory.EnumerateFiles(Path.Combine(transactionRoot, "staged")));
        Directory.CreateDirectory(generated);
        var victim = Path.Combine(generated, "victim.g.cs");
        File.WriteAllBytes(victim, replacement);
        var journal = JsonNode.Parse(File.ReadAllBytes(journalPath))!.AsObject();
        journal["phase"] = "applying";
        journal["items"]!.AsArray()[0]!["state"] = "applying";
        WriteCanonicalNode(journalPath, journal);

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.False(report.Succeeded);
        Assert.Equal(replacement, File.ReadAllBytes(victim));
        Assert.True(File.Exists(staged));
        Assert.True(File.Exists(journalPath));
    }

    [Fact]
    public void Partial_cleanup_phase_write_after_staged_validation_is_crash_idempotent()
    {
        var project = CasePath("partial-cleanup-state", "project");
        var generated = CasePath("partial-cleanup-state", "generated");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var plan = new MutationPlanBuilder(
                "partial-cleanup-state",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile("Schema/table.proto", Encoding.UTF8.GetBytes("schema"))
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/table.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.Staged)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        var transactionId = Guid.ParseExact(Path.GetFileName(journalDirectory), "N");
        var projectTransaction = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(project))!,
            $".exceldb-txn-{transactionId:N}-project");
        var statePath = Path.Combine(projectTransaction, "state.json");
        var state = JsonNode.Parse(File.ReadAllBytes(statePath))!.AsObject();
        state["phase"] = "cleanup";
        WriteCanonicalNode(statePath, state);

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.True(report.Succeeded, Format(report));
        Assert.False(File.Exists(Path.Combine(project, "Schema", "table.proto")));
        Assert.False(File.Exists(Path.Combine(generated, "Runtime", "table.g.cs")));
        Assert.False(Directory.Exists(journalDirectory));
    }

    [Fact]
    public void Cleanup_validation_failure_after_first_root_never_enters_rollback()
    {
        var project = CasePath("cleanup-failure", "project");
        var generated = CasePath("cleanup-failure", "generated");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var plan = new MutationPlanBuilder(
                "cleanup-failure",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile("Schema/table.proto", Encoding.UTF8.GetBytes("schema"))
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/table.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();
        var applier = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.RootCleaned)
                throw new InvalidDataException("Injected cleanup validation failure.");
        });

        var applied = applier.Apply(plan);

        Assert.True(applied.Succeeded, Format(applied));
        Assert.Contains(applied.Diagnostics, diagnostic => diagnostic.Code == "plan.cleanup-pending");
        Assert.Equal("schema", File.ReadAllText(Path.Combine(project, "Schema", "table.proto")));
        Assert.Equal("code", File.ReadAllText(Path.Combine(generated, "Runtime", "table.g.cs")));

        var recovered = MutationPlanApplier.RecoverPending(project);

        Assert.True(recovered.Succeeded, Format(recovered));
        Assert.Equal("schema", File.ReadAllText(Path.Combine(project, "Schema", "table.proto")));
        Assert.Equal("code", File.ReadAllText(Path.Combine(generated, "Runtime", "table.g.cs")));
    }

    [Fact]
    public void Recovery_retries_after_config_was_restored_before_rolledback_phase_was_persisted()
    {
        var project = CasePath("rollback-config-window", "project");
        var originalGenerated = Path.Combine(project, "Generated", "CSharp");
        var externalGenerated = CasePath("rollback-config-window", "external-generated");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, originalGenerated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var originalConfig = File.ReadAllBytes(projectFile);
        var candidateConfig = ExcelDbProject.Default
            .WithGeneratedCSharpDirectory(Path.GetFullPath(externalGenerated))
            .ToCanonicalJson();
        var plan = new MutationPlanBuilder(
                "rollback-config-window",
                "tests",
                project,
                externalGenerated,
                ContentFingerprint.FromBytes(originalConfig).Sha256,
                "catalog",
                1)
            .WriteFile(projectFile, candidateConfig)
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/owned.g.cs", Encoding.UTF8.GetBytes("generated"))
            .Build();
        var applyingCrash = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.ItemApplied
                && checkpoint.Root == PlanRootKind.GeneratedCSharp)
            {
                throw new SimulatedCrashException();
            }
        });
        Assert.Throws<SimulatedCrashException>(() => applyingCrash.Apply(plan));
        Assert.Equal(candidateConfig, File.ReadAllBytes(projectFile));
        var generatedFile = Path.Combine(externalGenerated, "Runtime", "owned.g.cs");
        Assert.True(File.Exists(generatedFile));

        var trigger = new MutationPlanBuilder(
                "recovery-trigger",
                "tests",
                project,
                externalGenerated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .Build();
        var rollbackCrash = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.RollbackCompleted)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => rollbackCrash.Apply(trigger));
        Assert.Equal(originalConfig, File.ReadAllBytes(projectFile));
        Assert.False(File.Exists(generatedFile));

        var recovered = MutationPlanApplier.RecoverPending(project);

        Assert.True(recovered.Succeeded, Format(recovered));
        Assert.Equal(originalConfig, File.ReadAllBytes(projectFile));
        Assert.False(File.Exists(generatedFile));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
    }

    [Fact]
    public void Direct_applier_rejects_external_root_not_authorized_by_installed_config()
    {
        var project = CasePath("direct-authority", "project");
        var configured = Path.Combine(project, "Generated", "CSharp");
        var victimRoot = CasePath("direct-authority", "victim");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, configured);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var plan = new MutationPlanBuilder(
                "direct-authority",
                "tests",
                project,
                victimRoot,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile(PlanRootKind.GeneratedCSharp, "owned.g.cs", Encoding.UTF8.GetBytes("must-not-write"))
            .Build();

        var report = new MutationPlanApplier().Apply(plan);

        Assert.False(report.Succeeded);
        Assert.False(File.Exists(Path.Combine(victimRoot, "owned.g.cs")));
    }

    [Fact]
    public void Direct_applier_rejects_project_config_delete()
    {
        var project = CasePath("config-delete", "project");
        var generated = Path.Combine(project, "Generated", "CSharp");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var original = File.ReadAllBytes(projectFile);
        var plan = new MutationPlanBuilder(
                "config-delete",
                "tests",
                project,
                generated,
                ContentFingerprint.FromBytes(original).Sha256,
                "catalog",
                1)
            .DeleteFile(projectFile)
            .Build();

        var report = new MutationPlanApplier().Apply(plan);

        Assert.False(report.Succeeded);
        Assert.Equal(original, File.ReadAllBytes(projectFile));
    }

    [Fact]
    public void Partial_root_bootstrap_cleans_only_existing_sealed_root()
    {
        var project = CasePath("partial-bootstrap", "project");
        var generated = CasePath("partial-bootstrap", "generated");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var plan = new MutationPlanBuilder(
                "partial-bootstrap",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile("Schema/table.proto", Encoding.UTF8.GetBytes("schema"))
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/table.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.RootPrepared
                && checkpoint.Root == PlanRootKind.Project)
            {
                throw new SimulatedCrashException();
            }
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        var transactionId = Guid.ParseExact(Path.GetFileName(journalDirectory), "N");
        var generatedTransaction = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(generated))!,
            $".exceldb-txn-{transactionId:N}-generatedcsharp");
        Assert.False(Directory.Exists(generatedTransaction));

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.True(report.Succeeded, Format(report));
        Assert.False(Directory.Exists(generatedTransaction));
        Assert.False(Directory.Exists(generated));
        Assert.False(File.Exists(Path.Combine(project, "Schema", "table.proto")));
    }

    [Theory]
    [InlineData(nameof(MutationPlanCheckpointKind.RootBootstrapTemporaryWritten))]
    [InlineData(nameof(MutationPlanCheckpointKind.RootBootstrapMarkerWritten))]
    [InlineData(nameof(MutationPlanCheckpointKind.RootBootstrapDirectoryCreated))]
    [InlineData(nameof(MutationPlanCheckpointKind.RootBootstrapDirectoriesCreated))]
    [InlineData(nameof(MutationPlanCheckpointKind.RootSealTemporaryWritten))]
    [InlineData(nameof(MutationPlanCheckpointKind.RootSealWritten))]
    public void Pre_seal_bootstrap_crashes_remove_only_capability_owned_external_state(
        string checkpointName)
    {
        var crashAt = Enum.Parse<MutationPlanCheckpointKind>(checkpointName);
        var container = CasePath("pre-seal-" + checkpointName);
        var project = Path.Combine(container, "project");
        var externalAnchor = Path.Combine(container, "external anchor");
        var missingParent = Path.Combine(externalAnchor, "外部 缺失 parent");
        var generated = Path.Combine(missingParent, "更深", "Generated CSharp");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(externalAnchor);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var plan = new MutationPlanBuilder(
                "pre-seal-bootstrap",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/table.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == crashAt
                && checkpoint.Root == PlanRootKind.GeneratedCSharp)
            {
                throw new SimulatedCrashException();
            }
        });

        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        var journalPath = Path.Combine(journalDirectory, "journal.json");
        var journal = JsonNode.Parse(File.ReadAllBytes(journalPath))!.AsObject();
        var seal = Assert.Single(journal["rootSeals"]!.AsArray())!.AsObject();
        var transactionPath = seal["transactionPath"]!.GetValue<string>();
        var nonce = seal["nonce"]!.GetValue<string>();
        var bootstrap = transactionPath + ".bootstrap";
        var bootstrapTemporary = bootstrap + $".{nonce}.tmp";
        Assert.True(File.Exists(bootstrap) || File.Exists(bootstrapTemporary));
        Assert.False(Directory.Exists(missingParent));

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.True(report.Succeeded, Format(report));
        Assert.False(Directory.Exists(transactionPath));
        Assert.False(File.Exists(bootstrap));
        Assert.False(File.Exists(bootstrapTemporary));
        Assert.False(Directory.Exists(missingParent));
        Assert.False(Directory.Exists(journalDirectory));
    }

    [Fact]
    public void Empty_same_name_bootstrap_directory_without_capability_is_preserved_as_forgery()
    {
        var container = CasePath("forged-empty-bootstrap");
        var project = Path.Combine(container, "project");
        var externalAnchor = Path.Combine(container, "external anchor");
        var generated = Path.Combine(externalAnchor, "missing", "Generated CSharp");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(externalAnchor);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var plan = new MutationPlanBuilder(
                "forged-empty-bootstrap",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/table.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.Prepared)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        var journalPath = Path.Combine(journalDirectory, "journal.json");
        var journal = JsonNode.Parse(File.ReadAllBytes(journalPath))!.AsObject();
        var transactionPath = Assert.Single(journal["rootSeals"]!.AsArray())!["transactionPath"]!.GetValue<string>();
        Directory.CreateDirectory(transactionPath);

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.False(report.Succeeded);
        Assert.True(Directory.Exists(transactionPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(transactionPath));
        Assert.True(File.Exists(journalPath));
    }

    [Fact]
    public void Modified_bootstrap_capability_blocks_cleanup_and_preserves_evidence()
    {
        var project = CasePath("modified-bootstrap", "project");
        var generated = CasePath("modified-bootstrap", "generated");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var plan = new MutationPlanBuilder(
                "modified-bootstrap",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/table.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.RootBootstrapDirectoriesCreated)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        var journalPath = Path.Combine(journalDirectory, "journal.json");
        var journal = JsonNode.Parse(File.ReadAllBytes(journalPath))!.AsObject();
        var transactionPath = Assert.Single(journal["rootSeals"]!.AsArray())!["transactionPath"]!.GetValue<string>();
        var bootstrap = transactionPath + ".bootstrap";
        File.WriteAllText(bootstrap, "modified");

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.False(report.Succeeded);
        Assert.True(Directory.Exists(transactionPath));
        Assert.Equal("modified", File.ReadAllText(bootstrap));
        Assert.True(File.Exists(journalPath));
    }

    [Fact]
    public void Prepared_recovery_uses_frozen_ancestor_transaction_path_without_creating_missing_parent_chain()
    {
        var container = CasePath("missing-parent-prepared");
        var project = Path.Combine(container, "project");
        var existingAnchor = Path.Combine(container, "external anchor");
        var missingParent = Path.Combine(existingAnchor, "外部 缺失 parent");
        var generated = Path.Combine(missingParent, "更深", "Generated CSharp");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(existingAnchor);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var plan = new MutationPlanBuilder(
                "missing-parent-prepared",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/table.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.RootPrepared)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        var journal = JsonNode.Parse(File.ReadAllBytes(Path.Combine(journalDirectory, "journal.json")))!.AsObject();
        var transactionPath = journal["rootSeals"]!.AsArray()[0]!["transactionPath"]!.GetValue<string>();
        Assert.Equal(existingAnchor, Path.GetDirectoryName(transactionPath), ignoreCase: true);
        Assert.Equal(2, journal["rootParentDirectories"]!.AsArray().Count);
        Assert.False(Directory.Exists(missingParent));

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.True(report.Succeeded, Format(report));
        Assert.False(Directory.Exists(missingParent));
        Assert.False(Directory.Exists(transactionPath));
    }

    [Fact]
    public void Applying_recovery_removes_every_owned_directory_in_deep_missing_parent_chain()
    {
        var container = CasePath("missing-parent-applying");
        var project = Path.Combine(container, "project");
        var existingAnchor = Path.Combine(container, "external anchor");
        var missingParent = Path.Combine(existingAnchor, "外部 缺失 parent");
        var generated = Path.Combine(missingParent, "更深", "Generated CSharp");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(existingAnchor);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var plan = new MutationPlanBuilder(
                "missing-parent-applying",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/table.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.ItemApplied
                && checkpoint.Root == PlanRootKind.GeneratedCSharp)
            {
                throw new SimulatedCrashException();
            }
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        Assert.Equal("code", File.ReadAllText(Path.Combine(generated, "Runtime", "table.g.cs")));

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.True(report.Succeeded, Format(report));
        Assert.False(Directory.Exists(missingParent));
        Assert.True(Directory.Exists(existingAnchor));
        Assert.Empty(Directory.EnumerateDirectories(existingAnchor, ".exceldb-txn-*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Forged_frozen_transaction_path_is_rejected_without_touching_real_or_victim_tree()
    {
        var existingAnchor = CasePath("forged-frozen-path");
        var project = Path.Combine(existingAnchor, "project");
        var generated = Path.Combine(existingAnchor, "missing", "Generated CSharp");
        var victimAnchor = CasePath("forged-frozen-path-victim");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(victimAnchor);
        var sentinel = Path.Combine(victimAnchor, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var plan = new MutationPlanBuilder(
                "forged-frozen-path",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile(PlanRootKind.GeneratedCSharp, "Runtime/table.g.cs", Encoding.UTF8.GetBytes("code"))
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.Staged)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        var journalPath = Path.Combine(journalDirectory, "journal.json");
        var journal = JsonNode.Parse(File.ReadAllBytes(journalPath))!.AsObject();
        var seal = journal["rootSeals"]!.AsArray()[0]!.AsObject();
        var realTransactionPath = seal["transactionPath"]!.GetValue<string>();
        seal["transactionPath"] = Path.Combine(victimAnchor, Path.GetFileName(realTransactionPath));
        WriteCanonicalNode(journalPath, journal);

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.False(report.Succeeded);
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.True(Directory.Exists(realTransactionPath));
        Assert.True(File.Exists(journalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initialization_recovery_envelope_is_not_mistaken_for_a_domain_mutation(
        bool afterRootPrepared)
    {
        var crashAt = afterRootPrepared
            ? MutationPlanCheckpointKind.RootPrepared
            : MutationPlanCheckpointKind.Prepared;
        var project = CasePath("init-envelope-" + crashAt, "project");
        Directory.CreateDirectory(project);
        var generated = Path.Combine(project, "Generated", "CSharp");
        var plan = new MutationPlanBuilder(
                "init-envelope",
                "tests",
                project,
                generated,
                "missing-config",
                "catalog",
                0)
            .CreateDirectory(".exceldb")
            .CreateDirectory(".exceldb/recovery")
            .CreateDirectory("Schema")
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == crashAt)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.True(report.Succeeded, Format(report));
        Assert.False(Directory.Exists(Path.Combine(project, "Schema")));
        Assert.False(File.Exists(Path.Combine(project, ExcelDbProject.RelativeFilePath)));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
    }

    [Fact]
    public void Unsealed_empty_external_cleanup_tombstone_is_preserved()
    {
        var project = CasePath("unsealed-tomb", "project");
        var generated = CasePath("unsealed-tomb", "generated");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var plan = new MutationPlanBuilder(
                "unsealed-tomb",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile(PlanRootKind.GeneratedCSharp, "victim.g.cs", Encoding.UTF8.GetBytes("content"))
            .Build();
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.Staged)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        var transactionId = Guid.ParseExact(Path.GetFileName(journalDirectory), "N");
        var transactionRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(generated))!,
            $".exceldb-txn-{transactionId:N}-generatedcsharp");
        var cleanupRoot = transactionRoot + ".cleanup";
        Directory.Delete(transactionRoot, recursive: true);
        Directory.CreateDirectory(cleanupRoot);

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.True(report.Succeeded, Format(report));
        Assert.True(Directory.Exists(cleanupRoot));
        Assert.False(Directory.Exists(journalDirectory));
    }

    [Fact]
    public void Empty_guid_and_exact_initial_atomic_tmp_are_idempotently_cleaned()
    {
        var recovery = Path.Combine(CasePath("tmp"), ".exceldb", "recovery");
        var empty = Path.Combine(recovery, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        var interrupted = Path.Combine(recovery, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(interrupted);
        File.WriteAllText(Path.Combine(interrupted, $".journal.json.{Guid.NewGuid():N}.tmp"), "partial-1");
        File.WriteAllText(Path.Combine(interrupted, $".journal.json.{Guid.NewGuid():N}.tmp"), "partial-2");

        var report = MutationPlanApplier.RecoverPending(CasePath("tmp"));

        Assert.True(report.Succeeded, Format(report));
        Assert.False(Directory.Exists(empty));
        Assert.False(Directory.Exists(interrupted));
    }

    [Fact]
    public void Legacy_absolute_journal_is_frozen_and_cannot_delete_victim()
    {
        var project = CasePath("legacy", "project");
        Directory.CreateDirectory(project);
        var victim = CasePath("legacy", "victim.txt");
        File.WriteAllText(victim, "keep");
        var legacy = Path.Combine(Path.GetDirectoryName(project)!, ".exceldb-txn-legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            projectRoot = Path.GetFullPath(project),
            phase = "applying",
            items = new[] { new { index = 0, destination = victim, backupPath = victim + ".backup", hadOriginal = false } },
            createdDirectories = Array.Empty<string>(),
        }, JournalJson));

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Message.Contains("frozen", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("keep", File.ReadAllText(victim));
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void Reparse_journal_directory_is_blocked_without_traversal()
    {
        var project = CasePath("reparse", "project");
        var outside = CasePath("reparse", "outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        var journalLink = Path.Combine(project, ".exceldb", "recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(journalLink)!);
        try
        {
            Directory.CreateSymbolicLink(journalLink, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var report = MutationPlanApplier.RecoverPending(project);

        Assert.False(report.Succeeded);
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Exceldb_junction_is_blocked_before_recovery_traversal()
    {
        var project = CasePath("internal-reparse", "project");
        var outside = CasePath("internal-reparse", "outside");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        var internalLink = Path.Combine(project, ".exceldb");
        try
        {
            Directory.CreateSymbolicLink(internalLink, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        try
        {
            var report = MutationPlanApplier.RecoverPending(project);

            Assert.False(report.Succeeded);
            Assert.Equal("keep", File.ReadAllText(sentinel));
        }
        finally
        {
            Directory.Delete(internalLink);
        }
    }

    [Fact]
    public async Task AcquireMany_serializes_two_projects_that_share_an_external_root()
    {
        var firstProject = CasePath("lease", "first");
        var secondProject = CasePath("lease", "second");
        var shared = CasePath("lease", "shared-generated");
        using var owner = ProjectOperationLease.AcquireMany([firstProject, shared]);

        var competing = await Task.Run(() => Record.Exception(() =>
            ProjectOperationLease.AcquireMany(
                [secondProject, shared],
                TimeSpan.FromMilliseconds(200))));

        Assert.IsType<ProjectBusyException>(competing);
    }

    [Fact]
    public void Declared_root_below_reparse_ancestor_is_rejected_before_lease_or_mutation()
    {
        var target = CasePath("ancestor-reparse", "target");
        var alias = CasePath("ancestor-reparse", "alias");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(alias, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        try
        {
            var declared = Path.Combine(alias, "project");
            var exception = Assert.Throws<InvalidDataException>(() => new MutationPlanBuilder(
                "reparse-ancestor",
                "tests",
                declared,
                declared,
                "config",
                "catalog",
                0));
            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    private CrashFixture CreateAppliedCrash(string name, bool hadOriginal)
    {
        var project = CasePath(name, "project");
        var generated = Path.Combine(project, "Generated", "CSharp");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, generated);
        var projectFile = Path.Combine(project, ExcelDbProject.RelativeFilePath);
        var destination = Path.Combine(project, "target.txt");
        var original = Encoding.UTF8.GetBytes("original");
        var replacement = Encoding.UTF8.GetBytes("replacement");
        if (hadOriginal)
            File.WriteAllBytes(destination, original);
        var plan = new MutationPlanBuilder(
                "security-test",
                "tests",
                project,
                generated,
                ContentFingerprint.FromFile(projectFile).Sha256,
                "catalog",
                1)
            .Observe(projectFile)
            .WriteFile(destination, replacement)
            .Build();
        var mutation = plan.Mutations.Select((item, index) => (item, index)).Single(pair =>
            pair.item.Kind == FileMutationKind.WriteFile
            && string.Equals(pair.item.RelativePath, "target.txt", StringComparison.Ordinal));
        var crashing = new MutationPlanApplier(checkpoint =>
        {
            if (checkpoint.Kind == MutationPlanCheckpointKind.ItemApplied
                && checkpoint.ItemIndex == mutation.index)
                throw new SimulatedCrashException();
        });
        Assert.Throws<SimulatedCrashException>(() => crashing.Apply(plan));
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(Path.Combine(project, ".exceldb", "recovery")));
        var transactionId = Guid.ParseExact(Path.GetFileName(journalDirectory), "N");
        var transactionRoot = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(project))!,
            $".exceldb-txn-{transactionId:N}-project");
        return new CrashFixture(
            project,
            destination,
            original,
            replacement,
            journalDirectory,
            transactionRoot,
            Path.Combine(transactionRoot, "backup", mutation.index.ToString()));
    }

    private static void WriteCanonicalNode(string path, JsonNode node) =>
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(node.ToJsonString(JournalJson)));

    private Fixture CreateFixture(string name, bool hadOriginal)
    {
        var project = CasePath(name, "project");
        var generated = Path.Combine(project, "Generated", "CSharp");
        Directory.CreateDirectory(project);
        WriteProjectConfig(project, generated);
        var destination = Path.Combine(project, "target.txt");
        var original = Encoding.UTF8.GetBytes("original");
        var replacement = Encoding.UTF8.GetBytes("replacement");
        if (hadOriginal)
            File.WriteAllBytes(destination, original);
        var plan = new MutationPlanBuilder(
                "security-test",
                "tests",
                project,
                generated,
                "config",
                "catalog",
                1)
            .WriteFile("target.txt", replacement)
            .Build();
        var pair = plan.Mutations.Select((mutation, index) => (mutation, index)).Single();
        var transactionId = Guid.NewGuid();
        var journalDirectory = Path.Combine(project, ".exceldb", "recovery", transactionId.ToString("N"));
        var journal = new TestJournal
        {
            FormatVersion = 2,
            TransactionId = transactionId,
            PlanHash = plan.PlanHash,
            PlanBase64 = Convert.ToBase64String(MutationPlanCodec.Serialize(plan)),
            ProjectRoot = Path.GetFullPath(project),
            GeneratedCSharpRoot = Path.GetFullPath(generated),
            Phase = "prepared",
            Items =
            [
                new TestJournalItem
                {
                    Index = pair.index,
                    Root = PlanRootKind.Project,
                    RelativePath = pair.mutation.RelativePath,
                    Kind = FileMutationKind.WriteFile,
                    ContentBase64 = pair.mutation.ContentBase64,
                    ContentSha256 = pair.mutation.ContentSha256,
                    HadOriginal = hadOriginal,
                    OriginalSha256 = hadOriginal ? ContentFingerprint.FromBytes(original).Sha256 : null,
                    NewSha256 = pair.mutation.ContentSha256,
                    OriginalAttributes = hadOriginal ? (int)FileAttributes.Normal : null,
                    State = "pending",
                },
            ],
        };
        return new Fixture(
            project,
            destination,
            original,
            replacement,
            journalDirectory,
            Path.Combine(Path.GetDirectoryName(project)!, $".exceldb-txn-{transactionId:N}-project"),
            journal);
    }

    private static void InstallNewDestination(Fixture fixture) =>
        File.WriteAllBytes(fixture.Destination, fixture.NewBytes);

    private static void CreateTransactionDirectories(Fixture fixture)
    {
        Directory.CreateDirectory(Path.Combine(fixture.TransactionRoot, "staged"));
        Directory.CreateDirectory(Path.Combine(fixture.TransactionRoot, "backup"));
    }

    private static void WriteJournal(Fixture fixture)
    {
        Directory.CreateDirectory(fixture.JournalDirectory);
        File.WriteAllBytes(fixture.JournalPath, JsonSerializer.SerializeToUtf8Bytes(fixture.Journal, JournalJson));
    }

    private static void WriteProjectConfig(string projectRoot, string generatedCSharpRoot)
    {
        var path = Path.Combine(projectRoot, ExcelDbProject.RelativeFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ExcelDbProject.Default
            .WithGeneratedCSharpDirectory(Path.GetFullPath(generatedCSharpRoot))
            .ToCanonicalJson());
    }

    private string CasePath(params string[] parts)
    {
        var path = _root;
        foreach (var part in parts)
            path = Path.Combine(path, part);
        return path;
    }

    private static string Format(ExcelDb.Core.Diagnostics.OperationReport report) =>
        string.Join(Environment.NewLine, report.Diagnostics.Select(static diagnostic => diagnostic.Message));

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private sealed record Fixture(
        string ProjectRoot,
        string Destination,
        byte[] OriginalBytes,
        byte[] NewBytes,
        string JournalDirectory,
        string TransactionRoot,
        TestJournal Journal)
    {
        public string JournalPath => Path.Combine(JournalDirectory, "journal.json");
    }

    private sealed record CrashFixture(
        string ProjectRoot,
        string Destination,
        byte[] OriginalBytes,
        byte[] NewBytes,
        string JournalDirectory,
        string TransactionRoot,
        string BackupPath)
    {
        public string JournalPath => Path.Combine(JournalDirectory, "journal.json");
    }

    private sealed class TestJournal
    {
        public int FormatVersion { get; set; }
        public Guid TransactionId { get; set; }
        public string PlanHash { get; set; } = string.Empty;
        public string PlanBase64 { get; set; } = string.Empty;
        public string ProjectRoot { get; set; } = string.Empty;
        public string GeneratedCSharpRoot { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public List<TestJournalItem> Items { get; set; } = [];
        public List<object> CreatedDirectories { get; set; } = [];
    }

    private sealed class TestJournalItem
    {
        public int Index { get; set; }
        public PlanRootKind Root { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public FileMutationKind Kind { get; set; }
        public string? ContentBase64 { get; set; }
        public string? ContentSha256 { get; set; }
        public bool HadOriginal { get; set; }
        public string? OriginalSha256 { get; set; }
        public string? NewSha256 { get; set; }
        public int? OriginalAttributes { get; set; }
        public string State { get; set; } = string.Empty;
    }

    private sealed class SimulatedCrashException : Exception;
}
