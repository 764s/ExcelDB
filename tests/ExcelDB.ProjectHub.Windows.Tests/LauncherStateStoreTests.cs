using System.Text.Json;
using ExcelDb.ProjectHub.Windows;
using Xunit;

namespace ExcelDb.ProjectHub.Windows.Tests;

public sealed class LauncherStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exceldb-hub-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RecordOpened_DeduplicatesOrdersAndLimitsToTen()
    {
        var tick = 0;
        var epoch = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
        var store = new LauncherStateStore(StatePath(), () => epoch.AddMinutes(++tick));

        for (var index = 0; index < 12; index++)
            store.RecordOpened(Path.Combine(_root, $"project-{index}"));
        store.RecordOpened(Path.Combine(_root, "project-5"));

        var state = store.Load();
        Assert.Equal(LauncherState.CurrentFormatVersion, state.FormatVersion);
        Assert.Equal(LauncherStateStore.MaximumRecentProjects, state.RecentProjects.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "project-5")), state.RecentProjects[0].Path);
        Assert.Single(state.RecentProjects, item => item.Path.EndsWith("project-5", StringComparison.Ordinal));
        Assert.DoesNotContain(state.RecentProjects, item => item.Path.EndsWith("project-0", StringComparison.Ordinal));
        Assert.True(state.RecentProjects.Zip(state.RecentProjects.Skip(1))
            .All(static pair => pair.First.LastOpenedUtc >= pair.Second.LastOpenedUtc));
    }

    [Fact]
    public void Load_CorruptOrUnknownStateReturnsEmptyWithoutRewriting()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath())!);
        File.WriteAllText(StatePath(), "not json");
        var store = new LauncherStateStore(StatePath());

        Assert.Empty(store.Load().RecentProjects);
        Assert.Equal("not json", File.ReadAllText(StatePath()));

        File.WriteAllText(StatePath(), "{\"formatVersion\":99,\"recentProjects\":[]}");
        Assert.Empty(store.Load().RecentProjects);
    }

    [Fact]
    public void MissingDirectory_IsKeptAndCanBeRemovedOrCleared()
    {
        var missing = Path.Combine(_root, "missing-project");
        var present = Path.Combine(_root, "present-project");
        Directory.CreateDirectory(present);
        var store = new LauncherStateStore(StatePath());
        store.RecordOpened(missing);
        store.RecordOpened(present);

        Assert.Contains(store.Load().RecentProjects, item => item.Path == Path.GetFullPath(missing));

        var afterRemove = store.Remove(missing);
        Assert.Single(afterRemove.RecentProjects);
        Assert.Equal(Path.GetFullPath(present), afterRemove.RecentProjects[0].Path);

        store.Clear();
        Assert.Empty(store.Load().RecentProjects);
    }

    [Fact]
    public void StateJsonContainsOnlyVersionPathAndTimeNavigationFields()
    {
        var store = new LauncherStateStore(StatePath());
        store.RecordOpened(Path.Combine(_root, "project"));

        using var json = JsonDocument.Parse(File.ReadAllBytes(StatePath()));
        var root = json.RootElement;
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        var item = Assert.Single(root.GetProperty("recentProjects").EnumerateArray());
        Assert.Equal(2, item.EnumerateObject().Count());
        Assert.True(item.TryGetProperty("path", out _));
        Assert.True(item.TryGetProperty("lastOpenedUtc", out _));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }

    private string StatePath() => Path.Combine(_root, "local-app-data", "ExcelDB", "launcher-state.json");
}
