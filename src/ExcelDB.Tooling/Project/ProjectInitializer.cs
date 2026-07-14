using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;
using ExcelDb.Tooling.Plans;

namespace ExcelDb.Tooling.Project;

public sealed class ProjectInitializer
{
    private readonly string _toolVersion;

    public ProjectInitializer(string toolVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        _toolVersion = toolVersion;
    }

    public MutationPlan Plan(string targetDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var root = Path.GetFullPath(targetDirectory);
        var projectFile = Path.Combine(root, ExcelDbProject.FileName);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ExcelDbProject project;
        var isNew = !File.Exists(projectFile);

        if (Directory.Exists(projectFile))
        {
            diagnostics.Add(new Diagnostic("project.collision", DiagnosticSeverity.Blocker, ExcelDbProject.FileName, "The project file path is occupied by a directory."));
            project = ExcelDbProject.Default;
        }
        else if (!isNew)
        {
            try
            {
                project = ExcelDbProject.Load(projectFile);
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                diagnostics.Add(new Diagnostic("project.invalid", DiagnosticSeverity.Blocker, ExcelDbProject.FileName, exception.Message));
                project = ExcelDbProject.Default;
            }
        }
        else
        {
            project = ExcelDbProject.Default;
        }

        var observations = ImmutableArray.CreateBuilder<PathObservation>();
        var mutations = ImmutableArray.CreateBuilder<FileMutation>();
        ObserveOnce(observations, root, ".");
        ObserveOnce(observations, root, ExcelDbProject.FileName);

        if (!Directory.Exists(root))
            mutations.Add(new FileMutation(FileMutationKind.CreateDirectory, "."));

        foreach (var relativeDirectory in EnumerateRequiredDirectories(project))
        {
            ObserveOnce(observations, root, relativeDirectory);
            var fullPath = PathFacts.ResolveContained(root, relativeDirectory);
            if (File.Exists(fullPath))
            {
                diagnostics.Add(new Diagnostic("project.collision", DiagnosticSeverity.Blocker, relativeDirectory, "A required directory path is occupied by a file."));
            }
            else if (!Directory.Exists(fullPath))
            {
                mutations.Add(new FileMutation(FileMutationKind.CreateDirectory, relativeDirectory));
            }
        }

        var configBytes = project.ToCanonicalJson();
        var configHash = ContentFingerprint.FromBytes(configBytes).Sha256;
        if (isNew && !Directory.Exists(projectFile))
        {
            mutations.Add(new FileMutation(
                FileMutationKind.WriteFile,
                ExcelDbProject.FileName,
                Convert.ToBase64String(configBytes),
                configHash));
        }

        var plan = new MutationPlan(
            MutationPlan.CurrentFormatVersion,
            "init",
            _toolVersion,
            root,
            configHash,
            0,
            observations.OrderBy(static item => item.RelativePath, StringComparer.Ordinal).ToImmutableArray(),
            mutations.OrderBy(static item => item.Kind).ThenBy(static item => item.RelativePath, StringComparer.Ordinal).ToImmutableArray(),
            diagnostics.ToImmutable(),
            [],
            string.Empty);
        return MutationPlanCodec.Seal(plan);
    }

    private static IEnumerable<string> EnumerateRequiredDirectories(ExcelDbProject project)
    {
        yield return NormalizeDirectory(project.SchemaDir);
        yield return NormalizeDirectory(project.GeneratedDir);
        foreach (var glob in project.Workbooks)
        {
            var wildcard = glob.IndexOfAny(['*', '?']);
            var prefix = wildcard < 0 ? glob : glob[..wildcard];
            var directory = Path.GetDirectoryName(prefix.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrEmpty(directory))
                directory = ".";
            yield return NormalizeDirectory(directory);
        }
        yield return NormalizeDirectory(Path.GetDirectoryName(project.BytesOutput) ?? ".");
        yield return NormalizeDirectory(project.CacheDir);
    }

    private static string NormalizeDirectory(string path)
    {
        if (Path.IsPathRooted(path))
            throw new InvalidDataException("init default directory creation only supports project-relative paths.");
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return string.IsNullOrEmpty(trimmed) ? "." : PathFacts.Normalize(trimmed);
    }

    private static void ObserveOnce(
        ImmutableArray<PathObservation>.Builder observations,
        string root,
        string relativePath)
    {
        var normalized = PathFacts.Normalize(relativePath);
        if (observations.Any(item => string.Equals(item.RelativePath, normalized, StringComparison.Ordinal)))
            return;
        observations.Add(PathFacts.Observe(root, normalized));
    }
}
