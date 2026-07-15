using System.Collections.Immutable;
using System.Text.Json;

namespace ExcelDb.Tooling.Project;

/// <summary>The only persisted ExcelDB project configuration shape.</summary>
public sealed record ExcelDbProject(
    int FormatVersion,
    string SchemaDir,
    string ExcelDir,
    string GeneratedCSharpDir,
    string GeneratedBytesDir)
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "formatVersion",
        "schemaDir",
        "excelDir",
        "generatedCSharpDir",
        "generatedBytesDir",
    };

    public const int CurrentFormatVersion = 2;
    public const string FileName = ".exceldb/project.json";
    public const string RelativeFilePath = FileName;
    public const string LegacyFileName = "ExcelDb.Project.json";

    public static ExcelDbProject Default { get; } = new(
        CurrentFormatVersion,
        "Schema",
        "Excel",
        "Generated/CSharp",
        "Generated/Bytes");

    public static ExcelDbProject Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (string.Equals(Path.GetFileName(fullPath), LegacyFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Legacy project configuration '{LegacyFileName}' is not supported. Reinitialize the project or arrange a Project v2 configuration manually; automatic migration is intentionally unavailable.");
        }

        var internalDirectory = Path.GetDirectoryName(fullPath);
        if (internalDirectory is not null
            && string.Equals(Path.GetFileName(fullPath), "project.json", StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(internalDirectory), ".exceldb", StringComparison.Ordinal))
        {
            var projectRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(internalDirectory))
                ?? throw new InvalidDataException("The .exceldb directory has no project root parent.");
            ProjectPathSafety.EnsureProjectRootAndInternalDirectoryArePlain(projectRoot);
            if (File.Exists(Path.Combine(projectRoot, LegacyFileName)))
            {
                throw new InvalidDataException(
                    $"Legacy project configuration '{LegacyFileName}' exists beside Project v2. Remove the dual configuration and reinitialize or arrange one Project v2 source explicitly; automatic migration is intentionally unavailable.");
            }
        }
        ProjectPathSafety.EnsurePlainFile(fullPath, "Project v2 configuration");

        return Parse(File.ReadAllBytes(fullPath));
    }

    public static ExcelDbProject Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(".exceldb/project.json must contain one JSON object.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"Duplicate .exceldb/project.json key '{property.Name}'.");
            if (!KnownKeys.Contains(property.Name))
                throw new InvalidDataException($"Unknown .exceldb/project.json key '{property.Name}'.");
        }

        foreach (var key in KnownKeys)
        {
            if (!seen.Contains(key))
                throw new InvalidDataException($"Required project key '{key}' is missing.");
        }

        var formatVersion = ReadRequiredInt32(root, "formatVersion");
        if (formatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Project formatVersion {formatVersion}; expected {CurrentFormatVersion}. Reinitialize the project or arrange Project v2 manually.");
        }

        return Validate(new ExcelDbProject(
            formatVersion,
            ReadRequiredString(root, "schemaDir"),
            ReadRequiredString(root, "excelDir"),
            ReadRequiredString(root, "generatedCSharpDir"),
            ReadRequiredString(root, "generatedBytesDir")));
    }

    public byte[] ToCanonicalJson()
    {
        Validate(this);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("schemaDir", SchemaDir);
            writer.WriteString("excelDir", ExcelDir);
            writer.WriteString("generatedCSharpDir", GeneratedCSharpDir);
            writer.WriteString("generatedBytesDir", GeneratedBytesDir);
            writer.WriteEndObject();
        }

        return [.. stream.ToArray(), (byte)'\n'];
    }

    public ProjectContext Resolve(string projectFilePath) => new(projectFilePath, Validate(this));

    public ExcelDbProject WithGeneratedCSharpDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Validate(this with { GeneratedCSharpDir = path });
    }

    private static ExcelDbProject Validate(ExcelDbProject project)
    {
        if (project.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"Project formatVersion must be {CurrentFormatVersion}.");

        ValidateProjectRelativeDirectory(project.SchemaDir, nameof(SchemaDir));
        ValidateProjectRelativeDirectory(project.ExcelDir, nameof(ExcelDir));
        ValidateGeneratedCSharpDirectory(project.GeneratedCSharpDir);
        ValidateProjectRelativeDirectory(project.GeneratedBytesDir, nameof(GeneratedBytesDir));
        return project;
    }

    private static int ReadRequiredInt32(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new InvalidDataException($"Project key '{name}' must be an integer.");
        return result;
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Project key '{name}' must be a non-empty string.");
        return value.GetString()!;
    }

    private static void ValidateProjectRelativeDirectory(string value, string name)
    {
        ValidateDirectorySyntax(value, name);
        if (Path.IsPathFullyQualified(value))
            throw new InvalidDataException($"Project directory '{name}' must be relative to the project root.");
        if (ContainsParentTraversal(value))
            throw new InvalidDataException($"Project directory '{name}' must not contain '..'.");

        var normalized = Path.TrimEndingDirectorySeparator(value.Replace('/', Path.DirectorySeparatorChar));
        if (normalized is "." or "")
            throw new InvalidDataException($"Project directory '{name}' must name a child directory, not the project root.");
    }

    private static void ValidateGeneratedCSharpDirectory(string value)
    {
        ValidateDirectorySyntax(value, nameof(GeneratedCSharpDir));
        if (Path.IsPathFullyQualified(value))
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            var filesystemRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!);
            if (string.Equals(full, filesystemRoot, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
            {
                throw new InvalidDataException("Project directory 'GeneratedCSharpDir' must not be a filesystem root.");
            }
        }
        if (!Path.IsPathFullyQualified(value) && ContainsParentTraversal(value))
            throw new InvalidDataException("Project directory 'GeneratedCSharpDir' must be absolute when it is outside the project root.");
    }

    private static void ValidateDirectorySyntax(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.IndexOf('\0') >= 0
            || value.IndexOfAny(['*', '?']) >= 0)
        {
            throw new InvalidDataException($"Project directory '{name}' is invalid.");
        }
    }

    private static bool ContainsParentTraversal(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment == "..");
}

