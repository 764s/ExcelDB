namespace ExcelDb.Runtime.Unity;

public sealed class UnityRuntimeProfiler : IRuntimeProfiler
{
    private readonly IUnityProfilerBackend _backend;

    public UnityRuntimeProfiler(IUnityProfilerBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public void Begin(RuntimeProfileMarker marker) => _backend.BeginSample(marker);

    public void End(RuntimeProfileMarker marker) => _backend.EndSample(marker);
}
