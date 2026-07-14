namespace ExcelDb.Runtime.Excel;

/// <summary>Read/watch seam used by the optional Excel source adapter.</summary>
public interface IExcelWorkbookStore
{
    bool SupportsWatch { get; }

    byte[] ReadAllBytes(string path);

    IDisposable Watch(IReadOnlyList<string> paths, Action changed);
}

public sealed class FileExcelWorkbookStore : IExcelWorkbookStore
{
    public static FileExcelWorkbookStore Instance { get; } = new();

    public bool SupportsWatch => true;

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public IDisposable Watch(IReadOnlyList<string> paths, Action changed)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(changed);
        if (paths.Count == 0)
            throw new ArgumentException("At least one workbook path is required.", nameof(paths));

        var normalized = paths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var watchers = new List<FileSystemWatcher>();
        foreach (var directory in normalized
                     .Select(static path => Path.GetDirectoryName(path)!)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
                Filter = "*",
            };
            FileSystemEventHandler onChanged = (_, args) =>
            {
                if (normalized.Contains(Path.GetFullPath(args.FullPath)))
                    changed();
            };
            RenamedEventHandler onRenamed = (_, args) =>
            {
                if (normalized.Contains(Path.GetFullPath(args.FullPath))
                    || normalized.Contains(Path.GetFullPath(args.OldFullPath)))
                {
                    changed();
                }
            };
            watcher.Changed += onChanged;
            watcher.Created += onChanged;
            watcher.Deleted += onChanged;
            watcher.Renamed += onRenamed;
            watcher.EnableRaisingEvents = true;
            watchers.Add(watcher);
        }

        return new WatcherSubscription(watchers);
    }

    private sealed class WatcherSubscription(List<FileSystemWatcher> watchers) : IDisposable
    {
        private List<FileSystemWatcher>? _watchers = watchers;

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _watchers, null);
            if (owned is null)
                return;

            foreach (var watcher in owned)
                watcher.Dispose();
        }
    }
}