public sealed class ProjectContext
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    internal ProjectContext(string projectFilePath, ExcelDbProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFilePath);
        ProjectFilePath = Path.GetFullPath(projectFilePath);
        var internalDirectory = Path.GetDirectoryName(ProjectFilePath)
            ?? throw new InvalidDataException("The project file has no parent directory.");
        if (!string.Equals(Path.GetFileName(ProjectFilePath), "project.json", StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(internalDirectory), ".exceldb", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Project v2 configuration must be located at '<ProjectRoot>/.exceldb/project.json'.");
        }

        InternalDirectory = internalDirectory;
        RootDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(internalDirectory))
            ?? throw new InvalidDataException("The .exceldb directory has no project root parent.");
        Project = project;

        SchemaDirectory = ResolveContainedDirectory(project.SchemaDir, nameof(project.SchemaDir));
        ExcelDirectory = ResolveContainedDirectory(project.ExcelDir, nameof(project.ExcelDir));
        GeneratedBytesDirectory = ResolveContainedDirectory(project.GeneratedBytesDir, nameof(project.GeneratedBytesDir));
        GeneratedCSharpDirectory = ResolveGeneratedCSharpDirectory(project.GeneratedCSharpDir);
        EnsureDisjointArtifactRoots();
        EnsurePhysicalRootsArePlain();
    }

    public string ProjectFilePath { get; }

    public string RootDirectory { get; }

    public string InternalDirectory { get; }

    public ExcelDbProject Project { get; }

    public string SchemaDirectory { get; }

    public string ExcelDirectory { get; }

    public string GeneratedCSharpDirectory { get; }

    public string GeneratedBytesDirectory { get; }

    public string CacheDirectory => Path.Combine(InternalDirectory, "cache");

    public string PlansDirectory => Path.Combine(InternalDirectory, "plans");

    public string ReportsDirectory => Path.Combine(InternalDirectory, "reports");

    public string RecoveryDirectory => Path.Combine(InternalDirectory, "recovery");

    public string PublishedDirectory => Path.Combine(InternalDirectory, "published");

    public string SystemImportsManifestPath => Path.Combine(InternalDirectory, "system-imports.json");

    public bool HasExternalGeneratedCSharpDirectory => !IsContained(RootDirectory, GeneratedCSharpDirectory);

    public string GetBytesDirectory(string targetId) =>
        Path.Combine(GeneratedBytesDirectory, ValidateTargetId(targetId));

    public string GetBytesOutputPath(string targetId) =>
        Path.Combine(GetBytesDirectory(targetId), "config.bytes");

    public string GetBytesManifestPath(string targetId) =>
        Path.Combine(GetBytesDirectory(targetId), "config.bytes.manifest.json");

    public ImmutableArray<string> EnumerateExcelFiles()
    {
        if (!Directory.Exists(ExcelDirectory))
            return [];
        if ((File.GetAttributes(ExcelDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("excelDir must not be a symbolic link, junction, or other reparse point.");

        var files = ImmutableArray.CreateBuilder<string>();
        var pending = new Stack<string>();
        pending.Push(ExcelDirectory);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                var name = Path.GetFileName(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || (attributes & FileAttributes.Hidden) != 0
                    || name.StartsWith(".", StringComparison.Ordinal))
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                if ((attributes & FileAttributes.System) != 0
                    || name.StartsWith("~$", StringComparison.Ordinal)
                    || !string.Equals(Path.GetExtension(name), ".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add(Path.GetFullPath(entry));
            }
        }

        return files.OrderBy(static path => path, StringComparer.Ordinal).ToImmutableArray();
    }

    public string ResolvePath(string path) => Path.IsPathFullyQualified(path)
        ? Path.GetFullPath(path)
        : Path.GetFullPath(path, RootDirectory);

    public static string ValidateTargetId(string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (targetId[0] is < 'a' or > 'z'
            || targetId.Any(static character =>
                character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && character != '-'))
        {
            throw new InvalidDataException($"Invalid export target id '{targetId}'. Expected ^[a-z][a-z0-9-]*$.");
        }

        return targetId;
    }

    private string ResolveContainedDirectory(string path, string name)
    {
        var resolved = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), RootDirectory);
        if (!IsContained(RootDirectory, resolved) || PathsEqual(RootDirectory, resolved))
            throw new InvalidDataException($"Project directory '{name}' escapes the project root.");
        return resolved;
    }

    private string ResolveGeneratedCSharpDirectory(string path)
    {
        var resolved = ResolvePath(path.Replace('/', Path.DirectorySeparatorChar));
        if (PathsEqual(RootDirectory, resolved))
            throw new InvalidDataException("generatedCSharpDir must name a child or external directory, not the project root.");
        if (!Path.IsPathFullyQualified(path) && !IsContained(RootDirectory, resolved))
            throw new InvalidDataException("Relative generatedCSharpDir must remain within the project root.");
        return resolved;
    }

    private void EnsureDisjointArtifactRoots()
    {
        var roots = new[]
        {
            (Name: "schemaDir", Path: SchemaDirectory),
            (Name: "excelDir", Path: ExcelDirectory),
            (Name: "generatedCSharpDir", Path: GeneratedCSharpDirectory),
            (Name: "generatedBytesDir", Path: GeneratedBytesDirectory),
            (Name: ".exceldb", Path: InternalDirectory),
        };
        for (var left = 0; left < roots.Length; left++)
        {
            for (var right = left + 1; right < roots.Length; right++)
            {
                if (IsContained(roots[left].Path, roots[right].Path)
                    || IsContained(roots[right].Path, roots[left].Path))
                {
                    throw new InvalidDataException(
                        $"Project directories '{roots[left].Name}' and '{roots[right].Name}' must not overlap.");
                }
            }
        }
    }

    private void EnsurePhysicalRootsArePlain()
    {
        ProjectPathSafety.EnsureProjectRootAndInternalDirectoryArePlain(RootDirectory);
        ProjectPathSafety.EnsureContainedPathIsPlain(
            RootDirectory,
            SchemaDirectory,
            "schemaDir",
            targetMustBeDirectory: true);
        ProjectPathSafety.EnsureContainedPathIsPlain(
            RootDirectory,
            ExcelDirectory,
            "excelDir",
            targetMustBeDirectory: true);
        ProjectPathSafety.EnsureContainedPathIsPlain(
            RootDirectory,
            GeneratedBytesDirectory,
            "generatedBytesDir",
            targetMustBeDirectory: true);
        ProjectPathSafety.EnsureDeclaredRootIsPlain(GeneratedCSharpDirectory, "generatedCSharpDir");
        foreach (var (path, purpose) in new[]
                 {
                     (CacheDirectory, "cache"),
                     (PlansDirectory, "plans"),
                     (ReportsDirectory, "reports"),
                     (RecoveryDirectory, "recovery"),
                     (PublishedDirectory, "published"),
                 })
        {
            ProjectPathSafety.EnsureContainedPathIsPlain(
                InternalDirectory,
                path,
                purpose,
                targetMustBeDirectory: true);
        }
        ProjectPathSafety.EnsureContainedPathIsPlain(
            InternalDirectory,
            SystemImportsManifestPath,
            "system imports ownership manifest");
    }

    private static bool IsContained(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathFullyQualified(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PathComparison);
}
