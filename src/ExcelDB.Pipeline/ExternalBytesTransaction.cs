using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;
using ExcelDb.Tooling.Plans;

namespace ExcelDb.Pipeline;

/// <summary>
/// Writes an explicitly external converted bytes file and its manifest as a recoverable pair.
/// The durable journal belongs to the project, while staging and backups stay beside the output
/// so every individual move remains on one volume.
/// </summary>
internal sealed class ExternalBytesTransaction
{
    internal const int CurrentFormatVersion = 2;
    internal const string RecoverySubtreeName = "external-bytes";
    internal const string JournalFileName = "journal.json";
    internal const string CapabilitySealFileName = "capability.seal.json";
    private const string ApplyingMarkerFileName = "applying.marker.json";
    private const string CommittedMarkerFileName = "committed.marker.json";
    private const string RolledBackMarkerFileName = "rolled-back.marker.json";
    private const string TransactionDirectoryPrefix = ".exceldb-bytes-txn-";
    private const string CleanupDirectorySuffix = ".cleanup";
    private const string BootstrapCapabilitySuffix = ".bootstrap";
    private const string DurableFormatVersion = "durable-external-v2";
    private static readonly StringComparer FileSystemComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };

    private readonly string _toolVersion;
    private readonly Action<ExternalBytesCheckpoint>? _checkpoint;

    internal ExternalBytesTransaction(
        string toolVersion,
        Action<ExternalBytesCheckpoint>? checkpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        _toolVersion = toolVersion;
        _checkpoint = checkpoint;
    }

    internal OperationReport Write(
        string projectRoot,
        string bytesPath,
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> manifest,
        string target,
        string contentHash)
    {
        try
        {
            var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(bytesPath))
                ?? throw new InvalidDataException("The external bytes output has no parent directory.");
            using var lease = ProjectRecovery.AcquireLeaseForOperation(projectRoot, [outputDirectory]);
            return WriteUnderLease(projectRoot, bytesPath, bytes, manifest, target, contentHash);
        }
        catch (ProjectBusyException exception)
        {
            return new OperationReport(
                "convert",
                _toolVersion,
                false,
                [new Diagnostic(
                    "project.busy",
                    DiagnosticSeverity.Blocker,
                    projectRoot,
                    exception.Message)],
                []);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            return Failure(
                Path.GetFullPath(bytesPath),
                exception.Message,
                []);
        }
    }

    internal OperationReport WriteUnderLease(
        string projectRoot,
        string bytesPath,
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> manifest,
        string target,
        string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(bytesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        var root = Path.GetFullPath(projectRoot);
        var destination = Path.GetFullPath(bytesPath);
        var manifestDestination = destination + ".manifest.json";
        var outputDirectory = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("The external bytes output has no parent directory.");

        var pending = ProjectRecovery.RecoverPendingUnderLease(root);
        if (!pending.Succeeded)
            return pending with { Operation = "convert", ToolVersion = _toolVersion };

        ExternalBytesJournal? journal = null;
        string? journalDirectory = null;
        try
        {
            EnsureOutputDirectory(outputDirectory);
            EnsureFileDestination(destination);
            EnsureFileDestination(manifestDestination);

            var transactionId = Guid.NewGuid();
            journalDirectory = GetJournalDirectory(root, transactionId);
            var transactionDirectory = GetTransactionDirectory(outputDirectory, transactionId);
            var cleanupDirectory = GetCleanupTransactionDirectory(outputDirectory, transactionId);
            if (File.Exists(transactionDirectory) || Directory.Exists(transactionDirectory)
                || File.Exists(cleanupDirectory) || Directory.Exists(cleanupDirectory))
                throw new IOException($"External bytes transaction path already exists: '{transactionDirectory}'.");

            journal = BuildJournal(
                transactionId,
                root,
                outputDirectory,
                destination,
                bytes,
                manifestDestination,
                manifest);

            Directory.CreateDirectory(journalDirectory);
            EnsurePlainDirectory(journalDirectory, "External bytes recovery journal");
            WriteJournal(Path.Combine(journalDirectory, JournalFileName), journal);
            _checkpoint?.Invoke(ExternalBytesCheckpoint.Prepared);

            WriteBootstrapCapability(journal);
            _checkpoint?.Invoke(ExternalBytesCheckpoint.BootstrapCapabilityWritten);
            Directory.CreateDirectory(transactionDirectory);
            EnsurePlainDirectory(transactionDirectory, "External bytes staging");
            EnsureTransactionDirectoryIsEmpty(transactionDirectory);
            _checkpoint?.Invoke(ExternalBytesCheckpoint.TransactionDirectoryCreated);
            InstallBootstrapCapability(journal, transactionDirectory);
            ValidateCapabilityEvidence(journal);
            _checkpoint?.Invoke(ExternalBytesCheckpoint.CapabilitySealInstalled);

            WriteStages(journal, transactionDirectory, bytes, manifest);

            journal.Phase = ExternalBytesPhase.Applying;
            WriteJournal(Path.Combine(journalDirectory, JournalFileName), journal);
            TransitionCapabilityPhase(journal, ExternalBytesPhase.Applying);
            _checkpoint?.Invoke(ExternalBytesCheckpoint.Applying);

            foreach (var item in journal.Items.OrderBy(static item => item.Index))
            {
                InstallItem(journal, transactionDirectory, item);
                _checkpoint?.Invoke(item.Index == 0
                    ? ExternalBytesCheckpoint.BytesInstalled
                    : ExternalBytesCheckpoint.ManifestInstalled);
            }

            ValidateCommittedOutputs(journal);
            journal.Phase = ExternalBytesPhase.Committed;
            WriteJournal(Path.Combine(journalDirectory, JournalFileName), journal);
            TransitionCapabilityPhase(journal, ExternalBytesPhase.Committed);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or JsonException
                                           or ArgumentException)
        {
            if (journal is null || journalDirectory is null)
            {
                return Failure(
                    destination,
                    exception.Message,
                    pending.Diagnostics);
            }

            try
            {
                if (TryCleanupPreSealBootstrap(journal, journalDirectory))
                {
                    return Failure(
                        destination,
                        $"{exception.Message} The previous bytes and manifest were preserved.",
                        pending.Diagnostics);
                }
                RollBackIncomplete(journal);
                var rolledBackEvidence = ValidateCapabilityEvidence(journal);
                if (string.Equals(rolledBackEvidence.EffectivePhase, ExternalBytesPhase.Applying, StringComparison.Ordinal))
                {
                    journal.Phase = ExternalBytesPhase.RolledBack;
                    WriteJournal(Path.Combine(journalDirectory, JournalFileName), journal);
                    TransitionCapabilityPhase(journal, ExternalBytesPhase.RolledBack);
                }
                else
                {
                    journal.Phase = ExternalBytesPhase.Prepared;
                    WriteJournal(Path.Combine(journalDirectory, JournalFileName), journal);
                }
                CleanupTransaction(journal, journalDirectory);
                return Failure(
                    destination,
                    $"{exception.Message} The previous bytes and manifest were preserved.",
                    pending.Diagnostics);
            }
            catch (Exception rollbackException) when (rollbackException is IOException
                                                       or UnauthorizedAccessException
                                                       or InvalidDataException)
            {
                return new OperationReport(
                    "convert",
                    _toolVersion,
                    false,
                    pending.Diagnostics
                        .Add(new Diagnostic(
                            "convert.external-write",
                            DiagnosticSeverity.Error,
                            destination,
                            exception.Message))
                        .Add(new Diagnostic(
                            "convert.external-recovery-failed",
                            DiagnosticSeverity.Blocker,
                            journalDirectory,
                            $"Rollback is incomplete and its journal/backups were retained: {rollbackException.Message}")),
                    []);
            }
        }

        var diagnostics = pending.Diagnostics.Add(new Diagnostic(
            "convert.target",
            DiagnosticSeverity.Info,
            target,
            $"Converted target '{target}', source {contentHash}; external output used a durable pair transaction."));
        try
        {
            CleanupTransaction(journal!, journalDirectory!);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            diagnostics = diagnostics.Add(new Diagnostic(
                "convert.external-cleanup-pending",
                DiagnosticSeverity.Warning,
                journalDirectory!,
                $"The bytes pair is committed; transaction cleanup will be retried on the next project operation: {exception.Message}"));
        }

        return Success(journal!, diagnostics);
    }

    internal static OperationReport RecoverPending(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        try
        {
            using var lease = ProjectRecovery.AcquireLeaseForOperation(projectRoot, []);
            return RecoverPendingUnderLease(projectRoot);
        }
        catch (ProjectBusyException exception)
        {
            return RecoveryFailure(projectRoot, exception.Message, "project.busy");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or ArgumentException)
        {
            return RecoveryFailure(projectRoot, exception.Message);
        }
    }

    internal static ImmutableArray<string> DiscoverRecoveryRootsUnderLease(string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot);
        var roots = ImmutableArray.CreateBuilder<string>();
        roots.Add(root);
        var recoveryRoot = GetRecoveryRoot(root);
        if (!Directory.Exists(recoveryRoot))
            return roots.ToImmutable();
        foreach (var journalDirectory in Directory.EnumerateDirectories(recoveryRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (!Guid.TryParseExact(Path.GetFileName(journalDirectory), "N", out var transactionId))
                continue;
            var journalPath = Path.Combine(journalDirectory, JournalFileName);
            if (!File.Exists(journalPath))
                continue;
            try
            {
                ValidateJournalEnvelopeBeforeRead(journalDirectory);
                var journal = ReadJournal(journalPath);
                ValidateJournal(journal, root, transactionId);
                roots.Add(journal.OutputDirectory);
                var transactionDirectory = GetTransactionDirectory(journal.OutputDirectory, journal.TransactionId);
                var cleanupDirectory = GetCleanupTransactionDirectory(journal.OutputDirectory, journal.TransactionId);
                EnsureExclusiveTransactionLocation(transactionDirectory, cleanupDirectory);
                if (Directory.Exists(transactionDirectory))
                {
                    if (File.Exists(Path.Combine(transactionDirectory, CapabilitySealFileName)))
                    {
                        if (GetValidatedBootstrapCapabilityPath(journal) is not null)
                            throw new InvalidDataException("External bytes has duplicate capability evidence.");
                        ValidateCapabilityEvidence(journal);
                    }
                    else
                    {
                        ValidateBootstrapTransactionTree(journal);
                    }
                }
                else if (Directory.Exists(cleanupDirectory))
                {
                    if (GetValidatedBootstrapCapabilityPath(journal) is not null)
                        throw new InvalidDataException("External bytes cleanup conflicts with bootstrap capability evidence.");
                    ValidateCleanupTombstone(journal, cleanupDirectory);
                }
                else if (GetValidatedBootstrapCapabilityPath(journal) is not null)
                {
                    ValidateBootstrapTransactionTree(journal);
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or JsonException
                                               or ArgumentException)
            {
                // Full recovery reports invalid physical evidence; a validated journal root remains lease-visible.
            }
        }
        return roots
            .Distinct(FileSystemComparer)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    internal static OperationReport RecoverPendingUnderLease(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.GetFullPath(projectRoot);
        var recoveryRoot = GetRecoveryRoot(root);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var recovered = false;
        try
        {
            if (File.Exists(recoveryRoot))
                return RecoveryFailure(recoveryRoot, "The external bytes recovery path is occupied by a file.");
            if (!Directory.Exists(recoveryRoot))
                return OperationReport.Success("external-bytes-recover", DurableFormatVersion);

            EnsurePlainDirectory(recoveryRoot, "External bytes recovery");
            if (Directory.EnumerateFiles(recoveryRoot, "*", SearchOption.TopDirectoryOnly).Any())
                throw new InvalidDataException("The external bytes recovery directory contains an unknown file.");

            foreach (var journalDirectory in Directory
                         .EnumerateDirectories(recoveryRoot, "*", SearchOption.TopDirectoryOnly)
                         .OrderBy(static path => path, StringComparer.Ordinal))
            {
                EnsurePlainDirectory(journalDirectory, "External bytes recovery journal");
                var name = Path.GetFileName(journalDirectory);
                if (!Guid.TryParseExact(name, "N", out var transactionId))
                    throw new InvalidDataException($"Unknown external bytes recovery entry '{name}'.");

                var journalPath = Path.Combine(journalDirectory, JournalFileName);
                if (!File.Exists(journalPath))
                {
                    if (TryCleanupJournalTempsWithoutJournal(journalDirectory))
                    {
                        Directory.Delete(journalDirectory);
                        recovered = true;
                        continue;
                    }
                    throw new InvalidDataException(
                        $"External bytes recovery transaction '{name}' has no journal.json and contains unknown state.");
                }
                ValidateJournalEnvelopeBeforeRead(journalDirectory);
                var journal = ReadJournal(journalPath);
                ValidateJournal(journal, root, transactionId);
                var transactionDirectory = GetTransactionDirectory(journal.OutputDirectory, journal.TransactionId);
                var cleanupDirectory = GetCleanupTransactionDirectory(journal.OutputDirectory, journal.TransactionId);
                EnsureExclusiveTransactionLocation(transactionDirectory, cleanupDirectory);
                if (TryCleanupPreSealBootstrap(journal, journalDirectory))
                {
                    recovered = true;
                    continue;
                }
                if (Directory.Exists(cleanupDirectory))
                {
                    ValidateCleanupTombstone(journal, cleanupDirectory);
                    ValidateCleanupDomainState(journal);
                    CleanupTransaction(journal, journalDirectory);
                    recovered = true;
                    continue;
                }
                if (!Directory.Exists(transactionDirectory))
                {
                    ValidateCleanupDomainState(journal);
                    CleanupJournalDirectory(journalDirectory);
                    recovered = true;
                    continue;
                }

                var evidence = ValidateCapabilityEvidence(journal);
                ValidatePhaseRelationship(journal.Phase, evidence.EffectivePhase);
                if (!string.Equals(journal.Phase, evidence.EffectivePhase, StringComparison.Ordinal)
                    && evidence.EffectivePhase is ExternalBytesPhase.Prepared or ExternalBytesPhase.Applying)
                {
                    journal.Phase = evidence.EffectivePhase;
                    WriteJournal(journalPath, journal);
                }

                if (string.Equals(evidence.EffectivePhase, ExternalBytesPhase.Committed, StringComparison.Ordinal))
                    ValidateCommittedOutputs(journal);
                else if (string.Equals(evidence.EffectivePhase, ExternalBytesPhase.Applying, StringComparison.Ordinal))
                {
                    RollBackIncomplete(journal);
                    journal.Phase = ExternalBytesPhase.RolledBack;
                    WriteJournal(journalPath, journal);
                    TransitionCapabilityPhase(journal, ExternalBytesPhase.RolledBack);
                }
                else if (string.Equals(evidence.EffectivePhase, ExternalBytesPhase.RolledBack, StringComparison.Ordinal))
                    ValidateRolledBackOutputs(journal);
                else
                    ValidatePreparedOutputs(journal);

                CleanupTransaction(journal, journalDirectory);
                recovered = true;
            }

            DeleteIfEmpty(recoveryRoot);
            if (recovered)
            {
                diagnostics.Add(new Diagnostic(
                    "convert.external-recovered",
                    DiagnosticSeverity.Info,
                    recoveryRoot,
                    "Recovered an incomplete external bytes/manifest transaction."));
            }

            return new OperationReport(
                "external-bytes-recover",
                DurableFormatVersion,
                recovered,
                diagnostics.ToImmutable(),
                []);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or JsonException
                                           or ArgumentException)
        {
            return RecoveryFailure(recoveryRoot, exception.Message);
        }
    }

    private static ExternalBytesJournal BuildJournal(
        Guid transactionId,
        string projectRoot,
        string outputDirectory,
        string bytesPath,
        ReadOnlySpan<byte> bytes,
        string manifestPath,
        ReadOnlySpan<byte> manifest)
    {
        var journal = new ExternalBytesJournal
        {
            FormatVersion = CurrentFormatVersion,
            TransactionId = transactionId,
            ProjectRoot = Path.GetFullPath(projectRoot),
            OutputDirectory = Path.GetFullPath(outputDirectory),
            CapabilityNonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            Phase = ExternalBytesPhase.Prepared,
            Items =
            [
                CreateItem(0, "bytes", bytesPath, bytes),
                CreateItem(1, "manifest", manifestPath, manifest),
            ],
        };
        journal.CapabilitySealSha256 = ContentFingerprint.FromBytes(SerializeCapabilitySeal(journal)).Sha256;
        return journal;
    }

    private static ExternalBytesJournalItem CreateItem(
        int index,
        string kind,
        string destination,
        ReadOnlySpan<byte> content)
    {
        var exists = File.Exists(destination);
        return new ExternalBytesJournalItem
        {
            Index = index,
            Kind = kind,
            Destination = Path.GetFullPath(destination),
            HadOriginal = exists,
            OriginalSha256 = exists ? ContentFingerprint.FromFile(destination).Sha256 : null,
            OriginalAttributes = exists ? (int)File.GetAttributes(destination) : null,
            NewSha256 = ContentFingerprint.FromBytes(content).Sha256,
        };
    }

    private static void WriteStages(
        ExternalBytesJournal journal,
        string transactionDirectory,
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> manifest)
    {
        WriteStage(journal, transactionDirectory, journal.Items[0], bytes);
        WriteStage(journal, transactionDirectory, journal.Items[1], manifest);
    }

    private static void WriteStage(
        ExternalBytesJournal journal,
        string transactionDirectory,
        ExternalBytesJournalItem item,
        ReadOnlySpan<byte> content)
    {
        ValidateCapabilityEvidence(journal);
        var path = GetStagePath(transactionDirectory, item);
        AtomicFile.WriteAllBytes(path, content);
        EnsureHash(path, item.NewSha256, $"Staged {item.Kind}");
    }

    private static void InstallItem(
        ExternalBytesJournal journal,
        string transactionDirectory,
        ExternalBytesJournalItem item)
    {
        ValidateCapabilityEvidence(journal);
        var destination = item.Destination;
        EnsureFileDestination(destination);
        if (item.HadOriginal)
        {
            EnsureHash(destination, item.OriginalSha256!, $"Original {item.Kind}");
            ClearReadOnly(destination);
            File.Move(destination, GetBackupPath(transactionDirectory, item));
        }
        else if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"External {item.Kind} destination changed while the transaction was being prepared: '{destination}'.");
        }

        ValidateCapabilityEvidence(journal);
        EnsureFileDestination(destination);
        File.Move(GetStagePath(transactionDirectory, item), destination);
        EnsureHash(destination, item.NewSha256, $"Installed {item.Kind}");
    }

    private static void RollBackIncomplete(ExternalBytesJournal journal)
    {
        var evidence = ValidateCapabilityEvidence(journal);
        var transactionDirectory = evidence.TransactionDirectory;

        if (string.Equals(evidence.EffectivePhase, ExternalBytesPhase.Prepared, StringComparison.Ordinal))
        {
            ValidatePreparedOutputs(journal);
            return;
        }
        if (!string.Equals(evidence.EffectivePhase, ExternalBytesPhase.Applying, StringComparison.Ordinal))
            throw new InvalidDataException($"External bytes phase '{evidence.EffectivePhase}' cannot be rolled back.");

        var actions = journal.Items
            .Select(item => ClassifyRollbackAction(transactionDirectory, item))
            .OrderByDescending(static action => action.Item.Index)
            .ToArray();
        foreach (var action in actions)
        {
            ValidateCapabilityEvidence(journal);
            var item = action.Item;
            var current = ClassifyRollbackAction(transactionDirectory, item);
            if (current.Kind != action.Kind
                || current.DeleteInstalledDestination != action.DeleteInstalledDestination)
            {
                throw new InvalidDataException($"External {item.Kind} rollback evidence changed during recovery.");
            }
            var destination = item.Destination;
            var backup = GetBackupPath(transactionDirectory, item);
            EnsureFileDestination(destination);
            if (action.Kind is RollbackActionKind.None)
            {
                if (item.HadOriginal)
                    RestoreAttributes(destination, item.OriginalAttributes);
                continue;
            }
            if (action.Kind is RollbackActionKind.RestoreBackup)
            {
                EnsureHash(backup, item.OriginalSha256!, $"Backup {item.Kind}");
                if (action.DeleteInstalledDestination)
                {
                    EnsureHash(destination, item.NewSha256, $"Installed {item.Kind}");
                    DeleteFile(destination);
                }
                else if (File.Exists(destination) || Directory.Exists(destination))
                    throw new InvalidDataException($"Cannot restore {item.Kind}; its destination was created by another writer.");
                ValidateCapabilityEvidence(journal);
                File.Move(backup, destination);
                RestoreAttributes(destination, item.OriginalAttributes);
                continue;
            }

            EnsureHash(destination, item.NewSha256, $"Installed {item.Kind}");
            DeleteFile(destination);
        }

        ValidateRolledBackOutputs(journal);
    }

    private static RollbackAction ClassifyRollbackAction(
        string transactionDirectory,
        ExternalBytesJournalItem item)
    {
        var stage = GetStagePath(transactionDirectory, item);
        var backup = GetBackupPath(transactionDirectory, item);
        var hasStage = File.Exists(stage);
        var hasBackup = File.Exists(backup);
        EnsureFileDestination(item.Destination);
        var hasDestination = File.Exists(item.Destination);
        var destinationHash = hasDestination
            ? ContentFingerprint.FromFile(item.Destination).Sha256
            : null;

        if (item.HadOriginal)
        {
            if (hasStage && !hasBackup)
            {
                RequireDestinationHash(item, destinationHash, item.OriginalSha256!, "untouched original");
                return new RollbackAction(item, RollbackActionKind.None, false);
            }
            if (hasStage && hasBackup)
            {
                if (hasDestination)
                    throw new InvalidDataException($"Cannot restore {item.Kind}; its stage proves the transaction did not install this destination.");
                return new RollbackAction(item, RollbackActionKind.RestoreBackup, false);
            }
            if (!hasStage && hasBackup)
            {
                if (!hasDestination)
                    return new RollbackAction(item, RollbackActionKind.RestoreBackup, false);
                RequireDestinationHash(item, destinationHash, item.NewSha256, "installed replacement");
                return new RollbackAction(item, RollbackActionKind.RestoreBackup, true);
            }

            RequireDestinationHash(item, destinationHash, item.OriginalSha256!, "restored original");
            return new RollbackAction(item, RollbackActionKind.None, false);
        }

        if (hasBackup)
            throw new InvalidDataException($"Unexpected backup exists for new {item.Kind}: '{backup}'.");
        if (hasStage)
        {
            if (hasDestination)
                throw new InvalidDataException($"Cannot remove {item.Kind}; its stage proves the transaction did not install this destination.");
            return new RollbackAction(item, RollbackActionKind.None, false);
        }
        if (!hasDestination)
            return new RollbackAction(item, RollbackActionKind.None, false);
        RequireDestinationHash(item, destinationHash, item.NewSha256, "installed new file");
        return new RollbackAction(item, RollbackActionKind.DeleteInstalled, false);
    }

    private static void RequireDestinationHash(
        ExternalBytesJournalItem item,
        string? actual,
        string expected,
        string state)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"External {item.Kind} destination does not contain the expected {state}: '{item.Destination}'.");
        }
    }

    private static void ValidateCommittedOutputs(ExternalBytesJournal journal)
    {
        foreach (var item in journal.Items)
        {
            EnsureFileDestination(item.Destination);
            EnsureHash(item.Destination, item.NewSha256, $"Committed {item.Kind}");
        }
    }

    private static void ValidatePreparedOutputs(ExternalBytesJournal journal)
    {
        foreach (var item in journal.Items)
        {
            EnsureFileDestination(item.Destination);
            if (item.HadOriginal)
                EnsureHash(item.Destination, item.OriginalSha256!, $"Prepared original {item.Kind}");
            else if (File.Exists(item.Destination))
                throw new InvalidDataException($"Prepared new {item.Kind} destination was already modified: '{item.Destination}'.");
        }
    }

    private static void ValidateRolledBackOutputs(ExternalBytesJournal journal)
    {
        foreach (var item in journal.Items)
        {
            EnsureFileDestination(item.Destination);
            if (item.HadOriginal)
                EnsureHash(item.Destination, item.OriginalSha256!, $"Restored {item.Kind}");
            else if (File.Exists(item.Destination))
                throw new InvalidDataException($"Rolled-back {item.Kind} unexpectedly exists: '{item.Destination}'.");
        }
    }

    private static void CleanupTransaction(ExternalBytesJournal journal, string journalDirectory)
    {
        ValidateJournalDirectory(journalDirectory, journal);
        if (GetValidatedBootstrapCapabilityPath(journal) is not null)
            throw new InvalidDataException("External bytes normal cleanup cannot discard bootstrap capability evidence.");
        var transactionDirectory = GetTransactionDirectory(journal.OutputDirectory, journal.TransactionId);
        var cleanupDirectory = GetCleanupTransactionDirectory(journal.OutputDirectory, journal.TransactionId);
        EnsureExclusiveTransactionLocation(transactionDirectory, cleanupDirectory);

        if (Directory.Exists(transactionDirectory))
        {
            var evidence = ValidateCapabilityEvidence(journal);
            if (!string.Equals(journal.Phase, evidence.EffectivePhase, StringComparison.Ordinal))
                throw new InvalidDataException("External bytes cleanup requires an acknowledged sealed phase.");
            ValidateCleanupDomainState(journal);
            if (Directory.Exists(cleanupDirectory) || File.Exists(cleanupDirectory))
                throw new InvalidDataException($"External bytes cleanup tombstone already exists: '{cleanupDirectory}'.");
            Directory.Move(transactionDirectory, cleanupDirectory);
        }

        if (!Directory.Exists(cleanupDirectory))
        {
            ValidateCleanupDomainState(journal);
            CleanupJournalDirectory(journalDirectory);
            DeleteIfEmpty(Path.GetDirectoryName(journalDirectory)!);
            return;
        }

        var cleanup = ValidateCleanupTombstone(journal, cleanupDirectory);
        ValidateCleanupDomainState(journal);
        if (!cleanup.HasSeal)
        {
            // A crash can leave an empty tombstone after the seal was deleted. With no remaining
            // external capability, remove only the project-local journal and leave the empty path.
            CleanupJournalDirectory(journalDirectory);
            DeleteIfEmpty(Path.GetDirectoryName(journalDirectory)!);
            return;
        }

        foreach (var file in cleanup.Files
                     .Where(file => !string.Equals(Path.GetFileName(file), CapabilitySealFileName, StringComparison.Ordinal))
                     .OrderBy(static file => file, StringComparer.Ordinal))
        {
            DeleteFile(file);
        }
        DeleteFile(Path.Combine(cleanupDirectory, CapabilitySealFileName));
        Directory.Delete(cleanupDirectory);

        CleanupJournalDirectory(journalDirectory);
        DeleteIfEmpty(Path.GetDirectoryName(journalDirectory)!);
    }

    private OperationReport Success(
        ExternalBytesJournal journal,
        ImmutableArray<Diagnostic> diagnostics) =>
        new(
            "convert",
            _toolVersion,
            true,
            diagnostics,
            journal.Items
                .OrderBy(static item => item.Index)
                .Select(item => new ArtifactRecord(item.Kind, item.Destination, item.NewSha256))
                .ToImmutableArray());

    private OperationReport Failure(
        string destination,
        string message,
        ImmutableArray<Diagnostic> priorDiagnostics) =>
        new(
            "convert",
            _toolVersion,
            false,
            priorDiagnostics.Add(new Diagnostic(
                "convert.external-write",
                DiagnosticSeverity.Blocker,
                destination,
                message)),
            []);

    private static void ValidateJournal(
        ExternalBytesJournal journal,
        string projectRoot,
        Guid transactionId)
    {
        if (journal.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported external bytes recovery format {journal.FormatVersion}.");
        if (journal.TransactionId != transactionId)
            throw new InvalidDataException("External bytes transaction id does not match its recovery directory.");
        if (string.IsNullOrWhiteSpace(journal.ProjectRoot)
            || string.IsNullOrWhiteSpace(journal.OutputDirectory)
            || !IsNonce(journal.CapabilityNonce)
            || !IsSha256(journal.CapabilitySealSha256))
        {
            throw new InvalidDataException("External bytes recovery journal has invalid path or capability data.");
        }
        var declaredProjectRoot = RequireCanonicalPath(journal.ProjectRoot, "project root");
        if (!FileSystemComparer.Equals(declaredProjectRoot, Path.GetFullPath(projectRoot)))
            throw new InvalidDataException("External bytes recovery journal belongs to another project.");
        if (journal.Phase is not (ExternalBytesPhase.Prepared
            or ExternalBytesPhase.Applying
            or ExternalBytesPhase.Committed
            or ExternalBytesPhase.RolledBack))
            throw new InvalidDataException($"Unknown external bytes transaction phase '{journal.Phase}'.");

        var outputDirectory = RequireCanonicalPath(journal.OutputDirectory, "output directory");
        if (File.Exists(outputDirectory))
            throw new InvalidDataException("External bytes output directory is occupied by a file.");
        if (!Directory.Exists(outputDirectory))
            throw new InvalidDataException("External bytes output directory is missing.");
        EnsurePlainDirectory(outputDirectory, "External bytes output");
        if (journal.Items is null
            || journal.Items.Count != 2
            || journal.Items.Any(static item => item is null)
            || journal.Items[0].Index != 0
            || journal.Items[1].Index != 1
            || !string.Equals(journal.Items[0].Kind, "bytes", StringComparison.Ordinal)
            || !string.Equals(journal.Items[1].Kind, "manifest", StringComparison.Ordinal))
        {
            throw new InvalidDataException("External bytes recovery journal must contain the bytes/manifest pair in canonical order.");
        }

        var bytesPath = RequireCanonicalPath(journal.Items[0].Destination, "bytes destination");
        var manifestPath = RequireCanonicalPath(journal.Items[1].Destination, "manifest destination");
        if (!FileSystemComparer.Equals(Path.GetDirectoryName(bytesPath)!, outputDirectory)
            || !FileSystemComparer.Equals(Path.GetDirectoryName(manifestPath)!, outputDirectory)
            || !FileSystemComparer.Equals(manifestPath, bytesPath + ".manifest.json"))
        {
            throw new InvalidDataException("External bytes recovery destinations do not form a canonical adjacent pair.");
        }

        foreach (var item in journal.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Destination))
                throw new InvalidDataException($"External bytes journal has an empty destination for {item.Kind}.");
            if (!IsSha256(item.NewSha256))
                throw new InvalidDataException($"External bytes journal has invalid new hash for {item.Kind}.");
            if (item.HadOriginal != (item.OriginalSha256 is not null)
                || item.HadOriginal != item.OriginalAttributes.HasValue)
                throw new InvalidDataException($"External bytes journal has inconsistent original state for {item.Kind}.");
            if (item.OriginalSha256 is not null && !IsSha256(item.OriginalSha256))
                throw new InvalidDataException($"External bytes journal has invalid original hash for {item.Kind}.");
            if (item.OriginalAttributes is int attributes
                && (((FileAttributes)attributes) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException($"External bytes journal has unsafe original attributes for {item.Kind}.");
            }
        }
    }

    private static string? GetValidatedBootstrapCapabilityPath(ExternalBytesJournal journal)
    {
        var canonical = GetBootstrapCapabilityPath(journal.OutputDirectory, journal.TransactionId);
        var temporary = GetBootstrapCapabilityTemporaryPath(journal);
        var existing = new[] { canonical, temporary }
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToArray();
        if (existing.Length > 1)
            throw new InvalidDataException("External bytes has duplicate bootstrap capability evidence.");
        if (existing.Length == 0)
            return null;

        var path = existing[0];
        EnsurePlainFile(path, "External bytes bootstrap capability");
        var actual = File.ReadAllBytes(path);
        if (!string.Equals(ContentFingerprint.FromBytes(actual).Sha256, journal.CapabilitySealSha256, StringComparison.Ordinal)
            || !actual.AsSpan().SequenceEqual(SerializeCapabilitySeal(journal)))
        {
            throw new InvalidDataException("External bytes bootstrap capability does not match the project journal.");
        }
        return path;
    }

    private static void ValidateBootstrapTransactionTree(ExternalBytesJournal journal)
    {
        if (GetValidatedBootstrapCapabilityPath(journal) is null)
            throw new InvalidDataException("External bytes partial bootstrap has no physical capability.");
        var transactionDirectory = GetTransactionDirectory(journal.OutputDirectory, journal.TransactionId);
        if (File.Exists(transactionDirectory))
            throw new InvalidDataException("External bytes bootstrap transaction path is occupied by a file.");
        if (!Directory.Exists(transactionDirectory))
            return;
        EnsurePlainDirectory(transactionDirectory, "External bytes bootstrap transaction");
        if (Directory.EnumerateFileSystemEntries(transactionDirectory).Any())
            throw new InvalidDataException("External bytes partial bootstrap transaction is not empty.");
    }

    private static bool TryCleanupPreSealBootstrap(
        ExternalBytesJournal journal,
        string journalDirectory)
    {
        var transactionDirectory = GetTransactionDirectory(journal.OutputDirectory, journal.TransactionId);
        var cleanupDirectory = GetCleanupTransactionDirectory(journal.OutputDirectory, journal.TransactionId);
        var bootstrap = GetValidatedBootstrapCapabilityPath(journal);
        EnsureExclusiveTransactionLocation(transactionDirectory, cleanupDirectory);
        if (Directory.Exists(cleanupDirectory))
        {
            if (bootstrap is not null)
                throw new InvalidDataException("External bytes cleanup tombstone conflicts with bootstrap capability evidence.");
            return false;
        }

        var sealPath = Path.Combine(transactionDirectory, CapabilitySealFileName);
        if (Directory.Exists(transactionDirectory) && File.Exists(sealPath))
        {
            if (bootstrap is not null)
                throw new InvalidDataException("External bytes has both bootstrap and installed capability evidence.");
            return false;
        }

        if (!string.Equals(journal.Phase, ExternalBytesPhase.Prepared, StringComparison.Ordinal))
        {
            if (bootstrap is not null || Directory.Exists(transactionDirectory))
                throw new InvalidDataException("External bytes non-prepared transaction has incomplete capability evidence.");
            return false;
        }

        if (Directory.Exists(transactionDirectory))
        {
            if (bootstrap is null)
                throw new InvalidDataException("External bytes partial bootstrap has no physical capability.");
            ValidateBootstrapTransactionTree(journal);
        }
        else if (bootstrap is not null)
        {
            ValidateBootstrapTransactionTree(journal);
        }

        ValidatePreparedOutputs(journal);
        if (Directory.Exists(transactionDirectory))
            Directory.Delete(transactionDirectory);
        if (bootstrap is not null)
            DeleteFile(bootstrap);
        CleanupJournalDirectory(journalDirectory);
        DeleteIfEmpty(Path.GetDirectoryName(journalDirectory)!);
        return true;
    }

    private static CapabilityEvidence ValidateCapabilityEvidence(ExternalBytesJournal journal)
    {
        var transactionDirectory = GetTransactionDirectory(journal.OutputDirectory, journal.TransactionId);
        if (!Directory.Exists(transactionDirectory))
            throw new InvalidDataException($"External bytes capability directory is missing: '{transactionDirectory}'.");
        EnsurePlainDirectory(transactionDirectory, "External bytes capability");

        var childDirectory = Directory.EnumerateDirectories(transactionDirectory, "*", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (childDirectory is not null)
            throw new InvalidDataException($"External bytes capability directory contains an unknown subdirectory: '{childDirectory}'.");

        var allowedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            CapabilitySealFileName,
            ApplyingMarkerFileName,
            CommittedMarkerFileName,
            RolledBackMarkerFileName,
        };
        foreach (var item in journal.Items)
        {
            allowedNames.Add($"{item.Index}.stage");
            if (item.HadOriginal)
                allowedNames.Add($"{item.Index}.backup");
        }

        var files = Directory.EnumerateFiles(transactionDirectory, "*", SearchOption.TopDirectoryOnly).ToArray();
        foreach (var file in files)
        {
            EnsurePlainFile(file, "External bytes capability");
            var name = Path.GetFileName(file);
            if (!allowedNames.Contains(name) && !IsTransactionAtomicTempName(name, journal))
                throw new InvalidDataException($"External bytes capability directory contains an unknown file: '{file}'.");
        }

        var sealPath = Path.Combine(transactionDirectory, CapabilitySealFileName);
        if (!files.Any(file => string.Equals(Path.GetFileName(file), CapabilitySealFileName, StringComparison.Ordinal)))
            throw new InvalidDataException($"External bytes capability seal is missing: '{sealPath}'.");
        EnsurePlainFile(sealPath, "External bytes capability seal");
        var sealBytes = File.ReadAllBytes(sealPath);
        var sealHash = ContentFingerprint.FromBytes(sealBytes).Sha256;
        if (!string.Equals(sealHash, journal.CapabilitySealSha256, StringComparison.Ordinal))
            throw new InvalidDataException("External bytes capability seal hash does not match the project journal.");

        var seal = JsonSerializer.Deserialize<ExternalBytesCapabilitySeal>(sealBytes, JournalJson)
                   ?? throw new JsonException("External bytes capability seal is empty.");
        var canonicalSeal = JsonSerializer.SerializeToUtf8Bytes(seal, JournalJson);
        if (!sealBytes.AsSpan().SequenceEqual(canonicalSeal))
            throw new InvalidDataException("External bytes capability seal is not canonical.");
        var expectedSeal = SerializeCapabilitySeal(journal);
        if (!sealBytes.AsSpan().SequenceEqual(expectedSeal))
            throw new InvalidDataException("External bytes capability seal does not match the project journal.");

        var fileNames = files.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        var hasApplyingMarker = fileNames.Contains(ApplyingMarkerFileName);
        var hasCommittedMarker = fileNames.Contains(CommittedMarkerFileName);
        var hasRolledBackMarker = fileNames.Contains(RolledBackMarkerFileName);
        if ((hasCommittedMarker || hasRolledBackMarker) && !hasApplyingMarker)
            throw new InvalidDataException("External bytes terminal phase marker is missing its applying predecessor.");
        if (hasCommittedMarker && hasRolledBackMarker)
            throw new InvalidDataException("External bytes capability contains conflicting terminal phase markers.");
        if (hasApplyingMarker)
            ValidatePhaseMarker(journal, transactionDirectory, ExternalBytesPhase.Applying);
        if (hasCommittedMarker)
            ValidatePhaseMarker(journal, transactionDirectory, ExternalBytesPhase.Committed);
        if (hasRolledBackMarker)
            ValidatePhaseMarker(journal, transactionDirectory, ExternalBytesPhase.RolledBack);

        var effectivePhase = hasCommittedMarker
            ? ExternalBytesPhase.Committed
            : hasRolledBackMarker
                ? ExternalBytesPhase.RolledBack
                : hasApplyingMarker
                    ? ExternalBytesPhase.Applying
                    : ExternalBytesPhase.Prepared;

        if (string.Equals(effectivePhase, ExternalBytesPhase.Prepared, StringComparison.Ordinal)
            && journal.Items.Any(item => File.Exists(GetBackupPath(transactionDirectory, item))))
        {
            throw new InvalidDataException("Prepared external bytes capability unexpectedly contains a backup.");
        }
        if (string.Equals(effectivePhase, ExternalBytesPhase.Committed, StringComparison.Ordinal)
            && journal.Items.Any(item => File.Exists(GetStagePath(transactionDirectory, item))))
        {
            throw new InvalidDataException("Committed external bytes capability unexpectedly contains a stage file.");
        }
        if (string.Equals(effectivePhase, ExternalBytesPhase.RolledBack, StringComparison.Ordinal)
            && journal.Items.Any(item => File.Exists(GetBackupPath(transactionDirectory, item))))
        {
            throw new InvalidDataException("Rolled-back external bytes capability unexpectedly contains a backup.");
        }

        foreach (var item in journal.Items)
        {
            var stage = GetStagePath(transactionDirectory, item);
            if (File.Exists(stage))
                EnsureHash(stage, item.NewSha256, $"Staged {item.Kind}");
            var backup = GetBackupPath(transactionDirectory, item);
            if (File.Exists(backup))
                EnsureHash(backup, item.OriginalSha256!, $"Backup {item.Kind}");
        }

        return new CapabilityEvidence(transactionDirectory, effectivePhase);
    }

    private static CleanupEvidence ValidateCleanupTombstone(
        ExternalBytesJournal journal,
        string cleanupDirectory)
    {
        if (!Directory.Exists(cleanupDirectory))
            throw new InvalidDataException($"External bytes cleanup tombstone is missing: '{cleanupDirectory}'.");
        EnsurePlainDirectory(cleanupDirectory, "External bytes cleanup tombstone");
        var childDirectory = Directory.EnumerateDirectories(cleanupDirectory, "*", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (childDirectory is not null)
            throw new InvalidDataException($"External bytes cleanup tombstone contains an unknown subdirectory: '{childDirectory}'.");

        var canonicalNames = new HashSet<string>(StringComparer.Ordinal)
        {
            CapabilitySealFileName,
            ApplyingMarkerFileName,
            CommittedMarkerFileName,
            RolledBackMarkerFileName,
        };
        foreach (var item in journal.Items)
        {
            canonicalNames.Add($"{item.Index}.stage");
            if (item.HadOriginal)
                canonicalNames.Add($"{item.Index}.backup");
        }

        var files = Directory.EnumerateFiles(cleanupDirectory, "*", SearchOption.TopDirectoryOnly).ToArray();
        foreach (var file in files)
        {
            EnsurePlainFile(file, "External bytes cleanup tombstone");
            var name = Path.GetFileName(file);
            if (!canonicalNames.Contains(name) && !IsTransactionAtomicTempName(name, journal))
                throw new InvalidDataException($"External bytes cleanup tombstone contains an unknown file: '{file}'.");
        }

        var sealPath = Path.Combine(cleanupDirectory, CapabilitySealFileName);
        if (!File.Exists(sealPath))
        {
            if (files.Length == 0)
                return new CleanupEvidence(false, []);
            throw new InvalidDataException("External bytes cleanup tombstone lost its seal before all owned files were removed.");
        }

        EnsurePlainFile(sealPath, "External bytes cleanup capability seal");
        var sealBytes = File.ReadAllBytes(sealPath);
        var sealHash = ContentFingerprint.FromBytes(sealBytes).Sha256;
        if (!string.Equals(sealHash, journal.CapabilitySealSha256, StringComparison.Ordinal)
            || !sealBytes.AsSpan().SequenceEqual(SerializeCapabilitySeal(journal)))
        {
            throw new InvalidDataException("External bytes cleanup tombstone seal does not match the project journal.");
        }

        foreach (var item in journal.Items)
        {
            var stage = GetStagePath(cleanupDirectory, item);
            if (File.Exists(stage))
                EnsureHash(stage, item.NewSha256, $"Cleanup staged {item.Kind}");
            var backup = GetBackupPath(cleanupDirectory, item);
            if (File.Exists(backup))
                EnsureHash(backup, item.OriginalSha256!, $"Cleanup backup {item.Kind}");
        }
        foreach (var phase in new[]
                 {
                     ExternalBytesPhase.Applying,
                     ExternalBytesPhase.Committed,
                     ExternalBytesPhase.RolledBack,
                 })
        {
            if (File.Exists(Path.Combine(cleanupDirectory, GetPhaseMarkerFileName(phase))))
                ValidatePhaseMarker(journal, cleanupDirectory, phase);
        }

        return new CleanupEvidence(true, files.ToImmutableArray());
    }

    private static void ValidateCleanupDomainState(ExternalBytesJournal journal)
    {
        if (string.Equals(journal.Phase, ExternalBytesPhase.Prepared, StringComparison.Ordinal))
            ValidatePreparedOutputs(journal);
        else if (string.Equals(journal.Phase, ExternalBytesPhase.Committed, StringComparison.Ordinal))
            ValidateCommittedOutputs(journal);
        else if (string.Equals(journal.Phase, ExternalBytesPhase.RolledBack, StringComparison.Ordinal))
            ValidateRolledBackOutputs(journal);
        else
            throw new InvalidDataException("An applying external bytes transaction must be rolled back before cleanup.");
    }

    private static void TransitionCapabilityPhase(ExternalBytesJournal journal, string nextPhase)
    {
        var evidence = ValidateCapabilityEvidence(journal);
        var expectedCurrent = nextPhase switch
        {
            ExternalBytesPhase.Applying => ExternalBytesPhase.Prepared,
            ExternalBytesPhase.Committed => ExternalBytesPhase.Applying,
            ExternalBytesPhase.RolledBack => ExternalBytesPhase.Applying,
            _ => throw new InvalidDataException($"External bytes phase '{nextPhase}' is not a valid capability transition."),
        };
        if (!string.Equals(evidence.EffectivePhase, expectedCurrent, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"External bytes capability cannot transition from '{evidence.EffectivePhase}' to '{nextPhase}'.");
        }

        if (string.Equals(nextPhase, ExternalBytesPhase.Applying, StringComparison.Ordinal))
            ValidatePreparedOutputs(journal);
        else if (string.Equals(nextPhase, ExternalBytesPhase.Committed, StringComparison.Ordinal))
            ValidateCommittedOutputs(journal);
        else
            ValidateRolledBackOutputs(journal);

        AtomicFile.WriteAllBytes(
            Path.Combine(evidence.TransactionDirectory, GetPhaseMarkerFileName(nextPhase)),
            SerializePhaseMarker(journal, nextPhase));
        var transitioned = ValidateCapabilityEvidence(journal);
        if (!string.Equals(transitioned.EffectivePhase, nextPhase, StringComparison.Ordinal))
            throw new InvalidDataException($"External bytes capability transition to '{nextPhase}' was not durable.");
    }

    private static void ValidatePhaseRelationship(string journalPhase, string effectivePhase)
    {
        if (string.Equals(effectivePhase, ExternalBytesPhase.Prepared, StringComparison.Ordinal))
            return;
        if (string.Equals(effectivePhase, ExternalBytesPhase.Applying, StringComparison.Ordinal)
            && journalPhase is ExternalBytesPhase.Applying
                or ExternalBytesPhase.Committed
                or ExternalBytesPhase.RolledBack)
        {
            return;
        }
        if (string.Equals(effectivePhase, ExternalBytesPhase.Committed, StringComparison.Ordinal)
            && string.Equals(journalPhase, ExternalBytesPhase.Committed, StringComparison.Ordinal))
        {
            return;
        }
        if (string.Equals(effectivePhase, ExternalBytesPhase.RolledBack, StringComparison.Ordinal)
            && string.Equals(journalPhase, ExternalBytesPhase.RolledBack, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidDataException(
            $"External bytes journal phase '{journalPhase}' is behind or conflicts with sealed phase '{effectivePhase}'.");
    }

    private static void ValidatePhaseMarker(
        ExternalBytesJournal journal,
        string transactionDirectory,
        string phase)
    {
        var path = Path.Combine(transactionDirectory, GetPhaseMarkerFileName(phase));
        EnsurePlainFile(path, $"External bytes {phase} marker");
        var actual = File.ReadAllBytes(path);
        var expected = SerializePhaseMarker(journal, phase);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException($"External bytes {phase} marker does not match its capability seal.");
    }

    private static byte[] SerializePhaseMarker(ExternalBytesJournal journal, string phase) =>
        JsonSerializer.SerializeToUtf8Bytes(new ExternalBytesPhaseMarker
        {
            FormatVersion = CurrentFormatVersion,
            TransactionId = journal.TransactionId,
            Phase = phase,
            CapabilitySealSha256 = journal.CapabilitySealSha256,
            Nonce = journal.CapabilityNonce,
        }, JournalJson);

    private static string GetPhaseMarkerFileName(string phase) => phase switch
    {
        ExternalBytesPhase.Applying => ApplyingMarkerFileName,
        ExternalBytesPhase.Committed => CommittedMarkerFileName,
        ExternalBytesPhase.RolledBack => RolledBackMarkerFileName,
        _ => throw new InvalidDataException($"External bytes phase '{phase}' has no marker file."),
    };

    private static bool IsTransactionAtomicTempName(string name, ExternalBytesJournal journal)
    {
        if (IsAtomicTempName(name, ApplyingMarkerFileName)
            || IsAtomicTempName(name, CommittedMarkerFileName)
            || IsAtomicTempName(name, RolledBackMarkerFileName))
        {
            return true;
        }
        return journal.Items.Any(item => IsAtomicTempName(name, $"{item.Index}.stage"));
    }

    private static bool IsAtomicTempName(string name, string canonicalName)
    {
        var prefix = $".{canonicalName}.";
        const string suffix = ".tmp";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }
        var identity = name[prefix.Length..^suffix.Length];
        return identity.Length == 32
               && identity.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static byte[] SerializeCapabilitySeal(ExternalBytesJournal journal) =>
        JsonSerializer.SerializeToUtf8Bytes(new ExternalBytesCapabilitySeal
        {
            FormatVersion = CurrentFormatVersion,
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

    private void WriteBootstrapCapability(ExternalBytesJournal journal)
    {
        var seal = SerializeCapabilitySeal(journal);
        var actualHash = ContentFingerprint.FromBytes(seal).Sha256;
        if (!string.Equals(actualHash, journal.CapabilitySealSha256, StringComparison.Ordinal))
            throw new InvalidDataException("External bytes capability seal hash changed before it was persisted.");
        var canonical = GetBootstrapCapabilityPath(journal.OutputDirectory, journal.TransactionId);
        var temporary = GetBootstrapCapabilityTemporaryPath(journal);
        if (File.Exists(canonical)
            || Directory.Exists(canonical)
            || File.Exists(temporary)
            || Directory.Exists(temporary))
        {
            throw new IOException("External bytes bootstrap capability path already exists.");
        }
        WriteNewFile(temporary, seal);
        _checkpoint?.Invoke(ExternalBytesCheckpoint.BootstrapCapabilityTemporaryWritten);
        File.Move(temporary, canonical);
    }

    private static void InstallBootstrapCapability(
        ExternalBytesJournal journal,
        string transactionDirectory)
    {
        var bootstrap = GetValidatedBootstrapCapabilityPath(journal)
            ?? throw new InvalidDataException("External bytes bootstrap capability is missing.");
        var canonical = GetBootstrapCapabilityPath(journal.OutputDirectory, journal.TransactionId);
        if (!FileSystemComparer.Equals(bootstrap, canonical))
            throw new InvalidDataException("External bytes bootstrap capability was not canonically published.");
        EnsureTransactionDirectoryIsEmpty(transactionDirectory);
        File.Move(bootstrap, Path.Combine(transactionDirectory, CapabilitySealFileName));
    }

    private static void EnsureTransactionDirectoryIsEmpty(string transactionDirectory)
    {
        if (Directory.EnumerateFileSystemEntries(transactionDirectory).Any())
            throw new InvalidDataException($"External bytes transaction directory is not empty: '{transactionDirectory}'.");
    }

    private static void ValidateJournalDirectory(string journalDirectory, ExternalBytesJournal journal)
    {
        ValidateJournalEnvelopeBeforeRead(journalDirectory);
        var journalPath = Path.Combine(journalDirectory, JournalFileName);
        var expected = JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson);
        if (!File.ReadAllBytes(journalPath).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("External bytes recovery journal changed during the operation.");
    }

    private static void ValidateJournalEnvelopeBeforeRead(string journalDirectory)
    {
        EnsurePlainDirectory(journalDirectory, "External bytes recovery journal");
        if (Directory.EnumerateDirectories(journalDirectory, "*", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidDataException("External bytes recovery journal contains an unknown directory.");
        var files = Directory.EnumerateFiles(journalDirectory, "*", SearchOption.TopDirectoryOnly).ToArray();
        foreach (var file in files)
        {
            EnsurePlainFile(file, "External bytes recovery journal");
            var name = Path.GetFileName(file);
            if (!string.Equals(name, JournalFileName, StringComparison.Ordinal) && !IsJournalTempName(name))
                throw new InvalidDataException($"External bytes recovery journal contains an unknown file: '{file}'.");
        }
        var journalPath = Path.Combine(journalDirectory, JournalFileName);
        if (!files.Any(file => string.Equals(Path.GetFileName(file), JournalFileName, StringComparison.Ordinal)))
            throw new InvalidDataException($"External bytes recovery journal is missing: '{journalPath}'.");
        EnsurePlainFile(journalPath, "External bytes recovery journal");
    }

    private static bool TryCleanupJournalTempsWithoutJournal(string journalDirectory)
    {
        EnsurePlainDirectory(journalDirectory, "External bytes recovery journal");
        if (Directory.EnumerateDirectories(journalDirectory, "*", SearchOption.TopDirectoryOnly).Any())
            return false;
        var files = Directory.EnumerateFiles(journalDirectory, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (files.Any(file => !IsJournalTempName(Path.GetFileName(file))))
            return false;
        foreach (var file in files)
        {
            EnsurePlainFile(file, "External bytes abandoned journal temporary");
            DeleteFile(file);
        }
        return true;
    }

    private static bool IsJournalTempName(string name)
    {
        const string prefix = ".journal.json.";
        const string suffix = ".tmp";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }
        var identity = name[prefix.Length..^suffix.Length];
        return identity.Length == 32
               && identity.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static void CleanupJournalDirectory(string journalDirectory)
    {
        ValidateJournalEnvelopeBeforeRead(journalDirectory);
        foreach (var temporary in Directory
                     .EnumerateFiles(journalDirectory, ".journal.json.*.tmp", SearchOption.TopDirectoryOnly)
                     .Where(path => IsJournalTempName(Path.GetFileName(path))))
        {
            DeleteFile(temporary);
        }
        var journalPath = Path.Combine(journalDirectory, JournalFileName);
        DeleteFile(journalPath);
        Directory.Delete(journalDirectory);
    }

    private static void EnsureOutputDirectory(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        EnsureNoReparseDirectoryChain(Path.GetDirectoryName(fullPath), "External bytes output parent");
        if (File.Exists(fullPath))
            throw new IOException($"External bytes output directory is occupied by a file: '{fullPath}'.");
        Directory.CreateDirectory(fullPath);
        EnsurePlainDirectory(fullPath, "External bytes output");
    }

    private static void EnsureFileDestination(string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsurePlainDirectory(Path.GetDirectoryName(fullPath)!, "External bytes destination parent");
        if (Directory.Exists(fullPath))
            throw new IOException($"External bytes destination is occupied by a directory: '{fullPath}'.");
        if (File.Exists(fullPath))
            EnsurePlainFile(fullPath, "External bytes destination");
    }

    private static void EnsurePlainDirectory(string path, string purpose)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"{purpose} directory does not exist: '{fullPath}'.");
        EnsureNoReparseDirectoryChain(fullPath, purpose);
    }

    private static void EnsureNoReparseDirectoryChain(string? path, string purpose)
    {
        for (var current = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
             current is not null;
             current = Directory.GetParent(current)?.FullName)
        {
            if (File.Exists(current))
                throw new InvalidDataException($"{purpose} path contains a file where a directory is required: '{current}'.");
            if (Directory.Exists(current)
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"{purpose} must not traverse a symbolic link, junction, or reparse point: '{current}'.");
            }
        }
    }

    private static void EnsurePlainFile(string path, string purpose)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new InvalidDataException($"{purpose} file is missing: '{fullPath}'.");
        EnsurePlainDirectory(Path.GetDirectoryName(fullPath)!, $"{purpose} parent");
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{purpose} file must not be a symbolic link or reparse point: '{fullPath}'.");
    }

    private static void EnsureHash(string path, string expected, string purpose)
    {
        EnsurePlainFile(path, purpose);
        var actual = ContentFingerprint.FromFile(path).Sha256;
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"{purpose} hash changed unexpectedly: '{path}'.");
    }

    private static void RestoreAttributes(string path, int? attributes)
    {
        if (attributes.HasValue && File.Exists(path))
        {
            EnsurePlainFile(path, "Restored external bytes destination");
            File.SetAttributes(path, (FileAttributes)attributes.Value);
        }
    }

    private static void ClearReadOnly(string path)
    {
        EnsurePlainFile(path, "External bytes file");
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void DeleteFile(string path)
    {
        if (!File.Exists(path))
            return;
        EnsurePlainFile(path, "External bytes cleanup");
        ClearReadOnly(path);
        File.Delete(path);
    }

    private static void WriteNewFile(string path, ReadOnlySpan<byte> content)
    {
        var fullPath = Path.GetFullPath(path);
        EnsurePlainDirectory(Path.GetDirectoryName(fullPath)!, "External bytes transaction");
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new IOException($"External bytes transaction file already exists: '{fullPath}'.");
        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            EnsurePlainDirectory(path, "External bytes recovery");
            Directory.Delete(path);
        }
    }

    private static string GetRecoveryRoot(string projectRoot) =>
        Path.Combine(Path.GetFullPath(projectRoot), ".exceldb", "recovery", RecoverySubtreeName);

    private static string GetJournalDirectory(string projectRoot, Guid transactionId) =>
        Path.Combine(GetRecoveryRoot(projectRoot), transactionId.ToString("N"));

    private static string GetTransactionDirectory(string outputDirectory, Guid transactionId) =>
        Path.Combine(Path.GetFullPath(outputDirectory), $"{TransactionDirectoryPrefix}{transactionId:N}");

    private static string GetCleanupTransactionDirectory(string outputDirectory, Guid transactionId) =>
        GetTransactionDirectory(outputDirectory, transactionId) + CleanupDirectorySuffix;

    private static string GetBootstrapCapabilityPath(string outputDirectory, Guid transactionId) =>
        GetTransactionDirectory(outputDirectory, transactionId) + BootstrapCapabilitySuffix;

    private static string GetBootstrapCapabilityTemporaryPath(ExternalBytesJournal journal) =>
        GetBootstrapCapabilityPath(journal.OutputDirectory, journal.TransactionId)
        + $".{journal.CapabilityNonce}.tmp";

    private static void EnsureExclusiveTransactionLocation(string transactionDirectory, string cleanupDirectory)
    {
        if (File.Exists(transactionDirectory) || File.Exists(cleanupDirectory))
            throw new InvalidDataException("External bytes transaction or cleanup path is occupied by a file.");
        if (Directory.Exists(transactionDirectory) && Directory.Exists(cleanupDirectory))
            throw new InvalidDataException("External bytes transaction and cleanup tombstone both exist.");
    }

    private static string GetStagePath(string transactionDirectory, ExternalBytesJournalItem item) =>
        Path.Combine(transactionDirectory, $"{item.Index}.stage");

    private static string GetBackupPath(string transactionDirectory, ExternalBytesJournalItem item) =>
        Path.Combine(transactionDirectory, $"{item.Index}.backup");

    private static string RequireCanonicalPath(string path, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"External bytes {purpose} must be an absolute canonical path.");
        var fullPath = Path.GetFullPath(path);
        if (!FileSystemComparer.Equals(path, fullPath))
            throw new InvalidDataException($"External bytes {purpose} is not canonical: '{path}'.");
        return fullPath;
    }

    private static bool IsNonce(string? value) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void WriteJournal(string path, ExternalBytesJournal journal) =>
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson));

    private static ExternalBytesJournal ReadJournal(string path)
    {
        EnsurePlainFile(path, "External bytes recovery journal");
        var bytes = File.ReadAllBytes(path);
        var journal = JsonSerializer.Deserialize<ExternalBytesJournal>(bytes, JournalJson)
                      ?? throw new JsonException("External bytes recovery journal is empty.");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson);
        if (!bytes.AsSpan().SequenceEqual(canonical))
            throw new InvalidDataException("External bytes recovery journal is not canonical.");
        return journal;
    }

    private static OperationReport RecoveryFailure(
        string location,
        string message,
        string code = "convert.external-recovery-failed") =>
        new(
            "external-bytes-recover",
            DurableFormatVersion,
            false,
            [new Diagnostic(
                code,
                DiagnosticSeverity.Blocker,
                location,
                message)],
            []);

    private sealed record CapabilityEvidence(string TransactionDirectory, string EffectivePhase);

    private sealed record CleanupEvidence(bool HasSeal, ImmutableArray<string> Files);

    private sealed record RollbackAction(
        ExternalBytesJournalItem Item,
        RollbackActionKind Kind,
        bool DeleteInstalledDestination);

    private enum RollbackActionKind
    {
        None,
        RestoreBackup,
        DeleteInstalled,
    }
}

