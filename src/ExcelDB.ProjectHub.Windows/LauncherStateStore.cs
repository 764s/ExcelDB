using System.Text.Json;

namespace ExcelDb.ProjectHub.Windows;

public sealed record RecentProject(string Path, DateTimeOffset LastOpenedUtc);

public sealed record LauncherState(int FormatVersion, IReadOnlyList<RecentProject> RecentProjects)
{
    public const int CurrentFormatVersion = 1;

    public static LauncherState Empty { get; } = new(CurrentFormatVersion, []);
}

/// <summary>
/// Stores machine-local navigation only. The file never contains project configuration or
/// operation progress and can be deleted without changing any ExcelDB project.
/// </summary>
public sealed class LauncherStateStore
{
    public const int MaximumRecentProjects = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    public LauncherStateStore(string? stateFilePath = null, Func<DateTimeOffset>? clock = null)
    {
        StateFilePath = Path.GetFullPath(stateFilePath ?? GetDefaultStateFilePath());
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string StateFilePath { get; }

    public static string GetDefaultStateFilePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("LOCALAPPDATA is unavailable; recent projects cannot be stored.");
        return Path.Combine(localAppData, "ExcelDB", "launcher-state.json");
    }

    public LauncherState Load()
    {
        lock (_gate)
            return LoadCore();
    }

    public LauncherState RecordOpened(string projectPath)
    {
        var canonicalPath = Canonicalize(projectPath);
        lock (_gate)
        {
            var existing = LoadCore().RecentProjects
                .Where(item => !string.Equals(item.Path, canonicalPath, StringComparison.OrdinalIgnoreCase));
            var state = new LauncherState(
                LauncherState.CurrentFormatVersion,
                new[] { new RecentProject(canonicalPath, _clock()) }
                    .Concat(existing)
                    .Take(MaximumRecentProjects)
                    .ToArray());
            SaveCore(state);
            return state;
        }
    }

    public LauncherState Remove(string projectPath)
    {
        var canonicalPath = Canonicalize(projectPath);
        lock (_gate)
        {
            var state = new LauncherState(
                LauncherState.CurrentFormatVersion,
                LoadCore().RecentProjects
                    .Where(item => !string.Equals(item.Path, canonicalPath, StringComparison.OrdinalIgnoreCase))
                    .ToArray());
            SaveCore(state);
            return state;
        }
    }

    public void Clear()
    {
        lock (_gate)
            SaveCore(LauncherState.Empty);
    }

    private LauncherState LoadCore()
    {
        try
        {
            if (!File.Exists(StateFilePath))
                return LauncherState.Empty;

            var loaded = JsonSerializer.Deserialize<LauncherState>(File.ReadAllBytes(StateFilePath), JsonOptions);
            if (loaded is null || loaded.FormatVersion != LauncherState.CurrentFormatVersion || loaded.RecentProjects is null)
                return LauncherState.Empty;

            var normalized = new List<RecentProject>();
            foreach (var item in loaded.RecentProjects.OrderByDescending(static item => item.LastOpenedUtc))
            {
                if (string.IsNullOrWhiteSpace(item.Path))
                    continue;
                string path;
                try
                {
                    path = Canonicalize(item.Path);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (normalized.Any(candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase)))
                    continue;
                normalized.Add(new RecentProject(path, item.LastOpenedUtc));
                if (normalized.Count == MaximumRecentProjects)
                    break;
            }

            return new LauncherState(LauncherState.CurrentFormatVersion, normalized);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return LauncherState.Empty;
        }
    }

    private void SaveCore(LauncherState state)
    {
        var directory = Path.GetDirectoryName(StateFilePath)
            ?? throw new InvalidOperationException("The launcher state path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(StateFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, [.. JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), (byte)'\n']);
            File.Move(temporaryPath, StateFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
