using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;

namespace ExcelDb.Tooling.Plans;

public sealed class MutationPlanApplier
{
    public OperationReport Apply(MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!MutationPlanCodec.Validate(plan))
            throw new InvalidDataException("MutationPlan planHash is invalid.");
        if (plan.HasBlockers)
            return new OperationReport(plan.Operation, plan.ToolVersion, false, plan.Diagnostics, [], plan.PlanHash);

        var root = Path.GetFullPath(plan.ProjectRoot);
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

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))
            ?? throw new InvalidOperationException("The project root has no parent directory.");
        Directory.CreateDirectory(parent);
        var transactionRoot = Path.Combine(parent, $".exceldb-txn-{Guid.NewGuid():N}");
        var staged = Path.Combine(transactionRoot, "staged");
        var backup = Path.Combine(transactionRoot, "backup");
        Directory.CreateDirectory(staged);
        Directory.CreateDirectory(backup);

        var createdDirectories = new List<string>();
        var committed = new List<(string Destination, string? Backup)>();
        try
        {
            StageWrites(plan, staged);
            foreach (var mutation in plan.Mutations.Where(static item => item.Kind == FileMutationKind.CreateDirectory))
            {
                var destination = PathFacts.ResolveContained(root, mutation.RelativePath);
                if (!Directory.Exists(destination))
                {
                    Directory.CreateDirectory(destination);
                    createdDirectories.Add(destination);
                }
            }

            foreach (var pair in plan.Mutations.Select((mutation, index) => (mutation, index)))
            {
                var mutation = pair.mutation;
                if (mutation.Kind == FileMutationKind.CreateDirectory)
                    continue;

                var destination = PathFacts.ResolveContained(root, mutation.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string? backupPath = null;
                if (File.Exists(destination))
                {
                    backupPath = Path.Combine(backup, pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.Move(destination, backupPath);
                }

                if (mutation.Kind == FileMutationKind.WriteFile)
                {
                    var stagedPath = Path.Combine(staged, pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    File.Move(stagedPath, destination);
                }

                committed.Add((destination, backupPath));
            }

            var artifacts = plan.Mutations
                .Where(static item => item.Kind == FileMutationKind.WriteFile)
                .Select(item => new ArtifactRecord("file", PathFacts.ResolveContained(root, item.RelativePath), item.ContentSha256))
                .ToImmutableArray();
            return new OperationReport(plan.Operation, plan.ToolVersion, true, plan.Diagnostics, artifacts, plan.PlanHash);
        }
        catch
        {
            RollBack(committed, createdDirectories);
            throw;
        }
        finally
        {
            if (Directory.Exists(transactionRoot))
                Directory.Delete(transactionRoot, recursive: true);
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
}
