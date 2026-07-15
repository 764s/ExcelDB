using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;

namespace ExcelDb.Tooling.Plans;

/// <summary>Builds the canonical, multi-root plan shape shared by every domain-writing command.</summary>
public sealed class MutationPlanBuilder
{
    private static readonly StringComparer FileSystemComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly string _operation;
    private readonly string _toolVersion;
    private readonly string _projectRoot;
    private readonly string _generatedCSharpRoot;
    private readonly string _projectConfigHash;
    private readonly string _systemCatalogHash;
    private readonly ulong _schemaHash;
    private readonly Dictionary<string, PathObservation> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InputSetObservation> _inputSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileMutation> _mutations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mutationDestinations = new(FileSystemComparer);
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly SortedSet<string> _risks = new(StringComparer.Ordinal);

    /// <summary>
    /// Compatibility constructor for project-only plans. The Generated C# declared root is the project root.
    /// </summary>
    public MutationPlanBuilder(
        string operation,
        string toolVersion,
        string projectRoot,
        string projectConfigHash,
        ulong schemaHash)
        : this(
            operation,
            toolVersion,
            projectRoot,
            projectRoot,
            projectConfigHash,
            systemCatalogHash: string.Empty,
            schemaHash)
    {
    }

    public MutationPlanBuilder(
        string operation,
        string toolVersion,
        string projectRoot,
        string generatedCSharpRoot,
        string projectConfigHash,
        string systemCatalogHash,
        ulong schemaHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedCSharpRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectConfigHash);
        ArgumentNullException.ThrowIfNull(systemCatalogHash);
        _operation = operation;
        _toolVersion = toolVersion;
        _projectRoot = PathFacts.CanonicalRoot(projectRoot);
        _generatedCSharpRoot = PathFacts.CanonicalRoot(generatedCSharpRoot);
        _projectConfigHash = projectConfigHash;
        _systemCatalogHash = systemCatalogHash;
        _schemaHash = schemaHash;

