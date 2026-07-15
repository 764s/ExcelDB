using System.Text;
using System.Text.Json;
using ExcelDb.Core.IO;
using ExcelDb.Tooling.Plans;

namespace ExcelDb.Pipeline.Tests;

public sealed class ExternalBytesTransactionTests : IDisposable
{
    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _projectRoot = Path.Combine(
        Path.GetTempPath(),
        "exceldb-external-bytes-project",
        Guid.NewGuid().ToString("N"));
    private readonly string _externalRoot = Path.Combine(
        Path.GetTempPath(),
        "exceldb-external-bytes-output",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Prepared_recovery_discards_staging_without_touching_original_pair()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Prepared);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.stage"), fixture.NewBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.stage"), fixture.NewManifest);
        WriteJournal(fixture);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.True(report.Applied);
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.OldManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(Directory.Exists(fixture.TransactionDirectory));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Theory]
    [InlineData(ExternalBytesPhase.Prepared)]
    [InlineData(ExternalBytesPhase.Applying)]
    public void Forged_journal_cannot_authorize_an_arbitrary_external_transaction_directory(string phase)
    {
        var fixture = CreateFixture(phase);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllText(Path.Combine(fixture.TransactionDirectory, "sentinel.txt"), "must-remain");
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.stage"), fixture.NewBytes);
        WriteProjectJournalOnly(fixture);
        var before = SnapshotTree(_externalRoot);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "convert.external-recovery-failed");
        Assert.Equal(before, SnapshotTree(_externalRoot));
        Assert.True(File.Exists(Path.Combine(fixture.JournalDirectory, ExternalBytesTransaction.JournalFileName)));
    }

    [Fact]
    public void Tampered_capability_seal_blocks_recovery_without_changing_external_state()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.stage"), fixture.NewBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.stage"), fixture.NewManifest);
        WriteJournal(fixture);
        File.AppendAllText(
            Path.Combine(fixture.TransactionDirectory, ExternalBytesTransaction.CapabilitySealFileName),
            " ");
        var before = SnapshotTree(_externalRoot);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "convert.external-recovery-failed");
        Assert.Equal(before, SnapshotTree(_externalRoot));
    }

    [Fact]
    public void Unknown_transaction_file_blocks_cleanup_without_deleting_any_external_evidence()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Prepared);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.stage"), fixture.NewBytes);
        WriteJournal(fixture);
        File.WriteAllText(Path.Combine(fixture.TransactionDirectory, "unknown.sentinel"), "must-remain");
        var before = SnapshotTree(_externalRoot);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "convert.external-recovery-failed");
        Assert.Equal(before, SnapshotTree(_externalRoot));
    }

    [Fact]
    public void Downgraded_applying_journal_is_blocked_without_deleting_backup_or_destination()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.Move(fixture.BytesPath, Path.Combine(fixture.TransactionDirectory, "0.backup"));
        File.WriteAllBytes(fixture.BytesPath, fixture.NewBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.stage"), fixture.NewManifest);
        WriteJournal(fixture);
        fixture.Journal.Phase = ExternalBytesPhase.Prepared;
        WriteProjectJournalOnly(fixture);
        var before = SnapshotTree(_externalRoot);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "convert.external-recovery-failed");
        Assert.Equal(before, SnapshotTree(_externalRoot));
    }

    [Fact]
    public void Applying_stage_proves_new_destination_was_written_by_a_competing_writer()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        var bytesItem = fixture.Journal.Items[0];
        bytesItem.HadOriginal = false;
        bytesItem.OriginalSha256 = null;
        bytesItem.OriginalAttributes = null;
        fixture.Journal.CapabilitySealSha256 = ContentFingerprint.FromBytes(SerializeSeal(fixture.Journal)).Sha256;
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.BytesPath)!);
        File.WriteAllBytes(fixture.ManifestPath, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.stage"), fixture.NewBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.stage"), fixture.NewManifest);
        WriteJournal(fixture);
        File.WriteAllBytes(fixture.BytesPath, fixture.NewBytes);
        var before = SnapshotTree(_externalRoot);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "convert.external-recovery-failed");
        Assert.Equal(before, SnapshotTree(_externalRoot));
    }

    [Fact]
    public void Backup_plus_stage_proves_replacement_was_not_installed_even_when_hash_matches()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.stage"), fixture.NewBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.stage"), fixture.NewManifest);
        File.Move(fixture.BytesPath, Path.Combine(fixture.TransactionDirectory, "0.backup"));
        WriteJournal(fixture);
        File.WriteAllBytes(fixture.BytesPath, fixture.NewBytes);
        var before = SnapshotTree(_externalRoot);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "convert.external-recovery-failed");
        Assert.Equal(before, SnapshotTree(_externalRoot));
    }

    [Fact]
    public void Recovery_continues_after_installed_destination_was_deleted_before_backup_restore()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.BytesPath)!);
        File.WriteAllBytes(fixture.ManifestPath, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.backup"), fixture.OldBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.stage"), fixture.NewManifest);
        WriteJournal(fixture);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.OldManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recovery_idempotently_restores_original_attributes_at_both_crash_boundaries(bool stageRemains)
    {
        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        fixture.Journal.Items[0].OriginalAttributes = (int)FileAttributes.ReadOnly;
        fixture.Journal.CapabilitySealSha256 = ContentFingerprint.FromBytes(SerializeSeal(fixture.Journal)).Sha256;
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        if (stageRemains)
            File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.stage"), fixture.NewBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.stage"), fixture.NewManifest);
        WriteJournal(fixture);
        File.SetAttributes(fixture.BytesPath, FileAttributes.Normal);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.True((File.GetAttributes(fixture.BytesPath) & FileAttributes.ReadOnly) != 0);
    }

    [Fact]
    public void Applying_recovery_restores_original_pair_after_first_file_was_installed()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.Move(fixture.BytesPath, Path.Combine(fixture.TransactionDirectory, "0.backup"));
        File.WriteAllBytes(fixture.BytesPath, fixture.NewBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.stage"), fixture.NewManifest);
        WriteJournal(fixture);
        File.WriteAllText(
            Path.Combine(fixture.JournalDirectory, $".journal.json.{Guid.NewGuid():N}.tmp"),
            "abandoned-atomic-write");

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.OldManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(Directory.Exists(fixture.TransactionDirectory));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Fact]
    public void Committed_recovery_keeps_new_pair_and_removes_only_transaction_state()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Committed);
        WritePair(fixture.BytesPath, fixture.NewBytes, fixture.NewManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.backup"), fixture.OldBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.backup"), fixture.OldManifest);
        WriteJournal(fixture);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.NewManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(Directory.Exists(fixture.TransactionDirectory));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Fact]
    public void Journal_first_applying_transition_with_only_atomic_temp_aborts_as_prepared()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Prepared);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.stage"), fixture.NewBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.stage"), fixture.NewManifest);
        WriteJournal(fixture);
        fixture.Journal.Phase = ExternalBytesPhase.Applying;
        WriteProjectJournalOnly(fixture);
        File.WriteAllText(
            Path.Combine(fixture.TransactionDirectory, $".applying.marker.json.{Guid.NewGuid():N}.tmp"),
            "partial-marker");

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.OldManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(Directory.Exists(fixture.TransactionDirectory));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Fact]
    public void Committed_journal_before_terminal_marker_recovers_as_applying_and_rolls_back()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        WritePair(fixture.BytesPath, fixture.NewBytes, fixture.NewManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.backup"), fixture.OldBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.backup"), fixture.OldManifest);
        WriteJournal(fixture);
        fixture.Journal.Phase = ExternalBytesPhase.Committed;
        WriteProjectJournalOnly(fixture);
        File.WriteAllText(
            Path.Combine(fixture.TransactionDirectory, $".committed.marker.json.{Guid.NewGuid():N}.tmp"),
            "partial-marker");

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.OldManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(Directory.Exists(fixture.TransactionDirectory));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Fact]
    public void Rolled_back_journal_with_only_atomic_marker_temp_finishes_idempotent_rollback()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.stage"), fixture.NewBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.stage"), fixture.NewManifest);
        WriteJournal(fixture);
        fixture.Journal.Phase = ExternalBytesPhase.RolledBack;
        WriteProjectJournalOnly(fixture);
        File.WriteAllText(
            Path.Combine(fixture.TransactionDirectory, $".rolled-back.marker.json.{Guid.NewGuid():N}.tmp"),
            "partial-marker");

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.OldManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(Directory.Exists(fixture.TransactionDirectory));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Fact]
    public void Full_cleanup_tombstone_is_idempotently_finished()
    {
        var fixture = CreateCommittedFixtureInTransactionDirectory();
        Directory.Move(fixture.TransactionDirectory, fixture.CleanupDirectory);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.NewManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(Directory.Exists(fixture.CleanupDirectory));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Fact]
    public void Partially_cleaned_tombstone_with_seal_is_idempotently_finished()
    {
        var fixture = CreateCommittedFixtureInTransactionDirectory();
        Directory.Move(fixture.TransactionDirectory, fixture.CleanupDirectory);
        File.Delete(Path.Combine(fixture.CleanupDirectory, "0.backup"));
        File.Delete(Path.Combine(fixture.CleanupDirectory, "1.backup"));
        File.Delete(Path.Combine(fixture.CleanupDirectory, "applying.marker.json"));

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.NewManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(Directory.Exists(fixture.CleanupDirectory));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Fact]
    public void Empty_sealless_cleanup_tombstone_unblocks_project_without_authorizing_external_delete()
    {
        var fixture = CreateCommittedFixtureInTransactionDirectory();
        Directory.Move(fixture.TransactionDirectory, fixture.CleanupDirectory);
        foreach (var file in Directory.EnumerateFiles(fixture.CleanupDirectory))
            File.Delete(file);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.NewManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.True(Directory.Exists(fixture.CleanupDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.CleanupDirectory));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Fact]
    public void Missing_cleanup_transaction_after_domain_commit_only_removes_project_journal()
    {
        var fixture = CreateCommittedFixtureInTransactionDirectory();
        foreach (var file in Directory.EnumerateFiles(fixture.TransactionDirectory))
            File.Delete(file);
        Directory.Delete(fixture.TransactionDirectory);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(fixture.NewBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.NewManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Fact]
    public void Failed_recovery_retains_journal_and_backup_until_a_later_retry_succeeds()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.Move(fixture.BytesPath, Path.Combine(fixture.TransactionDirectory, "0.backup"));
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.stage"), fixture.NewBytes);
        Directory.CreateDirectory(fixture.BytesPath);
        WriteJournal(fixture);

        var failed = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.False(failed.Succeeded);
        Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.Code == "convert.external-recovery-failed");
        Assert.True(File.Exists(Path.Combine(fixture.TransactionDirectory, "0.backup")));
        Assert.True(File.Exists(Path.Combine(fixture.JournalDirectory, ExternalBytesTransaction.JournalFileName)));

        Directory.Delete(fixture.BytesPath);
        var recovered = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(recovered.Succeeded, Format(recovered));
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.OldManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.False(Directory.Exists(fixture.TransactionDirectory));
        Assert.False(Directory.Exists(fixture.JournalDirectory));
    }

    [Fact]
    public void Write_failure_after_installing_bytes_rolls_back_both_original_files()
    {
        var bytesPath = Path.Combine(_externalRoot, "release", "config.bytes");
        var oldBytes = Encoding.UTF8.GetBytes("old-bytes");
        var oldManifest = Encoding.UTF8.GetBytes("old-manifest");
        var newBytes = Encoding.UTF8.GetBytes("new-bytes");
        var newManifest = Encoding.UTF8.GetBytes("new-manifest");
        WritePair(bytesPath, oldBytes, oldManifest);
        var transaction = new ExternalBytesTransaction(
            "tests",
            checkpoint =>
            {
                if (checkpoint == ExternalBytesCheckpoint.BytesInstalled)
                    throw new IOException("simulated interruption");
            });

        var report = transaction.Write(
            _projectRoot,
            bytesPath,
            newBytes,
            newManifest,
            "client",
            "test-content-hash");

        Assert.False(report.Succeeded);
        Assert.Equal(oldBytes, File.ReadAllBytes(bytesPath));
        Assert.Equal(oldManifest, File.ReadAllBytes(bytesPath + ".manifest.json"));
        Assert.False(Directory.Exists(Path.Combine(
            _projectRoot,
            ".exceldb",
            "recovery",
            ExternalBytesTransaction.RecoverySubtreeName)));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(bytesPath)!, ".exceldb-bytes-txn-*"));
    }

    [Theory]
    [InlineData(nameof(ExternalBytesCheckpoint.Prepared))]
    [InlineData(nameof(ExternalBytesCheckpoint.BootstrapCapabilityTemporaryWritten))]
    [InlineData(nameof(ExternalBytesCheckpoint.BootstrapCapabilityWritten))]
    [InlineData(nameof(ExternalBytesCheckpoint.TransactionDirectoryCreated))]
    [InlineData(nameof(ExternalBytesCheckpoint.CapabilitySealInstalled))]
    public void Journal_first_bootstrap_crashes_are_recovered_without_orphaning_external_state(
        string checkpointName)
    {
        var crashAt = Enum.Parse<ExternalBytesCheckpoint>(checkpointName);
        var bytesPath = Path.Combine(_externalRoot, checkpointName, "config.bytes");
        var oldBytes = Encoding.UTF8.GetBytes("old-bytes");
        var oldManifest = Encoding.UTF8.GetBytes("old-manifest");
        WritePair(bytesPath, oldBytes, oldManifest);
        var transaction = new ExternalBytesTransaction(
            "tests",
            checkpoint =>
            {
                if (checkpoint == crashAt)
                    throw new SimulatedCrashException();
            });

        Assert.Throws<SimulatedCrashException>(() => transaction.Write(
            _projectRoot,
            bytesPath,
            Encoding.UTF8.GetBytes("new-bytes"),
            Encoding.UTF8.GetBytes("new-manifest"),
            "client",
            "test-content-hash"));
        var recoveryRoot = Path.Combine(
            _projectRoot,
            ".exceldb",
            "recovery",
            ExternalBytesTransaction.RecoverySubtreeName);
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(recoveryRoot));
        var journalPath = Path.Combine(journalDirectory, ExternalBytesTransaction.JournalFileName);
        var journal = JsonSerializer.Deserialize<ExternalBytesJournal>(File.ReadAllBytes(journalPath), JournalJson)!;
        var transactionDirectory = Path.Combine(
            journal.OutputDirectory,
            $".exceldb-bytes-txn-{journal.TransactionId:N}");
        var bootstrap = transactionDirectory + ".bootstrap";
        var bootstrapTemporary = bootstrap + $".{journal.CapabilityNonce}.tmp";
        if (crashAt == ExternalBytesCheckpoint.Prepared)
        {
            Assert.False(Directory.Exists(transactionDirectory));
            Assert.False(File.Exists(bootstrap));
            Assert.False(File.Exists(bootstrapTemporary));
        }

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.Equal(oldBytes, File.ReadAllBytes(bytesPath));
        Assert.Equal(oldManifest, File.ReadAllBytes(bytesPath + ".manifest.json"));
        Assert.False(Directory.Exists(transactionDirectory));
        Assert.False(File.Exists(bootstrap));
        Assert.False(File.Exists(bootstrapTemporary));
        Assert.False(Directory.Exists(journalDirectory));
    }

    [Fact]
    public void Empty_same_name_external_transaction_without_capability_is_preserved_as_forgery()
    {
        var bytesPath = Path.Combine(_externalRoot, "forged-empty", "config.bytes");
        var oldBytes = Encoding.UTF8.GetBytes("old-bytes");
        var oldManifest = Encoding.UTF8.GetBytes("old-manifest");
        WritePair(bytesPath, oldBytes, oldManifest);
        var transaction = new ExternalBytesTransaction(
            "tests",
            checkpoint =>
            {
                if (checkpoint == ExternalBytesCheckpoint.Prepared)
                    throw new SimulatedCrashException();
            });
        Assert.Throws<SimulatedCrashException>(() => transaction.Write(
            _projectRoot,
            bytesPath,
            Encoding.UTF8.GetBytes("new-bytes"),
            Encoding.UTF8.GetBytes("new-manifest"),
            "client",
            "test-content-hash"));
        var recoveryRoot = Path.Combine(
            _projectRoot,
            ".exceldb",
            "recovery",
            ExternalBytesTransaction.RecoverySubtreeName);
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(recoveryRoot));
        var journal = JsonSerializer.Deserialize<ExternalBytesJournal>(File.ReadAllBytes(Path.Combine(
            journalDirectory,
            ExternalBytesTransaction.JournalFileName)), JournalJson)!;
        var transactionDirectory = Path.Combine(
            journal.OutputDirectory,
            $".exceldb-bytes-txn-{journal.TransactionId:N}");
        Directory.CreateDirectory(transactionDirectory);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.False(report.Succeeded);
        Assert.True(Directory.Exists(transactionDirectory));
        Assert.Empty(Directory.EnumerateFileSystemEntries(transactionDirectory));
        Assert.True(Directory.Exists(journalDirectory));
        Assert.Equal(oldBytes, File.ReadAllBytes(bytesPath));
        Assert.Equal(oldManifest, File.ReadAllBytes(bytesPath + ".manifest.json"));
    }

    [Fact]
    public void Modified_bootstrap_capability_and_unknown_transaction_file_are_zero_delete_blockers()
    {
        var bytesPath = Path.Combine(_externalRoot, "modified-bootstrap", "config.bytes");
        var oldBytes = Encoding.UTF8.GetBytes("old-bytes");
        var oldManifest = Encoding.UTF8.GetBytes("old-manifest");
        WritePair(bytesPath, oldBytes, oldManifest);
        var transaction = new ExternalBytesTransaction(
            "tests",
            checkpoint =>
            {
                if (checkpoint == ExternalBytesCheckpoint.TransactionDirectoryCreated)
                    throw new SimulatedCrashException();
            });
        Assert.Throws<SimulatedCrashException>(() => transaction.Write(
            _projectRoot,
            bytesPath,
            Encoding.UTF8.GetBytes("new-bytes"),
            Encoding.UTF8.GetBytes("new-manifest"),
            "client",
            "test-content-hash"));
        var recoveryRoot = Path.Combine(
            _projectRoot,
            ".exceldb",
            "recovery",
            ExternalBytesTransaction.RecoverySubtreeName);
        var journalDirectory = Assert.Single(Directory.EnumerateDirectories(recoveryRoot));
        var journal = JsonSerializer.Deserialize<ExternalBytesJournal>(File.ReadAllBytes(Path.Combine(
            journalDirectory,
            ExternalBytesTransaction.JournalFileName)), JournalJson)!;
        var transactionDirectory = Path.Combine(
            journal.OutputDirectory,
            $".exceldb-bytes-txn-{journal.TransactionId:N}");
        var bootstrap = transactionDirectory + ".bootstrap";
        File.WriteAllText(bootstrap, "modified");
        var sentinel = Path.Combine(transactionDirectory, "unknown.sentinel");
        File.WriteAllText(sentinel, "keep");
        var before = SnapshotTree(_externalRoot);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.False(report.Succeeded);
        Assert.Equal(before, SnapshotTree(_externalRoot));
        Assert.Equal("modified", File.ReadAllText(bootstrap));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.True(Directory.Exists(journalDirectory));
    }

    [Fact]
    public void Project_inspection_recovers_external_bytes_before_reading_project_state()
    {
        var service = new ExcelDbProjectService("tests");
        var location = new ProjectLocation(_projectRoot);
        var initialize = service.PlanInitialize(new InitializeProjectRequest(location));
        var initialized = service.Apply(initialize);
        Assert.True(initialized.Succeeded, Format(initialized));

        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.Move(fixture.BytesPath, Path.Combine(fixture.TransactionDirectory, "0.backup"));
        File.WriteAllBytes(fixture.BytesPath, fixture.NewBytes);
        WriteJournal(fixture);

        var inspection = service.Inspect(location);

        Assert.NotEqual(ProjectStatus.RecoveryRequired, inspection.Status);
        Assert.Contains(inspection.Diagnostics, diagnostic => diagnostic.Code == "convert.external-recovered");
        Assert.Equal(fixture.OldBytes, File.ReadAllBytes(fixture.BytesPath));
        Assert.Equal(fixture.OldManifest, File.ReadAllBytes(fixture.ManifestPath));
    }

    [Fact]
    public void Empty_external_recovery_guid_directory_is_idempotent_cleanup_state()
    {
        var emptyJournalDirectory = Path.Combine(
            _projectRoot,
            ".exceldb",
            "recovery",
            ExternalBytesTransaction.RecoverySubtreeName,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyJournalDirectory);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.True(report.Applied);
        Assert.False(Directory.Exists(emptyJournalDirectory));
    }

    [Fact]
    public void Exact_abandoned_atomic_journal_temp_without_journal_is_safe_project_only_cleanup()
    {
        var journalDirectory = Path.Combine(
            _projectRoot,
            ".exceldb",
            "recovery",
            ExternalBytesTransaction.RecoverySubtreeName,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(journalDirectory);
        File.WriteAllText(
            Path.Combine(journalDirectory, $".journal.json.{Guid.NewGuid():N}.tmp"),
            "partial");

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.True(report.Succeeded, Format(report));
        Assert.True(report.Applied);
        Assert.False(Directory.Exists(journalDirectory));
    }

    [Fact]
    public void Unknown_project_journal_file_blocks_before_any_external_rollback()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Applying);
        WritePair(fixture.BytesPath, fixture.OldBytes, fixture.OldManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.Move(fixture.BytesPath, Path.Combine(fixture.TransactionDirectory, "0.backup"));
        File.WriteAllBytes(fixture.BytesPath, fixture.NewBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.stage"), fixture.NewManifest);
        WriteJournal(fixture);
        File.WriteAllText(Path.Combine(fixture.JournalDirectory, "unknown.txt"), "must-remain");
        var before = SnapshotTree(_externalRoot);

        var report = ExternalBytesTransaction.RecoverPending(_projectRoot);

        Assert.False(report.Succeeded);
        Assert.Equal(before, SnapshotTree(_externalRoot));
        Assert.True(File.Exists(Path.Combine(fixture.JournalDirectory, "unknown.txt")));
    }

    [Fact]
    public async Task Project_recovery_waits_for_live_external_write_instead_of_rolling_it_back()
    {
        var bytesPath = Path.Combine(_externalRoot, "lease", "config.bytes");
        var oldBytes = Encoding.UTF8.GetBytes("old-bytes");
        var oldManifest = Encoding.UTF8.GetBytes("old-manifest");
        var newBytes = Encoding.UTF8.GetBytes("new-bytes");
        var newManifest = Encoding.UTF8.GetBytes("new-manifest");
        WritePair(bytesPath, oldBytes, oldManifest);
        using var applying = new ManualResetEventSlim();
        using var finishWrite = new ManualResetEventSlim();
        using var recoveryStarted = new ManualResetEventSlim();
        var transaction = new ExternalBytesTransaction(
            "tests",
            checkpoint =>
            {
                if (checkpoint != ExternalBytesCheckpoint.Applying)
                    return;
                applying.Set();
                Assert.True(finishWrite.Wait(TimeSpan.FromSeconds(10)));
            });

        var writeTask = Task.Run(() => transaction.Write(
            _projectRoot,
            bytesPath,
            newBytes,
            newManifest,
            "client",
            "test-content-hash"));
        Assert.True(applying.Wait(TimeSpan.FromSeconds(10)));

        var recoveryTask = Task.Run(() =>
        {
            recoveryStarted.Set();
            return ProjectRecovery.RecoverPending(_projectRoot);
        });
        Assert.True(recoveryStarted.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotSame(recoveryTask, await Task.WhenAny(recoveryTask, Task.Delay(250)));

        finishWrite.Set();
        var write = await writeTask.WaitAsync(TimeSpan.FromSeconds(10));
        var recovery = await recoveryTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(write.Succeeded, Format(write));
        Assert.True(recovery.Succeeded, Format(recovery));
        Assert.Equal(newBytes, File.ReadAllBytes(bytesPath));
        Assert.Equal(newManifest, File.ReadAllBytes(bytesPath + ".manifest.json"));
    }

    [Fact]
    public async Task Project_internal_output_override_cannot_replace_configuration_or_inputs()
    {
        var service = new ExcelDbProjectService("tests");
        var location = new ProjectLocation(_projectRoot);
        var initialized = service.Apply(service.PlanInitialize(new InitializeProjectRequest(location)));
        Assert.True(initialized.Succeeded, Format(initialized));
        var projectFile = Path.Combine(_projectRoot, ".exceldb", "project.json");
        var before = File.ReadAllBytes(projectFile);

        var report = await service.ConvertAsync(new ConvertRequest(
            location,
            "client",
            ExcelDb.Tooling.Project.ExcelDbProject.RelativeFilePath));

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "convert.output-reserved");
        Assert.Equal(before, File.ReadAllBytes(projectFile));
    }

    [Theory]
    [InlineData("Schema/protected.bytes", "Schema")]
    [InlineData("Excel/protected.bytes", "Excel")]
    [InlineData("Generated/CSharp/protected.bytes", "Generated C#")]
    [InlineData(".exceldb/cache/protected.bytes", ".exceldb")]
    public async Task Project_internal_output_override_rejects_each_protected_artifact_root(
        string outputPath,
        string protectedName)
    {
        var service = new ExcelDbProjectService("tests");
        var location = new ProjectLocation(_projectRoot);
        Assert.True(service.Apply(service.PlanInitialize(new InitializeProjectRequest(location))).Succeeded);

        var report = await service.ConvertAsync(new ConvertRequest(location, "client", outputPath));

        Assert.False(report.Succeeded);
        var diagnostic = Assert.Single(report.Diagnostics, item => item.Code == "convert.output-reserved");
        Assert.Contains(protectedName, diagnostic.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_projectRoot, outputPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task External_convert_rechecks_project_config_after_waiting_for_lease()
    {
        var service = new ExcelDbProjectService("tests");
        var location = new ProjectLocation(_projectRoot);
        Assert.True(service.Apply(service.PlanInitialize(new InitializeProjectRequest(location))).Succeeded);
        var projectFile = Path.Combine(_projectRoot, ".exceldb", "project.json");
        var loaded = PipelineProject.Load(projectFile);
        var output = Path.Combine(_externalRoot, "stale", "config.bytes");
        var outputDirectory = Path.GetDirectoryName(output)!;
        using var owner = ProjectOperationLease.AcquireMany([_projectRoot, outputDirectory]);
        var pipeline = new WorkbookPipeline("tests", new SchemaPipeline("tests"));

        var convertTask = Task.Run(() => pipeline.ConvertAsync(loaded, "client", output));
        await Task.Delay(200);
        var changed = ExcelDb.Tooling.Project.ExcelDbProject.Load(projectFile)
            .WithGeneratedCSharpDirectory(Path.Combine(_externalRoot, "changed-csharp"));
        File.WriteAllBytes(projectFile, changed.ToCanonicalJson());
        owner.Dispose();

        var report = await convertTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "plan.stale");
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Output_override_cannot_replace_file_in_external_generated_csharp_root()
    {
        var service = new ExcelDbProjectService("tests");
        var location = new ProjectLocation(_projectRoot);
        var externalCSharp = Path.Combine(_externalRoot, "generated-csharp");
        Assert.True(service.Apply(service.PlanInitialize(new InitializeProjectRequest(location, externalCSharp))).Succeeded);
        var sentinel = Path.Combine(externalCSharp, "keep.cs");
        Directory.CreateDirectory(externalCSharp);
        File.WriteAllText(sentinel, "keep");

        var report = await service.ConvertAsync(new ConvertRequest(location, "client", sentinel));

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "convert.output-reserved");
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.False(File.Exists(sentinel + ".manifest.json"));
    }

    private Fixture CreateFixture(string phase)
    {
        var id = Guid.NewGuid();
        var bytesPath = Path.Combine(_externalRoot, "release", "config.bytes");
        var manifestPath = bytesPath + ".manifest.json";
        var oldBytes = Encoding.UTF8.GetBytes("old-bytes");
        var oldManifest = Encoding.UTF8.GetBytes("old-manifest");
        var newBytes = Encoding.UTF8.GetBytes("new-bytes");
        var newManifest = Encoding.UTF8.GetBytes("new-manifest");
        var outputDirectory = Path.GetDirectoryName(bytesPath)!;
        var journal = new ExternalBytesJournal
        {
            FormatVersion = ExternalBytesTransaction.CurrentFormatVersion,
            TransactionId = id,
            ProjectRoot = Path.GetFullPath(_projectRoot),
            OutputDirectory = Path.GetFullPath(outputDirectory),
            CapabilityNonce = new string('a', 64),
            Phase = phase,
            Items =
            [
                Item(0, "bytes", bytesPath, oldBytes, newBytes),
                Item(1, "manifest", manifestPath, oldManifest, newManifest),
            ],
        };
        journal.CapabilitySealSha256 = ContentFingerprint.FromBytes(SerializeSeal(journal)).Sha256;
        return new Fixture(
            id,
            bytesPath,
            manifestPath,
            Path.Combine(outputDirectory, $".exceldb-bytes-txn-{id:N}"),
            Path.Combine(
                _projectRoot,
                ".exceldb",
                "recovery",
                ExternalBytesTransaction.RecoverySubtreeName,
                id.ToString("N")),
            oldBytes,
            oldManifest,
            newBytes,
            newManifest,
            journal);
    }

    private Fixture CreateCommittedFixtureInTransactionDirectory()
    {
        var fixture = CreateFixture(ExternalBytesPhase.Committed);
        WritePair(fixture.BytesPath, fixture.NewBytes, fixture.NewManifest);
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "0.backup"), fixture.OldBytes);
        File.WriteAllBytes(Path.Combine(fixture.TransactionDirectory, "1.backup"), fixture.OldManifest);
        WriteJournal(fixture);
        return fixture;
    }

    private static ExternalBytesJournalItem Item(
        int index,
        string kind,
        string destination,
        byte[] original,
        byte[] replacement) =>
        new()
        {
            Index = index,
            Kind = kind,
            Destination = Path.GetFullPath(destination),
            HadOriginal = true,
            OriginalSha256 = ContentFingerprint.FromBytes(original).Sha256,
            OriginalAttributes = (int)FileAttributes.Normal,
            NewSha256 = ContentFingerprint.FromBytes(replacement).Sha256,
        };

    private static void WriteJournal(Fixture fixture)
    {
        Directory.CreateDirectory(fixture.TransactionDirectory);
        File.WriteAllBytes(
            Path.Combine(fixture.TransactionDirectory, ExternalBytesTransaction.CapabilitySealFileName),
            SerializeSeal(fixture.Journal));
        if (fixture.Journal.Phase is ExternalBytesPhase.Applying
            or ExternalBytesPhase.Committed
            or ExternalBytesPhase.RolledBack)
        {
            WritePhaseMarker(fixture, ExternalBytesPhase.Applying);
        }
        if (string.Equals(fixture.Journal.Phase, ExternalBytesPhase.Committed, StringComparison.Ordinal))
            WritePhaseMarker(fixture, ExternalBytesPhase.Committed);
        if (string.Equals(fixture.Journal.Phase, ExternalBytesPhase.RolledBack, StringComparison.Ordinal))
            WritePhaseMarker(fixture, ExternalBytesPhase.RolledBack);
        WriteProjectJournalOnly(fixture);
    }

    private static void WritePhaseMarker(Fixture fixture, string phase)
    {
        var fileName = phase switch
        {
            ExternalBytesPhase.Applying => "applying.marker.json",
            ExternalBytesPhase.Committed => "committed.marker.json",
            ExternalBytesPhase.RolledBack => "rolled-back.marker.json",
            _ => throw new InvalidOperationException(),
        };
        File.WriteAllBytes(
            Path.Combine(fixture.TransactionDirectory, fileName),
            JsonSerializer.SerializeToUtf8Bytes(new ExternalBytesPhaseMarker
            {
                FormatVersion = ExternalBytesTransaction.CurrentFormatVersion,
                TransactionId = fixture.Journal.TransactionId,
                Phase = phase,
                CapabilitySealSha256 = fixture.Journal.CapabilitySealSha256,
                Nonce = fixture.Journal.CapabilityNonce,
            }, JournalJson));
    }

    private static void WriteProjectJournalOnly(Fixture fixture)
    {
        Directory.CreateDirectory(fixture.JournalDirectory);
        File.WriteAllBytes(
            Path.Combine(fixture.JournalDirectory, ExternalBytesTransaction.JournalFileName),
            JsonSerializer.SerializeToUtf8Bytes(fixture.Journal, JournalJson));
    }

    private static byte[] SerializeSeal(ExternalBytesJournal journal) =>
        JsonSerializer.SerializeToUtf8Bytes(new ExternalBytesCapabilitySeal
        {
            FormatVersion = ExternalBytesTransaction.CurrentFormatVersion,
            TransactionId = journal.TransactionId,
            ProjectRoot = journal.ProjectRoot,
            OutputDirectory = journal.OutputDirectory,
            Nonce = journal.CapabilityNonce,
            Items = journal.Items.Select(CloneItem).ToList(),
        }, JournalJson);

    private static ExternalBytesJournalItem CloneItem(ExternalBytesJournalItem item) =>
        new()
        {
            Index = item.Index,
            Kind = item.Kind,
            Destination = item.Destination,
            HadOriginal = item.HadOriginal,
            OriginalSha256 = item.OriginalSha256,
            OriginalAttributes = item.OriginalAttributes,
            NewSha256 = item.NewSha256,
        };

    private static void WritePair(string bytesPath, byte[] bytes, byte[] manifest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(bytesPath)!);
        File.WriteAllBytes(bytesPath, bytes);
        File.WriteAllBytes(bytesPath + ".manifest.json", manifest);
    }

    private static string Format(ExcelDb.Core.Diagnostics.OperationReport report) =>
        string.Join(Environment.NewLine, report.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));

    private static string SnapshotTree(string root)
    {
        if (!Directory.Exists(root))
            return "<missing>";
        var entries = Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => "D:" + Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Concat(Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path =>
                    "F:" + Path.GetRelativePath(root, path).Replace('\\', '/') + ":" +
                    ContentFingerprint.FromFile(path).Sha256))
            .OrderBy(static entry => entry, StringComparer.Ordinal);
        return string.Join("\n", entries);
    }

    public void Dispose()
    {
        DeleteTree(_projectRoot);
        DeleteTree(_externalRoot);
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private sealed record Fixture(
        Guid Id,
        string BytesPath,
        string ManifestPath,
        string TransactionDirectory,
        string JournalDirectory,
        byte[] OldBytes,
        byte[] OldManifest,
        byte[] NewBytes,
        byte[] NewManifest,
        ExternalBytesJournal Journal)
    {
        internal string CleanupDirectory => TransactionDirectory + ".cleanup";
    }

    private sealed class SimulatedCrashException : Exception;
}
