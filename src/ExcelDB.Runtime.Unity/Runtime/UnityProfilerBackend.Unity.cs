#if UNITY_2022_3_OR_NEWER
using System;
using Unity.Profiling;

namespace ExcelDb.Runtime.Unity
{

/// <summary>Unity ProfilerMarker backend, compiled only inside a Unity 2022.3+ host.</summary>
public sealed class UnityProfilerMarkerBackend : IUnityProfilerBackend
{
    private static readonly ProfilerMarker Candidate = new ProfilerMarker("ExcelDB.CandidateRead");
    private static readonly ProfilerMarker Validate = new ProfilerMarker("ExcelDB.Validate");
    private static readonly ProfilerMarker Diff = new ProfilerMarker("ExcelDB.Diff");
    private static readonly ProfilerMarker Commit = new ProfilerMarker("ExcelDB.Commit");
    private static readonly ProfilerMarker Publish = new ProfilerMarker("ExcelDB.Publish");
    private static readonly ProfilerMarker Dispatch = new ProfilerMarker("ExcelDB.ChangedDispatch");
    private static readonly ProfilerMarker Read = new ProfilerMarker("ExcelDB.Read");

    public void BeginSample(RuntimeProfileMarker marker) => Get(marker).Begin();

    public void EndSample(RuntimeProfileMarker marker) => Get(marker).End();

    private static ProfilerMarker Get(RuntimeProfileMarker marker) => marker switch
    {
        RuntimeProfileMarker.CandidateRead => Candidate,
        RuntimeProfileMarker.Validate => Validate,
        RuntimeProfileMarker.Diff => Diff,
        RuntimeProfileMarker.Commit => Commit,
        RuntimeProfileMarker.Publish => Publish,
        RuntimeProfileMarker.ChangedDispatch => Dispatch,
        RuntimeProfileMarker.Read => Read,
        _ => throw new ArgumentOutOfRangeException(nameof(marker)),
    };
}
}
#endif
