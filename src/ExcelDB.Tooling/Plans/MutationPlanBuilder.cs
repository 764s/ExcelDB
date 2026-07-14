using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;

namespace ExcelDb.Tooling.Plans;

/// <summary>Builds the one canonical plan shape shared by every domain-writing command.</summary>
public sealed class MutationPlanBuilder
{
    private readonly string _operation;
    private readonly string _toolVersion;
    private readonly string _root;
    private readonly string _projectConfigHash;
    private readonly ulong _schemaHash;
    private readonly Dictionary<string, PathObservation> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileMutation> _mutations = new(StringComparer.Ordinal);
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly SortedSet<string> _risks = new(StringComparer.Ordinal);

    public MutationPlanBuilder(
        string operation,
        string toolVersion,
        string projectRoot,
        string projectConfigHash,
        ulong schemaHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectConfigHash);
        _operation = operation;
        _toolVersion = toolVersion;
        _root = Path.GetFullPath(projectRoot);
        _projectConfigHash = projectConfigHash;
        _schemaHash = schemaHash;
    }

    public MutationPlanBuilder Observe(string path)
    {
        var relative = ToRelative(path);
        _observations.TryAdd(relative, PathFacts.Observe(_root, relative));
        return this;
    }

    public MutationPlanBuilder CreateDirectory(string path)
    {
        var relative = ToRelative(path);
        Observe(relative);
        AddMutation(new FileMutation(FileMutationKind.CreateDirectory, relative));
        return this;
    }

    public MutationPlanBuilder WriteFile(string path, ReadOnlySpan<byte> content)
    {
        var relative = ToRelative(path);
        Observe(relative);
        var bytes = content.ToArray();
        AddMutation(new FileMutation(
            FileMutationKind.WriteFile,
            relative,
            Convert.ToBase64String(bytes),
            ContentFingerprint.FromBytes(bytes).Sha256));
        return this;
    }

    public MutationPlanBuilder DeleteFile(string path)
    {
        var relative = ToRelative(path);
        Observe(relative);
        AddMutation(new FileMutation(FileMutationKind.DeleteFile, relative));
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
            _root,
            _projectConfigHash,
            _schemaHash,
            _observations.Values.OrderBy(static item => item.RelativePath, StringComparer.Ordinal).ToImmutableArray(),
            _mutations.Values.OrderBy(static item => item.RelativePath, StringComparer.Ordinal).ThenBy(static item => item.Kind).ToImmutableArray(),
            _diagnostics.OrderBy(static item => item.Code, StringComparer.Ordinal).ThenBy(static item => item.Location, StringComparer.Ordinal).ToImmutableArray(),
            _risks.ToImmutableArray(),
            string.Empty);
        return MutationPlanCodec.Seal(plan);
    }

    private void AddMutation(FileMutation mutation)
    {
        if (!_mutations.TryAdd(mutation.RelativePath, mutation))
            throw new InvalidOperationException($"A mutation for '{mutation.RelativePath}' already exists.");
    }

    private string ToRelative(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathRooted(path))
            return PathFacts.Normalize(path);
        var relative = Path.GetRelativePath(_root, Path.GetFullPath(path));
        _ = PathFacts.ResolveContained(_root, relative);
        return PathFacts.Normalize(relative);
    }
}
