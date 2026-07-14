using ExcelDb.Core.IO;

namespace ExcelDb.Workbooks.Authoring;

/// <summary>
/// Real filesystem watcher that only enqueues a refresh hint after debounce and a stable
/// fingerprint observation.  It never reads authoring state, publishes a view, or writes a file.
/// </summary>
public sealed class WorkbookFileWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly AuthoringRefreshScheduler _scheduler;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _stabilityWindow;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private ContentFingerprint? _candidate;
    private ContentFingerprint? _selfWrite;
    private bool _disposed;

    public WorkbookFileWatcher(
        string workbookPath,
        AuthoringRefreshScheduler scheduler,
        TimeSpan? debounce = null,
        TimeSpan? stabilityWindow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _path = Path.GetFullPath(workbookPath);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(150);
        _stabilityWindow = stabilityWindow ?? TimeSpan.FromMilliseconds(75);
        if (_debounce < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce));
        if (_stabilityWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stabilityWindow));
        var directory = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("Workbook path has no parent directory.", nameof(workbookPath));
        Directory.CreateDirectory(directory);
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.LastWrite
                           | NotifyFilters.Size
                           | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    /// <summary>
    /// Marks the fingerprint just committed by ExcelDB.  All duplicate watcher events for that
    /// exact version are suppressed until a different version is observed.
    /// </summary>
    public void RecordSelfWrite(ContentFingerprint fingerprint)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _selfWrite = fingerprint;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Deleted -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _timer.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => Schedule();

    private void OnRenamed(object sender, RenamedEventArgs args) => Schedule();

    private void OnError(object sender, ErrorEventArgs args) => Schedule();

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _candidate = null;
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer(object? state)
    {
        ContentFingerprint? current = null;
        try
        {
            if (File.Exists(_path))
                current = ContentFingerprint.FromFile(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                if (!_disposed)
                    _timer.Change(_stabilityWindow, Timeout.InfiniteTimeSpan);
            }
            return;
        }

        lock (_gate)
        {
            if (_disposed)
                return;
            if (current is null)
            {
                _candidate = null;
                _selfWrite = null;
                _scheduler.SignalWatcher();
                return;
            }

            if (_candidate is null || _candidate.Value != current.Value)
            {
                _candidate = current;
                _timer.Change(_stabilityWindow, Timeout.InfiniteTimeSpan);
                return;
            }

            _candidate = null;
            if (_selfWrite is { } self && self == current.Value)
                return;
            _selfWrite = null;
            _scheduler.SignalWatcher();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