internal static class ProjectRecovery
{
    private static readonly StringComparer FileSystemComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal static OperationReport RecoverPending(string projectRoot)
    {
        try
        {
            using var lease = AcquireLeaseForOperation(projectRoot, []);
            return RecoverPendingUnderLease(projectRoot);
        }
        catch (ProjectBusyException exception)
        {
            return Failure("project-recover", "durable-v2", projectRoot, exception.Message, "project.busy");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            return Failure("project-recover", "durable-v2", projectRoot, exception.Message, "plan.recovery-failed");
        }
    }

    internal static OperationReport RecoverPendingUnderLease(string projectRoot)
    {
        var external = ExternalBytesTransaction.RecoverPendingUnderLease(projectRoot);
        if (!external.Succeeded)
            return external with { Operation = "project-recover" };

        var mutation = MutationPlanApplier.RecoverPendingUnderLease(projectRoot);
        return new OperationReport(
            "project-recover",
            "durable-v2",
            external.Applied || mutation.Applied,
            external.Diagnostics.AddRange(mutation.Diagnostics),
            []);
    }

    internal static OperationReport Apply(MutationPlan plan)
    {
        if (!MutationPlanCodec.Validate(plan))
            throw new InvalidDataException("MutationPlan planHash is invalid or its body is not canonical.");
        try
        {
            using var lease = AcquireLeaseForOperation(
                plan.ProjectRoot,
                [plan.GetRoot(PlanRootKind.GeneratedCSharp)]);
            var recovery = RecoverPendingUnderLease(plan.ProjectRoot);
            if (!recovery.Succeeded)
                return recovery with { Operation = plan.Operation, ToolVersion = plan.ToolVersion, PlanHash = plan.PlanHash };
            var applied = new MutationPlanApplier().ApplyUnderLease(plan);
            return recovery.Diagnostics.IsEmpty
                ? applied
                : applied with { Diagnostics = recovery.Diagnostics.AddRange(applied.Diagnostics) };
        }
        catch (ProjectBusyException exception)
        {
            return Failure(plan.Operation, plan.ToolVersion, plan.ProjectRoot, exception.Message, "project.busy", plan.PlanHash);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            return Failure(plan.Operation, plan.ToolVersion, plan.ProjectRoot, exception.Message, "plan.apply-failed", plan.PlanHash);
        }
    }

