using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;
using ExcelDb.Tooling.Plans;

namespace ExcelDb.Tooling.Project;

public sealed class ProjectInitializer
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly string[] InternalGitIgnoreEntries = ["/cache/", "/plans/", "/reports/", "/recovery/"];
    private static readonly string[] SchemaGitIgnoreEntries = ["/exceldb/", "/google/protobuf/"];

    private readonly string _toolVersion;
    private readonly string _systemCatalogHash;

    public ProjectInitializer(string toolVersion, string systemCatalogHash = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);
        ArgumentNullException.ThrowIfNull(systemCatalogHash);
        _toolVersion = toolVersion;
        _systemCatalogHash = systemCatalogHash;
    }

    public MutationPlan Plan(string targetDirectory) => Plan(targetDirectory, generatedCSharpDirectory: null, extendPlan: null);

    public MutationPlan Plan(string targetDirectory, string? generatedCSharpDirectory) =>
        Plan(targetDirectory, generatedCSharpDirectory, extendPlan: null);

    public MutationPlan Plan(
        string targetDirectory,
        string? generatedCSharpDirectory,
        Action<MutationPlanBuilder, ProjectContext>? extendPlan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var root = Path.GetFullPath(targetDirectory);
        var projectFile = Path.Combine(root, ExcelDbProject.FileName);
        var legacyFile = Path.Combine(root, ExcelDbProject.LegacyFileName);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var configExists = File.Exists(projectFile);
        byte[]? existingConfigBytes = null;
        ExcelDbProject project;

        if (File.Exists(root))
        {
            diagnostics.Add(new Diagnostic(
                "project.collision",
                DiagnosticSeverity.Blocker,
                root,
                "The selected project directory path is occupied by a file."));
        }

        if (File.Exists(legacyFile) || Directory.Exists(legacyFile))
        {
            diagnostics.Add(new Diagnostic(
                "project.legacy-v1",
                DiagnosticSeverity.Blocker,
                ExcelDbProject.LegacyFileName,
                $"Legacy '{ExcelDbProject.LegacyFileName}' is not supported. Reinitialize into an empty directory or arrange Project v2 manually; automatic migration is intentionally unavailable."));
        }

        if (Directory.Exists(projectFile))
        {
            diagnostics.Add(new Diagnostic(
                "project.collision",
                DiagnosticSeverity.Blocker,
                ExcelDbProject.FileName,
                "The Project v2 configuration path is occupied by a directory."));
            project = CreateNewProject(root, generatedCSharpDirectory, diagnostics);
        }
        else if (configExists)
        {
            try
            {
                project = ExcelDbProject.Load(projectFile);
                existingConfigBytes = File.ReadAllBytes(projectFile);
                ValidateRequestedGeneratedCSharpDirectory(root, project, generatedCSharpDirectory, diagnostics);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                diagnostics.Add(new Diagnostic(
                    "project.invalid",
                    DiagnosticSeverity.Blocker,
                    ExcelDbProject.FileName,
                    exception.Message));
                project = ExcelDbProject.Default;
            }
        }
        else
        {
            project = CreateNewProject(root, generatedCSharpDirectory, diagnostics);
        }

        ProjectContext context;
        try
        {
            context = project.Resolve(projectFile);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic(
                "project.invalid",
                DiagnosticSeverity.Blocker,
                ExcelDbProject.FileName,
                exception.Message));
            project = ExcelDbProject.Default;
            context = project.Resolve(projectFile);
        }

        var canonicalConfig = project.ToCanonicalJson();
        var configHash = ContentFingerprint.FromBytes(existingConfigBytes ?? canonicalConfig).Sha256;
        var builder = new MutationPlanBuilder(
            "init",
            _toolVersion,
            root,
            context.GeneratedCSharpDirectory,
            configHash,
            _systemCatalogHash,
            schemaHash: 0);

        TryObserve(builder, PlanRootKind.Project, ".", diagnostics);
        TryObserve(builder, PlanRootKind.Project, ExcelDbProject.FileName, diagnostics);
        TryObserve(builder, PlanRootKind.Project, ExcelDbProject.LegacyFileName, diagnostics);
        if (diagnostics.Any(static diagnostic => diagnostic.IsBlocker))
        {
            foreach (var diagnostic in diagnostics)
                builder.AddDiagnostic(diagnostic);
            return builder.Build();
        }

        EnsureDirectory(builder, PlanRootKind.Project, root, ".", diagnostics);
        EnsureDirectory(builder, PlanRootKind.Project, root, ToProjectRelative(root, context.SchemaDirectory), diagnostics);
        EnsureDirectory(builder, PlanRootKind.Project, root, ToProjectRelative(root, context.ExcelDirectory), diagnostics);
        EnsureDirectory(builder, PlanRootKind.Project, root, ToProjectRelative(root, context.GeneratedBytesDirectory), diagnostics);
        EnsureDirectory(builder, PlanRootKind.GeneratedCSharp, context.GeneratedCSharpDirectory, ".", diagnostics);
        EnsureDirectory(builder, PlanRootKind.Project, root, ".exceldb", diagnostics);
        EnsureDirectory(builder, PlanRootKind.Project, root, ".exceldb/published", diagnostics);
        EnsureDirectory(builder, PlanRootKind.Project, root, ".exceldb/cache", diagnostics);
        EnsureDirectory(builder, PlanRootKind.Project, root, ".exceldb/plans", diagnostics);
        EnsureDirectory(builder, PlanRootKind.Project, root, ".exceldb/reports", diagnostics);
        EnsureDirectory(builder, PlanRootKind.Project, root, ".exceldb/recovery", diagnostics);

        PlanGitIgnore(builder, root, ".exceldb/.gitignore", InternalGitIgnoreEntries, diagnostics);
        PlanGitIgnore(
            builder,
            root,
            CombineRelative(ToProjectRelative(root, context.SchemaDirectory), ".gitignore"),
            SchemaGitIgnoreEntries,
            diagnostics);

        if (!configExists || existingConfigBytes is null || !existingConfigBytes.AsSpan().SequenceEqual(canonicalConfig))
        {
            try
            {
                builder.WriteFile(PlanRootKind.Project, ExcelDbProject.FileName, canonicalConfig);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(PathDiagnostic(ExcelDbProject.FileName, exception));
            }
        }

        if (!diagnostics.Any(static diagnostic => diagnostic.IsBlocker) && extendPlan is not null)
        {
            try
            {
                extendPlan(builder, context);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or ArgumentException)
            {
                diagnostics.Add(new Diagnostic(
                    "project.extension-failed",
                    DiagnosticSeverity.Blocker,
                    ".",
                    exception.Message));
            }
        }

        foreach (var diagnostic in diagnostics)
            builder.AddDiagnostic(diagnostic);

        return builder.Build();
    }

    public ImmutableArray<Diagnostic> RepairOwnedAttributes(
        ProjectContext context,
        IEnumerable<string>? systemProtoLogicalPaths = null) =>
        ProjectOwnedAttributes.Repair(context, systemProtoLogicalPaths);

    private static ExcelDbProject CreateNewProject(
        string root,
        string? generatedCSharpDirectory,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(generatedCSharpDirectory))
            return ExcelDbProject.Default;

        try
        {
            var absolute = Path.GetFullPath(generatedCSharpDirectory, root);
            var value = IsContained(root, absolute)
                ? NormalizeRelative(Path.GetRelativePath(root, absolute))
                : absolute;
            return ExcelDbProject.Default.WithGeneratedCSharpDirectory(value);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic(
                "project.generated-csharp-invalid",
                DiagnosticSeverity.Blocker,
                generatedCSharpDirectory,
                exception.Message));
            return ExcelDbProject.Default;
        }
    }

    private static void ValidateRequestedGeneratedCSharpDirectory(
        string root,
        ExcelDbProject project,
        string? requested,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return;

        var existing = project.Resolve(Path.Combine(root, ExcelDbProject.FileName)).GeneratedCSharpDirectory;
        var requestedFull = Path.GetFullPath(requested, root);
        if (!PathsEqual(existing, requestedFull))
        {
            diagnostics.Add(new Diagnostic(
                "project.configure-required",
                DiagnosticSeverity.Blocker,
                nameof(ExcelDbProject.GeneratedCSharpDir),
                "The project already exists with a different Generated C# directory. Use project settings/configure; init does not move or retarget existing generated files."));
        }
    }

    private static void EnsureDirectory(
        MutationPlanBuilder builder,
        PlanRootKind rootKind,
        string declaredRoot,
        string relativePath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var fullPath = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), declaredRoot);
            builder.Observe(rootKind, relativePath);
            if (File.Exists(fullPath))
            {
                diagnostics.Add(new Diagnostic(
                    "project.collision",
                    DiagnosticSeverity.Blocker,
                    relativePath,
                    "A required directory path is occupied by a file."));
            }
            else if (!Directory.Exists(fullPath))
            {
                builder.CreateDirectory(rootKind, relativePath);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(PathDiagnostic(relativePath, exception));
        }
    }

    private static void PlanGitIgnore(
        MutationPlanBuilder builder,
        string projectRoot,
        string relativePath,
        IReadOnlyCollection<string> requiredEntries,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), projectRoot);
            builder.Observe(PlanRootKind.Project, relativePath);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(PathDiagnostic(relativePath, exception));
            return;
        }
        if (Directory.Exists(fullPath))
        {
            diagnostics.Add(new Diagnostic(
                "project.collision",
                DiagnosticSeverity.Blocker,
                relativePath,
                "The required .gitignore path is occupied by a directory."));
            return;
        }

        string existing;
        try
        {
            existing = File.Exists(fullPath) ? StrictUtf8.GetString(File.ReadAllBytes(fullPath)) : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            diagnostics.Add(new Diagnostic(
                "project.gitignore-invalid",
                DiagnosticSeverity.Blocker,
                relativePath,
                $"The existing .gitignore cannot be safely preserved: {exception.Message}"));
            return;
        }

        var existingEntries = existing.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(static line => line.Trim())
            .ToHashSet(StringComparer.Ordinal);
        var missing = requiredEntries.Where(entry => !existingEntries.Contains(entry)).ToArray();
        if (missing.Length == 0)
            return;

        var updated = existing;
        if (updated.Length > 0 && !updated.EndsWith('\n'))
            updated += "\n";
        updated += string.Join("\n", missing) + "\n";
        try
        {
            builder.WriteFile(PlanRootKind.Project, relativePath, StrictUtf8.GetBytes(updated));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(PathDiagnostic(relativePath, exception));
        }
    }

    private static void TryObserve(
        MutationPlanBuilder builder,
        PlanRootKind root,
        string relativePath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            builder.Observe(root, relativePath);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(PathDiagnostic(relativePath, exception));
        }
    }

    private static Diagnostic PathDiagnostic(string location, Exception exception) => new(
        "project.path-invalid",
        DiagnosticSeverity.Blocker,
        location,
        exception.Message);

    private static string ToProjectRelative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Path '{path}' is not contained by the project root.");
        }
        return NormalizeRelative(relative);
    }

    private static string CombineRelative(string left, string right) =>
        NormalizeRelative(Path.Combine(left.Replace('/', Path.DirectorySeparatorChar), right));

    private static string NormalizeRelative(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool IsContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathFullyQualified(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
