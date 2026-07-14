using System.Collections.Immutable;
using System.Text.Json;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;

namespace ExcelDb.Tooling.Plans;

public sealed class MutationPlanApplier
{
    private const string JournalFileName = "journal.json";
    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public OperationReport Apply(MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!MutationPlanCodec.Validate(plan))
            throw new InvalidDataException("MutationPlan planHash is invalid.");
        if (plan.HasBlockers)
            return new OperationReport(plan.Operation, plan.ToolVersion, false, plan.Diagnostics, [], plan.PlanHash);

        var root = Path.GetFullPath(plan.ProjectRoot);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))
            ?? throw new InvalidOperationException("The project root has no parent directory.");
        var recovery = RecoverPending(root);
        if (!recovery.Succeeded)
            return recovery with { Operation = plan.Operation, PlanHash = plan.PlanHash };
        foreach (var observation in plan.Observations)
        {
            if (!PathFacts.Matches(root, observation))
            {
                return new OperationReport(
                    plan.Operation,
                    plan.ToolVersion,
                    false,
                    [new Diagnostic("plan.stale", DiagnosticSeverity.Blocker, observation.RelativePath, "Observed input changed after the plan was created.")],
                    [],
                    plan.PlanHash);
            }
        }

        if (plan.Mutations.IsEmpty)
            return new OperationReport(plan.Operation, plan.ToolVersion, false, plan.Diagnostics, [], plan.PlanHash);

        Directory.CreateDirectory(parent);
        var transactionRoot = Path.Combine(parent, $".exceldb-txn-{Guid.NewGuid():N}");
        var staged = Path.Combine(transactionRoot, "staged");
        var backup = Path.Combine(transactionRoot, "backup");
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(backup);
        var journalPath = Path.Combine(transactionRoot, JournalFileName);

        var createdDirectories = new List<string>();
        var committed = new List<(string Destination, string? Backup)>();
        var cleanup = false;
        try
        {
            StageWrites(plan, staged);
            var journal = BuildJournal(plan, root, transactionRoot);
            WriteJournal(journalPath, journal);
            foreach (var mutation in plan.Mutations.Where(static item => item.Kind == FileMutationKind.CreateDirectory))
            {
                var destination = PathFacts.ResolveContained(root, mutation.RelativePath);
                if (!Directory.Exists(destination))
                {
                    Directory.CreateDirectory(destination);
                    createdDirectories.Add(destination);
                }
            }

            journal.Phase = "applying";
            WriteJournal(journalPath, journal);
            foreach (var pair in plan.Mutations.Select((mutation, index) => (mutation, index)))
            {
                var mutation = pair.mutation;
                if (mutation.Kind == FileMutationKind.CreateDirectory)
                    continue;

                var destination = PathFacts.ResolveContained(root, mutation.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var journalItem = journal.Items.Single(item => item.Index == pair.index);
                if (File.Exists(destination) != journalItem.HadOriginal)
                    throw new IOException($"Destination '{mutation.RelativePath}' changed while the durable transaction was being prepared.");
                string? backupPath = null;
                if (File.Exists(destination))
                {
                    backupPath = journalItem.BackupPath;
                    File.Move(destination, backupPath);
                }

                if (mutation.Kind == FileMutationKind.WriteFile)
                {
                    var stagedPath = Path.Combine(staged, pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.Move(stagedPath, destination);
                }

                committed.Add((destination, backupPath));
                journalItem.Applied = true;
                WriteJournal(journalPath, journal);
            }

            journal.Phase = "committed";
            WriteJournal(journalPath, journal);
            var artifacts = plan.Mutations
                .Where(static item => item.Kind == FileMutationKind.WriteFile)
                .Select(item => new ArtifactRecord("file", PathFacts.ResolveContained(root, item.RelativePath), item.ContentSha256))
                .ToImmutableArray();
            cleanup = true;
            return new OperationReport(plan.Operation, plan.ToolVersion, true, plan.Diagnostics, artifacts, plan.PlanHash);
        }
        catch
        {
            RollBack(committed, createdDirectories);
            cleanup = true;
            throw;
        }
        finally
        {
            if (cleanup && Directory.Exists(transactionRoot))
                Directory.Delete(transactionRoot, recursive: true);
        }
    }

    /// <summary>Rolls every incomplete durable plan transaction for this project back to its old file set.</summary>
    public static OperationReport RecoverPending(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.GetFullPath(projectRoot);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root));
        if (parent is null || !Directory.Exists(parent))
            return OperationReport.Success("plan-recover", "durable-v1", applied: false);
        var recovered = false;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(parent, ".exceldb-txn-*", SearchOption.TopDirectoryOnly))
            {
                var journalPath = Path.Combine(directory, JournalFileName);
                if (!File.Exists(journalPath))
                    continue;
                var journal = ReadJournal(journalPath);
                if (!string.Equals(Path.GetFullPath(journal.ProjectRoot), root, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(journal.Phase, "committed", StringComparison.Ordinal))
                {
                    foreach (var item in journal.Items.OrderByDescending(static item => item.Index))
                    {
                        if (item.HadOriginal && File.Exists(item.BackupPath))
                        {
                            if (File.Exists(item.Destination))
                                File.Delete(item.Destination);
                            Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
                            File.Move(item.BackupPath, item.Destination);
                        }
                        else if (!item.HadOriginal && File.Exists(item.Destination))
                        {
                            File.Delete(item.Destination);
                        }
                    }
                    foreach (var created in journal.CreatedDirectories.OrderByDescending(static path => path.Length))
                    {
                        if (Directory.Exists(created) && !Directory.EnumerateFileSystemEntries(created).Any())
                            Directory.Delete(created);
                    }
                    recovered = true;
                }
                Directory.Delete(directory, recursive: true);
            }
            return new OperationReport(
                "plan-recover",
                "durable-v1",
                recovered,
                recovered
                    ? [new Diagnostic("plan.recovered", DiagnosticSeverity.Info, root, "Recovered an incomplete cross-artifact mutation plan.")]
                    : [],
                []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new OperationReport(
                "plan-recover",
                "durable-v1",
                false,
                [new Diagnostic("plan.recovery-failed", DiagnosticSeverity.Blocker, root, exception.Message)],
                []);
        }
    }

    private static void StageWrites(MutationPlan plan, string staged)
    {
        foreach (var pair in plan.Mutations.Select((mutation, index) => (mutation, index)))
        {
            if (pair.mutation.Kind != FileMutationKind.WriteFile)
                continue;
            if (pair.mutation.ContentBase64 is null)
                throw new InvalidDataException($"Write mutation '{pair.mutation.RelativePath}' has no content.");
            var content = Convert.FromBase64String(pair.mutation.ContentBase64);
            var actual = ContentFingerprint.FromBytes(content).Sha256;
            if (!string.Equals(actual, pair.mutation.ContentSha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Write mutation '{pair.mutation.RelativePath}' content hash is invalid.");
            AtomicFile.WriteAllBytes(Path.Combine(staged, pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture)), content);
        }
    }

    private static PlanJournal BuildJournal(
        MutationPlan plan,
        string root,
        string transactionRoot)
    {
        var items = plan.Mutations
            .Select((mutation, index) => (mutation, index))
            .Where(static pair => pair.mutation.Kind != FileMutationKind.CreateDirectory)
            .Select(pair =>
            {
                var destination = PathFacts.ResolveContained(root, pair.mutation.RelativePath);
                return new PlanJournalItem
                {
                    Index = pair.index,
                    Destination = destination,
                    StagePath = Path.Combine(transactionRoot, "staged", pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    BackupPath = Path.Combine(transactionRoot, "backup", pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    HadOriginal = File.Exists(destination),
                };
            })
            .ToList();
        var createdDirectories = plan.Mutations
            .Where(static mutation => mutation.Kind == FileMutationKind.CreateDirectory)
            .Select(mutation => PathFacts.ResolveContained(root, mutation.RelativePath))
            .Where(static path => !Directory.Exists(path))
            .ToList();
        return new PlanJournal
        {
            ProjectRoot = root,
            PlanHash = plan.PlanHash,
            Phase = "prepared",
            Items = items,
            CreatedDirectories = createdDirectories,
        };
    }

    private static void WriteJournal(string path, PlanJournal journal) =>
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson));

    private static PlanJournal ReadJournal(string path) =>
        JsonSerializer.Deserialize<PlanJournal>(File.ReadAllBytes(path), JournalJson)
        ?? throw new JsonException("Mutation plan journal is empty.");

    private static void RollBack(
        List<(string Destination, string? Backup)> committed,
        List<string> createdDirectories)
    {
        for (var index = committed.Count - 1; index >= 0; index--)
        {
            var item = committed[index];
            if (File.Exists(item.Destination))
                File.Delete(item.Destination);
            if (item.Backup is not null && File.Exists(item.Backup))
                File.Move(item.Backup, item.Destination);
        }

        for (var index = createdDirectories.Count - 1; index >= 0; index--)
        {
            if (Directory.Exists(createdDirectories[index])
                && !Directory.EnumerateFileSystemEntries(createdDirectories[index]).Any())
            {
                Directory.Delete(createdDirectories[index]);
            }
        }
    }

    private sealed class PlanJournal
    {
        public string ProjectRoot { get; set; } = string.Empty;
        public string PlanHash { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public List<PlanJournalItem> Items { get; set; } = [];
        public List<string> CreatedDirectories { get; set; } = [];
    }

    private sealed class PlanJournalItem
    {
        public int Index { get; set; }
        public string Destination { get; set; } = string.Empty;
        public string StagePath { get; set; } = string.Empty;
        public string BackupPath { get; set; } = string.Empty;
        public bool HadOriginal { get; set; }
        public bool Applied { get; set; }
    }
}
