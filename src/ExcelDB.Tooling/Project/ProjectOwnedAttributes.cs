using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;

namespace ExcelDb.Tooling.Project;

/// <summary>
/// Repairs presentation-only attributes for tool-owned project state. Failures are warnings and never
/// change domain artifacts or project validity.
/// </summary>
public static class ProjectOwnedAttributes
{
    public static ImmutableArray<Diagnostic> Repair(
        ProjectContext context,
        IEnumerable<string>? systemProtoLogicalPaths = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!OperatingSystem.IsWindows())
            return [];

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        SetAttribute(context.InternalDirectory, FileAttributes.Hidden, diagnostics);

        var exceldbMirror = Path.Combine(context.SchemaDirectory, "exceldb");
        var googleMirror = Path.Combine(context.SchemaDirectory, "google");
        var protobufMirror = Path.Combine(googleMirror, "protobuf");
        var schemaSafe = EnsurePlainPath(context.SchemaDirectory, context.SchemaDirectory, diagnostics);
        var exceldbSafe = schemaSafe && EnsurePlainPath(context.SchemaDirectory, exceldbMirror, diagnostics);
        var googleSafe = schemaSafe && EnsurePlainPath(context.SchemaDirectory, googleMirror, diagnostics);
        var protobufSafe = googleSafe && EnsurePlainPath(context.SchemaDirectory, protobufMirror, diagnostics);
        if (exceldbSafe)
            SetAttribute(exceldbMirror, FileAttributes.Hidden, diagnostics);
        if (googleSafe)
            SetAttribute(googleMirror, FileAttributes.Hidden, diagnostics);
        if (protobufSafe)
            SetAttribute(protobufMirror, FileAttributes.Hidden, diagnostics);

        foreach (var logicalPath in systemProtoLogicalPaths ?? [])
        {
            try
            {
                var normalized = logicalPath.Replace('\\', '/');
                if ((normalized.StartsWith("exceldb/", StringComparison.Ordinal) && !exceldbSafe)
                    || (normalized.StartsWith("google/protobuf/", StringComparison.Ordinal) && !protobufSafe))
                {
                    continue;
                }
                var fullPath = ResolveSystemProtoPath(context.SchemaDirectory, logicalPath);
                if (EnsurePlainPath(context.SchemaDirectory, fullPath, diagnostics))
                    SetAttribute(fullPath, FileAttributes.ReadOnly, diagnostics);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or ArgumentException
                                               or InvalidDataException
                                               or NotSupportedException)
            {
                diagnostics.Add(new Diagnostic(
                    "project.attribute-warning",
                    DiagnosticSeverity.Warning,
                    logicalPath,
                    exception.Message));
            }
        }

        return diagnostics.ToImmutable();
    }

    private static bool EnsurePlainPath(
        string root,
        string candidate,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var fullPath = Path.GetFullPath(candidate);
            var relative = Path.GetRelativePath(canonicalRoot, fullPath);
            if (Path.IsPathFullyQualified(relative)
                || relative == ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Tool-owned attribute path '{candidate}' escapes Schema.");
            }

            var current = canonicalRoot;
            if (!EnsurePlainEntry(current, diagnostics))
                return false;
            if (relative == ".")
                return true;

            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current))
                    return true;
                if (!EnsurePlainEntry(current, diagnostics))
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or InvalidDataException
                                           or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic(
                "project.attribute-warning",
                DiagnosticSeverity.Warning,
                candidate,
                $"Could not validate tool-owned attributes safely: {exception.Message}"));
            return false;
        }
    }

    private static bool EnsurePlainEntry(
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            return true;
        diagnostics.Add(new Diagnostic(
            "project.attribute-warning",
            DiagnosticSeverity.Warning,
            path,
            "A symbolic link, junction, or reparse point was found; ExcelDB did not modify attributes through it."));
        return false;
    }

    private static string ResolveSystemProtoPath(string schemaDirectory, string logicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        var normalized = logicalPath.Replace('\\', '/');
        if (!(normalized.StartsWith("exceldb/", StringComparison.Ordinal)
              || normalized.StartsWith("google/protobuf/", StringComparison.Ordinal))
            || Path.IsPathFullyQualified(logicalPath)
            || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(static part => part == ".."))
        {
            throw new InvalidDataException($"'{logicalPath}' is not a system proto logical path.");
        }

        var fullPath = Path.GetFullPath(normalized.Replace('/', Path.DirectorySeparatorChar), schemaDirectory);
        var relative = Path.GetRelativePath(schemaDirectory, fullPath);
        if (Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"System proto path '{logicalPath}' escapes Schema.");
        }
        return fullPath;
    }

    private static void SetAttribute(
        string path,
        FileAttributes attribute,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        try
        {
            var current = File.GetAttributes(path);
            if ((current & FileAttributes.ReparsePoint) != 0)
            {
                diagnostics.Add(new Diagnostic(
                    "project.attribute-warning",
                    DiagnosticSeverity.Warning,
                    path,
                    "A symbolic link, junction, or reparse point was found; ExcelDB did not modify its attributes."));
                return;
            }
            if ((current & attribute) != attribute)
                File.SetAttributes(path, current | attribute);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic(
                "project.attribute-warning",
                DiagnosticSeverity.Warning,
                path,
                $"Could not set {attribute}: {exception.Message}"));
        }
    }
}
