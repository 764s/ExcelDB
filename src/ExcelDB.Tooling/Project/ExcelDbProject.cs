using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace ExcelDb.Tooling.Project;

public sealed record ExcelDbProject(
    string SchemaDir,
    string GeneratedDir,
    ImmutableArray<string> Workbooks,
    string BytesOutput,
    string CacheDir)
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "schemaDir",
        "generatedDir",
        "workbooks",
        "bytesOutput",
        "cacheDir",
    };

    public const string FileName = "ExcelDb.Project.json";

    public static ExcelDbProject Default { get; } = new(
        "Schema",
        "Generated",
        ["Data/*.xlsx"],
        "Build/config.bytes",
        ".exceldb");

    public static ExcelDbProject Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("ExcelDb.Project.json must contain one JSON object.");

        foreach (var property in root.EnumerateObject())
        {
            if (!KnownKeys.Contains(property.Name))
                throw new InvalidDataException($"Unknown ExcelDb.Project.json key '{property.Name}'.");
        }

        var schemaDir = ReadOptionalString(root, "schemaDir") ?? Default.SchemaDir;
        var generatedDir = ReadRequiredString(root, "generatedDir");
        var bytesOutput = ReadRequiredString(root, "bytesOutput");
        var cacheDir = ReadOptionalString(root, "cacheDir") ?? Default.CacheDir;

        if (!root.TryGetProperty("workbooks", out var workbookElement)
            || workbookElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Project key 'workbooks' must be a non-empty string array.");
        }

        var workbooks = workbookElement.EnumerateArray().Select((item, index) =>
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidDataException($"Project key 'workbooks[{index}]' must be a non-empty string.");
            return item.GetString()!;
        }).ToImmutableArray();
        if (workbooks.IsDefaultOrEmpty)
            throw new InvalidDataException("Project key 'workbooks' must contain at least one glob.");

        return Validate(new ExcelDbProject(schemaDir, generatedDir, workbooks, bytesOutput, cacheDir));
    }

    public byte[] ToCanonicalJson()
    {
        Validate(this);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaDir", SchemaDir);
            writer.WriteString("generatedDir", GeneratedDir);
            writer.WriteStartArray("workbooks");
            foreach (var workbook in Workbooks)
                writer.WriteStringValue(workbook);
            writer.WriteEndArray();
            writer.WriteString("bytesOutput", BytesOutput);
            writer.WriteString("cacheDir", CacheDir);
            writer.WriteEndObject();
        }

        return [.. stream.ToArray(), (byte)'\n'];
    }

    public ProjectContext Resolve(string projectFilePath) => new(projectFilePath, this);

    private static ExcelDbProject Validate(ExcelDbProject project)
    {
        ValidatePath(project.SchemaDir, nameof(SchemaDir));
        ValidatePath(project.GeneratedDir, nameof(GeneratedDir));
        ValidatePath(project.BytesOutput, nameof(BytesOutput));
        ValidatePath(project.CacheDir, nameof(CacheDir));
        foreach (var workbook in project.Workbooks)
            ValidatePath(workbook, nameof(Workbooks));
        return project;
    }

    private static string ReadRequiredString(JsonElement root, string name) =>
        ReadOptionalString(root, name)
        ?? throw new InvalidDataException($"Required project key '{name}' is missing.");

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Project key '{name}' must be a non-empty string.");
        return value.GetString();
    }

    private static void ValidatePath(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
            throw new InvalidDataException($"Project path '{name}' is invalid.");
    }
}

public sealed class ProjectContext
{
    internal ProjectContext(string projectFilePath, ExcelDbProject project)
    {
        ProjectFilePath = Path.GetFullPath(projectFilePath);
        RootDirectory = Path.GetDirectoryName(ProjectFilePath)
            ?? throw new InvalidDataException("The project file has no parent directory.");
        Project = project;
    }

    public string ProjectFilePath { get; }

    public string RootDirectory { get; }

    public ExcelDbProject Project { get; }

    public string SchemaDirectory => ResolvePath(Project.SchemaDir);

    public string GeneratedDirectory => ResolvePath(Project.GeneratedDir);

    public string BytesOutputPath => ResolvePath(Project.BytesOutput);

    public string CacheDirectory => ResolvePath(Project.CacheDir);

    public string ResolvePath(string path) => Path.GetFullPath(path, RootDirectory);
}
