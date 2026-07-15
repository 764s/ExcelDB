using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;
using ExcelDb.Tooling.Project;

namespace ExcelDb.Tooling.Plans;

/// <summary>
/// Applies a format 2 plan using one same-volume staging area per declared root and a durable
/// project-owned recovery journal. Cross-root visibility is not instantaneous, but every failure
/// is rolled back immediately or is recoverable on the next project operation.
/// </summary>
public sealed class MutationPlanApplier
{
    private const string JournalFileName = "journal.json";
    private const string SealFileName = "seal.json";
    private const string StateFileName = "state.json";
    private const int RootSealFormatVersion = 1;
    private const string RecoveryRelativePath = ".exceldb/recovery";
    private static readonly StringComparer FileSystemComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
    };
    private readonly Action<MutationPlanCheckpoint>? _checkpoint;

    public MutationPlanApplier()
    {
    }

    internal MutationPlanApplier(Action<MutationPlanCheckpoint> checkpoint) =>
        _checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));

    public OperationReport Apply(MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureCurrentFormat(plan.FormatVersion);
        if (!MutationPlanCodec.Validate(plan))
            throw new InvalidDataException("MutationPlan planHash is invalid or its body is not canonical.");
        try
        {
            using var lease = AcquireRecoveryLease(
                plan.ProjectRoot,
                [plan.GetRoot(PlanRootKind.GeneratedCSharp)]);
            return ApplyUnderLease(plan);
        }
        catch (ProjectBusyException exception)
        {
            return Failed(plan, new Diagnostic(
                "project.busy",
                DiagnosticSeverity.Blocker,
                plan.ProjectRoot,
                exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Failed(plan, new Diagnostic(
                "plan.lease-failed",
                DiagnosticSeverity.Blocker,
                plan.ProjectRoot,
                exception.Message));
        }
    }

    /// <summary>Applies a plan while the caller continuously owns its ProjectOperationLease.</summary>
    public OperationReport ApplyUnderLease(MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureCurrentFormat(plan.FormatVersion);
        if (!MutationPlanCodec.Validate(plan))
            throw new InvalidDataException("MutationPlan planHash is invalid or its body is not canonical.");
        var recovery = RecoverPendingUnderLeaseCore(plan.ProjectRoot, _checkpoint);
        if (!recovery.Succeeded)
            return recovery with { Operation = plan.Operation, PlanHash = plan.PlanHash };
        if (plan.HasBlockers)
            return new OperationReport(plan.Operation, plan.ToolVersion, false, plan.Diagnostics, [], plan.PlanHash);

        var shapeDiagnostic = ValidateShape(plan);
        if (shapeDiagnostic is not null)
            return Failed(plan, shapeDiagnostic);

        foreach (var observation in plan.Observations)
        {
            try
            {
                if (!PathFacts.Matches(plan.GetRoot(observation.Root), observation))
                {
                    return Failed(plan, new Diagnostic(
                        "plan.stale",
                        DiagnosticSeverity.Blocker,
                        Display(observation.Root, observation.RelativePath),
                        "Observed input changed after the plan was created."));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Failed(plan, new Diagnostic(
                    "plan.observe-failed",
                    DiagnosticSeverity.Blocker,
                    Display(observation.Root, observation.RelativePath),
                    exception.Message));
            }
        }

        foreach (var inputSet in plan.InputSets)
        {
            try
            {
                if (!InputSetSnapshot.Matches(plan.GetRoot(inputSet.Root), inputSet))
                {
                    return Failed(plan, new Diagnostic(
                        "plan.stale-input-set",
                        DiagnosticSeverity.Blocker,
                        Display(inputSet.Root, inputSet.RelativeRoot),
                        $"The filtered {inputSet.Kind} input set changed after the plan was created."));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Failed(plan, new Diagnostic(
                    "plan.observe-input-set-failed",
                    DiagnosticSeverity.Blocker,
                    Display(inputSet.Root, inputSet.RelativeRoot),
                    exception.Message));
            }
        }

        if (plan.Mutations.IsEmpty)
            return new OperationReport(plan.Operation, plan.ToolVersion, false, plan.Diagnostics, [], plan.PlanHash);

        return ApplyTransaction(plan);
    }

    /// <summary>Rolls back every incomplete format 2 transaction, then imports legacy v1 recovery state.</summary>
    public static OperationReport RecoverPending(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string root;
        try
        {
            root = PathFacts.CanonicalRoot(projectRoot);
            ValidateProjectInfrastructure(root);
        }
        catch (InvalidDataException exception)
        {
            return RecoveryFailure(projectRoot, exception.Message);
        }

        try
        {
            using var lease = AcquireRecoveryLease(root, []);
            return RecoverPendingUnderLease(root);
        }
        catch (ProjectBusyException exception)
        {
            return RecoveryFailure(root, exception.Message, "project.busy");
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            return RecoveryFailure(root, exception.Message);
        }
    }

    public static ImmutableArray<string> DiscoverRecoveryRootsUnderLease(string projectRoot)
    {
        var root = PathFacts.CanonicalRoot(projectRoot);
        ValidateProjectInfrastructure(root);
        var roots = ImmutableArray.CreateBuilder<string>();
        roots.Add(root);
        var recoveryRoot = PathFacts.ResolveContained(root, RecoveryRelativePath);
        if (!Directory.Exists(recoveryRoot))
            return roots.ToImmutable();

        foreach (var journalDirectory in Directory.EnumerateDirectories(recoveryRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var directoryName = Path.GetFileName(journalDirectory);
            if (!Guid.TryParseExact(directoryName, "N", out _))
                continue;
            var journalPath = Path.Combine(journalDirectory, JournalFileName);
            if (!File.Exists(journalPath))
                continue;
            try
            {
                EnsurePlainDirectory(journalDirectory, "MutationPlan recovery journal");
                ValidateJournalDirectory(journalDirectory, requireJournal: true);
                var journal = ReadJournal(journalPath);
                var frozen = MutationPlanCodec.Deserialize(Convert.FromBase64String(journal.PlanBase64));
                if (FileSystemComparer.Equals(frozen.ProjectRoot, root)
                    && string.Equals(frozen.PlanHash, journal.PlanHash, StringComparison.Ordinal)
                    && FileSystemComparer.Equals(frozen.GetRoot(PlanRootKind.GeneratedCSharp), journal.GeneratedCSharpRoot))
                {
                    roots.Add(PathFacts.CanonicalRoot(journal.GeneratedCSharpRoot));
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or JsonException
                                               or FormatException)
            {
                // Full recovery validation reports the blocker without trusting a root from invalid evidence.
            }
        }
        return roots
            .Distinct(FileSystemComparer)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>Recovers MutationPlan journals while the caller continuously owns the project lease.</summary>
    public static OperationReport RecoverPendingUnderLease(string projectRoot)
        => RecoverPendingUnderLeaseCore(projectRoot, checkpoint: null);

    private static OperationReport RecoverPendingUnderLeaseCore(
        string projectRoot,
        Action<MutationPlanCheckpoint>? checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string root;
        try
        {
            root = PathFacts.CanonicalRoot(projectRoot);
            ValidateProjectInfrastructure(root);
        }
        catch (InvalidDataException exception)
        {
            return RecoveryFailure(projectRoot, exception.Message);
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var recovered = false;
        try
        {
            var recoveryRoot = PathFacts.ResolveContained(root, RecoveryRelativePath);
            if (Directory.Exists(recoveryRoot))
            {
                EnsurePlainDirectory(recoveryRoot, "MutationPlan recovery root");
                if (Directory.EnumerateFiles(recoveryRoot, "*", SearchOption.TopDirectoryOnly).Any())
                    throw new InvalidDataException("MutationPlan recovery root contains an unknown file.");
                foreach (var journalDirectory in Directory
                             .EnumerateDirectories(recoveryRoot, "*", SearchOption.TopDirectoryOnly)
                             .OrderBy(static path => path, StringComparer.Ordinal))
                {
                    EnsurePlainDirectory(journalDirectory, "MutationPlan recovery journal");
                    var directoryName = Path.GetFileName(journalDirectory);
                    if (!Guid.TryParseExact(directoryName, "N", out var transactionId))
                        throw new InvalidDataException($"MutationPlan recovery root contains an unknown directory '{directoryName}'.");

                    var journalPath = Path.Combine(journalDirectory, JournalFileName);
                    if (!File.Exists(journalPath))
                    {
                        if (!Directory.EnumerateFileSystemEntries(journalDirectory).Any())
                        {
                            Directory.Delete(journalDirectory);
                            recovered = true;
                            continue;
                        }
                        if (TryCleanInitialJournalTemporary(journalDirectory, transactionId, root))
                        {
                            recovered = true;
                            continue;
                        }
                        throw new InvalidDataException(
                            $"Recovery transaction '{directoryName}' has no journal.json and contains unknown state.");
                    }

                    ValidateJournalDirectory(journalDirectory, requireJournal: true);
                    var journal = ReadJournal(journalPath);
                    var frozenPlan = ValidateJournal(journal, root, transactionId);
                    if (string.Equals(journal.Phase, JournalPhase.Cleanup, StringComparison.Ordinal))
                    {
                        SetExistingRootPhases(journal, JournalPhase.Cleanup);
                        WriteJournal(journalPath, journal);
                        CleanupRootTransactions(journal);
                    }
                    else if (string.Equals(journal.Phase, JournalPhase.Prepared, StringComparison.Ordinal)
                        || string.Equals(journal.Phase, JournalPhase.Staged, StringComparison.Ordinal))
                    {
                        ValidateDomainsUnchanged(journal);
                        SetExistingRootPhases(journal, JournalPhase.Cleanup);
                        journal.Phase = JournalPhase.Cleanup;
                        WriteJournal(journalPath, journal);
                        CleanupRootTransactions(journal);
                    }
                    else if (string.Equals(journal.Phase, JournalPhase.Applying, StringComparison.Ordinal))
                    {
                        ValidateRecoveryProjectConfigContract(frozenPlan);
                        var authorizedRoots = GetAuthorizedDomainRoots(
                            journal,
                            requireInstalledConfig: true,
                            allowRecoveryRollbackCapability: true);
                        EnsureUnauthorizedRootsAreUntouched(journal, authorizedRoots);
                        RollBackTransaction(journal, authorizedRoots);
                        checkpoint?.Invoke(new MutationPlanCheckpoint(MutationPlanCheckpointKind.RollbackCompleted));
                        SetExistingRootPhases(journal, JournalPhase.RolledBack);
                        journal.Phase = JournalPhase.RolledBack;
                        WriteJournal(journalPath, journal);
                        SetExistingRootPhases(journal, JournalPhase.Cleanup);
                        journal.Phase = JournalPhase.Cleanup;
                        WriteJournal(journalPath, journal);
                        CleanupRootTransactions(journal);
                    }
                    else if (string.Equals(journal.Phase, JournalPhase.RolledBack, StringComparison.Ordinal))
                    {
                        ValidateRolledBackFiles(journal);
                        SetExistingRootPhases(journal, JournalPhase.Cleanup);
                        journal.Phase = JournalPhase.Cleanup;
                        WriteJournal(journalPath, journal);
                        CleanupRootTransactions(journal);
                    }
                    else
                    {
                        ValidateCommittedFiles(journal);
                        SetExistingRootPhases(journal, JournalPhase.Cleanup);
                        journal.Phase = JournalPhase.Cleanup;
                        WriteJournal(journalPath, journal);
                        CleanupRootTransactions(journal);
                    }

                    CleanupJournalDirectory(journalDirectory);
                    recovered = true;
                }
            }

            RejectLegacyTransactions(root);
            if (recovered)
            {
                diagnostics.Add(new Diagnostic(
                    "plan.recovered",
                    DiagnosticSeverity.Info,
                    root,
                    "Recovered an incomplete multi-root mutation plan."));
            }

            return new OperationReport("plan-recover", "durable-v2", recovered, diagnostics.ToImmutable(), []);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or InvalidDataException
                                           or ArgumentException)
        {
            return RecoveryFailure(root, exception.Message);
        }
    }

    private OperationReport ApplyTransaction(MutationPlan plan)
    {
        var transactionId = Guid.NewGuid();
        PlanJournal? journal = null;
        var journalDirectory = Path.Combine(
            PathFacts.ResolveContained(plan.ProjectRoot, RecoveryRelativePath),
            transactionId.ToString("N"));
        var journalPath = Path.Combine(journalDirectory, JournalFileName);

        try
        {
            journal = BuildJournal(plan, transactionId);
            Directory.CreateDirectory(journalDirectory);
            _ = PathFacts.ResolveContained(plan.ProjectRoot, Path.GetRelativePath(plan.ProjectRoot, journalDirectory));
            WriteJournal(journalPath, journal);
            _checkpoint?.Invoke(new MutationPlanCheckpoint(MutationPlanCheckpointKind.Prepared));
            CreateRootTransactions(journal);
            StageWrites(journal);
            SetRootPhases(journal, JournalPhase.Staged);
            journal.Phase = JournalPhase.Staged;
            WriteJournal(journalPath, journal);
            _checkpoint?.Invoke(new MutationPlanCheckpoint(MutationPlanCheckpointKind.Staged));

            SetRootPhases(journal, JournalPhase.Applying);
            journal.Phase = JournalPhase.Applying;
            WriteJournal(journalPath, journal);
            EnsureAllObservationsMatch(plan);
            _checkpoint?.Invoke(new MutationPlanCheckpoint(MutationPlanCheckpointKind.Applying));
            CreateRootParentDirectories(journal, journalPath, PlanRootKind.Project);
            CreatePlannedDirectories(journal, journalPath, PlanRootKind.Project);
            foreach (var item in journal.Items
                         .Where(static item => IsProjectConfigItem(item))
                         .OrderBy(static item => item.ApplyOrder))
            {
                ApplyItem(journal, journalPath, item);
                _checkpoint?.Invoke(new MutationPlanCheckpoint(MutationPlanCheckpointKind.ItemApplied, item.Root, item.Index));
            }
            CreateRootParentDirectories(journal, journalPath, PlanRootKind.GeneratedCSharp);
            CreatePlannedDirectories(journal, journalPath, PlanRootKind.GeneratedCSharp);
            foreach (var item in journal.Items
                         .Where(static item => !IsProjectConfigItem(item))
                         .OrderBy(static item => item.ApplyOrder))
            {
                ApplyItem(journal, journalPath, item);
                _checkpoint?.Invoke(new MutationPlanCheckpoint(MutationPlanCheckpointKind.ItemApplied, item.Root, item.Index));
            }

            SetRootPhases(journal, JournalPhase.Committed);
            journal.Phase = JournalPhase.Committed;
            WriteJournal(journalPath, journal);

            var artifacts = plan.Mutations
                .Where(static item => item.Kind == FileMutationKind.WriteFile)
                .Select(item => new ArtifactRecord(
                    "file",
                    PathFacts.ResolveContained(plan.GetRoot(item.Root), item.RelativePath),
                    item.ContentSha256))
                .ToImmutableArray();

            var diagnostics = plan.Diagnostics;
            try
            {
                SetRootPhases(journal, JournalPhase.Cleanup);
                journal.Phase = JournalPhase.Cleanup;
                WriteJournal(journalPath, journal);
                CleanupRootTransactions(journal, _checkpoint);
                CleanupJournalDirectory(journalDirectory);
            }
            catch (Exception cleanupException) when (cleanupException is IOException
                                                       or UnauthorizedAccessException
                                                       or InvalidDataException
                                                       or JsonException)
            {
                diagnostics = diagnostics.Add(new Diagnostic(
                    "plan.cleanup-pending",
                    DiagnosticSeverity.Warning,
                    journalDirectory,
                    $"Artifacts were committed; transaction cleanup will be retried next time: {cleanupException.Message}"));
            }

            return new OperationReport(plan.Operation, plan.ToolVersion, true, diagnostics, artifacts, plan.PlanHash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            if (journal is null)
            {
                return Failed(plan, new Diagnostic(
                    "plan.apply-failed",
                    DiagnosticSeverity.Error,
                    plan.ProjectRoot,
                    $"The multi-root transaction could not be prepared: {exception.Message}"));
            }

            try
            {
                RollBackTransaction(journal, GetAuthorizedDomainRoots(
                    journal,
                    requireInstalledConfig: false,
                    allowRecoveryRollbackCapability: false));
                SetExistingRootPhases(journal, JournalPhase.RolledBack);
                journal.Phase = JournalPhase.RolledBack;
                WriteJournal(journalPath, journal);
                SetExistingRootPhases(journal, JournalPhase.Cleanup);
                journal.Phase = JournalPhase.Cleanup;
                WriteJournal(journalPath, journal);
                CleanupRootTransactions(journal);
                CleanupJournalDirectory(journalDirectory);
                return Failed(plan, new Diagnostic(
                    exception is InputSetChangedException ? "plan.stale-input-set" : "plan.apply-failed",
                    exception is InputSetChangedException ? DiagnosticSeverity.Blocker : DiagnosticSeverity.Error,
                    plan.ProjectRoot,
                    exception is InputSetChangedException
                        ? $"The filtered input set changed while staging; no domain mutations were retained: {exception.Message}"
                        : $"The multi-root transaction failed and was rolled back: {exception.Message}"));
            }
            catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Failed(plan, new Diagnostic(
                    "plan.rollback-failed",
                    DiagnosticSeverity.Blocker,
                    journalDirectory,
                    $"Rollback is incomplete; the next project operation will retry recovery: {rollbackException.Message}"));
            }
        }
    }

    private static Diagnostic? ValidateShape(MutationPlan plan)
    {
        var destinations = new HashSet<string>(FileSystemComparer);
        try
        {
            var projectRoot = PathFacts.CanonicalRoot(plan.ProjectRoot);
            var generatedRoot = PathFacts.CanonicalRoot(plan.GetRoot(PlanRootKind.GeneratedCSharp));
            ValidateProjectConfigContract(plan);
            if (File.Exists(projectRoot))
                return new Diagnostic("plan.root-collision", DiagnosticSeverity.Blocker, projectRoot, "The Project declared root is occupied by a file.");
            if (File.Exists(generatedRoot))
                return new Diagnostic("plan.root-collision", DiagnosticSeverity.Blocker, generatedRoot, "The Generated C# declared root is occupied by a file.");
            var observationKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var observation in plan.Observations)
            {
                _ = PathFacts.ResolveContained(plan.GetRoot(observation.Root), observation.RelativePath);
                var key = ObservationKey(observation.Root, observation.RelativePath);
                if (!observationKeys.Add(key))
                {
                    return new Diagnostic(
                        "plan.observation-duplicate",
                        DiagnosticSeverity.Blocker,
                        Display(observation.Root, observation.RelativePath),
                        "A MutationPlan must not contain duplicate observations for one destination.");
                }
            }

            var inputSetKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var inputSet in plan.InputSets)
            {
                _ = PathFacts.ResolveContained(plan.GetRoot(inputSet.Root), inputSet.RelativeRoot);
                var key = $"{(byte)inputSet.Root}:{(byte)inputSet.Kind}:{PathFacts.Normalize(inputSet.RelativeRoot)}";
                if (!inputSetKeys.Add(key))
                {
                    return new Diagnostic(
                        "plan.input-set-duplicate",
                        DiagnosticSeverity.Blocker,
                        Display(inputSet.Root, inputSet.RelativeRoot),
                        "A MutationPlan must not contain duplicate filtered input-set observations.");
                }
            }

            foreach (var mutation in plan.Mutations)
            {
                var destination = PathFacts.ResolveContained(plan.GetRoot(mutation.Root), mutation.RelativePath);
                var observation = plan.Observations.SingleOrDefault(item =>
                    item.Root == mutation.Root
                    && string.Equals(
                        PathFacts.Normalize(item.RelativePath),
                        PathFacts.Normalize(mutation.RelativePath),
                        StringComparison.Ordinal));
                if (observation is null)
                {
                    return new Diagnostic(
                        "plan.observation-missing",
                        DiagnosticSeverity.Blocker,
                        Display(mutation.Root, mutation.RelativePath),
                        "Every mutation must have exactly one same-root, same-path observation.");
                }
                if ((mutation.Kind == FileMutationKind.CreateDirectory && observation.Kind == ObservedPathKind.File)
                    || (mutation.Kind != FileMutationKind.CreateDirectory && observation.Kind == ObservedPathKind.Directory))
                {
                    return new Diagnostic(
                        "plan.observation-invalid",
                        DiagnosticSeverity.Blocker,
                        Display(mutation.Root, mutation.RelativePath),
                        "The mutation kind contradicts its frozen observation kind.");
                }
                if (!destinations.Add(destination))
                {
                    return new Diagnostic(
                        "plan.path-alias",
                        DiagnosticSeverity.Blocker,
                        Display(mutation.Root, mutation.RelativePath),
                        "Multiple plan paths resolve to the same destination.");
                }

                if (mutation.Root == PlanRootKind.Project
                    && IsRecoveryPath(mutation.RelativePath)
                    && !(mutation.Kind == FileMutationKind.CreateDirectory
                         && string.Equals(
                             PathFacts.Normalize(mutation.RelativePath),
                             RecoveryRelativePath,
                             StringComparison.OrdinalIgnoreCase)))
                {
                    return new Diagnostic(
                        "plan.recovery-reserved",
                        DiagnosticSeverity.Blocker,
                        mutation.RelativePath,
                        "The MutationPlan recovery directory is tool-owned and cannot be a domain mutation target.");
                }

                if (mutation.Kind == FileMutationKind.CreateDirectory)
                {
                    if (File.Exists(destination))
                        return Collision(mutation, "A directory mutation collides with an existing file.");
                    if (mutation.ContentBase64 is not null || mutation.ContentSha256 is not null)
                        return InvalidMutation(mutation, "A directory mutation cannot carry file content.");
                    continue;
                }

                if (Directory.Exists(destination))
                    return Collision(mutation, "A file mutation collides with an existing directory.");
                if (mutation.Kind == FileMutationKind.DeleteFile)
                {
                    if (mutation.ContentBase64 is not null || mutation.ContentSha256 is not null)
                        return InvalidMutation(mutation, "A delete mutation cannot carry file content.");
                    continue;
                }

                if (mutation.Kind != FileMutationKind.WriteFile || mutation.ContentBase64 is null || mutation.ContentSha256 is null)
                    return InvalidMutation(mutation, "A write mutation must carry content and its SHA-256.");
                byte[] content;
                try
                {
                    content = Convert.FromBase64String(mutation.ContentBase64);
                }
                catch (FormatException)
                {
                    return InvalidMutation(mutation, "A write mutation has invalid base64 content.");
                }

                var actual = ContentFingerprint.FromBytes(content).Sha256;
                if (!string.Equals(actual, mutation.ContentSha256, StringComparison.Ordinal))
                    return InvalidMutation(mutation, "A write mutation content hash is invalid.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new Diagnostic("plan.path-unsafe", DiagnosticSeverity.Blocker, plan.ProjectRoot, exception.Message);
        }

        return null;
    }

    private static PlanJournal BuildJournal(MutationPlan plan, Guid transactionId)
    {
        var journal = new PlanJournal
        {
            FormatVersion = MutationPlan.CurrentFormatVersion,
            TransactionId = transactionId,
            PlanHash = plan.PlanHash,
            PlanBase64 = Convert.ToBase64String(MutationPlanCodec.Serialize(plan)),
            ProjectRoot = plan.ProjectRoot,
            GeneratedCSharpRoot = plan.GetRoot(PlanRootKind.GeneratedCSharp),
            ProjectConfigHash = plan.ProjectConfigHash,
            SystemCatalogHash = plan.SystemCatalogHash,
            Phase = JournalPhase.Prepared,
        };

        var fileMutations = plan.Mutations
            .Select((mutation, index) => (mutation, index))
            .Where(static pair => pair.mutation.Kind != FileMutationKind.CreateDirectory)
            .ToArray();
        var applyOrderByIndex = fileMutations
            .OrderBy(static pair => IsProjectConfigMutation(pair.mutation) ? 0 : 1)
            .ThenBy(static pair => pair.index)
            .Select((pair, applyOrder) => (pair.index, applyOrder))
            .ToDictionary(static pair => pair.index, static pair => pair.applyOrder);
        foreach (var pair in fileMutations)
        {
            var destination = PathFacts.ResolveContained(plan.GetRoot(pair.mutation.Root), pair.mutation.RelativePath);
            var hadOriginal = File.Exists(destination);
            var item = new PlanJournalItem
            {
                Index = pair.index,
                ApplyOrder = applyOrderByIndex[pair.index],
                Root = pair.mutation.Root,
                RelativePath = pair.mutation.RelativePath,
                Kind = pair.mutation.Kind,
                ContentBase64 = pair.mutation.ContentBase64,
                ContentSha256 = pair.mutation.ContentSha256,
                HadOriginal = hadOriginal,
                OriginalSha256 = hadOriginal ? ContentFingerprint.FromFile(destination).Sha256 : null,
                OriginalLength = hadOriginal ? new FileInfo(destination).Length : null,
                NewSha256 = pair.mutation.Kind == FileMutationKind.WriteFile
                    ? pair.mutation.ContentSha256
                    : null,
                OriginalAttributes = hadOriginal ? (int)File.GetAttributes(destination) : null,
                State = JournalItemState.Pending,
            };
            ValidateOriginalEvidence(plan, item);
            journal.Items.Add(item);
        }

        var directories = new Dictionary<string, JournalPath>(StringComparer.Ordinal);
        foreach (var mutation in plan.Mutations)
        {
            var root = plan.GetRoot(mutation.Root);
            var destination = PathFacts.ResolveContained(root, mutation.RelativePath);
            var directory = mutation.Kind == FileMutationKind.CreateDirectory
                ? destination
                : Path.GetDirectoryName(destination)!;
            AddMissingDirectories(directories, mutation.Root, root, directory);
        }

        journal.CreatedDirectories = directories.Values
            .OrderBy(static item => item.Root)
            .ThenBy(static item => Depth(item.RelativePath))
            .ThenBy(static item => item.RelativePath, StringComparer.Ordinal)
            .ToList();
        foreach (var rootKind in TouchedRoots(journal))
        {
            var placement = FreezeRootTransactionPlacement(journal, rootKind);
            journal.RootParentDirectories.AddRange(placement.MissingParentDirectories.Select(path =>
                new JournalAbsolutePath { Root = rootKind, AbsolutePath = path }));
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var rootDigest = ComputeRootDigest(journal, rootKind);
            var sealBytes = BuildRootSealBytes(
                journal,
                rootKind,
                placement.TransactionPath,
                nonce,
                rootDigest);
            journal.RootSeals.Add(new JournalRootSeal
            {
                Root = rootKind,
                TransactionPath = placement.TransactionPath,
                Nonce = nonce,
                RootDigestSha256 = rootDigest,
                SealSha256 = ContentFingerprint.FromBytes(sealBytes).Sha256,
            });
        }
        return journal;
    }

    private static RootTransactionPlacement FreezeRootTransactionPlacement(
        PlanJournal journal,
        PlanRootKind rootKind)
    {
        var declaredRoot = PathFacts.CanonicalRoot(GetRoot(journal, rootKind));
        var directParent = Path.GetDirectoryName(declaredRoot)
            ?? throw new InvalidDataException($"Declared root '{declaredRoot}' has no parent directory.");
        var missing = new List<string>();
        var anchor = directParent;
        while (!Directory.Exists(anchor))
        {
            if (File.Exists(anchor))
                throw new InvalidDataException($"Declared root '{declaredRoot}' has a file in its missing parent chain: '{anchor}'.");
            missing.Add(Path.GetFullPath(anchor));
            var parent = Path.GetDirectoryName(anchor);
            if (parent is null || FileSystemComparer.Equals(parent, anchor))
                throw new InvalidDataException($"Declared root '{declaredRoot}' has no existing same-volume ancestor.");
            anchor = parent;
        }

        EnsurePlainDirectory(anchor, $"MutationPlan {rootKind} transaction anchor");
        missing.Reverse();
        var transactionPath = Path.Combine(anchor, TransactionDirectoryName(journal.TransactionId, rootKind));
        EnsureSameVolume(declaredRoot, transactionPath, rootKind);
        return new RootTransactionPlacement(Path.GetFullPath(transactionPath), missing);
    }

    private static void AddMissingDirectories(
        Dictionary<string, JournalPath> directories,
        PlanRootKind rootKind,
        string root,
        string directory)
    {
        var current = directory;
        while (PathFacts.IsContained(root, current))
        {
            var relative = PathFacts.Normalize(Path.GetRelativePath(root, current));
            var full = PathFacts.ResolveContained(root, relative);
            if (File.Exists(full))
                throw new IOException($"Required directory '{rootKind}:{relative}' is occupied by a file.");
            if (!Directory.Exists(full))
                directories.TryAdd($"{(byte)rootKind}:{relative}", new JournalPath { Root = rootKind, RelativePath = relative });
            if (relative == ".")
                break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private void CreateRootTransactions(PlanJournal journal)
    {
        foreach (var rootKind in TouchedRoots(journal))
        {
            var transactionRoot = GetRootTransactionPath(journal, rootKind);
            if (File.Exists(transactionRoot) || Directory.Exists(transactionRoot))
                throw new InvalidDataException($"MutationPlan transaction path already exists: '{transactionRoot}'.");
            var parent = Path.GetDirectoryName(transactionRoot)
                ?? throw new InvalidDataException($"MutationPlan transaction path has no parent: '{transactionRoot}'.");
            EnsurePlainDirectory(parent, "MutationPlan transaction parent");
            var seal = journal.RootSeals.Single(item => item.Root == rootKind);
            var sealBytes = BuildRootSealBytes(
                journal,
                rootKind,
                seal.TransactionPath,
                seal.Nonce,
                seal.RootDigestSha256);
            WriteBootstrapCapability(journal, rootKind, sealBytes);
            _checkpoint?.Invoke(new MutationPlanCheckpoint(
                MutationPlanCheckpointKind.RootBootstrapMarkerWritten,
                rootKind));
            Directory.CreateDirectory(transactionRoot);
            EnsureTransactionRootSafe(journal, rootKind, transactionRoot);
            _checkpoint?.Invoke(new MutationPlanCheckpoint(
                MutationPlanCheckpointKind.RootBootstrapDirectoryCreated,
                rootKind));
            var staged = Path.Combine(transactionRoot, "staged");
            var backup = Path.Combine(transactionRoot, "backup");
            Directory.CreateDirectory(staged);
            EnsurePlainDirectory(staged, "MutationPlan staged");
            Directory.CreateDirectory(backup);
            EnsurePlainDirectory(backup, "MutationPlan backup");
            _checkpoint?.Invoke(new MutationPlanCheckpoint(
                MutationPlanCheckpointKind.RootBootstrapDirectoriesCreated,
                rootKind));
            WriteRootSeal(journal, rootKind, sealBytes);
            EnsureFileHash(
                Path.Combine(transactionRoot, SealFileName),
                seal.SealSha256,
                $"MutationPlan {rootKind} root seal");
            _checkpoint?.Invoke(new MutationPlanCheckpoint(MutationPlanCheckpointKind.RootSealWritten, rootKind));
            WriteRootProgress(journal, rootKind, JournalPhase.Prepared);
            DeleteFileIfPresent(GetRootBootstrapPath(journal, rootKind));
            _checkpoint?.Invoke(new MutationPlanCheckpoint(MutationPlanCheckpointKind.RootPrepared, rootKind));
        }
    }

    private void WriteRootSeal(PlanJournal journal, PlanRootKind rootKind, ReadOnlySpan<byte> sealBytes)
    {
        var destination = Path.Combine(GetRootTransactionPath(journal, rootKind), SealFileName);
        var temporary = GetRootSealTemporaryPath(journal, rootKind);
        WriteNewDurableFile(temporary, sealBytes);
        _checkpoint?.Invoke(new MutationPlanCheckpoint(
            MutationPlanCheckpointKind.RootSealTemporaryWritten,
            rootKind));
        File.Move(temporary, destination);
    }

    private void WriteBootstrapCapability(
        PlanJournal journal,
        PlanRootKind rootKind,
        ReadOnlySpan<byte> sealBytes)
    {
        var destination = GetRootBootstrapPath(journal, rootKind);
        var temporary = GetRootBootstrapTemporaryPath(journal, rootKind);
        if (File.Exists(destination)
            || Directory.Exists(destination)
            || File.Exists(temporary)
            || Directory.Exists(temporary))
        {
            throw new InvalidDataException($"MutationPlan {rootKind} bootstrap capability path already exists.");
        }
        WriteNewDurableFile(temporary, sealBytes);
        _checkpoint?.Invoke(new MutationPlanCheckpoint(
            MutationPlanCheckpointKind.RootBootstrapTemporaryWritten,
            rootKind));
        File.Move(temporary, destination);
    }

    private static void WriteNewDurableFile(string path, ReadOnlySpan<byte> content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private static void StageWrites(PlanJournal journal)
    {
        foreach (var item in journal.Items.Where(static item => item.Kind == FileMutationKind.WriteFile))
        {
            if (item.ContentBase64 is null || item.ContentSha256 is null)
                throw new InvalidDataException($"Write mutation '{item.Root}:{item.RelativePath}' has no content.");
            var content = Convert.FromBase64String(item.ContentBase64);
            var actual = ContentFingerprint.FromBytes(content).Sha256;
            if (!string.Equals(actual, item.ContentSha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Write mutation '{item.Root}:{item.RelativePath}' content hash is invalid.");
            AtomicFile.WriteAllBytes(GetStagePath(journal, item), content);
        }
    }

    private static void CreatePlannedDirectories(
        PlanJournal journal,
        string journalPath,
        PlanRootKind rootKind)
    {
        foreach (var item in journal.CreatedDirectories
                     .Where(item => item.Root == rootKind)
                     .OrderBy(static item => Depth(item.RelativePath))
                     .ThenBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            var destination = Resolve(journal, item.Root, item.RelativePath);
            if (File.Exists(destination))
                throw new IOException($"Directory destination '{item.Root}:{item.RelativePath}' became a file.");
            item.State = JournalDirectoryState.Creating;
            PersistProgress(journal, journalPath, item.Root);
            Directory.CreateDirectory(destination);
            _ = Resolve(journal, item.Root, item.RelativePath);
            item.State = JournalDirectoryState.Created;
            PersistProgress(journal, journalPath, item.Root);
        }
    }

    private static void CreateRootParentDirectories(
        PlanJournal journal,
        string journalPath,
        PlanRootKind rootKind)
    {
        foreach (var item in journal.RootParentDirectories
                     .Where(item => item.Root == rootKind)
                     .OrderBy(static item => PathDepth(item.AbsolutePath))
                     .ThenBy(static item => item.AbsolutePath, StringComparer.Ordinal))
        {
            if (File.Exists(item.AbsolutePath))
                throw new IOException($"Declared-root parent became a file: '{item.AbsolutePath}'.");
            if (Directory.Exists(item.AbsolutePath)
                && !IsAlreadyJournalOwnedDirectory(journal, item))
            {
                throw new IOException($"Declared-root parent appeared after the transaction was prepared: '{item.AbsolutePath}'.");
            }

            item.State = JournalDirectoryState.Creating;
            PersistProgress(journal, journalPath, item.Root);
            var parent = Path.GetDirectoryName(item.AbsolutePath)
                ?? throw new InvalidDataException($"Declared-root parent has no parent: '{item.AbsolutePath}'.");
            EnsurePlainDirectory(parent, $"MutationPlan {rootKind} declared-root parent");
            Directory.CreateDirectory(item.AbsolutePath);
            EnsurePlainDirectory(item.AbsolutePath, $"MutationPlan {rootKind} declared-root parent");
            item.State = JournalDirectoryState.Created;
            PersistProgress(journal, journalPath, item.Root);
        }
    }

    private static bool IsAlreadyJournalOwnedDirectory(PlanJournal journal, JournalAbsolutePath candidate)
    {
        if (candidate.Root == PlanRootKind.Project)
            return true; // The project-local recovery envelope may have created this missing ancestor.
        if (journal.RootParentDirectories.Any(item =>
                !ReferenceEquals(item, candidate)
                && item.State is JournalDirectoryState.Creating or JournalDirectoryState.Created
                && FileSystemComparer.Equals(item.AbsolutePath, candidate.AbsolutePath)))
        {
            return true;
        }
        return journal.CreatedDirectories.Any(item =>
            item.State is JournalDirectoryState.Creating or JournalDirectoryState.Created
            && FileSystemComparer.Equals(Resolve(journal, item.Root, item.RelativePath), candidate.AbsolutePath));
    }

    private static void ApplyItem(PlanJournal journal, string journalPath, PlanJournalItem item)
    {
        var destination = Resolve(journal, item.Root, item.RelativePath);
        if (Directory.Exists(destination))
            throw new IOException($"Destination '{item.Root}:{item.RelativePath}' became a directory.");
        if (item.HadOriginal)
            EnsureFileHash(destination, item.OriginalSha256!, $"Original {item.Root}:{item.RelativePath}");
        else if (File.Exists(destination))
            throw new IOException($"Destination '{item.Root}:{item.RelativePath}' changed while the transaction was being prepared.");

        item.State = JournalItemState.Applying;
        PersistProgress(journal, journalPath, item.Root);
        var backup = GetBackupPath(journal, item);
        if (item.HadOriginal)
            PathFacts.ClearReadOnly(destination);

        if (item.Kind == FileMutationKind.WriteFile)
        {
            var staged = GetStagePath(journal, item);
            EnsureRegularFile(staged, $"Staged {item.Root}:{item.RelativePath}");
            EnsureFileHash(staged, item.NewSha256!, $"Staged {item.Root}:{item.RelativePath}");
            if (item.HadOriginal)
            {
                File.Replace(staged, destination, backup, ignoreMetadataErrors: true);
                EnsureFileHash(backup, item.OriginalSha256!, $"Backup {item.Root}:{item.RelativePath}");
            }
            else
                File.Move(staged, destination);
            EnsureFileHash(destination, item.NewSha256!, $"Installed {item.Root}:{item.RelativePath}");
        }
        else if (item.Kind == FileMutationKind.DeleteFile && item.HadOriginal)
        {
            File.Move(destination, backup);
            EnsureFileHash(backup, item.OriginalSha256!, $"Backup {item.Root}:{item.RelativePath}");
        }

        item.State = JournalItemState.Applied;
        PersistProgress(journal, journalPath, item.Root);
    }

    private static void RollBackTransaction(
        PlanJournal journal,
        IReadOnlySet<PlanRootKind> authorizedRoots)
    {
        foreach (var item in journal.Items
                     .Where(item => authorizedRoots.Contains(item.Root) && !IsProjectConfigItem(item))
                     .OrderByDescending(static item => item.ApplyOrder))
        {
            RollBackItem(journal, item);
        }

        RemoveCreatedDirectories(journal, authorizedRoots);
        RemoveCreatedRootParentDirectories(journal, authorizedRoots);

        foreach (var item in journal.Items
                     .Where(item => authorizedRoots.Contains(item.Root) && IsProjectConfigItem(item))
                     .OrderByDescending(static item => item.ApplyOrder))
        {
            RollBackItem(journal, item);
        }
    }

    private static void RollBackItem(PlanJournal journal, PlanJournalItem item)
    {
        var destination = Resolve(journal, item.Root, item.RelativePath);
        var staged = GetStagePath(journal, item);
        var backup = GetBackupPath(journal, item);
        if (Directory.Exists(destination))
            throw new InvalidDataException($"Cannot recover '{item.Root}:{item.RelativePath}'; destination became a directory.");
        if (Directory.Exists(staged))
            throw new InvalidDataException($"Cannot recover '{item.Root}:{item.RelativePath}'; staged content became a directory.");
        if (Directory.Exists(backup))
            throw new InvalidDataException($"Cannot recover '{item.Root}:{item.RelativePath}'; backup became a directory.");

        var hasDestination = File.Exists(destination);
        var hasStaged = File.Exists(staged);
        var hasBackup = File.Exists(backup);

        if (item.Kind == FileMutationKind.WriteFile)
        {
            if (hasStaged)
                EnsureFileHash(staged, item.NewSha256!, $"Staged {item.Root}:{item.RelativePath}");

            if (!item.HadOriginal)
            {
                if (hasBackup)
                    throw new InvalidDataException($"Unexpected backup exists for new file '{item.Root}:{item.RelativePath}'.");
                if (hasStaged)
                {
                    if (hasDestination)
                    {
                        throw new InvalidDataException(
                            $"Cannot recover new file '{item.Root}:{item.RelativePath}'; staged content proves the transaction did not install the competing destination.");
                    }
                    return;
                }
                if (!hasDestination)
                    return;

                EnsureFileHash(destination, item.NewSha256!, $"Installed {item.Root}:{item.RelativePath}");
                DeleteFileIfPresent(destination);
                return;
            }

            if (hasStaged)
            {
                if (hasBackup || !hasDestination)
                {
                    throw new InvalidDataException(
                        $"Cannot recover '{item.Root}:{item.RelativePath}'; staged/original evidence is not a legal pre-install state.");
                }
                EnsureFileHash(destination, item.OriginalSha256!, $"Original {item.Root}:{item.RelativePath}");
                RestoreAttributes(destination, item.OriginalAttributes);
                return;
            }

            if (hasBackup)
            {
                EnsureFileHash(backup, item.OriginalSha256!, $"Backup {item.Root}:{item.RelativePath}");
                if (hasDestination)
                    EnsureFileHash(destination, item.NewSha256!, $"Installed {item.Root}:{item.RelativePath}");
                if (hasDestination)
                    DeleteFileIfPresent(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(backup, destination);
                EnsureFileHash(destination, item.OriginalSha256!, $"Restored {item.Root}:{item.RelativePath}");
                RestoreAttributes(destination, item.OriginalAttributes);
                return;
            }

            if (!hasDestination)
            {
                throw new InvalidDataException(
                    $"Cannot recover '{item.Root}:{item.RelativePath}'; both the original destination and its backup are missing.");
            }
            EnsureFileHash(destination, item.OriginalSha256!, $"Original {item.Root}:{item.RelativePath}");
            RestoreAttributes(destination, item.OriginalAttributes);
            return;
        }

        if (hasStaged)
            throw new InvalidDataException($"Delete recovery has unexpected staged content for '{item.Root}:{item.RelativePath}'.");
        if (!item.HadOriginal)
        {
            if (hasBackup || hasDestination)
            {
                throw new InvalidDataException(
                    $"Cannot recover delete '{item.Root}:{item.RelativePath}'; a previously absent path now has transaction or domain content.");
            }
            return;
        }

        if (hasBackup)
        {
            EnsureFileHash(backup, item.OriginalSha256!, $"Backup {item.Root}:{item.RelativePath}");
            if (hasDestination)
            {
                throw new InvalidDataException(
                    $"Cannot recover delete '{item.Root}:{item.RelativePath}'; backup and destination cannot legally coexist.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(backup, destination);
            EnsureFileHash(destination, item.OriginalSha256!, $"Restored {item.Root}:{item.RelativePath}");
            RestoreAttributes(destination, item.OriginalAttributes);
            return;
        }

        if (!hasDestination)
        {
            throw new InvalidDataException(
                $"Cannot recover delete '{item.Root}:{item.RelativePath}'; both the original destination and its backup are missing.");
        }
        EnsureFileHash(destination, item.OriginalSha256!, $"Original {item.Root}:{item.RelativePath}");
        RestoreAttributes(destination, item.OriginalAttributes);
    }

    private static void RemoveCreatedDirectories(
        PlanJournal journal,
        IReadOnlySet<PlanRootKind> authorizedRoots)
    {
        foreach (var item in journal.CreatedDirectories
                     .Where(item => authorizedRoots.Contains(item.Root)
                                    && item.State is JournalDirectoryState.Creating or JournalDirectoryState.Created)
                     .OrderByDescending(static item => Depth(item.RelativePath))
                     .ThenByDescending(static item => item.RelativePath, StringComparer.Ordinal))
        {
            var directory = Resolve(journal, item.Root, item.RelativePath);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    private static void RemoveCreatedRootParentDirectories(
        PlanJournal journal,
        IReadOnlySet<PlanRootKind> authorizedRoots)
    {
        foreach (var item in journal.RootParentDirectories
                     .Where(item => authorizedRoots.Contains(item.Root)
                                    && item.State is JournalDirectoryState.Creating or JournalDirectoryState.Created)
                     .OrderByDescending(static item => PathDepth(item.AbsolutePath))
                     .ThenByDescending(static item => item.AbsolutePath, StringComparer.Ordinal))
        {
            if (File.Exists(item.AbsolutePath))
                throw new InvalidDataException($"Cannot recover declared-root parent; it became a file: '{item.AbsolutePath}'.");
            if (Directory.Exists(item.AbsolutePath))
            {
                EnsurePlainDirectory(item.AbsolutePath, "MutationPlan declared-root parent rollback");
                if (!Directory.EnumerateFileSystemEntries(item.AbsolutePath).Any())
                    Directory.Delete(item.AbsolutePath);
            }
        }
    }

    private static void CleanupRootTransactions(
        PlanJournal journal,
        Action<MutationPlanCheckpoint>? checkpoint = null)
    {
        var roots = TouchedRoots(journal);
        foreach (var rootKind in roots)
        {
            var live = GetRootTransactionPath(journal, rootKind);
            var tombstone = GetRootCleanupTransactionPath(journal, rootKind);
            var bootstrapCapability = GetValidatedBootstrapCapabilityPath(journal, rootKind);
            EnsureTransactionLocationsAreDirectories(rootKind, live, tombstone);
            if (Directory.Exists(live) && Directory.Exists(tombstone))
                throw new InvalidDataException($"MutationPlan {rootKind} has both live and cleanup transaction roots.");
            if (Directory.Exists(live))
            {
                if (!File.Exists(Path.Combine(live, StateFileName)))
                {
                    if (bootstrapCapability is null)
                        throw new InvalidDataException($"MutationPlan {rootKind} partial bootstrap has no physical capability.");
                    ValidateBootstrapTransactionTree(journal, rootKind);
                }
                else
                {
                    ValidateTransactionTree(journal, rootKind, requireSeal: true);
                }
            }
            else if (Directory.Exists(tombstone))
                ValidateCleanupTransactionTree(journal, rootKind, tombstone);
            else if (bootstrapCapability is not null)
                ValidateBootstrapTransactionTree(journal, rootKind);
        }
        foreach (var rootKind in roots)
        {
            CleanupRootTransaction(journal, rootKind);
            checkpoint?.Invoke(new MutationPlanCheckpoint(MutationPlanCheckpointKind.RootCleaned, rootKind));
        }
    }

    private static void CleanupRootTransaction(PlanJournal journal, PlanRootKind rootKind)
    {
        var transactionRoot = GetRootTransactionPath(journal, rootKind);
        var cleanupRoot = GetRootCleanupTransactionPath(journal, rootKind);
        var bootstrapCapability = GetValidatedBootstrapCapabilityPath(journal, rootKind);
        EnsureTransactionLocationsAreDirectories(rootKind, transactionRoot, cleanupRoot);
        if (Directory.Exists(transactionRoot)
            && !File.Exists(Path.Combine(transactionRoot, StateFileName)))
        {
            if (bootstrapCapability is null)
                throw new InvalidDataException($"MutationPlan {rootKind} partial bootstrap has no physical capability.");
            CleanupBootstrapTransactionTree(journal, rootKind, bootstrapCapability);
            return;
        }
        if (!Directory.Exists(transactionRoot) && !Directory.Exists(cleanupRoot))
        {
            if (bootstrapCapability is not null)
                DeleteFileIfPresent(bootstrapCapability);
            return;
        }
        if (Directory.Exists(transactionRoot))
            Directory.Move(transactionRoot, cleanupRoot);
        ValidateCleanupTransactionTree(journal, rootKind, cleanupRoot);
        if (!File.Exists(Path.Combine(cleanupRoot, SealFileName)))
        {
            if (bootstrapCapability is not null)
            {
                Directory.Delete(cleanupRoot);
                DeleteFileIfPresent(bootstrapCapability);
            }
            return;
        }
        foreach (var directoryName in new[] { "staged", "backup" })
        {
            var directory = Path.Combine(cleanupRoot, directoryName);
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                    DeleteFileIfPresent(file);
                Directory.Delete(directory);
            }
        }
        DeleteFileIfPresent(Path.Combine(cleanupRoot, StateFileName));
        foreach (var file in Directory.EnumerateFiles(cleanupRoot, ".state.json.*.tmp", SearchOption.TopDirectoryOnly))
            DeleteFileIfPresent(file);
        DeleteFileIfPresent(Path.Combine(cleanupRoot, SealFileName));
        Directory.Delete(cleanupRoot);
        if (bootstrapCapability is not null)
            DeleteFileIfPresent(bootstrapCapability);
    }

    private static void CleanupBootstrapTransactionTree(
        PlanJournal journal,
        PlanRootKind rootKind,
        string bootstrapCapability)
    {
        ValidateBootstrapTransactionTree(journal, rootKind);
        var transactionRoot = GetRootTransactionPath(journal, rootKind);
        if (Directory.Exists(transactionRoot))
        {
            DeleteFileIfPresent(GetRootSealTemporaryPath(journal, rootKind));
            DeleteFileIfPresent(Path.Combine(transactionRoot, SealFileName));
            foreach (var file in Directory.EnumerateFiles(transactionRoot, "*", SearchOption.TopDirectoryOnly)
                         .Where(path => IsStateTemporary(Path.GetFileName(path))))
            {
                DeleteFileIfPresent(file);
            }
            foreach (var directoryName in new[] { "staged", "backup" })
            {
                var directory = Path.Combine(transactionRoot, directoryName);
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
            }
            Directory.Delete(transactionRoot);
        }
        DeleteFileIfPresent(bootstrapCapability);
    }

    private static string Resolve(PlanJournal journal, PlanRootKind root, string relativePath) =>
        PathFacts.ResolveContained(GetRoot(journal, root), relativePath);

    private static string GetRoot(PlanJournal journal, PlanRootKind root) => root switch
    {
        PlanRootKind.Project => journal.ProjectRoot,
        PlanRootKind.GeneratedCSharp => journal.GeneratedCSharpRoot,
        _ => throw new InvalidDataException($"Unknown journal root kind '{root}'."),
    };

    private static string GetRootTransactionPath(PlanJournal journal, PlanRootKind rootKind)
    {
        var matches = journal.RootSeals.Where(item => item.Root == rootKind).Take(2).ToArray();
        if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].TransactionPath))
            throw new InvalidDataException($"MutationPlan has no unique frozen transaction path for {rootKind}.");
        return Path.GetFullPath(matches[0].TransactionPath);
    }

    private static string TransactionDirectoryName(Guid transactionId, PlanRootKind rootKind) =>
        $".exceldb-txn-{transactionId:N}-{rootKind.ToString().ToLowerInvariant()}";

    private static void EnsureSameVolume(string declaredRoot, string transactionPath, PlanRootKind rootKind)
    {
        var declaredVolume = Path.GetPathRoot(Path.GetFullPath(declaredRoot));
        var transactionVolume = Path.GetPathRoot(Path.GetFullPath(transactionPath));
        if (string.IsNullOrWhiteSpace(declaredVolume)
            || string.IsNullOrWhiteSpace(transactionVolume)
            || !FileSystemComparer.Equals(
                Path.TrimEndingDirectorySeparator(declaredVolume),
                Path.TrimEndingDirectorySeparator(transactionVolume)))
        {
            throw new InvalidDataException(
                $"MutationPlan {rootKind} transaction path must be on the declared root volume.");
        }
    }

    private static string GetRootCleanupTransactionPath(PlanJournal journal, PlanRootKind rootKind) =>
        GetRootTransactionPath(journal, rootKind) + ".cleanup";

    private static string GetRootBootstrapPath(PlanJournal journal, PlanRootKind rootKind) =>
        GetRootTransactionPath(journal, rootKind) + ".bootstrap";

    private static string GetRootBootstrapTemporaryPath(PlanJournal journal, PlanRootKind rootKind)
    {
        var seal = journal.RootSeals.Single(item => item.Root == rootKind);
        return GetRootBootstrapPath(journal, rootKind) + $".{seal.Nonce}.tmp";
    }

    private static string GetRootSealTemporaryPath(PlanJournal journal, PlanRootKind rootKind)
    {
        var seal = journal.RootSeals.Single(item => item.Root == rootKind);
        return Path.Combine(GetRootTransactionPath(journal, rootKind), $".{SealFileName}.{seal.Nonce}.tmp");
    }

    private static string? GetValidatedBootstrapCapabilityPath(
        PlanJournal journal,
        PlanRootKind rootKind)
    {
        var canonical = GetRootBootstrapPath(journal, rootKind);
        var temporary = GetRootBootstrapTemporaryPath(journal, rootKind);
        var existing = new[] { canonical, temporary }
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToArray();
        if (existing.Length > 1)
            throw new InvalidDataException($"MutationPlan {rootKind} has duplicate bootstrap capability evidence.");
        if (existing.Length == 0)
            return null;

        var path = existing[0];
        EnsureRegularFile(path, $"MutationPlan {rootKind} bootstrap capability");
        var seal = journal.RootSeals.Single(item => item.Root == rootKind);
        EnsureFileHash(path, seal.SealSha256, $"MutationPlan {rootKind} bootstrap capability");
        var expected = BuildRootSealBytes(
            journal,
            rootKind,
            seal.TransactionPath,
            seal.Nonce,
            seal.RootDigestSha256);
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException($"MutationPlan {rootKind} bootstrap capability is not canonical.");
        return path;
    }

    private static void ValidateBootstrapTransactionTree(
        PlanJournal journal,
        PlanRootKind rootKind)
    {
        if (GetValidatedBootstrapCapabilityPath(journal, rootKind) is null)
            throw new InvalidDataException($"MutationPlan {rootKind} partial bootstrap has no physical capability.");

        var transactionRoot = GetRootTransactionPath(journal, rootKind);
        if (File.Exists(transactionRoot))
            throw new InvalidDataException($"MutationPlan {rootKind} bootstrap root became a file.");
        if (!Directory.Exists(transactionRoot))
            return;

        EnsureTransactionRootSafe(journal, rootKind, transactionRoot);
        var sealPath = Path.Combine(transactionRoot, SealFileName);
        var sealTemporary = GetRootSealTemporaryPath(journal, rootKind);
        var topFiles = Directory.EnumerateFiles(transactionRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (topFiles.Any(path =>
                !FileSystemComparer.Equals(path, sealPath)
                && !FileSystemComparer.Equals(path, sealTemporary)
                && !IsStateTemporary(Path.GetFileName(path)))
            || (File.Exists(sealPath) && File.Exists(sealTemporary)))
        {
            throw new InvalidDataException($"MutationPlan {rootKind} partial bootstrap contains unknown metadata.");
        }
        foreach (var file in topFiles)
            EnsureRegularFile(file, $"MutationPlan {rootKind} bootstrap metadata");
        if (File.Exists(sealPath))
        {
            var seal = journal.RootSeals.Single(item => item.Root == rootKind);
            EnsureFileHash(sealPath, seal.SealSha256, $"MutationPlan {rootKind} bootstrap seal");
            var expected = BuildRootSealBytes(
                journal,
                rootKind,
                seal.TransactionPath,
                seal.Nonce,
                seal.RootDigestSha256);
            if (!File.ReadAllBytes(sealPath).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException($"MutationPlan {rootKind} bootstrap seal is not canonical.");
        }

        var childDirectories = Directory.EnumerateDirectories(transactionRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (childDirectories.Any(path => Path.GetFileName(path) is not ("staged" or "backup")))
            throw new InvalidDataException($"MutationPlan {rootKind} partial bootstrap contains an unknown directory.");
        foreach (var directory in childDirectories)
        {
            EnsurePlainDirectory(directory, $"MutationPlan {rootKind} bootstrap directory");
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                throw new InvalidDataException($"MutationPlan {rootKind} partial bootstrap directory is not empty.");
        }
    }

    private static string GetStagePath(PlanJournal journal, PlanJournalItem item) =>
        Path.Combine(GetRootTransactionPath(journal, item.Root), "staged", item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string GetBackupPath(PlanJournal journal, PlanJournalItem item) =>
        Path.Combine(GetRootTransactionPath(journal, item.Root), "backup", item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void EnsureTransactionRootSafe(PlanJournal journal, PlanRootKind kind, string transactionRoot)
    {
        var expected = GetRootTransactionPath(journal, kind);
        if (!FileSystemComparer.Equals(Path.GetFullPath(expected), Path.GetFullPath(transactionRoot)))
            throw new InvalidDataException("MutationPlan transaction staging path is invalid.");
        if ((File.GetAttributes(transactionRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Transaction staging path is a reparse point: '{transactionRoot}'.");
    }

    private static MutationPlan ValidateJournal(PlanJournal journal, string projectRoot, Guid transactionId)
    {
        if (journal.FormatVersion != MutationPlan.CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported recovery journal format {journal.FormatVersion}.");
        if (journal.TransactionId != transactionId)
            throw new InvalidDataException("Recovery journal transaction id does not match its directory.");
        if (!IsSha256(journal.PlanHash) || string.IsNullOrWhiteSpace(journal.PlanBase64))
            throw new InvalidDataException("Recovery journal has no valid frozen plan evidence.");
        if (!FileSystemComparer.Equals(PathFacts.CanonicalRoot(journal.ProjectRoot), projectRoot))
            throw new InvalidDataException("Recovery journal belongs to a different project root.");
        if (journal.Phase is not (JournalPhase.Prepared
            or JournalPhase.Staged
            or JournalPhase.Applying
            or JournalPhase.Committed
            or JournalPhase.RolledBack
            or JournalPhase.Cleanup))
        {
            throw new InvalidDataException($"Recovery journal has unknown phase '{journal.Phase}'.");
        }
        var declaredJournalPhase = journal.Phase;

        MutationPlan frozenPlan;
        try
        {
            frozenPlan = MutationPlanCodec.Deserialize(Convert.FromBase64String(journal.PlanBase64));
        }
        catch (Exception exception) when (exception is FormatException or InvalidDataException or JsonException)
        {
            throw new InvalidDataException("Recovery journal frozen plan evidence is invalid.", exception);
        }
        if (!string.Equals(frozenPlan.PlanHash, journal.PlanHash, StringComparison.Ordinal)
            || !FileSystemComparer.Equals(frozenPlan.ProjectRoot, journal.ProjectRoot)
            || !FileSystemComparer.Equals(
                frozenPlan.GetRoot(PlanRootKind.GeneratedCSharp),
                PathFacts.CanonicalRoot(journal.GeneratedCSharpRoot))
            || !string.Equals(frozenPlan.ProjectConfigHash, journal.ProjectConfigHash, StringComparison.Ordinal)
            || !string.Equals(frozenPlan.SystemCatalogHash, journal.SystemCatalogHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Recovery journal roots, config/catalog hashes, or planHash do not match its frozen canonical plan.");
        }

        if (journal.Items is null
            || journal.CreatedDirectories is null
            || journal.RootParentDirectories is null
            || journal.RootSeals is null)
            throw new InvalidDataException("Recovery journal collections must not be null.");
        var expectedItems = frozenPlan.Mutations
            .Select((mutation, index) => (mutation, index))
            .Where(static pair => pair.mutation.Kind != FileMutationKind.CreateDirectory)
            .ToArray();
        var expectedApplyOrders = expectedItems
            .OrderBy(static pair => IsProjectConfigMutation(pair.mutation) ? 0 : 1)
            .ThenBy(static pair => pair.index)
            .Select((pair, applyOrder) => (pair.index, applyOrder))
            .ToDictionary(static pair => pair.index, static pair => pair.applyOrder);
        if (journal.Items.Count != expectedItems.Length)
            throw new InvalidDataException("Recovery journal file items do not match its frozen plan.");
        for (var itemIndex = 0; itemIndex < journal.Items.Count; itemIndex++)
        {
            var item = journal.Items[itemIndex]
                ?? throw new InvalidDataException("Recovery journal contains a null item.");
            var expected = expectedItems[itemIndex];
            if (!Enum.IsDefined(item.Root))
                throw new InvalidDataException($"Recovery journal has unknown root kind '{item.Root}'.");
            if (item.Kind is not (FileMutationKind.WriteFile or FileMutationKind.DeleteFile))
                throw new InvalidDataException($"Recovery journal has unsupported file mutation kind '{item.Kind}'.");
            if (item.State is not (JournalItemState.Pending or JournalItemState.Applying or JournalItemState.Applied))
                throw new InvalidDataException($"Recovery journal has unknown item state '{item.State}'.");
            if (item.Index != expected.index
                || item.ApplyOrder != expectedApplyOrders[expected.index]
                || item.Root != expected.mutation.Root
                || item.Kind != expected.mutation.Kind
                || !string.Equals(item.RelativePath, expected.mutation.RelativePath, StringComparison.Ordinal)
                || !string.Equals(item.ContentBase64, expected.mutation.ContentBase64, StringComparison.Ordinal)
                || !string.Equals(item.ContentSha256, expected.mutation.ContentSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Recovery journal file item does not match its frozen plan mutation.");
            }
            ValidateOriginalEvidence(frozenPlan, item);
            if (item.HadOriginal != (item.OriginalSha256 is not null)
                || (item.HadOriginal && (!IsSha256(item.OriginalSha256) || item.OriginalAttributes is null)))
            {
                throw new InvalidDataException($"Recovery journal has invalid original evidence for '{item.Root}:{item.RelativePath}'.");
            }
            if (item.Kind == FileMutationKind.WriteFile)
            {
                if (!IsSha256(item.NewSha256)
                    || !string.Equals(item.NewSha256, item.ContentSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Recovery journal has invalid new evidence for '{item.Root}:{item.RelativePath}'.");
                }
                byte[] content;
                try
                {
                    content = Convert.FromBase64String(item.ContentBase64!);
                }
                catch (FormatException exception)
                {
                    throw new InvalidDataException($"Recovery journal has invalid content for '{item.Root}:{item.RelativePath}'.", exception);
                }
                if (!string.Equals(ContentFingerprint.FromBytes(content).Sha256, item.NewSha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Recovery journal content hash is invalid for '{item.Root}:{item.RelativePath}'.");
            }
            else if (item.NewSha256 is not null || item.ContentBase64 is not null || item.ContentSha256 is not null)
            {
                throw new InvalidDataException($"Recovery delete item carries unexpected content for '{item.Root}:{item.RelativePath}'.");
            }
            _ = Resolve(journal, item.Root, item.RelativePath);
        }
        var allowedDirectories = BuildAllowedCreatedDirectories(frozenPlan);
        var seenDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in journal.CreatedDirectories)
        {
            if (!Enum.IsDefined(item.Root))
                throw new InvalidDataException($"Recovery journal has unknown directory root kind '{item.Root}'.");
            if (item.State is not (JournalDirectoryState.Pending
                or JournalDirectoryState.Creating
                or JournalDirectoryState.Created))
            {
                throw new InvalidDataException($"Recovery journal has unknown created-directory state '{item.State}'.");
            }
            _ = Resolve(journal, item.Root, item.RelativePath);
            var key = $"{(byte)item.Root}:{item.RelativePath}";
            if (!seenDirectories.Add(key) || !allowedDirectories.Contains(key))
                throw new InvalidDataException($"Recovery journal contains an unexpected created directory '{key}'.");
        }

        ValidateRootSealDeclarations(journal);
        LoadAuthoritativeRootProgress(journal, declaredJournalPhase);
        ValidateItemStateSequence(journal);
        foreach (var rootKind in TouchedRoots(journal))
        {
            var liveRoot = GetRootTransactionPath(journal, rootKind);
            var cleanupRoot = GetRootCleanupTransactionPath(journal, rootKind);
            var bootstrapCapability = GetValidatedBootstrapCapabilityPath(journal, rootKind);
            if (Directory.Exists(liveRoot))
            {
                if (!File.Exists(Path.Combine(liveRoot, StateFileName))
                    && bootstrapCapability is not null)
                {
                    ValidateBootstrapTransactionTree(journal, rootKind);
                }
                else
                {
                    ValidateTransactionTree(journal, rootKind, requireSeal: true);
                }
            }
            else if (Directory.Exists(cleanupRoot))
                ValidateCleanupTransactionTree(journal, rootKind, cleanupRoot);
            else if (bootstrapCapability is not null)
                ValidateBootstrapTransactionTree(journal, rootKind);
        }
        return frozenPlan;
    }

    private static void ValidateOriginalEvidence(MutationPlan frozenPlan, PlanJournalItem item)
    {
        var observations = frozenPlan.Observations.Where(observation =>
            observation.Root == item.Root
            && string.Equals(
                PathFacts.Normalize(observation.RelativePath),
                PathFacts.Normalize(item.RelativePath),
                StringComparison.Ordinal)).ToArray();
        if (observations.Length != 1)
            throw new InvalidDataException($"Recovery item '{item.Root}:{item.RelativePath}' does not have one frozen observation.");
        var observation = observations[0];
        if (observation.Kind == ObservedPathKind.Missing)
        {
            if (item.HadOriginal || item.OriginalSha256 is not null || item.OriginalLength is not null)
                throw new InvalidDataException($"Recovery item '{item.Root}:{item.RelativePath}' contradicts its missing observation.");
            return;
        }
        if (observation.Kind != ObservedPathKind.File
            || !item.HadOriginal
            || !IsSha256(observation.Sha256)
            || !string.Equals(item.OriginalSha256, observation.Sha256, StringComparison.Ordinal)
            || item.OriginalLength != observation.Length)
        {
            throw new InvalidDataException($"Recovery item '{item.Root}:{item.RelativePath}' original hash/length does not match its frozen observation.");
        }
    }

    private static void ValidateRecoveryObservations(PlanJournal journal, MutationPlan frozenPlan)
    {
        ValidateRecoveryProjectConfigContract(frozenPlan);
        var mutationKeys = frozenPlan.Mutations
            .Select(static mutation => ObservationKey(mutation.Root, mutation.RelativePath))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var observation in frozenPlan.Observations)
        {
            if (mutationKeys.Contains(ObservationKey(observation.Root, observation.RelativePath)))
                continue;
            if (!PathFacts.Matches(frozenPlan.GetRoot(observation.Root), observation))
            {
                throw new InvalidDataException(
                    $"Frozen observation changed before recovery: '{observation.Root}:{observation.RelativePath}'.");
            }
        }
    }

    private static void ValidateRecoveryProjectConfigContract(MutationPlan plan)
    {
        var projectFile = PathFacts.ResolveContained(plan.ProjectRoot, ExcelDbProject.RelativeFilePath);
        var observations = plan.Observations.Where(static item =>
            item.Root == PlanRootKind.Project
            && string.Equals(
                PathFacts.Normalize(item.RelativePath),
                ExcelDbProject.RelativeFilePath,
                StringComparison.Ordinal)).ToArray();
        var candidates = plan.Mutations.Where(static item =>
            IsProjectConfigPath(item.Root, item.RelativePath)).ToArray();
        if (observations.Length > 1 || candidates.Length > 1)
            throw new InvalidDataException("Recovery plan has duplicate project configuration evidence.");
        if (candidates.Length == 1 && candidates[0].Kind != FileMutationKind.WriteFile)
            throw new InvalidDataException("Recovery plan cannot delete the Project v2 configuration.");

        var observation = observations.SingleOrDefault();
        var candidate = candidates.SingleOrDefault();
        if (observation is null)
        {
            if (!PathFacts.IsContained(plan.ProjectRoot, plan.GetRoot(PlanRootKind.GeneratedCSharp)))
                throw new InvalidDataException("External recovery has no Project v2 configuration observation.");
            return;
        }
        if (observation.Kind == ObservedPathKind.File)
        {
            if (!string.Equals(observation.Sha256, plan.ProjectConfigHash, StringComparison.Ordinal))
                throw new InvalidDataException("Recovery projectConfigHash does not match its original config observation.");
        }
        else if (observation.Kind != ObservedPathKind.Missing
                 || candidate is null
                 || !string.Equals(candidate.ContentSha256, plan.ProjectConfigHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Recovery missing-config evidence is not bound to one candidate Project v2 write.");
        }
        if (candidate is not null)
            ValidateCandidateProjectConfig(plan, projectFile, candidate, observation.Kind == ObservedPathKind.Missing);

        if (!File.Exists(projectFile))
            return;
        EnsureRegularFile(projectFile, "Recovery Project v2 configuration");
        var bytes = File.ReadAllBytes(projectFile);
        var hash = ContentFingerprint.FromBytes(bytes).Sha256;
        var candidateHash = candidate?.ContentSha256;
        if (!string.Equals(hash, plan.ProjectConfigHash, StringComparison.Ordinal)
            && !string.Equals(hash, candidateHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Installed Project v2 configuration matches neither original nor candidate recovery evidence.");
        }
        var parsed = ExcelDbProject.Parse(bytes);
        if (!parsed.ToCanonicalJson().AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("Installed recovery Project v2 configuration is not canonical JSON.");
        if (string.Equals(hash, candidateHash, StringComparison.Ordinal)
            && !FileSystemComparer.Equals(
                parsed.Resolve(projectFile).GeneratedCSharpDirectory,
                PathFacts.CanonicalRoot(plan.GetRoot(PlanRootKind.GeneratedCSharp))))
        {
            throw new InvalidDataException("Installed candidate Project v2 configuration does not authorize the recovery Generated C# root.");
        }
    }

    private static void EnsureAllObservationsMatch(MutationPlan plan)
    {
        foreach (var observation in plan.Observations)
        {
            if (IsRecoveryInfrastructureObservation(plan, observation))
                continue;
            if (!PathFacts.Matches(plan.GetRoot(observation.Root), observation))
            {
                throw new InvalidDataException(
                    $"Observed input changed while staging the transaction: '{observation.Root}:{observation.RelativePath}'.");
            }
        }
        foreach (var inputSet in plan.InputSets)
        {
            if (!InputSetSnapshot.Matches(plan.GetRoot(inputSet.Root), inputSet))
            {
                throw new InputSetChangedException(
                    $"Filtered input set changed while staging the transaction: '{inputSet.Root}:{inputSet.RelativeRoot}' ({inputSet.Kind}).");
            }
        }
    }

    private static bool IsRecoveryInfrastructureObservation(
        MutationPlan plan,
        PathObservation observation)
    {
        if (observation.Root != PlanRootKind.Project || observation.Kind != ObservedPathKind.Missing)
            return false;
        var relative = PathFacts.Normalize(observation.RelativePath);
        if (relative is not ("." or ".exceldb" or RecoveryRelativePath))
            return false;
        return plan.Mutations.Any(mutation =>
            mutation.Root == PlanRootKind.Project
            && mutation.Kind == FileMutationKind.CreateDirectory
            && string.Equals(PathFacts.Normalize(mutation.RelativePath), relative, StringComparison.Ordinal));
    }

    private static void ValidateProjectConfigContract(MutationPlan plan)
    {
        var projectFile = PathFacts.ResolveContained(plan.ProjectRoot, ExcelDbProject.RelativeFilePath);
        var observations = plan.Observations.Where(static observation =>
            observation.Root == PlanRootKind.Project
            && string.Equals(
                PathFacts.Normalize(observation.RelativePath),
                ExcelDbProject.RelativeFilePath,
                StringComparison.Ordinal)).ToArray();
        var candidates = plan.Mutations.Where(static mutation =>
            IsProjectConfigPath(mutation.Root, mutation.RelativePath)).ToArray();
        if (observations.Length > 1 || candidates.Length > 1)
            throw new InvalidDataException("MutationPlan has duplicate project configuration evidence.");
        if (Directory.Exists(projectFile))
            throw new InvalidDataException("Project v2 configuration path is occupied by a directory.");
        if (!File.Exists(projectFile))
        {
            if (candidates.Length == 0
                && PathFacts.IsContained(plan.ProjectRoot, plan.GetRoot(PlanRootKind.GeneratedCSharp)))
                return;
            if (observations.Length != 1
                || observations[0].Kind != ObservedPathKind.Missing
                || observations[0].Length != 0
                || observations[0].Sha256 is not null
                || candidates.Length != 1)
            {
                throw new InvalidDataException("A missing Project v2 configuration requires one missing observation and one canonical candidate write.");
            }
            ValidateCandidateProjectConfig(plan, projectFile, candidates[0], requirePlanConfigHash: true);
            return;
        }

        EnsureRegularFile(projectFile, "Project v2 configuration");
        var currentBytes = File.ReadAllBytes(projectFile);
        var currentHash = ContentFingerprint.FromBytes(currentBytes).Sha256;
        var current = ExcelDbProject.Parse(currentBytes);
        if (!current.ToCanonicalJson().AsSpan().SequenceEqual(currentBytes))
            throw new InvalidDataException("Installed Project v2 configuration is not canonical JSON.");
        if (observations.Length != 1)
            throw new InvalidDataException("A frozen MutationPlan must observe the installed Project v2 configuration exactly once.");
        var observation = observations[0];
        if (observation.Kind != ObservedPathKind.File
            || observation.Length != currentBytes.LongLength
            || !string.Equals(observation.Sha256, currentHash, StringComparison.Ordinal)
            || !string.Equals(plan.ProjectConfigHash, currentHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("MutationPlan projectConfigHash/observation does not match the installed Project v2 configuration.");
        }
        if (candidates.Length == 0)
        {
            var installedRoot = current.Resolve(projectFile).GeneratedCSharpDirectory;
            if (!FileSystemComparer.Equals(installedRoot, PathFacts.CanonicalRoot(plan.GetRoot(PlanRootKind.GeneratedCSharp))))
                throw new InvalidDataException("MutationPlan Generated C# root is not authorized by the installed Project v2 configuration.");
            return;
        }

        ValidateCandidateProjectConfig(plan, projectFile, candidates[0], requirePlanConfigHash: false);
    }

    private static void ValidateCandidateProjectConfig(
        MutationPlan plan,
        string projectFile,
        FileMutation candidateMutation,
        bool requirePlanConfigHash)
    {
        if (candidateMutation.Kind != FileMutationKind.WriteFile
            || candidateMutation.ContentBase64 is null
            || candidateMutation.ContentSha256 is null)
        {
            throw new InvalidDataException("Candidate Project v2 configuration mutation has no write content evidence.");
        }
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(candidateMutation.ContentBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Candidate Project v2 configuration has invalid base64 content.", exception);
        }
        var hash = ContentFingerprint.FromBytes(bytes).Sha256;
        var candidate = ExcelDbProject.Parse(bytes);
        var candidateRoot = candidate.Resolve(projectFile).GeneratedCSharpDirectory;
        if (!candidate.ToCanonicalJson().AsSpan().SequenceEqual(bytes)
            || !string.Equals(hash, candidateMutation.ContentSha256, StringComparison.Ordinal)
            || (requirePlanConfigHash && !string.Equals(hash, plan.ProjectConfigHash, StringComparison.Ordinal))
            || !FileSystemComparer.Equals(candidateRoot, PathFacts.CanonicalRoot(plan.GetRoot(PlanRootKind.GeneratedCSharp))))
        {
            throw new InvalidDataException("Candidate Project v2 configuration is not canonically bound to the MutationPlan Generated C# root.");
        }
    }

    private static HashSet<PlanRootKind> GetAuthorizedDomainRoots(
        PlanJournal journal,
        bool requireInstalledConfig,
        bool allowRecoveryRollbackCapability)
    {
        var authorized = new HashSet<PlanRootKind> { PlanRootKind.Project };
        if (PathFacts.IsContained(journal.ProjectRoot, journal.GeneratedCSharpRoot))
        {
            authorized.Add(PlanRootKind.GeneratedCSharp);
            return authorized;
        }

        var projectFile = PathFacts.ResolveContained(journal.ProjectRoot, ExcelDbProject.RelativeFilePath);
        if (File.Exists(projectFile))
        {
            try
            {
                EnsureRegularFile(projectFile, "Project v2 configuration");
                var bytes = File.ReadAllBytes(projectFile);
                var hash = ContentFingerprint.FromBytes(bytes).Sha256;
                var project = ExcelDbProject.Parse(bytes);
                if (project.ToCanonicalJson().AsSpan().SequenceEqual(bytes))
                {
                    var context = project.Resolve(projectFile);
                    var candidateHash = journal.Items
                        .Where(static item => IsProjectConfigItem(item) && item.Kind == FileMutationKind.WriteFile)
                        .Select(static item => item.NewSha256)
                        .SingleOrDefault();
                    var hashIsAuthorized = string.Equals(hash, journal.ProjectConfigHash, StringComparison.Ordinal)
                                           || string.Equals(hash, candidateHash, StringComparison.Ordinal);
                    if (FileSystemComparer.Equals(context.GeneratedCSharpDirectory, journal.GeneratedCSharpRoot)
                        && hashIsAuthorized)
                    {
                        authorized.Add(PlanRootKind.GeneratedCSharp);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or JsonException)
            {
                if (!requireInstalledConfig)
                    return authorized;
            }
        }

        if (!authorized.Contains(PlanRootKind.GeneratedCSharp)
            && allowRecoveryRollbackCapability
            && HasSealedRecoveryRollbackCapability(journal))
        {
            authorized.Add(PlanRootKind.GeneratedCSharp);
        }
        return authorized;
    }

    private static bool HasSealedRecoveryRollbackCapability(PlanJournal journal)
    {
        if (journal.Phase != JournalPhase.Applying)
            return false;
        var candidate = journal.Items.SingleOrDefault(static item => IsProjectConfigItem(item));
        if (candidate?.State != JournalItemState.Applied)
            return false;
        if (!journal.Items.Any(static item =>
                item.Root == PlanRootKind.GeneratedCSharp && item.State != JournalItemState.Pending)
            && !journal.CreatedDirectories.Any(static item =>
                item.Root == PlanRootKind.GeneratedCSharp && item.State != JournalDirectoryState.Pending)
            && !journal.RootParentDirectories.Any(static item =>
                item.Root == PlanRootKind.GeneratedCSharp && item.State != JournalDirectoryState.Pending))
        {
            return false;
        }

        var transactionRoot = GetRootTransactionPath(journal, PlanRootKind.GeneratedCSharp);
        return Directory.Exists(transactionRoot)
               && !Directory.Exists(GetRootCleanupTransactionPath(journal, PlanRootKind.GeneratedCSharp))
               && File.Exists(Path.Combine(transactionRoot, SealFileName))
               && File.Exists(Path.Combine(transactionRoot, StateFileName));
    }

    private static void EnsureUnauthorizedRootsAreUntouched(
        PlanJournal journal,
        IReadOnlySet<PlanRootKind> authorizedRoots)
    {
        foreach (var rootKind in TouchedRoots(journal).Where(root => !authorizedRoots.Contains(root)))
        {
            if (journal.Items.Any(item => item.Root == rootKind && item.State != JournalItemState.Pending)
                || journal.CreatedDirectories.Any(item => item.Root == rootKind && item.State != JournalDirectoryState.Pending)
                || journal.RootParentDirectories.Any(item =>
                    item.Root == rootKind && item.State != JournalDirectoryState.Pending))
            {
                throw new InvalidDataException(
                    $"Recovery cannot touch {rootKind} domain paths until the matching Project v2 configuration is installed.");
            }
        }
    }

    private static void EnsureAllTouchedRootsAuthorized(
        PlanJournal journal,
        IReadOnlySet<PlanRootKind> authorizedRoots)
    {
        var unauthorized = TouchedRoots(journal).FirstOrDefault(root => !authorizedRoots.Contains(root));
        if (TouchedRoots(journal).Any(root => !authorizedRoots.Contains(root)))
        {
            throw new InvalidDataException(
                $"Committed recovery root '{unauthorized}' is not authorized by the installed Project v2 configuration.");
        }
    }

    private static void ValidateDomainsUnchanged(PlanJournal journal)
    {
        if (journal.Items.Any(static item => item.State != JournalItemState.Pending)
            || journal.CreatedDirectories.Any(static item => item.State != JournalDirectoryState.Pending)
            || journal.RootParentDirectories.Any(static item => item.State != JournalDirectoryState.Pending))
        {
            throw new InvalidDataException($"Recovery phase '{journal.Phase}' requires untouched domain state.");
        }
        foreach (var item in journal.Items)
        {
            var destination = Resolve(journal, item.Root, item.RelativePath);
            if (item.HadOriginal)
                EnsureFileHash(destination, item.OriginalSha256!, $"Unchanged {item.Root}:{item.RelativePath}");
            else if (File.Exists(destination) || Directory.Exists(destination))
                throw new InvalidDataException($"Prepared/staged destination appeared: '{item.Root}:{item.RelativePath}'.");
        }
        foreach (var item in journal.CreatedDirectories)
        {
            var destination = Resolve(journal, item.Root, item.RelativePath);
            if (IsRecoveryEnvelopeDirectory(item))
            {
                EnsurePlainDirectory(destination, $"MutationPlan recovery envelope {item.RelativePath}");
                continue;
            }
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new InvalidDataException($"Prepared/staged directory appeared: '{item.Root}:{item.RelativePath}'.");
        }
        foreach (var item in journal.RootParentDirectories)
        {
            if (item.Root == PlanRootKind.Project && Directory.Exists(item.AbsolutePath))
            {
                EnsurePlainDirectory(item.AbsolutePath, "MutationPlan project recovery-envelope parent");
                continue;
            }
            if (File.Exists(item.AbsolutePath) || Directory.Exists(item.AbsolutePath))
                throw new InvalidDataException($"Prepared/staged declared-root parent appeared: '{item.AbsolutePath}'.");
        }
    }

    private static bool IsRecoveryEnvelopeDirectory(JournalPath item)
    {
        if (item.Root != PlanRootKind.Project)
            return false;
        var relative = PathFacts.Normalize(item.RelativePath);
        return relative is "." or ".exceldb" or RecoveryRelativePath;
    }

    private static void ValidateItemStateSequence(PlanJournal journal)
    {
        if (string.Equals(journal.Phase, JournalPhase.Prepared, StringComparison.Ordinal)
            || string.Equals(journal.Phase, JournalPhase.Staged, StringComparison.Ordinal))
        {
            if (journal.Items.Any(static item => item.State != JournalItemState.Pending)
                || journal.CreatedDirectories.Any(static item => item.State != JournalDirectoryState.Pending)
                || journal.RootParentDirectories.Any(static item => item.State != JournalDirectoryState.Pending))
            {
                throw new InvalidDataException($"Recovery journal phase '{journal.Phase}' requires every item and directory to be pending.");
            }
            return;
        }
        if (string.Equals(journal.Phase, JournalPhase.Committed, StringComparison.Ordinal))
        {
            if (journal.Items.Any(static item => item.State != JournalItemState.Applied)
                || journal.CreatedDirectories.Any(static item => item.State != JournalDirectoryState.Created)
                || journal.RootParentDirectories.Any(static item => item.State != JournalDirectoryState.Created))
            {
                throw new InvalidDataException("Committed recovery journal requires every item and directory to be applied.");
            }
            return;
        }

        var previousRank = -1;
        var applyingCount = 0;
        foreach (var item in journal.Items.OrderBy(static item => item.ApplyOrder))
        {
            var rank = item.State switch
            {
                JournalItemState.Applied => 0,
                JournalItemState.Applying => 1,
                JournalItemState.Pending => 2,
                _ => throw new InvalidDataException($"Unknown recovery item state '{item.State}'."),
            };
            if (rank < previousRank)
                throw new InvalidDataException("Recovery journal item states are not a valid applied/applying/pending prefix.");
            if (rank == 1 && ++applyingCount > 1)
                throw new InvalidDataException("Recovery journal has more than one applying item.");
            previousRank = rank;
        }
    }

    private static HashSet<string> BuildAllowedCreatedDirectories(MutationPlan plan)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mutation in plan.Mutations)
        {
            var root = plan.GetRoot(mutation.Root);
            var destination = PathFacts.ResolveContained(root, mutation.RelativePath);
            var directory = mutation.Kind == FileMutationKind.CreateDirectory
                ? destination
                : Path.GetDirectoryName(destination)!;
            while (PathFacts.IsContained(root, directory))
            {
                var relative = PathFacts.Normalize(Path.GetRelativePath(root, directory));
                allowed.Add($"{(byte)mutation.Root}:{relative}");
                if (relative == ".")
                    break;
                directory = Path.GetDirectoryName(directory)!;
            }
        }
        return allowed;
    }

    private static void ValidateTransactionTree(
        PlanJournal journal,
        PlanRootKind rootKind,
        bool requireSeal)
    {
        var transactionRoot = GetRootTransactionPath(journal, rootKind);
        if (File.Exists(transactionRoot))
            throw new InvalidDataException($"MutationPlan transaction root became a file: '{transactionRoot}'.");
        if (!Directory.Exists(transactionRoot))
        {
            if (requireSeal)
                throw new InvalidDataException($"MutationPlan transaction root is missing: '{transactionRoot}'.");
            return;
        }
        EnsureTransactionRootSafe(journal, rootKind, transactionRoot);
        var topFiles = Directory.EnumerateFiles(transactionRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (!topFiles.Any(path => string.Equals(Path.GetFileName(path), SealFileName, StringComparison.Ordinal))
            || topFiles.Count(path => string.Equals(Path.GetFileName(path), StateFileName, StringComparison.Ordinal)) > 1
            || topFiles.Any(path =>
                !string.Equals(Path.GetFileName(path), SealFileName, StringComparison.Ordinal)
                && !string.Equals(Path.GetFileName(path), StateFileName, StringComparison.Ordinal)
                && !IsStateTemporary(Path.GetFileName(path))))
        {
            throw new InvalidDataException($"MutationPlan transaction root must contain only canonical {SealFileName}/{StateFileName}: '{transactionRoot}'.");
        }
        foreach (var file in topFiles)
            EnsureRegularFile(file, "MutationPlan transaction metadata");
        var sealPath = Path.Combine(transactionRoot, SealFileName);
        EnsureRegularFile(sealPath, $"MutationPlan {rootKind} root seal");
        var seal = journal.RootSeals.Single(item => item.Root == rootKind);
        EnsureFileHash(sealPath, seal.SealSha256, $"MutationPlan {rootKind} root seal");
        var expectedSeal = BuildRootSealBytes(
            journal,
            rootKind,
            seal.TransactionPath,
            seal.Nonce,
            seal.RootDigestSha256);
        if (!File.ReadAllBytes(sealPath).AsSpan().SequenceEqual(expectedSeal))
            throw new InvalidDataException($"MutationPlan {rootKind} root seal is not canonical.");
        var childDirectories = Directory.EnumerateDirectories(transactionRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (childDirectories.Length != 2
            || childDirectories.Any(path => Path.GetFileName(path) is not ("staged" or "backup")))
            throw new InvalidDataException($"MutationPlan transaction root contains an unknown directory: '{transactionRoot}'.");

        var stagedDirectory = Path.Combine(transactionRoot, "staged");
        var backupDirectory = Path.Combine(transactionRoot, "backup");
        if (!Directory.Exists(stagedDirectory) || !Directory.Exists(backupDirectory))
            throw new InvalidDataException($"MutationPlan transaction staging is incomplete: '{transactionRoot}'.");
        var items = journal.Items.Where(item => item.Root == rootKind).ToDictionary(static item => item.Index);
        var stagedIndexes = ValidateTransactionFiles(
            stagedDirectory,
            items,
            isBackup: false,
            allowAtomicTemporary: string.Equals(journal.Phase, JournalPhase.Prepared, StringComparison.Ordinal));
        var backupIndexes = ValidateTransactionFiles(backupDirectory, items, isBackup: true, allowAtomicTemporary: false);

        if (string.Equals(journal.Phase, JournalPhase.Prepared, StringComparison.Ordinal) && backupIndexes.Count != 0)
            throw new InvalidDataException("Prepared recovery journal unexpectedly has backup files.");
        if (string.Equals(journal.Phase, JournalPhase.Staged, StringComparison.Ordinal))
        {
            foreach (var item in items.Values.Where(static item => item.Kind == FileMutationKind.WriteFile))
            {
                if (!stagedIndexes.Contains(item.Index))
                    throw new InvalidDataException($"Staged recovery journal is missing write {item.Index}.");
            }
            if (backupIndexes.Count != 0)
                throw new InvalidDataException("Staged recovery journal unexpectedly has backup files.");
        }
        if (string.Equals(journal.Phase, JournalPhase.Applying, StringComparison.Ordinal))
        {
            foreach (var item in items.Values)
            {
                if (item.State == JournalItemState.Pending
                    && item.Kind == FileMutationKind.WriteFile
                    && !stagedIndexes.Contains(item.Index))
                {
                    throw new InvalidDataException($"Pending recovery write {item.Index} has no staged content.");
                }
                if (item.State == JournalItemState.Pending && backupIndexes.Contains(item.Index))
                    throw new InvalidDataException($"Pending recovery item {item.Index} unexpectedly has a backup.");
                if (item.State == JournalItemState.Applied
                    && item.Kind == FileMutationKind.WriteFile
                    && stagedIndexes.Contains(item.Index))
                {
                    throw new InvalidDataException($"Applied recovery write {item.Index} still has staged content.");
                }
            }
        }
        if (string.Equals(journal.Phase, JournalPhase.Committed, StringComparison.Ordinal))
        {
            foreach (var item in items.Values)
            {
                if (item.Kind == FileMutationKind.WriteFile && stagedIndexes.Contains(item.Index))
                    throw new InvalidDataException($"Committed recovery write {item.Index} still has staged content.");
                if (item.HadOriginal && !backupIndexes.Contains(item.Index))
                    throw new InvalidDataException($"Committed recovery item {item.Index} has no original backup.");
            }
        }
        if (string.Equals(journal.Phase, JournalPhase.RolledBack, StringComparison.Ordinal)
            && backupIndexes.Count != 0)
        {
            throw new InvalidDataException("Rolled-back recovery journal still contains an original backup.");
        }
    }

    private static void ValidateCleanupTransactionTree(
        PlanJournal journal,
        PlanRootKind rootKind,
        string cleanupRoot)
    {
        EnsurePlainDirectory(cleanupRoot, $"MutationPlan {rootKind} cleanup tombstone");
        var childDirectories = Directory.EnumerateDirectories(cleanupRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (childDirectories.Any(path => Path.GetFileName(path) is not ("staged" or "backup")))
            throw new InvalidDataException($"MutationPlan {rootKind} cleanup tombstone contains an unknown directory.");
        var topFiles = Directory.EnumerateFiles(cleanupRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (topFiles.Any(path =>
            !string.Equals(Path.GetFileName(path), SealFileName, StringComparison.Ordinal)
            && !string.Equals(Path.GetFileName(path), StateFileName, StringComparison.Ordinal)
            && !IsStateTemporary(Path.GetFileName(path))))
        {
            throw new InvalidDataException($"MutationPlan {rootKind} cleanup tombstone contains an unknown file.");
        }
        foreach (var file in topFiles)
            EnsureRegularFile(file, "MutationPlan cleanup metadata");
        var sealPath = Path.Combine(cleanupRoot, SealFileName);
        if (!File.Exists(sealPath))
        {
            if (Directory.EnumerateFileSystemEntries(cleanupRoot).Any())
                throw new InvalidDataException($"MutationPlan {rootKind} cleanup tombstone lost its seal before becoming empty.");
            return;
        }
        var seal = journal.RootSeals.Single(item => item.Root == rootKind);
        EnsureFileHash(sealPath, seal.SealSha256, $"MutationPlan {rootKind} cleanup seal");
        var expectedSeal = BuildRootSealBytes(
            journal,
            rootKind,
            seal.TransactionPath,
            seal.Nonce,
            seal.RootDigestSha256);
        if (!File.ReadAllBytes(sealPath).AsSpan().SequenceEqual(expectedSeal))
            throw new InvalidDataException($"MutationPlan {rootKind} cleanup seal is not canonical.");
        var statePath = Path.Combine(cleanupRoot, StateFileName);
        if (File.Exists(statePath)
            && !File.ReadAllBytes(statePath).AsSpan().SequenceEqual(
                BuildRootProgressBytes(journal, rootKind, JournalPhase.Cleanup)))
        {
            throw new InvalidDataException($"MutationPlan {rootKind} cleanup progress is not canonical.");
        }
        var items = journal.Items
            .Where(item => item.Root == rootKind)
            .ToDictionary(static item => item.Index);
        foreach (var directory in childDirectories)
        {
            EnsurePlainDirectory(directory, "MutationPlan cleanup transaction directory");
            if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any())
                throw new InvalidDataException("MutationPlan cleanup transaction contains an unknown subdirectory.");
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                EnsureRegularFile(file, "MutationPlan cleanup transaction file");
                var name = Path.GetFileName(file);
                var isStaged = string.Equals(Path.GetFileName(directory), "staged", StringComparison.Ordinal);
                if (isStaged && TryParseAtomicStageTemporary(name, items, out _))
                    continue;
                if (!int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                    || !string.Equals(name, index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    || !items.TryGetValue(index, out var item))
                {
                    throw new InvalidDataException($"MutationPlan cleanup transaction contains an unknown file '{name}'.");
                }
                if (isStaged)
                {
                    if (item.Kind != FileMutationKind.WriteFile)
                        throw new InvalidDataException($"MutationPlan cleanup has staged content for delete item {index}.");
                    EnsureFileHash(file, item.NewSha256!, $"Cleanup staged item {index}");
                }
                else
                {
                    if (!item.HadOriginal)
                        throw new InvalidDataException($"MutationPlan cleanup has an unexpected backup for item {index}.");
                    EnsureFileHash(file, item.OriginalSha256!, $"Cleanup backup item {index}");
                }
            }
        }
    }

    private static bool IsStateTemporary(string fileName)
    {
        const string prefix = ".state.json.";
        const string suffix = ".tmp";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        return Guid.TryParseExact(fileName[prefix.Length..^suffix.Length], "N", out _);
    }

    private static bool HasExactStateTemporary(string transactionRoot) =>
        Directory.EnumerateFiles(transactionRoot, "*", SearchOption.TopDirectoryOnly)
            .Any(path => IsStateTemporary(Path.GetFileName(path)));

    private static HashSet<int> ValidateTransactionFiles(
        string directory,
        IReadOnlyDictionary<int, PlanJournalItem> items,
        bool isBackup,
        bool allowAtomicTemporary)
    {
        var indexes = new HashSet<int>();
        var temporaryIndexes = new HashSet<int>();
        if (File.Exists(directory))
            throw new InvalidDataException($"MutationPlan transaction directory became a file: '{directory}'.");
        if (!Directory.Exists(directory))
            return indexes;
        EnsurePlainDirectory(directory, isBackup ? "MutationPlan backup" : "MutationPlan staged");
        if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidDataException($"MutationPlan transaction directory contains an unknown subdirectory: '{directory}'.");
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            EnsureRegularFile(file, "MutationPlan transaction file");
            var fileName = Path.GetFileName(file);
            if (!isBackup
                && allowAtomicTemporary
                && TryParseAtomicStageTemporary(fileName, items, out var temporaryIndex))
            {
                if (!temporaryIndexes.Add(temporaryIndex))
                    throw new InvalidDataException($"MutationPlan staged directory has duplicate atomic temporary files for item {temporaryIndex}.");
                continue;
            }
            if (!int.TryParse(fileName, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                || !string.Equals(fileName, index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                || !items.TryGetValue(index, out var item)
                || !indexes.Add(index))
            {
                throw new InvalidDataException($"MutationPlan transaction directory contains an unknown file: '{file}'.");
            }
            if (isBackup)
            {
                if (!item.HadOriginal)
                    throw new InvalidDataException($"MutationPlan has an unexpected backup for new item {index}.");
                EnsureFileHash(file, item.OriginalSha256!, $"Backup item {index}");
            }
            else
            {
                if (item.Kind != FileMutationKind.WriteFile)
                    throw new InvalidDataException($"MutationPlan has unexpected staged content for delete item {index}.");
                EnsureFileHash(file, item.NewSha256!, $"Staged item {index}");
            }
        }
        if (temporaryIndexes.Overlaps(indexes))
            throw new InvalidDataException("MutationPlan staged directory contains both canonical and atomic temporary content for one item.");
        return indexes;
    }

    private static void ValidateCommittedFiles(PlanJournal journal)
    {
        foreach (var item in journal.Items)
        {
            var destination = Resolve(journal, item.Root, item.RelativePath);
            if (item.Kind == FileMutationKind.WriteFile)
                EnsureFileHash(destination, item.NewSha256!, $"Committed {item.Root}:{item.RelativePath}");
            else if (File.Exists(destination) || Directory.Exists(destination))
                throw new InvalidDataException($"Committed delete destination exists: '{item.Root}:{item.RelativePath}'.");
        }
        foreach (var item in journal.CreatedDirectories)
        {
            var destination = Resolve(journal, item.Root, item.RelativePath);
            EnsurePlainDirectory(destination, $"Committed {item.Root}:{item.RelativePath}");
        }
        foreach (var item in journal.RootParentDirectories)
            EnsurePlainDirectory(item.AbsolutePath, $"Committed {item.Root} declared-root parent");
    }

    private static void ValidateRolledBackFiles(PlanJournal journal)
    {
        foreach (var item in journal.Items)
        {
            var destination = Resolve(journal, item.Root, item.RelativePath);
            if (item.HadOriginal)
            {
                EnsureFileHash(destination, item.OriginalSha256!, $"Rolled back {item.Root}:{item.RelativePath}");
                RestoreAttributes(destination, item.OriginalAttributes);
            }
            else if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new InvalidDataException($"Rolled-back new destination exists: '{item.Root}:{item.RelativePath}'.");
            }
        }
    }

    private static string ObservationKey(PlanRootKind root, string relativePath) =>
        $"{(byte)root}:{PathFacts.Normalize(relativePath)}";

    private static int PathDepth(string path) => Path.GetFullPath(path).Count(static character =>
        character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar);

    private static bool IsProjectConfigMutation(FileMutation mutation) =>
        mutation.Kind == FileMutationKind.WriteFile
        && IsProjectConfigPath(mutation.Root, mutation.RelativePath);

    private static bool IsProjectConfigPath(PlanRootKind root, string relativePath) =>
        root == PlanRootKind.Project
        && string.Equals(
            PathFacts.Normalize(relativePath),
            ExcelDbProject.RelativeFilePath,
            StringComparison.Ordinal);

    private static bool IsProjectConfigItem(PlanJournalItem item) =>
        item.Root == PlanRootKind.Project
        && item.Kind == FileMutationKind.WriteFile
        && string.Equals(
            PathFacts.Normalize(item.RelativePath),
            ExcelDbProject.RelativeFilePath,
            StringComparison.Ordinal);

    private static ImmutableArray<PlanRootKind> TouchedRoots(PlanJournal journal) =>
        journal.Items.Select(static item => item.Root)
            .Concat(journal.CreatedDirectories.Select(static item => item.Root))
            .Concat(journal.RootParentDirectories.Select(static item => item.Root))
            .Distinct()
            .OrderBy(static root => root)
            .ToImmutableArray();

    private static string ComputeRootDigest(PlanJournal journal, PlanRootKind rootKind)
    {
        var document = new RootDigestDocument
        {
            Root = rootKind,
            Items = journal.Items
                .Where(item => item.Root == rootKind)
                .OrderBy(static item => item.Index)
                .Select(static item => new RootDigestItem
                {
                    Index = item.Index,
                    ApplyOrder = item.ApplyOrder,
                    RelativePath = item.RelativePath,
                    Kind = item.Kind,
                    HadOriginal = item.HadOriginal,
                    OriginalSha256 = item.OriginalSha256,
                    OriginalLength = item.OriginalLength,
                    OriginalAttributes = item.OriginalAttributes,
                    NewSha256 = item.NewSha256,
                }).ToList(),
            CreatedDirectories = journal.CreatedDirectories
                .Where(item => item.Root == rootKind)
                .Select(static item => item.RelativePath)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToList(),
            RootParentDirectories = journal.RootParentDirectories
                .Where(item => item.Root == rootKind)
                .Select(static item => item.AbsolutePath)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToList(),
        };
        return ContentFingerprint.FromBytes(JsonSerializer.SerializeToUtf8Bytes(document, JournalJson)).Sha256;
    }

    private static byte[] BuildRootSealBytes(
        PlanJournal journal,
        PlanRootKind rootKind,
        string transactionPath,
        string nonce,
        string rootDigest) =>
        JsonSerializer.SerializeToUtf8Bytes(new RootTransactionSeal
        {
            FormatVersion = RootSealFormatVersion,
            TransactionId = journal.TransactionId,
            Root = rootKind,
            ProjectRoot = PathFacts.CanonicalRoot(journal.ProjectRoot),
            DeclaredRoot = PathFacts.CanonicalRoot(GetRoot(journal, rootKind)),
            TransactionPath = Path.GetFullPath(transactionPath),
            PlanHash = journal.PlanHash,
            ProjectConfigHash = journal.ProjectConfigHash,
            SystemCatalogHash = journal.SystemCatalogHash,
            Nonce = nonce,
            RootDigestSha256 = rootDigest,
        }, JournalJson);

    private static void ValidateRootSealDeclarations(PlanJournal journal)
    {
        var roots = TouchedRoots(journal);
        if (journal.RootSeals.Count != roots.Length)
            throw new InvalidDataException("Recovery journal root seals do not cover every touched root exactly once.");
        var canonicalParents = journal.RootParentDirectories
            .OrderBy(static item => item.Root)
            .ThenBy(static item => PathDepth(item.AbsolutePath))
            .ThenBy(static item => item.AbsolutePath, StringComparer.Ordinal)
            .ToArray();
        if (!journal.RootParentDirectories.SequenceEqual(canonicalParents))
            throw new InvalidDataException("Recovery journal declared-root parent directories are not canonically ordered.");
        var seen = new HashSet<PlanRootKind>();
        foreach (var seal in journal.RootSeals)
        {
            if (!Enum.IsDefined(seal.Root)
                || !roots.Contains(seal.Root)
                || !seen.Add(seal.Root)
                || !IsSha256(seal.Nonce)
                || !IsSha256(seal.RootDigestSha256)
                || !IsSha256(seal.SealSha256))
            {
                throw new InvalidDataException("Recovery journal contains invalid or duplicate root seal evidence.");
            }
            ValidateFrozenRootPlacement(journal, seal);
            var digest = ComputeRootDigest(journal, seal.Root);
            var sealBytes = BuildRootSealBytes(
                journal,
                seal.Root,
                seal.TransactionPath,
                seal.Nonce,
                digest);
            if (!string.Equals(digest, seal.RootDigestSha256, StringComparison.Ordinal)
                || !string.Equals(
                    ContentFingerprint.FromBytes(sealBytes).Sha256,
                    seal.SealSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Recovery journal {seal.Root} seal digest does not match immutable transaction evidence.");
            }
        }
    }

    private static void ValidateFrozenRootPlacement(PlanJournal journal, JournalRootSeal seal)
    {
        if (string.IsNullOrWhiteSpace(seal.TransactionPath)
            || !Path.IsPathFullyQualified(seal.TransactionPath))
        {
            throw new InvalidDataException($"MutationPlan {seal.Root} has no absolute frozen transaction path.");
        }
        var transactionPath = Path.GetFullPath(seal.TransactionPath);
        if (!FileSystemComparer.Equals(transactionPath, seal.TransactionPath)
            || !string.Equals(
                Path.GetFileName(transactionPath),
                TransactionDirectoryName(journal.TransactionId, seal.Root),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"MutationPlan {seal.Root} frozen transaction path is not canonical or transaction-bound.");
        }

        var declaredRoot = PathFacts.CanonicalRoot(GetRoot(journal, seal.Root));
        EnsureSameVolume(declaredRoot, transactionPath, seal.Root);
        var anchor = Path.GetDirectoryName(transactionPath)
            ?? throw new InvalidDataException($"MutationPlan {seal.Root} frozen transaction path has no parent.");
        EnsurePlainDirectory(anchor, $"MutationPlan {seal.Root} frozen transaction anchor");
        var directParent = Path.GetDirectoryName(declaredRoot)
            ?? throw new InvalidDataException($"MutationPlan {seal.Root} declared root has no parent.");
        if (!PathFacts.IsContained(anchor, directParent))
            throw new InvalidDataException($"MutationPlan {seal.Root} frozen transaction path is outside the declared-root ancestor chain.");

        var expected = new List<string>();
        var current = directParent;
        while (!FileSystemComparer.Equals(current, anchor))
        {
            expected.Add(Path.GetFullPath(current));
            var parent = Path.GetDirectoryName(current);
            if (parent is null || FileSystemComparer.Equals(parent, current))
                throw new InvalidDataException($"MutationPlan {seal.Root} frozen parent chain does not reach its transaction anchor.");
            current = parent;
        }
        expected.Reverse();

        var actual = journal.RootParentDirectories
            .Where(item => item.Root == seal.Root)
            .OrderBy(static item => PathDepth(item.AbsolutePath))
            .ThenBy(static item => item.AbsolutePath, StringComparer.Ordinal)
            .ToArray();
        if (actual.Length != expected.Count)
            throw new InvalidDataException($"MutationPlan {seal.Root} frozen parent-directory chain is incomplete.");
        for (var index = 0; index < actual.Length; index++)
        {
            if (actual[index].State is not (JournalDirectoryState.Pending
                or JournalDirectoryState.Creating
                or JournalDirectoryState.Created)
                || !Path.IsPathFullyQualified(actual[index].AbsolutePath)
                || !FileSystemComparer.Equals(
                    Path.GetFullPath(actual[index].AbsolutePath),
                    actual[index].AbsolutePath)
                || !FileSystemComparer.Equals(Path.GetFullPath(actual[index].AbsolutePath), expected[index]))
            {
                throw new InvalidDataException($"MutationPlan {seal.Root} frozen parent-directory chain is invalid.");
            }
            if (Directory.Exists(actual[index].AbsolutePath))
                EnsurePlainDirectory(actual[index].AbsolutePath, $"MutationPlan {seal.Root} frozen parent directory");
            else if (File.Exists(actual[index].AbsolutePath))
                throw new InvalidDataException($"MutationPlan {seal.Root} frozen parent directory became a file.");
        }
    }

    private static void PersistProgress(
        PlanJournal journal,
        string journalPath,
        PlanRootKind rootKind)
    {
        WriteRootProgress(journal, rootKind, journal.Phase);
        WriteJournal(journalPath, journal);
    }

    private static void SetRootPhases(PlanJournal journal, string phase)
    {
        foreach (var rootKind in TouchedRoots(journal))
            WriteRootProgress(journal, rootKind, phase);
    }

    private static void SetExistingRootPhases(PlanJournal journal, string phase)
    {
        foreach (var rootKind in TouchedRoots(journal))
        {
            var transactionRoot = GetRootTransactionPath(journal, rootKind);
            if (Directory.Exists(transactionRoot)
                && File.Exists(Path.Combine(transactionRoot, StateFileName)))
                WriteRootProgress(journal, rootKind, phase);
        }
    }

    private static void WriteRootProgress(
        PlanJournal journal,
        PlanRootKind rootKind,
        string phase)
    {
        var transactionRoot = GetRootTransactionPath(journal, rootKind);
        AtomicFile.WriteAllBytes(
            Path.Combine(transactionRoot, StateFileName),
            BuildRootProgressBytes(journal, rootKind, phase));
    }

    private static byte[] BuildRootProgressBytes(
        PlanJournal journal,
        PlanRootKind rootKind,
        string phase)
    {
        var seal = journal.RootSeals.Single(item => item.Root == rootKind);
        return JsonSerializer.SerializeToUtf8Bytes(new RootTransactionProgress
        {
            FormatVersion = RootSealFormatVersion,
            TransactionId = journal.TransactionId,
            Root = rootKind,
            SealSha256 = seal.SealSha256,
            Phase = phase,
            Items = journal.Items
                .Where(item => item.Root == rootKind)
                .OrderBy(static item => item.Index)
                .Select(static item => new RootProgressItem { Index = item.Index, State = item.State })
                .ToList(),
            CreatedDirectories = journal.CreatedDirectories
                .Where(item => item.Root == rootKind)
                .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
                .Select(static item => new RootProgressDirectory
                {
                    RelativePath = item.RelativePath,
                    State = item.State,
                }).ToList(),
            RootParentDirectories = journal.RootParentDirectories
                .Where(item => item.Root == rootKind)
                .OrderBy(static item => item.AbsolutePath, StringComparer.Ordinal)
                .Select(static item => new RootProgressAbsoluteDirectory
                {
                    AbsolutePath = item.AbsolutePath,
                    State = item.State,
                }).ToList(),
        }, JournalJson);
    }

    private static void LoadAuthoritativeRootProgress(PlanJournal journal, string declaredJournalPhase)
    {
        var phases = new List<string>();
        var missingRootCount = 0;
        var cleanupRootCount = 0;
        foreach (var rootKind in TouchedRoots(journal))
        {
            var liveRoot = GetRootTransactionPath(journal, rootKind);
            var cleanupRoot = GetRootCleanupTransactionPath(journal, rootKind);
            var bootstrapCapability = GetValidatedBootstrapCapabilityPath(journal, rootKind);
            EnsureTransactionLocationsAreDirectories(rootKind, liveRoot, cleanupRoot);
            if (Directory.Exists(liveRoot) && Directory.Exists(cleanupRoot))
                throw new InvalidDataException($"MutationPlan {rootKind} has both live and cleanup progress roots.");
            if (!Directory.Exists(liveRoot) && !Directory.Exists(cleanupRoot))
            {
                if (bootstrapCapability is not null)
                {
                    phases.Add(JournalPhase.Prepared);
                    continue;
                }
                missingRootCount++;
                continue;
            }
            var isCleanupRoot = Directory.Exists(cleanupRoot);
            var transactionRoot = isCleanupRoot ? cleanupRoot : liveRoot;
            var path = Path.Combine(transactionRoot, StateFileName);
            if (!File.Exists(path))
            {
                if (isCleanupRoot)
                {
                    var sealExists = File.Exists(Path.Combine(transactionRoot, SealFileName));
                    if (!sealExists)
                    {
                        if (Directory.EnumerateFileSystemEntries(transactionRoot).Any())
                            throw new InvalidDataException($"MutationPlan {rootKind} cleanup tombstone lost its progress and seal evidence.");
                        missingRootCount++;
                        continue;
                    }
                    cleanupRootCount++;
                    phases.Add(JournalPhase.Cleanup);
                    continue;
                }
                if (bootstrapCapability is not null)
                {
                    phases.Add(JournalPhase.Prepared);
                    continue;
                }
                throw new InvalidDataException($"MutationPlan {rootKind} root progress is missing.");
            }
            EnsureRegularFile(path, $"MutationPlan {rootKind} root progress");
            var bytes = File.ReadAllBytes(path);
            var progress = JsonSerializer.Deserialize<RootTransactionProgress>(bytes, JournalJson)
                ?? throw new InvalidDataException($"MutationPlan {rootKind} root progress is empty.");
            if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(progress, JournalJson)))
                throw new InvalidDataException($"MutationPlan {rootKind} root progress is not canonical JSON.");
            var seal = journal.RootSeals.Single(item => item.Root == rootKind);
            if (progress.FormatVersion != RootSealFormatVersion
                || progress.TransactionId != journal.TransactionId
                || progress.Root != rootKind
                || !string.Equals(progress.SealSha256, seal.SealSha256, StringComparison.Ordinal)
                || progress.Phase is not (JournalPhase.Prepared
                    or JournalPhase.Staged
                    or JournalPhase.Applying
                    or JournalPhase.Committed
                    or JournalPhase.RolledBack
                    or JournalPhase.Cleanup))
            {
                throw new InvalidDataException($"MutationPlan {rootKind} root progress does not match its seal.");
            }
            if (isCleanupRoot && progress.Phase != JournalPhase.Cleanup)
                throw new InvalidDataException($"MutationPlan {rootKind} cleanup tombstone has non-cleanup progress.");
            if (isCleanupRoot)
            {
                if (!File.Exists(Path.Combine(cleanupRoot, SealFileName)))
                    throw new InvalidDataException($"MutationPlan {rootKind} cleanup progress has no immutable seal.");
                cleanupRootCount++;
            }
            var expectedItems = journal.Items.Where(item => item.Root == rootKind).OrderBy(static item => item.Index).ToArray();
            if (progress.Items.Count != expectedItems.Length)
                throw new InvalidDataException($"MutationPlan {rootKind} root progress item set is incomplete.");
            for (var index = 0; index < expectedItems.Length; index++)
            {
                var state = progress.Items[index];
                if (state.Index != expectedItems[index].Index
                    || state.State is not (JournalItemState.Pending or JournalItemState.Applying or JournalItemState.Applied))
                {
                    throw new InvalidDataException($"MutationPlan {rootKind} root progress item is invalid.");
                }
                expectedItems[index].State = state.State;
            }
            var expectedDirectories = journal.CreatedDirectories
                .Where(item => item.Root == rootKind)
                .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
                .ToArray();
            if (progress.CreatedDirectories.Count != expectedDirectories.Length)
                throw new InvalidDataException($"MutationPlan {rootKind} root progress directory set is incomplete.");
            for (var index = 0; index < expectedDirectories.Length; index++)
            {
                var state = progress.CreatedDirectories[index];
                if (!string.Equals(state.RelativePath, expectedDirectories[index].RelativePath, StringComparison.Ordinal)
                    || state.State is not (JournalDirectoryState.Pending
                        or JournalDirectoryState.Creating
                        or JournalDirectoryState.Created))
                {
                    throw new InvalidDataException($"MutationPlan {rootKind} root progress directory is invalid.");
                }
                expectedDirectories[index].State = state.State;
            }
            var expectedParentDirectories = journal.RootParentDirectories
                .Where(item => item.Root == rootKind)
                .OrderBy(static item => item.AbsolutePath, StringComparer.Ordinal)
                .ToArray();
            if (progress.RootParentDirectories.Count != expectedParentDirectories.Length)
                throw new InvalidDataException($"MutationPlan {rootKind} root progress parent-directory set is incomplete.");
            for (var index = 0; index < expectedParentDirectories.Length; index++)
            {
                var state = progress.RootParentDirectories[index];
                if (!FileSystemComparer.Equals(state.AbsolutePath, expectedParentDirectories[index].AbsolutePath)
                    || state.State is not (JournalDirectoryState.Pending
                        or JournalDirectoryState.Creating
                        or JournalDirectoryState.Created))
                {
                    throw new InvalidDataException($"MutationPlan {rootKind} root progress parent directory is invalid.");
                }
                expectedParentDirectories[index].State = state.State;
            }
            phases.Add(progress.Phase);
        }

        journal.Phase = EffectiveProgressPhase(
            phases,
            missingRootCount,
            cleanupRootCount,
            declaredJournalPhase);
    }

    private static void EnsureTransactionLocationsAreDirectories(
        PlanRootKind rootKind,
        string liveRoot,
        string cleanupRoot)
    {
        EnsureTransactionLocationIsDirectoryOrMissing(rootKind, liveRoot, "live");
        EnsureTransactionLocationIsDirectoryOrMissing(rootKind, cleanupRoot, "cleanup");
    }

    private static void EnsureTransactionLocationIsDirectoryOrMissing(
        PlanRootKind rootKind,
        string path,
        string kind)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException($"MutationPlan {rootKind} {kind} transaction path has no parent.");
        if (!Directory.Exists(parent))
            return;
        var entry = FindExactChild(parent, Path.GetFileName(path));
        if (entry is null)
            return;
        var attributes = File.GetAttributes(entry);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"MutationPlan {rootKind} {kind} transaction path is not a plain directory: '{path}'.");
        }
    }

    private static string EffectiveProgressPhase(
        IReadOnlyCollection<string> phases,
        int missingRootCount,
        int cleanupRootCount,
        string declaredJournalPhase)
    {
        if (phases.Count == 0)
        {
            if (declaredJournalPhase == JournalPhase.Cleanup)
                return JournalPhase.Cleanup;
            if (declaredJournalPhase is JournalPhase.Prepared or JournalPhase.Staged)
                return declaredJournalPhase;
            throw new InvalidDataException(
                $"MutationPlan lost every touched-root transaction while its journal phase was '{declaredJournalPhase}'.");
        }
        if (missingRootCount > 0)
        {
            if (phases.All(static phase => phase == JournalPhase.Cleanup))
                return JournalPhase.Cleanup;
            if (phases.Contains(JournalPhase.Cleanup, StringComparer.Ordinal))
                return EffectiveCleanupTransitionPhase(phases);
            if (cleanupRootCount == 0
                && declaredJournalPhase == JournalPhase.Prepared
                && phases.All(static phase => phase == JournalPhase.Prepared))
                return JournalPhase.Prepared;
            throw new InvalidDataException("MutationPlan has a missing touched-root transaction after bootstrap completed.");
        }
        if (phases.All(static phase => phase == JournalPhase.Cleanup))
            return JournalPhase.Cleanup;
        if (phases.Any(static phase => phase == JournalPhase.Cleanup))
            return EffectiveCleanupTransitionPhase(phases);
        if (phases.All(static phase => phase == JournalPhase.Committed))
            return JournalPhase.Committed;
        if (phases.All(static phase => phase == JournalPhase.RolledBack))
            return JournalPhase.RolledBack;
        if (phases.All(static phase => phase is JournalPhase.Prepared or JournalPhase.Staged))
            return phases.All(static phase => phase == JournalPhase.Staged)
                ? JournalPhase.Staged
                : JournalPhase.Prepared;
        if (phases.Contains(JournalPhase.Committed, StringComparer.Ordinal)
            && phases.Contains(JournalPhase.RolledBack, StringComparer.Ordinal))
        {
            throw new InvalidDataException("MutationPlan roots disagree between committed and rolled-back terminal states.");
        }
        if (phases.Contains(JournalPhase.Prepared, StringComparer.Ordinal)
            || !phases.Contains(JournalPhase.Applying, StringComparer.Ordinal))
        {
            throw new InvalidDataException("MutationPlan roots have an impossible phase transition.");
        }
        return JournalPhase.Applying;
    }

    private static string EffectiveCleanupTransitionPhase(IEnumerable<string> phases)
    {
        var remaining = phases.Where(static phase => phase != JournalPhase.Cleanup).ToArray();
        if (remaining.Length == 0
            || remaining.All(static phase => phase is JournalPhase.Prepared or JournalPhase.Staged)
            || remaining.All(static phase => phase == JournalPhase.Committed)
            || remaining.All(static phase => phase == JournalPhase.RolledBack))
        {
            return JournalPhase.Cleanup;
        }
        throw new InvalidDataException("MutationPlan cleanup progress is mixed with an impossible root phase.");
    }

    private static bool TryParseAtomicStageTemporary(
        string fileName,
        IReadOnlyDictionary<int, PlanJournalItem> items,
        out int index)
    {
        index = -1;
        if (!fileName.StartsWith(".", StringComparison.Ordinal)
            || !fileName.EndsWith(".tmp", StringComparison.Ordinal))
            return false;
        var body = fileName[1..^4];
        var separator = body.LastIndexOf('.');
        if (separator <= 0
            || !Guid.TryParseExact(body[(separator + 1)..], "N", out _)
            || !int.TryParse(
                body[..separator],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out index)
            || !string.Equals(body[..separator], index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            || !items.TryGetValue(index, out var item)
            || item.Kind != FileMutationKind.WriteFile)
        {
            index = -1;
            return false;
        }
        return true;
    }

    private static void ValidateJournalDirectory(string journalDirectory, bool requireJournal)
    {
        EnsurePlainDirectory(journalDirectory, "MutationPlan recovery journal");
        if (Directory.EnumerateDirectories(journalDirectory, "*", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidDataException("MutationPlan journal directory contains an unknown subdirectory.");
        var files = Directory.EnumerateFiles(journalDirectory, "*", SearchOption.TopDirectoryOnly).ToArray();
        var journalCount = 0;
        foreach (var file in files)
        {
            EnsureRegularFile(file, "MutationPlan journal");
            var name = Path.GetFileName(file);
            if (string.Equals(name, JournalFileName, StringComparison.Ordinal))
                journalCount++;
            else if (IsInitialJournalTemporary(name))
                continue;
            else
                throw new InvalidDataException($"MutationPlan journal directory contains an unknown file '{name}'.");
        }
        if ((requireJournal && journalCount != 1) || journalCount > 1)
            throw new InvalidDataException("MutationPlan journal directory does not have one canonical journal state.");
    }

    private static void CleanupJournalDirectory(string journalDirectory)
    {
        ValidateJournalDirectory(journalDirectory, requireJournal: true);
        foreach (var file in Directory.EnumerateFiles(journalDirectory, "*", SearchOption.TopDirectoryOnly))
            DeleteFileIfPresent(file);
        Directory.Delete(journalDirectory);
    }

    private static bool TryCleanInitialJournalTemporary(
        string journalDirectory,
        Guid transactionId,
        string projectRoot)
    {
        if (Directory.EnumerateDirectories(journalDirectory, "*", SearchOption.TopDirectoryOnly).Any())
            return false;
        var files = Directory.EnumerateFiles(journalDirectory, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (files.Length == 0 || files.Any(file => !IsInitialJournalTemporary(Path.GetFileName(file))))
            return false;
        foreach (var file in files)
            EnsureRegularFile(file, "Initial MutationPlan journal temporary");
        var parent = Path.GetDirectoryName(projectRoot)
            ?? throw new InvalidDataException("Project root has no parent directory.");
        if (Directory.EnumerateFileSystemEntries(parent, $".exceldb-txn-{transactionId:N}-*", SearchOption.TopDirectoryOnly).Any())
            return false;
        foreach (var file in files)
            DeleteFileIfPresent(file);
        Directory.Delete(journalDirectory);
        return true;
    }

    private static bool IsInitialJournalTemporary(string fileName)
    {
        const string prefix = ".journal.json.";
        const string suffix = ".tmp";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }
        var id = fileName[prefix.Length..^suffix.Length];
        return Guid.TryParseExact(id, "N", out _);
    }

    private static void WriteJournal(string path, PlanJournal journal) =>
        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson));

    private static PlanJournal ReadJournal(string path)
    {
        EnsureRegularFile(path, "MutationPlan recovery journal");
        var bytes = File.ReadAllBytes(path);
        var journal = JsonSerializer.Deserialize<PlanJournal>(bytes, JournalJson)
            ?? throw new JsonException("Mutation plan recovery journal is empty.");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson);
        if (!bytes.AsSpan().SequenceEqual(canonical))
            throw new InvalidDataException("MutationPlan recovery journal is not canonical JSON.");
        return journal;
    }

    private static void RejectLegacyTransactions(string root)
    {
        var parent = Path.GetDirectoryName(root);
        if (parent is null || !Directory.Exists(parent))
            return;
        foreach (var directory in Directory.EnumerateDirectories(parent, ".exceldb-txn-*", SearchOption.TopDirectoryOnly))
        {
            EnsurePlainDirectory(directory, "Legacy MutationPlan recovery");
            var journalPath = Path.Combine(directory, JournalFileName);
            if (!File.Exists(journalPath))
                continue;
            LegacyPlanJournal? journal = null;
            try
            {
                journal = JsonSerializer.Deserialize<LegacyPlanJournal>(File.ReadAllBytes(journalPath), JournalJson);
            }
            catch (JsonException)
            {
                // A format 2 staging directory has no root journal and must be handled through .exceldb/recovery.
            }
            if (journal is null || !FileSystemComparer.Equals(Path.GetFullPath(journal.ProjectRoot), root))
                continue;
            throw new InvalidDataException(
                $"Legacy MutationPlan recovery is frozen and will not execute absolute paths. Preserve '{directory}' for manual inspection, then create a new format 2 plan.");
        }
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (!File.Exists(path))
            return;
        EnsureRegularFile(path, "MutationPlan-owned file");
        PathFacts.ClearReadOnly(path);
        File.Delete(path);
    }

    private static void ValidateProjectInfrastructure(string projectRoot)
    {
        if (!Directory.Exists(projectRoot))
            return;
        EnsurePlainDirectory(projectRoot, "ExcelDB project root");
        var internalRoot = FindExactChild(projectRoot, ".exceldb");
        if (internalRoot is null)
            return;
        EnsurePlainDirectory(internalRoot, "ExcelDB internal state");
        var recoveryRoot = FindExactChild(internalRoot, "recovery");
        if (recoveryRoot is not null)
            EnsurePlainDirectory(recoveryRoot, "MutationPlan recovery root");
    }

    private static string? FindExactChild(string parent, string childName)
    {
        var matches = Directory.EnumerateFileSystemEntries(parent, "*", SearchOption.TopDirectoryOnly)
            .Where(path => FileSystemComparer.Equals(Path.GetFileName(path), childName))
            .Take(2)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException($"Directory '{parent}' contains duplicate '{childName}' entries."),
        };
    }

    private static void EnsurePlainDirectory(string path, string purpose)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"{purpose} directory is missing: '{path}'.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{purpose} directory is a symbolic link, junction, or reparse point: '{path}'.");
    }

    private static void EnsureRegularFile(string path, string purpose)
    {
        if (!File.Exists(path) || Directory.Exists(path))
            throw new InvalidDataException($"{purpose} file is missing: '{path}'.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{purpose} file is a symbolic link or reparse point: '{path}'.");
    }

    private static void EnsureFileHash(string path, string expectedSha256, string purpose)
    {
        EnsureRegularFile(path, purpose);
        var actual = ContentFingerprint.FromFile(path).Sha256;
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"{purpose} hash does not match recovery evidence: '{path}'.");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RestoreAttributes(string path, int? attributes)
    {
        if (attributes.HasValue && File.Exists(path))
            File.SetAttributes(path, (FileAttributes)attributes.Value);
    }

    private static bool IsRecoveryPath(string relativePath)
    {
        var normalized = PathFacts.Normalize(relativePath);
        return string.Equals(normalized, RecoveryRelativePath, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(RecoveryRelativePath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static int Depth(string relativePath) => relativePath == "."
        ? 0
        : relativePath.Count(static character => character == '/') + 1;

    private static string Display(PlanRootKind root, string relativePath) => $"{root}:{relativePath}";

    private static ProjectOperationLease AcquireRecoveryLease(
        string projectRoot,
        IEnumerable<string> additionalRoots)
    {
        var extras = additionalRoots.Select(PathFacts.CanonicalRoot).ToArray();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ImmutableArray<string> discovered;
            using (ProjectOperationLease.Acquire(projectRoot))
                discovered = DiscoverRecoveryRootsUnderLease(projectRoot).AddRange(extras);
            discovered = discovered.Distinct(FileSystemComparer).ToImmutableArray();
            var lease = ProjectOperationLease.AcquireMany(discovered);
            var rescanned = DiscoverRecoveryRootsUnderLease(projectRoot).AddRange(extras);
            if (rescanned.All(root => discovered.Contains(root, FileSystemComparer)))
                return lease;
            lease.Dispose();
        }
        throw new ProjectBusyException("MutationPlan recovery roots changed repeatedly while acquiring their ordered leases.");
    }

    private static Diagnostic Collision(FileMutation mutation, string message) =>
        new("plan.path-collision", DiagnosticSeverity.Blocker, Display(mutation.Root, mutation.RelativePath), message);

    private static Diagnostic InvalidMutation(FileMutation mutation, string message) =>
        new("plan.mutation-invalid", DiagnosticSeverity.Blocker, Display(mutation.Root, mutation.RelativePath), message);

    private static OperationReport Failed(MutationPlan plan, Diagnostic diagnostic) =>
        new(plan.Operation, plan.ToolVersion, false, plan.Diagnostics.Add(diagnostic), [], plan.PlanHash);

    private static OperationReport RecoveryFailure(
        string location,
        string message,
        string code = "plan.recovery-failed") =>
        new(
            "plan-recover",
            "durable-v2",
            false,
            [new Diagnostic(code, DiagnosticSeverity.Blocker, location, message)],
            []);

    private static void EnsureCurrentFormat(int formatVersion)
    {
        if (formatVersion == MutationPlan.CurrentFormatVersion)
            return;
        if (formatVersion == 1)
        {
            throw new InvalidDataException(
                "MutationPlan format 1 is frozen and cannot be applied. Create a new format 2 plan.");
        }
        throw new InvalidDataException($"Unsupported MutationPlan format {formatVersion}; expected format 2.");
    }

    private static class JournalPhase
    {
        public const string Prepared = "prepared";
        public const string Staged = "staged";
        public const string Applying = "applying";
        public const string Committed = "committed";
        public const string RolledBack = "rolled-back";
        public const string Cleanup = "cleanup";
    }

    private static class JournalItemState
    {
        public const string Pending = "pending";
        public const string Applying = "applying";
        public const string Applied = "applied";
    }

    private static class JournalDirectoryState
    {
        public const string Pending = "pending";
        public const string Creating = "creating";
        public const string Created = "created";
    }

    private sealed class PlanJournal
    {
        public int FormatVersion { get; set; }
        public Guid TransactionId { get; set; }
        public string PlanHash { get; set; } = string.Empty;
        public string PlanBase64 { get; set; } = string.Empty;
        public string ProjectRoot { get; set; } = string.Empty;
        public string GeneratedCSharpRoot { get; set; } = string.Empty;
        public string ProjectConfigHash { get; set; } = string.Empty;
        public string SystemCatalogHash { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public List<PlanJournalItem> Items { get; set; } = [];
        public List<JournalPath> CreatedDirectories { get; set; } = [];
        public List<JournalAbsolutePath> RootParentDirectories { get; set; } = [];
        public List<JournalRootSeal> RootSeals { get; set; } = [];
    }

    private sealed class PlanJournalItem
    {
        public int Index { get; set; }
        public int ApplyOrder { get; set; }
        public PlanRootKind Root { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public FileMutationKind Kind { get; set; }
        public string? ContentBase64 { get; set; }
        public string? ContentSha256 { get; set; }
        public bool HadOriginal { get; set; }
        public string? OriginalSha256 { get; set; }
        public long? OriginalLength { get; set; }
        public string? NewSha256 { get; set; }
        public int? OriginalAttributes { get; set; }
        public string State { get; set; } = JournalItemState.Pending;
    }

    private sealed class JournalPath
    {
        public PlanRootKind Root { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public string State { get; set; } = JournalDirectoryState.Pending;
    }

    private sealed class JournalRootSeal
    {
        public PlanRootKind Root { get; set; }
        public string TransactionPath { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string RootDigestSha256 { get; set; } = string.Empty;
        public string SealSha256 { get; set; } = string.Empty;
    }

    private sealed class RootTransactionSeal
    {
        public int FormatVersion { get; set; }
        public Guid TransactionId { get; set; }
        public PlanRootKind Root { get; set; }
        public string ProjectRoot { get; set; } = string.Empty;
        public string DeclaredRoot { get; set; } = string.Empty;
        public string TransactionPath { get; set; } = string.Empty;
        public string PlanHash { get; set; } = string.Empty;
        public string ProjectConfigHash { get; set; } = string.Empty;
        public string SystemCatalogHash { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string RootDigestSha256 { get; set; } = string.Empty;
    }

    private sealed class RootDigestDocument
    {
        public PlanRootKind Root { get; set; }
        public List<RootDigestItem> Items { get; set; } = [];
        public List<string> CreatedDirectories { get; set; } = [];
        public List<string> RootParentDirectories { get; set; } = [];
    }

    private sealed class RootDigestItem
    {
        public int Index { get; set; }
        public int ApplyOrder { get; set; }
        public string RelativePath { get; set; } = string.Empty;
        public FileMutationKind Kind { get; set; }
        public bool HadOriginal { get; set; }
        public string? OriginalSha256 { get; set; }
        public long? OriginalLength { get; set; }
        public int? OriginalAttributes { get; set; }
        public string? NewSha256 { get; set; }
    }

    private sealed class RootTransactionProgress
    {
        public int FormatVersion { get; set; }
        public Guid TransactionId { get; set; }
        public PlanRootKind Root { get; set; }
        public string SealSha256 { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public List<RootProgressItem> Items { get; set; } = [];
        public List<RootProgressDirectory> CreatedDirectories { get; set; } = [];
        public List<RootProgressAbsoluteDirectory> RootParentDirectories { get; set; } = [];
    }

    private sealed class RootProgressItem
    {
        public int Index { get; set; }
        public string State { get; set; } = string.Empty;
    }

    private sealed class RootProgressDirectory
    {
        public string RelativePath { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
    }

    private sealed class RootProgressAbsoluteDirectory
    {
        public string AbsolutePath { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
    }

    private sealed class JournalAbsolutePath
    {
        public PlanRootKind Root { get; set; }
        public string AbsolutePath { get; set; } = string.Empty;
        public string State { get; set; } = JournalDirectoryState.Pending;
    }

    private sealed record RootTransactionPlacement(
        string TransactionPath,
        IReadOnlyList<string> MissingParentDirectories);

    private sealed class LegacyPlanJournal
    {
        public string ProjectRoot { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public List<LegacyPlanJournalItem> Items { get; set; } = [];
        public List<string> CreatedDirectories { get; set; } = [];
    }

    private sealed class LegacyPlanJournalItem
    {
        public int Index { get; set; }
        public string Destination { get; set; } = string.Empty;
        public string BackupPath { get; set; } = string.Empty;
        public bool HadOriginal { get; set; }
    }

    private sealed class InputSetChangedException(string message) : IOException(message);
}

internal enum MutationPlanCheckpointKind
{
    Prepared,
    RootBootstrapTemporaryWritten,
    RootBootstrapMarkerWritten,
    RootBootstrapDirectoryCreated,
    RootBootstrapDirectoriesCreated,
    RootSealTemporaryWritten,
    RootSealWritten,
    RootPrepared,
    Staged,
    Applying,
    ItemApplied,
    RollbackCompleted,
    RootCleaned,
}

internal sealed record MutationPlanCheckpoint(
    MutationPlanCheckpointKind Kind,
    PlanRootKind? Root = null,
    int? ItemIndex = null);
