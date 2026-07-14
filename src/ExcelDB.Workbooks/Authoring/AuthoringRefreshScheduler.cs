namespace ExcelDb.Workbooks.Authoring;

[Flags]
public enum AuthoringRefreshTrigger
{
    None = 0,
    Mount = 1 << 0,
    Explicit = 1 << 1,
    Preflight = 1 << 2,
    Watcher = 1 << 3,
}

/// <summary>
/// Serial authoring publish-point scheduler. Watchers only enqueue hints; mount, explicit refresh,
/// preflight and watcher work are all drained through the same refresh delegate.
/// </summary>
public sealed class AuthoringRefreshScheduler
{
    private readonly object _gate = new();
    private readonly Action _refresh;
    private AuthoringRefreshTrigger _pending;
    private bool _draining;

    public AuthoringRefreshScheduler(Action refresh) =>
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));

    public AuthoringRefreshTrigger Pending
    {
        get
        {
            lock (_gate)
                return _pending;
        }
    }

    public AuthoringRefreshTrigger LastDrained { get; private set; }

    public void SignalWatcher() => Enqueue(AuthoringRefreshTrigger.Watcher);

    public bool RefreshExplicit() => EnqueueAndDrain(AuthoringRefreshTrigger.Explicit);

    public bool RefreshMounted() => EnqueueAndDrain(AuthoringRefreshTrigger.Mount);

    public bool RefreshPreflight() => EnqueueAndDrain(AuthoringRefreshTrigger.Preflight);

    public void Enqueue(AuthoringRefreshTrigger trigger)
    {
        if (trigger == AuthoringRefreshTrigger.None)
            throw new ArgumentOutOfRangeException(nameof(trigger));
        lock (_gate)
            _pending |= trigger;
    }

    /// <summary>
    /// Runs at most one refresh at the current publish point. Signals raised by callbacks remain
    /// queued for the next publish point, preventing publication reentrancy.
    /// </summary>
    public bool Drain()
    {
        AuthoringRefreshTrigger batch;
        lock (_gate)
        {
            if (_draining || _pending == AuthoringRefreshTrigger.None)
                return false;
            _draining = true;
            batch = _pending;
            _pending = AuthoringRefreshTrigger.None;
        }

        try
        {
            _refresh();
            LastDrained = batch;
            return true;
        }
        catch
        {
            lock (_gate)
                _pending |= batch;
            throw;
        }
        finally
        {
            lock (_gate)
                _draining = false;
        }
    }

    private bool EnqueueAndDrain(AuthoringRefreshTrigger trigger)
    {
        Enqueue(trigger);
        return Drain();
    }
}
