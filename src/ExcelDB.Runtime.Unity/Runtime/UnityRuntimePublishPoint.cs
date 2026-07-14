namespace ExcelDb.Runtime.Unity;

/// <summary>Queues watcher work and drains it only from the configured Unity publish tick.</summary>
public sealed class UnityRuntimePublishPoint : IRuntimePublishPoint, IDisposable
{
    private readonly IUnityRuntimeLoop _loop;
    private int _requested;
    private bool _disposed;

    public UnityRuntimePublishPoint(IUnityRuntimeLoop loop)
    {
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        _loop.PublishTick += OnPublishTick;
    }

    public bool IsOwnerContext => _loop.IsMainThread;

    public void RequestPublish()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnityRuntimePublishPoint));
        if (Interlocked.Exchange(ref _requested, 1) == 0)
            _loop.RequestPublishTick();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loop.PublishTick -= OnPublishTick;
        Interlocked.Exchange(ref _requested, 0);
    }

    private void OnPublishTick()
    {
        if (!_loop.IsMainThread)
            throw new InvalidOperationException("The Unity runtime publish tick must run on the main thread.");
        if (Interlocked.Exchange(ref _requested, 0) == 0)
            return;

        RuntimeDatabase.ProcessPendingHotReload();
        if (RuntimeDatabase.HasPendingHotReload)
            RequestPublish();
    }
}