        if (!PathFacts.IsContained(_projectRoot, _generatedCSharpRoot))
            _risks.Add($"External Generated C# root: {_generatedCSharpRoot}");
    }

    public MutationPlanBuilder Observe(string path) => Observe(PlanRootKind.Project, path);

    public MutationPlanBuilder Observe(PlanRootKind root, string path)
    {
        var relative = ToRelative(root, path);
        var key = Key(root, relative);
        _observations.TryAdd(key, PathFacts.Observe(GetRoot(root), relative, root));
        return this;
    }

    /// <summary>Adds an already frozen path observation and proves the disk still matches it.</summary>
    public MutationPlanBuilder Observe(PathObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var relative = ToRelative(observation.Root, observation.RelativePath);
        var canonical = observation with { RelativePath = relative };
        try
        {
            if (!PathFacts.Matches(GetRoot(canonical.Root), canonical))
            {
                _diagnostics.Add(new Diagnostic(
                    "plan.stale",
                    DiagnosticSeverity.Blocker,
                    $"{canonical.Root}:{canonical.RelativePath}",
                    "Observed input changed while the plan was being created; create a fresh plan."));
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            _diagnostics.Add(new Diagnostic(
                "plan.observe-failed",
                DiagnosticSeverity.Blocker,
                $"{canonical.Root}:{canonical.RelativePath}",
                exception.Message));
        }
        var key = Key(canonical.Root, canonical.RelativePath);
        if (_observations.TryGetValue(key, out var existing) && existing != canonical)
            throw new InvalidDataException($"Conflicting observations were captured for '{canonical.Root}:{canonical.RelativePath}'.");
        _observations.TryAdd(key, canonical);
        return this;
    }

    public MutationPlanBuilder ObserveInputSet(
        PlanRootKind root,
        string path,
        InputSetKind kind) =>
        ObserveInputSet(InputSetSnapshot.Capture(GetRoot(root), path, kind, root));

    /// <summary>Adds an already frozen filtered set and proves the current set still matches it.</summary>
    public MutationPlanBuilder ObserveInputSet(InputSetObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var canonical = InputSetSnapshot.Canonicalize(GetRoot(observation.Root), observation);
        try
        {
            if (!InputSetSnapshot.Matches(GetRoot(canonical.Root), canonical))
            {
                _diagnostics.Add(new Diagnostic(
                    "plan.stale-input-set",
                    DiagnosticSeverity.Blocker,
                    $"{canonical.Root}:{canonical.RelativeRoot}",
                    $"The filtered {canonical.Kind} input set changed while the plan was being created; create a fresh plan."));
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException)
        {
            _diagnostics.Add(new Diagnostic(
                "plan.observe-input-set-failed",
                DiagnosticSeverity.Blocker,
                $"{canonical.Root}:{canonical.RelativeRoot}",
                exception.Message));
        }
        var key = InputSetKey(canonical.Root, canonical.Kind, canonical.RelativeRoot);
        if (_inputSets.TryGetValue(key, out var existing)
            && !InputSetSnapshot.Equivalent(existing, canonical))
        {
            throw new InvalidDataException(
                $"Conflicting filtered input sets were captured for '{canonical.Root}:{canonical.RelativeRoot}' ({canonical.Kind}).");
        }
        _inputSets.TryAdd(key, canonical);
        return this;
    }

    public MutationPlanBuilder ObserveSchemaInputSet(string schemaDirectory) =>
        ObserveInputSet(PlanRootKind.Project, schemaDirectory, InputSetKind.SchemaProto);

    public MutationPlanBuilder ObserveSchemaInputSet(InputSetObservation observation)
    {
        if (observation.Kind != InputSetKind.SchemaProto)
            throw new ArgumentException("Expected a SchemaProto input-set observation.", nameof(observation));
        return ObserveInputSet(observation);
    }

    public MutationPlanBuilder ObserveExcelInputSet(string excelDirectory) =>
        ObserveInputSet(PlanRootKind.Project, excelDirectory, InputSetKind.ExcelWorkbook);

    public MutationPlanBuilder ObserveExcelInputSet(InputSetObservation observation)
    {
        if (observation.Kind != InputSetKind.ExcelWorkbook)
            throw new ArgumentException("Expected an ExcelWorkbook input-set observation.", nameof(observation));
        return ObserveInputSet(observation);
    }

    public MutationPlanBuilder ObserveSystemProtoMirrorSet(string schemaDirectory) =>
        ObserveInputSet(PlanRootKind.Project, schemaDirectory, InputSetKind.SystemProtoMirror);

    public MutationPlanBuilder ObserveSystemProtoMirrorSet(InputSetObservation observation)
    {
        if (observation.Kind != InputSetKind.SystemProtoMirror)
            throw new ArgumentException("Expected a SystemProtoMirror input-set observation.", nameof(observation));
        return ObserveInputSet(observation);
    }

    public MutationPlanBuilder CreateDirectory(string path) => CreateDirectory(PlanRootKind.Project, path);

    public MutationPlanBuilder CreateDirectory(PlanRootKind root, string path)
    {
        var relative = ToRelative(root, path);
        Observe(root, relative);
        AddMutation(new FileMutation(FileMutationKind.CreateDirectory, relative, Root: root));
        return this;
    }

    public MutationPlanBuilder WriteFile(string path, ReadOnlySpan<byte> content) =>
        WriteFile(PlanRootKind.Project, path, content);

    public MutationPlanBuilder WriteFile(PlanRootKind root, string path, ReadOnlySpan<byte> content)
    {
        var relative = ToRelative(root, path);
        Observe(root, relative);
        var bytes = content.ToArray();
        AddMutation(new FileMutation(
            FileMutationKind.WriteFile,
            relative,
            Convert.ToBase64String(bytes),
            ContentFingerprint.FromBytes(bytes).Sha256,
            root));
        return this;
    }

    public MutationPlanBuilder DeleteFile(string path) => DeleteFile(PlanRootKind.Project, path);

    public MutationPlanBuilder DeleteFile(PlanRootKind root, string path)
    {
        var relative = ToRelative(root, path);
        Observe(root, relative);
        AddMutation(new FileMutation(FileMutationKind.DeleteFile, relative, Root: root));
        return this;
    }

    public MutationPlanBuilder AddDiagnostic(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        _diagnostics.Add(diagnostic);
        return this;
    }

    public MutationPlanBuilder AddRisk(string risk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(risk);
        _risks.Add(risk);
        return this;
    }

    public MutationPlan Build()
    {
        var plan = new MutationPlan(
            MutationPlan.CurrentFormatVersion,
            _operation,
            _toolVersion,
            _projectRoot,
            _projectConfigHash,
            _schemaHash,
            _observations.Values.ToImmutableArray(),
            _mutations.Values.ToImmutableArray(),
            _diagnostics.ToImmutableArray(),
            _risks.ToImmutableArray(),
            string.Empty,
            _generatedCSharpRoot,
            _systemCatalogHash,
            _inputSets.Values.ToImmutableArray());
        return MutationPlanCodec.Seal(plan);
    }

    private void AddMutation(FileMutation mutation)
    {
        var key = Key(mutation.Root, mutation.RelativePath);
        if (!_mutations.TryAdd(key, mutation))
            throw new InvalidOperationException($"A mutation for '{mutation.Root}:{mutation.RelativePath}' already exists.");

        var destination = PathFacts.ResolveContained(GetRoot(mutation.Root), mutation.RelativePath);
        if (!_mutationDestinations.Add(destination))
        {
            _mutations.Remove(key);
            throw new InvalidOperationException(
                $"Multiple declared-root paths resolve to the same mutation destination: '{destination}'.");
        }
    }

    private string ToRelative(PlanRootKind root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var declaredRoot = GetRoot(root);
        string relative;
        if (Path.IsPathRooted(path))
        {
            relative = Path.GetRelativePath(declaredRoot, Path.GetFullPath(path));
        }
        else
        {
            relative = path;
        }

        relative = PathFacts.Normalize(relative);
        _ = PathFacts.ResolveContained(declaredRoot, relative);
        return relative;
    }

    private string GetRoot(PlanRootKind root) => root switch
    {
        PlanRootKind.Project => _projectRoot,
        PlanRootKind.GeneratedCSharp => _generatedCSharpRoot,
        _ => throw new ArgumentOutOfRangeException(nameof(root), root, "Unknown MutationPlan root kind."),
    };

    private static string Key(PlanRootKind root, string relativePath) => $"{(byte)root}:{relativePath}";

    private static string InputSetKey(PlanRootKind root, InputSetKind kind, string relativeRoot) =>
        $"{(byte)root}:{(byte)kind}:{relativeRoot}";
}
