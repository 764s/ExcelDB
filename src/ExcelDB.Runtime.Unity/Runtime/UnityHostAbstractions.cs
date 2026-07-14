namespace ExcelDb.Runtime.Unity;

/// <summary>
/// Minimal Unity main-loop seam. A Unity package integration maps this to one stable editor tick or
/// PlayerLoop point; tests and non-Unity hosts can implement it without Unity assemblies.
/// </summary>
public interface IUnityRuntimeLoop
{
    bool IsMainThread { get; }

    event Action PublishTick;

    void RequestPublishTick();
}

public interface IUnityProfilerBackend
{
    void BeginSample(RuntimeProfileMarker marker);

    void EndSample(RuntimeProfileMarker marker);
}

public interface IUnityAssetHost
{
    bool TryResolve<T>(int providerKey, out T? asset)
        where T : class;
}
