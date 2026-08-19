using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;

namespace ExcelDb.Workbooks.Transactions;

public enum WorkbookTransactionPhase
{
    Prepared,
    Applying,
    Committed,
    RolledBack,
}

public sealed class WorkbookTransactionItem
{
    public required string TargetPath { get; init; }

    public required string StagePath { get; init; }

    public required string BackupPath { get; init; }

    public required bool OriginallyExisted { get; init; }

    public required ContentFingerprint SourceFingerprint { get; init; }

    public required ContentFingerprint TargetFingerprint { get; init; }

    public bool Applied { get; set; }
}

public sealed class WorkbookTransactionJournal
{
    public required string TransactionId { get; init; }

    public WorkbookTransactionPhase Phase { get; set; }

    public required List<WorkbookTransactionItem> Items { get; init; }
}

public sealed record WorkbookTransactionResult(
    OperationReport Report,
    string? JournalPath);

/// <summary>
/// Cross-workbook write protocol: every output is staged beside its target, every original is
/// backed up, and a durable journal is advanced after each replacement. Recovery always rolls a
/// non-committed journal back to the complete pre-transaction set.
/// </summary>
public static class MultiWorkbookTransaction
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static WorkbookTransactionResult Commit(
        IEnumerable<PreparedWorkbookWrite> writes,
        string? journalDirectory = null)
    {
        var stage = Stage(writes, journalDirectory);
        if (!stage.Report.Succeeded || stage.JournalPath is null)
            return stage;
        return CommitStaged(stage.JournalPath);
    }

    public static WorkbookTransactionResult Stage(
        IEnumerable<PreparedWorkbookWrite> writes,
        string? journalDirectory = null)
    {
        Guard.NotNull(writes);
        var writeArray = writes.ToArray();
        if (writeArray.Length == 0)
            return Failure("multi-workbook-stage", "", "EXWB3100", "No workbook writes were supplied.");
        var duplicate = writeArray
            .GroupBy(write => Path.GetFullPath(write.Plan.WorkbookPath), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            return Failure("multi-workbook-stage", duplicate.Key, "EXWB3101", "A workbook target occurs more than once.");

        foreach (var write in writeArray)
        {
            if (!File.Exists(write.Plan.WorkbookPath)
                || ContentFingerprint.FromFile(write.Plan.WorkbookPath) != write.Plan.SourceFingerprint)
            {
                return Failure(
                    "multi-workbook-stage",
                    write.Plan.WorkbookPath,
                    "EXWB3102",
                    "A workbook changed before transaction staging.");
            }
        }

        var transactionId = Guid.NewGuid().ToString("N");
        var firstDirectory = Path.GetDirectoryName(Path.GetFullPath(writeArray[0].Plan.WorkbookPath))
            ?? throw new InvalidOperationException("Workbook target has no parent directory.");
        var journalRoot = Path.GetFullPath(journalDirectory ?? firstDirectory);
        Directory.CreateDirectory(journalRoot);
        var journalPath = Path.Combine(journalRoot, $".exceldb-{transactionId}.journal.json");
        var journal = new WorkbookTransactionJournal
        {
            TransactionId = transactionId,
            Phase = WorkbookTransactionPhase.Prepared,
            Items = [],
        };

        try
        {
            foreach (var write in writeArray)
            {
                var target = Path.GetFullPath(write.Plan.WorkbookPath);
                var directory = Path.GetDirectoryName(target)!;
                var name = Path.GetFileName(target);
                var stagePath = Path.Combine(directory, $".{name}.{transactionId}.stage");
                var backupPath = Path.Combine(directory, $".{name}.{transactionId}.backup");
                WriteDurable(stagePath, write.Bytes);
                CopyDurable(target, backupPath);
                journal.Items.Add(new WorkbookTransactionItem
                {
                    TargetPath = target,
                    StagePath = stagePath,
                    BackupPath = backupPath,
                    OriginallyExisted = true,
                    SourceFingerprint = write.Plan.SourceFingerprint,
                    TargetFingerprint = write.Fingerprint,
                });
            }

            WriteJournal(journalPath, journal);
            var planHash = string.Join(
                "+",
                writeArray.Select(static write => write.Plan.PlanHash).OrderBy(static hash => hash, StringComparer.Ordinal));
            return new WorkbookTransactionResult(
                new OperationReport(
                    "multi-workbook-stage",
                    WorkbookWriteService.ToolVersion,
                    false,
                    [],
                    [new ArtifactRecord("transaction-journal", journalPath)],
                    planHash),
                journalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CleanupFiles(journal.Items.SelectMany(static item => new[] { item.StagePath, item.BackupPath }));
            CleanupFiles([journalPath]);
            return Failure("multi-workbook-stage", journalPath, "EXWB3103", exception.Message);
        }
    }

    public static WorkbookTransactionResult CommitStaged(string journalPath)
    {
        Guard.NotNullOrWhiteSpace(journalPath);
        WorkbookTransactionJournal journal;
        try
        {
            journal = ReadJournal(journalPath);
            if (journal.Phase != WorkbookTransactionPhase.Prepared)
                return Failure("multi-workbook-commit", journalPath, "EXWB3104", $"Journal is in phase {journal.Phase}.");
            foreach (var item in journal.Items)
            {
                if (!File.Exists(item.StagePath)
                    || ContentFingerprint.FromFile(item.StagePath) != item.TargetFingerprint
                    || !File.Exists(item.TargetPath)
                    || ContentFingerprint.FromFile(item.TargetPath) != item.SourceFingerprint)
                {
                    throw new InvalidOperationException($"Transaction input changed for '{item.TargetPath}'.");
                }
            }

            journal.Phase = WorkbookTransactionPhase.Applying;
            WriteJournal(journalPath, journal);
            foreach (var item in journal.Items)
            {
                PlatformCompatibility.MoveOverwrite(item.StagePath, item.TargetPath);
                item.Applied = true;
                WriteJournal(journalPath, journal);
            }

            journal.Phase = WorkbookTransactionPhase.Committed;
            WriteJournal(journalPath, journal);
            var artifacts = journal.Items
                .Select(static item => new ArtifactRecord("workbook", item.TargetPath, item.TargetFingerprint.Sha256))
                .ToImmutableArray();
            CleanupFiles(journal.Items.Select(static item => item.BackupPath));
            CleanupFiles(journal.Items.Select(static item => item.StagePath));
            CleanupFiles([journalPath]);
            return new WorkbookTransactionResult(
                new OperationReport(
                    "multi-workbook-commit",
                    WorkbookWriteService.ToolVersion,
                    true,
                    [],
                    artifacts),
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            try
            {
                Recover(journalPath);
            }
            catch (Exception recoveryException) when (recoveryException is IOException or UnauthorizedAccessException or JsonException)
            {
                return Failure(
                    "multi-workbook-commit",
                    journalPath,
                    "EXWB3105",
                    $"Commit failed ({exception.Message}) and recovery also failed ({recoveryException.Message}). Journal retained.",
                    journalPath);
            }

            return Failure(
                "multi-workbook-commit",
                journalPath,
                "EXWB3106",
                $"Commit failed and was rolled back: {exception.Message}");
        }
    }

    public static OperationReport Recover(string journalPath)
    {
        Guard.NotNullOrWhiteSpace(journalPath);
        var journal = ReadJournal(journalPath);
        if (journal.Phase == WorkbookTransactionPhase.Committed)
        {
            CleanupFiles(journal.Items.SelectMany(static item => new[] { item.StagePath, item.BackupPath }));
            CleanupFiles([journalPath]);
            return OperationReport.Success("multi-workbook-recover", WorkbookWriteService.ToolVersion, applied: false);
        }

        foreach (var item in journal.Items.AsEnumerable().Reverse())
        {
            if (item.OriginallyExisted)
            {
                if (!File.Exists(item.BackupPath))
                    throw new IOException($"Recovery backup is missing for '{item.TargetPath}'.");
                PlatformCompatibility.MoveOverwrite(item.BackupPath, item.TargetPath);
            }
            else if (File.Exists(item.TargetPath))
            {
                File.Delete(item.TargetPath);
            }
        }

        journal.Phase = WorkbookTransactionPhase.RolledBack;
        WriteJournal(journalPath, journal);
        CleanupFiles(journal.Items.SelectMany(static item => new[] { item.StagePath, item.BackupPath }));
        CleanupFiles([journalPath]);
        return new OperationReport(
            "multi-workbook-recover",
            WorkbookWriteService.ToolVersion,
            true,
            [new Diagnostic("EXWB3107", DiagnosticSeverity.Info, journalPath, "Recovered the complete pre-transaction workbook set.")],
            []);
    }

    public static ImmutableArray<OperationReport> RecoverPending(string directory)
    {
        Guard.NotNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
            return [];
        var reports = ImmutableArray.CreateBuilder<OperationReport>();
        foreach (var path in Directory
                     .EnumerateFiles(directory, ".exceldb-*.journal.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            try
            {
                reports.Add(Recover(path));
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or InvalidOperationException)
            {
                reports.Add(new OperationReport(
                    "multi-workbook-recover",
                    WorkbookWriteService.ToolVersion,
                    false,
                    [new Diagnostic(
                        "EXWB3108",
                        DiagnosticSeverity.Blocker,
                        path,
                        $"Pending transaction could not be recovered; import must remain closed and the journal was retained: {exception.Message}")],
                    []));
            }
        }

        return reports.ToImmutable();
    }

    private static WorkbookTransactionJournal ReadJournal(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return JsonSerializer.Deserialize<WorkbookTransactionJournal>(bytes, JsonOptions)
            ?? throw new JsonException("Transaction journal is empty.");
    }

    private static void WriteJournal(string path, WorkbookTransactionJournal journal) =>
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions));

    private static void WriteDurable(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void CopyDurable(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void CleanupFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static WorkbookTransactionResult Failure(
        string operation,
        string location,
        string code,
        string message,
        string? journalPath = null) =>
        new(
            new OperationReport(
                operation,
                WorkbookWriteService.ToolVersion,
                false,
                [new Diagnostic(code, DiagnosticSeverity.Blocker, location, message)],
                []),
            journalPath);
}