    internal static ProjectOperationLease AcquireLeaseForOperation(
        string projectRoot,
        IEnumerable<string> additionalRoots)
    {
        ArgumentNullException.ThrowIfNull(additionalRoots);
        var extras = additionalRoots.Select(Path.GetFullPath).ToArray();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            string[] discovered;
            using (ProjectOperationLease.Acquire(projectRoot))
                discovered = DiscoverRootsUnderLease(projectRoot, extras);
            var lease = ProjectOperationLease.AcquireMany(discovered);
            var rescanned = DiscoverRootsUnderLease(projectRoot, extras);
            if (rescanned.All(root => discovered.Contains(root, FileSystemComparer)))
                return lease;
            lease.Dispose();
        }
        throw new ProjectBusyException("Project recovery roots changed repeatedly while acquiring their ordered leases.");
    }

    private static string[] DiscoverRootsUnderLease(string projectRoot, IEnumerable<string> additionalRoots) =>
        ExternalBytesTransaction.DiscoverRecoveryRootsUnderLease(projectRoot)
            .Concat(MutationPlanApplier.DiscoverRecoveryRootsUnderLease(projectRoot))
            .Concat(additionalRoots)
            .Distinct(FileSystemComparer)
            .OrderBy(static path => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path, StringComparer.Ordinal)
            .ToArray();

    internal static void EnsureRecovered(string projectRoot)
    {
        var recovery = RecoverPending(projectRoot);
        if (recovery.Succeeded)
            return;
        var detail = recovery.Diagnostics.FirstOrDefault()?.Message
                     ?? "An incomplete project mutation could not be recovered.";
        throw new InvalidDataException($"Project recovery must complete before this operation: {detail}");
    }

    private static OperationReport Failure(
        string operation,
        string toolVersion,
        string projectRoot,
        string message,
        string code,
        string? planHash = null) =>
        new(
            operation,
            toolVersion,
            false,
            [new Diagnostic(code, DiagnosticSeverity.Blocker, projectRoot, message)],
            [],
            planHash);
}

