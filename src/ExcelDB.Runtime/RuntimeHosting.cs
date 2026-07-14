namespace ExcelDb.Runtime;

/// <summary>Stable runtime markers understood by CoreCLR and host-specific profilers.</summary>
public enum RuntimeProfileMarker : byte
{
    CandidateRead = 0,
    Validate = 1,
    Diff = 2,
    Commit = 3,
    Publish = 4,
    ChangedDispatch = 5,
    Read = 6,
}

/// <summary>
/// A host notification target. Implementations must only queue the request; they must not execute a
/// runtime write inline from <see cref="RequestPublish"/>.
/// </summary>
public interface IRuntimePublishPoint
{
    bool IsOwnerContext { get; }

    void RequestPublish();
}

/// <summary>
/// Low-level marker seam. Implementations are expected to be allocation-free and must not throw.
/// Begin/End calls are balanced, including failed runtime operations.
/// </summary>
public interface IRuntimeProfiler
{
    void Begin(RuntimeProfileMarker marker);

    void End(RuntimeProfileMarker marker);
}
