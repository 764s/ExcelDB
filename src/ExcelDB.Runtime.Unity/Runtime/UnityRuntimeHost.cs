using ExcelDb.Core.Identity;
using ExcelDb.Runtime.References;

namespace ExcelDb.Runtime.Unity;

/// <summary>
/// Explicit Unity composition root. Source, generated registry, mode and hot reload remain
/// per-session inputs; no EditorPrefs or static environment state is consulted.
/// </summary>
public sealed class UnityRuntimeHost : IDisposable
{
    private readonly IUnityRuntimeLoop _loop;
    private readonly UnityRuntimePublishPoint _publishPoint;
    private readonly IRuntimeProfiler? _profiler;
    private bool _ownsSession;
    private bool _disposed;

    public UnityRuntimeHost(IUnityRuntimeLoop loop, IUnityProfilerBackend? profiler = null)
    {
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        _publishPoint = new UnityRuntimePublishPoint(loop);
        _profiler = profiler is null ? null : new UnityRuntimeProfiler(profiler);
    }

    public bool Open(
        IDataSource source,
        RuntimeSchemaRegistry generatedRegistry,
        RuntimeMode mode,
        bool enableHotReload,
        RuntimeReferenceServices? referenceServices = null)
    {
        ThrowIfDisposed();
        RequireMainThread();
        var opened = RuntimeDatabase.Open(
            source,
            generatedRegistry,
            new RuntimeBootstrapOptions(
                mode,
                enableHotReload,
                _publishPoint,
                _profiler,
                referenceServices));
        _ownsSession = opened;
        return opened;
    }

    public bool SwitchDataSource(IDataSource source)
    {
        ThrowIfDisposed();
        RequireOwnedSession();
        RequireMainThread();
        return RuntimeDatabase.SwitchDataSource(source);
    }

    public bool Refresh()
    {
        ThrowIfDisposed();
        RequireOwnedSession();
        RequireMainThread();
        return RuntimeDatabase.Refresh();
    }

    public void EnableHotReload()
    {
        ThrowIfDisposed();
        RequireOwnedSession();
        RequireMainThread();
        RuntimeDatabase.EnableHotReload();
    }

    public void DisableHotReload()
    {
        ThrowIfDisposed();
        RequireOwnedSession();
        RequireMainThread();
        RuntimeDatabase.DisableHotReload();
    }

    public T? LoadAsset<T>(string key)
        where T : class
    {
        ThrowIfDisposed();
        RequireOwnedSession();
        return RuntimeDatabase.LoadAsset<T>(key);
    }

    public bool TryGetAsset<T>(AssetKey key, out T? asset)
        where T : class
    {
        ThrowIfDisposed();
        RequireOwnedSession();
        return RuntimeDatabase.TryGetAsset(key, out asset);
    }

    public void Close()
    {
        ThrowIfDisposed();
        if (!_ownsSession)
            return;
        RequireMainThread();
        RuntimeDatabase.Close();
        _ownsSession = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_ownsSession)
        {
            RequireMainThread();
            RuntimeDatabase.Close();
            _ownsSession = false;
        }

        _publishPoint.Dispose();
        _disposed = true;
    }

    private void RequireOwnedSession()
    {
        if (!_ownsSession)
            throw new InvalidOperationException("This Unity adapter does not own an open runtime session.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnityRuntimeHost));
    }

    private void RequireMainThread()
    {
        if (!_loop.IsMainThread)
            throw new InvalidOperationException("Unity runtime writes must execute on the main thread.");
    }
}