internal enum ExternalBytesCheckpoint
{
    Prepared,
    BootstrapCapabilityTemporaryWritten,
    BootstrapCapabilityWritten,
    TransactionDirectoryCreated,
    CapabilitySealInstalled,
    Applying,
    BytesInstalled,
    ManifestInstalled,
}

internal static class ExternalBytesPhase
{
    internal const string Prepared = "prepared";
    internal const string Applying = "applying";
    internal const string Committed = "committed";
    internal const string RolledBack = "rolled-back";
}

internal sealed class ExternalBytesJournal
{
    public int FormatVersion { get; set; }
    public Guid TransactionId { get; set; }
    public string ProjectRoot { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string CapabilityNonce { get; set; } = string.Empty;
    public string CapabilitySealSha256 { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public List<ExternalBytesJournalItem> Items { get; set; } = [];
}

internal sealed class ExternalBytesCapabilitySeal
{
    public int FormatVersion { get; set; }
    public Guid TransactionId { get; set; }
    public string ProjectRoot { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public List<ExternalBytesJournalItem> Items { get; set; } = [];
}

internal sealed class ExternalBytesPhaseMarker
{
    public int FormatVersion { get; set; }
    public Guid TransactionId { get; set; }
    public string Phase { get; set; } = string.Empty;
    public string CapabilitySealSha256 { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
}

internal sealed class ExternalBytesJournalItem
{
    public int Index { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public bool HadOriginal { get; set; }
    public string? OriginalSha256 { get; set; }
    public int? OriginalAttributes { get; set; }
    public string NewSha256 { get; set; } = string.Empty;
}
