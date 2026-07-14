using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Runtime.References;

namespace ExcelDb.Runtime;

public static class RuntimeDatabase
{
    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<object, ObjectState> ObjectStates = new();

    private static Session? _session;
    private static bool _writeInProgress;
    private static bool _publishing;
    private static bool _hotReloadPending;
    private static int _generationCounter;
    private static RuntimeOperationReport _lastReport = RuntimeOperationReport.Empty;
    private static ImmutableArray<Action<ChangeSet>> _changedHandlers = [];

    public static event Action<ChangeSet>? Changed
    {
        add
        {
            var handler = value ?? throw new ArgumentNullException(nameof(value));
            lock (Sync)
                _changedHandlers = _changedHandlers.Add(handler);
        }
        remove
        {
            if (value is null)
                return;
            lock (Sync)
            {
                var index = _changedHandlers.LastIndexOf(value);
                if (index >= 0)
                    _changedHandlers = _changedHandlers.RemoveAt(index);
            }
        }
    }

    // Compatibility projection for the provisional M7 spelling.
    public static event Action<ChangeSet>? changed
    {
        add => Changed += value;
        remove => Changed -= value;
    }

    public static bool IsOpen
    {
        get
        {
            lock (Sync)
                return _session is not null;
        }
    }

    public static RuntimeMode Mode
    {
        get
        {
            lock (Sync)
                return _session?.Mode ?? default;
        }
    }

    public static RuntimeMode mode => Mode;

    public static ExportTargetId ExportTarget
    {
        get
        {
            lock (Sync)
                return _session?.Registry.ExpectedExportTarget ?? default;
        }
    }

    public static ExportTargetId exportTarget => ExportTarget;

    public static bool IsHotReloadEnabled
    {
        get
        {
            lock (Sync)
                return _session?.HotReloadEnabled ?? false;
        }
    }

    public static bool HasPendingHotReload
    {
        get
        {
            lock (Sync)
                return _hotReloadPending;
        }
    }

    public static RuntimeOperationReport LastReport
    {
        get
        {
            lock (Sync)
                return _lastReport;
        }
    }

    public static ImmutableArray<Diagnostic> LastDiagnostics => LastReport.Diagnostics;

    public static bool Open(
        IDataSource source,
        RuntimeSchemaRegistry registry,
        in RuntimeBootstrapOptions options)
    {
        RuntimeCompatibility.NotNull(source, nameof(source));
        RuntimeCompatibility.NotNull(registry, nameof(registry));

        if (!TryBeginOpen())
            return false;

        SourceSnapshot? snapshot = null;
        IDisposable? watchSubscription = null;
        try
        {
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            var sourceInfo = InspectSource(source, diagnostics);
            if (!ValidateModeAndCapabilities(source, sourceInfo, options, diagnostics))
                return Fail("open", registry, source, sourceInfo, null, diagnostics);

            options.Profiler?.Begin(RuntimeProfileMarker.CandidateRead);
            try
            {
                snapshot = ReadSnapshot(source.Open, diagnostics);
            }
            finally
            {
                options.Profiler?.End(RuntimeProfileMarker.CandidateRead);
            }
            if (snapshot is null)
                return Fail("open", registry, source, sourceInfo, null, diagnostics);

            IReadOnlyDictionary<int, RuntimeTableBinding>? bindings;
            options.Profiler?.Begin(RuntimeProfileMarker.Validate);
            try
            {
                bindings = Validate(registry, source, sourceInfo, snapshot, diagnostics);
            }
            finally
            {
                options.Profiler?.End(RuntimeProfileMarker.Validate);
            }
            if (bindings is null)
                return Fail("open", registry, source, sourceInfo, snapshot, diagnostics);

            Session candidate;
            try
            {
                options.Profiler?.Begin(RuntimeProfileMarker.Commit);
                try
                {
                    candidate = BuildInitialSession(source, sourceInfo, snapshot, registry, bindings, options);
                    if (options.EnableHotReload)
                    {
                        watchSubscription = StartWatching(source);
                        candidate = candidate.WithWatch(watchSubscription);
                        watchSubscription = null;
                    }
                }
                finally
                {
                    options.Profiler?.End(RuntimeProfileMarker.Commit);
                }
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                diagnostics.Add(Blocker(
                    RuntimeDiagnosticCodes.CommitFailure,
                    "runtime",
                    $"Failed to construct the initial object graph: {exception.Message}"));
                return Fail("open", registry, source, sourceInfo, snapshot, diagnostics);
            }

            var events = candidate.Entries.Values
                .Where(static entry => entry.State == RuntimeAssetState.Resident)
                .Select(static entry => new ChangeEvent(
                    ChangeKind.Added,
                    entry.Handle,
                    entry.Record.Identity,
                    entry.Binding.RuntimeType))
                .OrderBy(static item => item, ChangeEventComparer.Instance)
                .ToImmutableArray();

            lock (Sync)
            {
                _session = candidate;
                _hotReloadPending = false;
                foreach (var entry in candidate.Entries.Values)
                    SetObjectState(entry.Instance, entry.Record.Identity, entry.State);
            }

            var report = SuccessReport("open", changedValue: events.Length > 0, candidate, source, snapshot, diagnostics);
            Publish(events, candidate.Version, report, candidate.Profiler);
            return true;
        }
        finally
        {
            snapshot?.Dispose();
            watchSubscription?.Dispose();
            EndWrite();
        }
    }

    public static void Close()
    {
        var current = TryBeginActiveWrite("close");
        if (current is null)
            return;

        try
        {
            lock (Sync)
            {
                foreach (var entry in current.Entries.Values)
                    SetObjectState(entry.Instance, entry.Record.Identity, RuntimeAssetState.Missing);

                _session = null;
                _hotReloadPending = false;
                _lastReport = new RuntimeOperationReport(
                    "close",
                    true,
                    false,
                    current.Identity,
                    current.Identity,
                    current.Identity,
                    current.SourceInfo,
                    []);
            }

            current.WatchSubscription?.Dispose();
        }
        finally
        {
            EndWrite();
        }
    }

    public static bool SwitchDataSource(IDataSource source)
    {
        RuntimeCompatibility.NotNull(source, nameof(source));
        var current = TryBeginActiveWrite("switch");
        if (current is null)
            return false;

        return ReplaceFromSource("switch", current, source, refresh: false);
    }

    public static bool Refresh()
    {
        var current = TryBeginActiveWrite("refresh");
        if (current is null)
            return false;

        if (current.Source is not IRefreshableDataSource refreshable
            || (current.SourceInfo.Capabilities & RuntimeSourceCapabilities.Refresh) == 0)
        {
            try
            {
                var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
                diagnostics.Add(Error(
                    RuntimeDiagnosticCodes.SourceRejected,
                    "source",
                    "The active source does not support refresh."));
                return Fail("refresh", current.Registry, current.Source, current.SourceInfo, null, diagnostics);
            }
            finally
            {
                EndWrite();
            }
        }

        return ReplaceFromSource("refresh", current, refreshable, refresh: true);
    }

    public static bool ProcessPendingHotReload()
    {
        lock (Sync)
        {
            if (!_hotReloadPending)
                return false;
            if (_session?.PublishPoint is { IsOwnerContext: false })
                throw new InvalidOperationException("Pending runtime work must execute at the host owner publish point.");
            _hotReloadPending = false;
        }

        return Refresh();
    }

    public static void EnableHotReload() => TryEnableHotReload();

    public static bool TryEnableHotReload()
    {
        var current = TryBeginActiveWrite("hot-reload.enable");
        if (current is null)
            return false;

        IDisposable? subscription = null;
        try
        {
            if (current.HotReloadEnabled)
            {
                var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
                diagnostics.Add(new Diagnostic(
                    RuntimeDiagnosticCodes.HotReloadRejected,
                    DiagnosticSeverity.Warning,
                    "runtime",
                    "Hot reload is already enabled."));
                lock (Sync)
                    _lastReport = SuccessReport("hot-reload.enable", false, current, current.Source, null, diagnostics);
                return true;
            }

            if (current.Mode == RuntimeMode.Release
                || current.Source is not IWatchableDataSource
                || !current.SourceInfo.Capabilities.HasFlag(RuntimeSourceCapabilities.Watch))
            {
                var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
                diagnostics.Add(Error(
                    RuntimeDiagnosticCodes.HotReloadRejected,
                    "source",
                    "Hot reload is not available for the active mode/source."));
                return Fail("hot-reload.enable", current.Registry, current.Source, current.SourceInfo, null, diagnostics);
            }

            subscription = StartWatching(current.Source);
            var next = current.WithHotReload(subscription);
            subscription = null;
            lock (Sync)
            {
                _session = next;
                _lastReport = SuccessReport("hot-reload.enable", false, next, next.Source, null, []);
            }
            return true;
        }
        catch (RuntimeReentrancyException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            diagnostics.Add(Error(
                RuntimeDiagnosticCodes.HotReloadRejected,
                "source",
                $"Could not enable hot reload: {exception.Message}"));
            return Fail("hot-reload.enable", current.Registry, current.Source, current.SourceInfo, null, diagnostics);
        }
        finally
        {
            subscription?.Dispose();
            EndWrite();
        }
    }

    public static void DisableHotReload() => TryDisableHotReload();

    public static bool TryDisableHotReload()
    {
        var current = TryBeginActiveWrite("hot-reload.disable");
        if (current is null)
            return false;

        try
        {
            if (!current.HotReloadEnabled)
            {
                var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
                diagnostics.Add(new Diagnostic(
                    RuntimeDiagnosticCodes.HotReloadRejected,
                    DiagnosticSeverity.Warning,
                    "runtime",
                    "Hot reload is already disabled."));
                lock (Sync)
                    _lastReport = SuccessReport("hot-reload.disable", false, current, current.Source, null, diagnostics);
                return true;
            }

            var subscription = current.WatchSubscription;
            var next = current.WithoutHotReload();
            lock (Sync)
            {
                _session = next;
                _hotReloadPending = false;
                _lastReport = SuccessReport("hot-reload.disable", false, next, next.Source, null, []);
            }

            subscription?.Dispose();
            return true;
        }
        finally
        {
            EndWrite();
        }
    }

    public static void Prewarm()
    {
        lock (Sync)
        {
            if (_session is null)
            {
                _lastReport = ClosedReport("prewarm");
                return;
            }

            _session.Workspace.EnsureCapacity(_session.Entries.Count);
            _session.Entries.EnsureCapacity(_session.Entries.Count);
            _session.KeyIndex.EnsureCapacity(_session.Entries.Count);
            _lastReport = SuccessReport("prewarm", false, _session, _session.Source, null, []);
        }
    }

    public static T? LoadAsset<T>(string key)
        where T : class
    {
        if (!TryGetAssetKey<T>(key, out var assetKey))
            return null;
        return TryGetAsset<T>(assetKey, out var asset) ? asset : null;
    }

    public static bool TryGetAssetKey<T>(string key, out AssetKey assetKey)
        where T : class
    {
        RuntimeCompatibility.NotNull(key, nameof(key));
        lock (Sync)
        {
            var profiler = _session?.Profiler;
            profiler?.Begin(RuntimeProfileMarker.Read);
            try
            {
                assetKey = default;
                if (_session is null
                    || !_session.TableByType.TryGetValue(typeof(T), out var tableId)
                    || !_session.KeyIndex.TryGetValue(new AssetLookupKey(tableId, key), out var entry)
                    || entry.State != RuntimeAssetState.Resident)
                {
                    return false;
                }

                assetKey = entry.Handle;
                return true;
            }
            finally
            {
                profiler?.End(RuntimeProfileMarker.Read);
            }
        }
    }

    public static bool TryGetAsset<T>(AssetKey key, out T? asset)
        where T : class
    {
        lock (Sync)
        {
            var profiler = _session?.Profiler;
            profiler?.Begin(RuntimeProfileMarker.Read);
            try
            {
                asset = null;
                if (_session is null
                    || !key.IsValid
                    || !_session.Slots.TryGetValue(key.TableId, out var slots)
                    || key.Slot >= slots.Length)
                {
                    return false;
                }

                var entry = slots[key.Slot];
                if (entry is null
                    || entry.Handle.Generation != key.Generation
                    || entry.State != RuntimeAssetState.Resident
                    || entry.Instance is not T typed)
                {
                    return false;
                }

                asset = typed;
                return true;
            }
            finally
            {
                profiler?.End(RuntimeProfileMarker.Read);
            }
        }
    }

    public static RuntimeQueryStatus GetAssets<T>(Span<T> buffer, out int count)
        where T : class
    {
        lock (Sync)
        {
            var profiler = _session?.Profiler;
            profiler?.Begin(RuntimeProfileMarker.Read);
            try
            {
                count = 0;
                if (_session is null)
                    return RuntimeQueryStatus.Closed;
                if (!_session.TableByType.TryGetValue(typeof(T), out var tableId)
                    || !_session.Slots.TryGetValue(tableId, out var slots))
                {
                    return RuntimeQueryStatus.Success;
                }

                foreach (var entry in slots)
                {
                    if (entry is null || entry.State != RuntimeAssetState.Resident || entry.Instance is not T asset)
                        continue;

                    if (count < buffer.Length)
                        buffer[count] = asset;
                    count++;
                }

                return count > buffer.Length ? RuntimeQueryStatus.Truncated : RuntimeQueryStatus.Success;
            }
            finally
            {
                profiler?.End(RuntimeProfileMarker.Read);
            }
        }
    }

    public static bool TryGetAssetState(object instance, out RuntimeAssetState state)
    {
        RuntimeCompatibility.NotNull(instance, nameof(instance));
        lock (Sync)
        {
            if (!ObjectStates.TryGetValue(instance, out var holder))
            {
                state = default;
                return false;
            }

            state = holder.State;
            return true;
        }
    }

    public static bool TryGetAssetState(AssetIdentity identity, out RuntimeAssetState state)
    {
        lock (Sync)
        {
            if (_session is null || !_session.Entries.TryGetValue(identity, out var entry))
            {
                state = default;
                return false;
            }

            state = entry.State;
            return true;
        }
    }

    private static bool ReplaceFromSource(string operation, Session current, IDataSource source, bool refresh)
    {
        SourceSnapshot? snapshot = null;
        IDisposable? replacementWatch = null;
        try
        {
            SourceInfo sourceInfo;
            try
            {
                sourceInfo = source.Inspect();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                var inspectDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
                inspectDiagnostics.Add(Error(
                    RuntimeDiagnosticCodes.SourceFailure,
                    "source",
                    $"Source inspection failed: {exception.Message}"));
                return Fail(operation, current.Registry, source, default, null, inspectDiagnostics);
            }

            current.Profiler?.Begin(RuntimeProfileMarker.CandidateRead);
            try
            {
                snapshot = refresh && source is IRefreshableDataSource refreshable
                    ? refreshable.Refresh()
                    : source.Open();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                var readDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
                readDiagnostics.Add(Error(
                    RuntimeDiagnosticCodes.SourceFailure,
                    "source",
                    $"Source open/refresh failed: {exception.Message}"));
                return Fail(operation, current.Registry, source, sourceInfo, null, readDiagnostics);
            }
            finally
            {
                current.Profiler?.End(RuntimeProfileMarker.CandidateRead);
            }
            if (snapshot is null)
            {
                var nullDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
                nullDiagnostics.Add(Error(
                    RuntimeDiagnosticCodes.SourceFailure,
                    "source",
                    "The source returned a null snapshot."));
                return Fail(operation, current.Registry, source, sourceInfo, null, nullDiagnostics);
            }

            var isSteadyCandidate = false;
            var sameSource = ReferenceEquals(source, current.Source);
            if ((refresh && sameSource)
                || (!refresh && (!current.HotReloadEnabled || sameSource)))
            {
                current.Profiler?.Begin(RuntimeProfileMarker.Validate);
                try
                {
                    isSteadyCandidate = IsValidSteadyRefreshCandidate(
                        current,
                        source,
                        sourceInfo,
                        snapshot,
                        refresh);
                }
                finally
                {
                    current.Profiler?.End(RuntimeProfileMarker.Validate);
                }
            }

            if (isSteadyCandidate)
            {
                return CommitSteadyRefresh(operation, current, source, sourceInfo, snapshot);
            }

            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            var options = new RuntimeBootstrapOptions(
                current.Mode,
                current.HotReloadEnabled,
                current.PublishPoint,
                current.Profiler,
                current.ReferenceServices);
            if (!ValidateModeAndCapabilities(source, sourceInfo, options, diagnostics))
                return Fail(operation, current.Registry, source, sourceInfo, snapshot, diagnostics);

            IReadOnlyDictionary<int, RuntimeTableBinding>? bindings;
            current.Profiler?.Begin(RuntimeProfileMarker.Validate);
            try
            {
                bindings = Validate(current.Registry, source, sourceInfo, snapshot, diagnostics);
            }
            finally
            {
                current.Profiler?.End(RuntimeProfileMarker.Validate);
            }
            if (bindings is null)
                return Fail(operation, current.Registry, source, sourceInfo, snapshot, diagnostics);

            if (current.HotReloadEnabled && !ReferenceEquals(source, current.Source))
            {
                replacementWatch = StartWatching(source);
            }

            UpdatePlan plan;
            try
            {
                current.Profiler?.Begin(RuntimeProfileMarker.Diff);
                try
                {
                    plan = BuildUpdatePlan(current, source, sourceInfo, snapshot, bindings, replacementWatch);
                    replacementWatch = null;
                }
                finally
                {
                    current.Profiler?.End(RuntimeProfileMarker.Diff);
                }
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                diagnostics.Add(Blocker(
                    RuntimeDiagnosticCodes.CommitFailure,
                    "runtime",
                    $"Failed to prepare the candidate object graph: {exception.Message}"));
                return Fail(operation, current.Registry, source, sourceInfo, snapshot, diagnostics);
            }

            bool mutationsApplied;
            current.Profiler?.Begin(RuntimeProfileMarker.Commit);
            try
            {
                mutationsApplied = ApplyMutations(plan, diagnostics);
            }
            finally
            {
                current.Profiler?.End(RuntimeProfileMarker.Commit);
            }

            if (!mutationsApplied)
            {
                if (!ReferenceEquals(plan.Next.WatchSubscription, current.WatchSubscription))
                    plan.Next.WatchSubscription?.Dispose();
                return Fail(operation, current.Registry, source, sourceInfo, snapshot, diagnostics);
            }

            lock (Sync)
            {
                _session = plan.Next;
                _hotReloadPending = false;

                foreach (var previous in plan.RecreatedPrevious)
                    SetObjectState(previous.Instance, previous.Record.Identity, RuntimeAssetState.Missing);
                foreach (var entry in plan.Next.Entries.Values)
                    SetObjectState(entry.Instance, entry.Record.Identity, entry.State);
            }

            if (!ReferenceEquals(current.WatchSubscription, plan.Next.WatchSubscription))
                current.WatchSubscription?.Dispose();

            var report = SuccessReport(
                operation,
                plan.Events.Length > 0,
                plan.Next,
                source,
                snapshot,
                diagnostics);
            Publish(plan.Events, plan.Next.Version, report, plan.Next.Profiler);

            return operation == "refresh" ? plan.Events.Length > 0 : true;
        }
        catch (RuntimeReentrancyException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            diagnostics.Add(Error(
                RuntimeDiagnosticCodes.SourceFailure,
                "source",
                $"The source operation failed: {exception.Message}"));
            return Fail(operation, current.Registry, source, TryInspectNoThrow(source), snapshot, diagnostics);
        }
        finally
        {
            snapshot?.Dispose();
            replacementWatch?.Dispose();
            EndWrite();
        }
    }

    private static bool IsValidSteadyRefreshCandidate(
        Session current,
        IDataSource source,
        SourceInfo sourceInfo,
        SourceSnapshot snapshot,
        bool requireRefresh)
    {
        if ((sourceInfo.Capabilities & RuntimeSourceCapabilities.Read) == 0
            || (requireRefresh
                && ((sourceInfo.Capabilities & RuntimeSourceCapabilities.Refresh) == 0
                    || source is not IRefreshableDataSource))
            || (current.HotReloadEnabled
                && ((sourceInfo.Capabilities & RuntimeSourceCapabilities.Watch) == 0
                    || source is not IWatchableDataSource))
            || current.PublishPoint is { IsOwnerContext: false }
            || (current.Mode == RuntimeMode.Release && sourceInfo.Kind == RuntimeSourceKind.Excel)
            || current.Registry.ExpectedSchemaHash != source.SchemaHash
            || current.Registry.ExpectedSchemaHash != snapshot.SchemaHash
            || current.Registry.ExpectedExportTarget != source.ExportTarget
            || current.Registry.ExpectedExportTarget != snapshot.ExportTarget
            || sourceInfo.FormatVersion <= 0
            || sourceInfo.FormatVersion != snapshot.FormatVersion
            || snapshot.Diagnostics.Length != 0
            || snapshot.Assets.Length != current.Entries.Count
            || !string.Equals(sourceInfo.Revision, snapshot.Revision, StringComparison.Ordinal)
            || !string.Equals(sourceInfo.ContentHash, snapshot.ContentHash, StringComparison.Ordinal))
        {
            return false;
        }

        var workspace = current.Workspace;
        workspace.EnsureCapacity(snapshot.Assets.Length);
        workspace.BeginValidation();
        foreach (var record in snapshot.Assets)
        {
            if (record.Identity.TableId <= 0
                || record.Identity.RowGuid.IsEmpty
                || string.IsNullOrEmpty(record.Key)
                || !workspace.Identities.Add(record.Identity)
                || !workspace.Keys.Add(new AssetLookupKey(record.TableId, record.Key))
                || !current.Entries.TryGetValue(record.Identity, out var entry)
                || entry.State != RuntimeAssetState.Resident
                || entry.Binding.TableId != record.TableId
                || (!entry.Record.CanonicallyEquals(record)
                    && !entry.Binding.CanPatch(entry.Record, record)))
            {
                return false;
            }

            for (var left = 0; left < record.Fields.Length; left++)
            {
                for (var right = left + 1; right < record.Fields.Length; right++)
                {
                    if (RuntimeFieldValue.ComparePath(record.Fields[left], record.Fields[right]) == 0)
                        return false;
                }
            }
        }

        foreach (var record in snapshot.Assets)
        {
            foreach (var dependency in record.Dependencies)
            {
                if (!workspace.Identities.Contains(dependency))
                    return false;
            }
        }

        return true;
    }

    private static bool CommitSteadyRefresh(
        string operation,
        Session current,
        IDataSource source,
        SourceInfo sourceInfo,
        SourceSnapshot snapshot)
    {
        var workspace = current.Workspace;
        current.Profiler?.Begin(RuntimeProfileMarker.Diff);
        try
        {
            workspace.BeginPlan();
            foreach (var candidate in snapshot.Assets)
            {
                var entry = current.Entries[candidate.Identity];
                if (entry.Record.CanonicallyEquals(candidate))
                    continue;

                workspace.AddMutation(entry, candidate);
                if (!string.Equals(entry.Record.Path, candidate.Path, StringComparison.Ordinal))
                    workspace.AddEvent(ToEvent(ChangeKind.Moved, entry, candidate));
                if (!string.Equals(entry.Record.Key, candidate.Key, StringComparison.Ordinal))
                    workspace.AddEvent(ToEvent(ChangeKind.Renamed, entry, candidate));
                if (!entry.Record.FieldsEqual(candidate))
                    workspace.AddEvent(ToEvent(ChangeKind.PropertyChanged, entry, candidate));
                if (!entry.Record.DependenciesEqual(candidate))
                    workspace.AddEvent(ToEvent(ChangeKind.DependencyChanged, entry, candidate));
            }

            for (var index = 0; index < workspace.EventCount; index++)
            {
                var change = workspace.Events[index];
                if (change.Kind != ChangeKind.DependencyChanged)
                    workspace.EnqueueDependency(change.AssetIdentity);
            }

            for (var queueIndex = 0; queueIndex < workspace.DependencyQueueCount; queueIndex++)
            {
                var changedIdentity = workspace.GetDependency(queueIndex);
                foreach (var candidate in snapshot.Assets)
                {
                    if (!ContainsIdentity(candidate.Dependencies, changedIdentity))
                        continue;

                    var dependent = current.Entries[candidate.Identity];
                    workspace.AddEvent(ToEvent(ChangeKind.DependencyChanged, dependent, candidate));
                    workspace.EnqueueDependency(candidate.Identity);
                }
            }

            workspace.SortEvents();
        }
        finally
        {
            current.Profiler?.End(RuntimeProfileMarker.Diff);
        }

        var attempted = 0;
        current.Profiler?.Begin(RuntimeProfileMarker.Commit);
        try
        {
            for (var index = 0; index < workspace.MutationCount; index++)
            {
                var entry = workspace.GetMutationEntry(index);
                object state;
                if (entry.Binding.SupportsReusableRollbackState)
                {
                    state = entry.ReusableRollbackState
                        ?? throw new InvalidOperationException("Reusable rollback state was not initialized.");
                    entry.Binding.CaptureReusableRollbackState(entry.Instance, state);
                }
                else
                {
                    state = entry.Binding.CaptureState(entry.Instance);
                }
                workspace.SetCapturedState(index, state);
            }

            for (var index = 0; index < workspace.MutationCount; index++)
            {
                attempted = index + 1;
                var entry = workspace.GetMutationEntry(index);
                entry.Binding.Apply(
                    entry.Instance,
                    workspace.GetMutationCandidate(index),
                    current.ReferenceServices);
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            for (var index = attempted - 1; index >= 0; index--)
            {
                try
                {
                    var entry = workspace.GetMutationEntry(index);
                    entry.Binding.RestoreState(entry.Instance, workspace.GetCapturedState(index)!);
                }
                catch (Exception rollbackException) when (!IsFatal(rollbackException))
                {
                    _ = rollbackException;
                }
            }

            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            diagnostics.Add(Blocker(
                RuntimeDiagnosticCodes.CommitFailure,
                "runtime",
                $"Candidate commit failed and the old session was retained: {exception.Message}."));
            return Fail(operation, current.Registry, current.Source, sourceInfo, snapshot, diagnostics);
        }
        finally
        {
            current.Profiler?.End(RuntimeProfileMarker.Commit);
        }

        for (var index = 0; index < workspace.MutationCount; index++)
        {
            var entry = workspace.GetMutationEntry(index);
            var candidate = workspace.GetMutationCandidate(index);
            if (!string.Equals(entry.Record.Key, candidate.Key, StringComparison.Ordinal))
                current.KeyIndex.Remove(new AssetLookupKey(entry.Record.TableId, entry.Record.Key));
        }
        for (var index = 0; index < workspace.MutationCount; index++)
        {
            var entry = workspace.GetMutationEntry(index);
            var candidate = workspace.GetMutationCandidate(index);
            if (!string.Equals(entry.Record.Key, candidate.Key, StringComparison.Ordinal))
                current.KeyIndex.Add(new AssetLookupKey(candidate.TableId, candidate.Key), entry);
            entry.Record = candidate;
        }

        current.Source = source;
        current.SourceInfo = sourceInfo;
        current.SourceRevision = snapshot.Revision;
        current.SourceContentHash = snapshot.ContentHash;
        if (workspace.EventCount > 0)
            current.Version = checked(current.Version + 1);

        lock (Sync)
            _hotReloadPending = false;

        var report = SuccessReportWithoutDiagnostics(
            operation,
            workspace.EventCount > 0,
            current,
            source,
            snapshot);
        Publish(workspace.Events, workspace.EventCount, current.Version, report, current.Profiler);
        return operation == "refresh" ? workspace.EventCount > 0 : true;
    }

    private static bool ContainsIdentity(
        ImmutableArray<AssetIdentity> identities,
        AssetIdentity value)
    {
        for (var index = 0; index < identities.Length; index++)
        {
            if (identities[index] == value)
                return true;
        }
        return false;
    }

    private static UpdatePlan BuildUpdatePlan(
        Session current,
        IDataSource source,
        SourceInfo sourceInfo,
        SourceSnapshot snapshot,
        IReadOnlyDictionary<int, RuntimeTableBinding> bindings,
        IDisposable? replacementWatch)
    {
        var candidateByIdentity = snapshot.Assets.ToDictionary(static item => item.Identity);
        var nextEntries = new List<Entry>(Math.Max(current.Entries.Count, candidateByIdentity.Count));
        var mutations = new List<Mutation>();
        var rawEvents = new List<ChangeEvent>();
        var recreatedPrevious = new List<Entry>();

        var nextSlot = current.Slots.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.Length);

        foreach (var previous in current.Entries.Values.OrderBy(static item => item.Record.Identity, AssetIdentityComparer.Instance))
        {
            if (!candidateByIdentity.Remove(previous.Record.Identity, out var candidate))
            {
                var next = previous.Copy(
                    previous.Record,
                    previous.Binding,
                    RuntimeAssetState.Missing);
                nextEntries.Add(next);
                if (previous.State == RuntimeAssetState.Resident)
                {
                    mutations.Add(new Mutation(previous, MutationKind.Reset));
                    rawEvents.Add(ToEvent(ChangeKind.Removed, previous));
                }
                continue;
            }

            var binding = bindings[candidate.TableId];
            if (previous.State == RuntimeAssetState.Missing)
            {
                var next = previous.Copy(
                    candidate,
                    binding,
                    RuntimeAssetState.Resident);
                nextEntries.Add(next);
                mutations.Add(new Mutation(previous, MutationKind.Apply, candidate));
                rawEvents.Add(ToEvent(ChangeKind.Added, next));
                continue;
            }

            if (!binding.CanPatch(previous.Record, candidate))
            {
                var instance = binding.Create();
                binding.Apply(instance, candidate, current.ReferenceServices);
                var next = new Entry(
                    candidate,
                    binding,
                    instance,
                    RuntimeAssetState.Resident,
                    new AssetKey(candidate.TableId, previous.Handle.Slot, AllocateGeneration()));
                nextEntries.Add(next);
                mutations.Add(new Mutation(previous, MutationKind.Reset));
                recreatedPrevious.Add(previous);
                rawEvents.Add(ToEvent(ChangeKind.Recreated, next));
                continue;
            }

            var patched = previous.Copy(candidate, binding, previous.State);
            nextEntries.Add(patched);
            if (previous.Record.CanonicallyEquals(candidate))
                continue;

            mutations.Add(new Mutation(previous, MutationKind.Apply, candidate));
            if (!string.Equals(previous.Record.Path, candidate.Path, StringComparison.Ordinal))
                rawEvents.Add(ToEvent(ChangeKind.Moved, patched));
            if (!string.Equals(previous.Record.Key, candidate.Key, StringComparison.Ordinal))
                rawEvents.Add(ToEvent(ChangeKind.Renamed, patched));
            if (!previous.Record.FieldsEqual(candidate))
                rawEvents.Add(ToEvent(ChangeKind.PropertyChanged, patched));
            if (!previous.Record.DependenciesEqual(candidate))
                rawEvents.Add(ToEvent(ChangeKind.DependencyChanged, patched));
        }

        foreach (var candidate in candidateByIdentity.Values.OrderBy(static item => item.Identity, AssetIdentityComparer.Instance))
        {
            var binding = bindings[candidate.TableId];
            var instance = binding.Create();
            binding.Apply(instance, candidate, current.ReferenceServices);
            var slot = nextSlot.GetValueOrDefault(candidate.TableId);
            nextSlot[candidate.TableId] = checked(slot + 1);
            var entry = new Entry(
                candidate,
                binding,
                instance,
                RuntimeAssetState.Resident,
                new AssetKey(candidate.TableId, slot, AllocateGeneration()));
            nextEntries.Add(entry);
            rawEvents.Add(ToEvent(ChangeKind.Added, entry));
        }

        var events = CompleteAndSortEvents(current, nextEntries, rawEvents);
        var nextVersion = events.Length == 0 ? current.Version : checked(current.Version + 1);
        var subscription = replacementWatch ?? current.WatchSubscription;
        var nextSession = CreateSession(
            source,
            sourceInfo,
            current.Registry,
            current.Mode,
            current.HotReloadEnabled,
            subscription,
            current.PublishPoint,
            current.Profiler,
            current.ReferenceServices,
            snapshot.Revision,
            snapshot.ContentHash,
            nextVersion,
            nextEntries);

        return new UpdatePlan(nextSession, mutations, events, recreatedPrevious);
    }

    private static bool ApplyMutations(UpdatePlan plan, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var captured = new List<CapturedState>(plan.Mutations.Count);
        try
        {
            foreach (var mutation in plan.Mutations)
            {
                captured.Add(new CapturedState(
                    mutation.Entry,
                    mutation.Entry.Binding.CaptureState(mutation.Entry.Instance)));
            }

            foreach (var mutation in plan.Mutations)
            {
                if (mutation.Kind == MutationKind.Reset)
                    mutation.Entry.Binding.ResetToDefaults(mutation.Entry.Instance);
                else
                    mutation.Entry.Binding.Apply(
                        mutation.Entry.Instance,
                        mutation.Candidate!,
                        plan.Next.ReferenceServices);
            }

            return true;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var rollbackFailures = new List<string>();
            foreach (var state in captured.AsEnumerable().Reverse())
            {
                try
                {
                    state.Entry.Binding.RestoreState(state.Entry.Instance, state.Value);
                }
                catch (Exception rollbackException) when (!IsFatal(rollbackException))
                {
                    rollbackFailures.Add(rollbackException.Message);
                }
            }

            var suffix = rollbackFailures.Count == 0
                ? string.Empty
                : $" Rollback failures: {string.Join(" | ", rollbackFailures)}";
            diagnostics.Add(Blocker(
                RuntimeDiagnosticCodes.CommitFailure,
                "runtime",
                $"Candidate commit failed and the old session was retained: {exception.Message}.{suffix}"));
            return false;
        }
    }

    private static ImmutableArray<ChangeEvent> CompleteAndSortEvents(
        Session current,
        IReadOnlyCollection<Entry> nextEntries,
        IReadOnlyCollection<ChangeEvent> rawEvents)
    {
        var eventMap = rawEvents.ToDictionary(
            static item => new ChangeEventIdentity(item.Kind, item.AssetIdentity),
            static item => item);
        var changedIdentities = rawEvents
            .Where(static item => item.Kind != ChangeKind.DependencyChanged)
            .Select(static item => item.AssetIdentity)
            .ToHashSet();

        var nextByIdentity = nextEntries.ToDictionary(static item => item.Record.Identity);
        var reverseDependencies = new Dictionary<AssetIdentity, List<AssetIdentity>>();
        foreach (var entry in nextEntries.Where(static item => item.State == RuntimeAssetState.Resident))
        {
            foreach (var dependency in entry.Record.Dependencies)
            {
                if (!reverseDependencies.TryGetValue(dependency, out var dependents))
                {
                    dependents = [];
                    reverseDependencies.Add(dependency, dependents);
                }
                dependents.Add(entry.Record.Identity);
            }
        }

        var queue = new Queue<AssetIdentity>(changedIdentities.OrderBy(static item => item, AssetIdentityComparer.Instance));
        var visited = new HashSet<AssetIdentity>(changedIdentities);
        while (queue.Count > 0)
        {
            var changedIdentity = queue.Dequeue();
            if (!reverseDependencies.TryGetValue(changedIdentity, out var dependents))
                continue;

            foreach (var dependentIdentity in dependents.OrderBy(static item => item, AssetIdentityComparer.Instance))
            {
                if (!nextByIdentity.TryGetValue(dependentIdentity, out var dependent)
                    || dependent.State != RuntimeAssetState.Resident)
                {
                    continue;
                }

                var eventIdentity = new ChangeEventIdentity(ChangeKind.DependencyChanged, dependentIdentity);
                eventMap.TryAdd(eventIdentity, ToEvent(ChangeKind.DependencyChanged, dependent));
                if (visited.Add(dependentIdentity))
                    queue.Enqueue(dependentIdentity);
            }
        }

        _ = current;
        return eventMap.Values.OrderBy(static item => item, ChangeEventComparer.Instance).ToImmutableArray();
    }

    private static Session BuildInitialSession(
        IDataSource source,
        SourceInfo sourceInfo,
        SourceSnapshot snapshot,
        RuntimeSchemaRegistry registry,
        IReadOnlyDictionary<int, RuntimeTableBinding> bindings,
        in RuntimeBootstrapOptions options)
    {
        var nextSlot = new Dictionary<int, int>();
        var entries = new List<Entry>(snapshot.Assets.Length);
        foreach (var record in snapshot.Assets.OrderBy(static item => item.Identity, AssetIdentityComparer.Instance))
        {
            var binding = bindings[record.TableId];
            var instance = binding.Create();
            binding.Apply(instance, record, options.ReferenceServices);
            var slot = nextSlot.GetValueOrDefault(record.TableId);
            nextSlot[record.TableId] = checked(slot + 1);
            entries.Add(new Entry(
                record,
                binding,
                instance,
                RuntimeAssetState.Resident,
                new AssetKey(record.TableId, slot, AllocateGeneration())));
        }

        return CreateSession(
            source,
            sourceInfo,
            registry,
            options.Mode,
            options.EnableHotReload,
            null,
            options.PublishPoint,
            options.Profiler,
            options.ReferenceServices,
            snapshot.Revision,
            snapshot.ContentHash,
            1,
            entries);
    }

    private static Session CreateSession(
        IDataSource source,
        SourceInfo sourceInfo,
        RuntimeSchemaRegistry registry,
        RuntimeMode mode,
        bool hotReloadEnabled,
        IDisposable? watchSubscription,
        IRuntimePublishPoint? publishPoint,
        IRuntimeProfiler? profiler,
        RuntimeReferenceServices referenceServices,
        string sourceRevision,
        string sourceContentHash,
        uint version,
        IEnumerable<Entry> entries)
    {
        var entryMap = entries.ToDictionary(static item => item.Record.Identity);
        var tableByType = registry.Bindings.ToDictionary(static item => item.RuntimeType, static item => item.TableId);
        var keyIndex = new Dictionary<AssetLookupKey, Entry>();
        var slots = new Dictionary<int, Entry?[]>();

        foreach (var tableGroup in entryMap.Values.GroupBy(static item => item.Record.TableId))
        {
            var length = tableGroup.Max(static item => item.Handle.Slot) + 1;
            var tableSlots = new Entry?[length];
            foreach (var entry in tableGroup)
            {
                tableSlots[entry.Handle.Slot] = entry;
                if (entry.State == RuntimeAssetState.Resident)
                    keyIndex.Add(new AssetLookupKey(entry.Record.TableId, entry.Record.Key), entry);
            }
            slots.Add(tableGroup.Key, tableSlots);
        }

        return new Session(
            source,
            sourceInfo,
            registry,
            mode,
            hotReloadEnabled,
            watchSubscription,
            publishPoint,
            profiler,
            referenceServices,
            sourceRevision,
            sourceContentHash,
            version,
            Environment.CurrentManagedThreadId,
            entryMap,
            keyIndex,
            tableByType,
            slots);
    }

    private static IReadOnlyDictionary<int, RuntimeTableBinding>? Validate(
        RuntimeSchemaRegistry registry,
        IDataSource source,
        SourceInfo sourceInfo,
        SourceSnapshot snapshot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Dictionary<int, RuntimeTableBinding>? bindings = null;
        try
        {
            if (registry.ExpectedSchemaHash == 0 || registry.ExpectedExportTarget.IsEmpty)
            {
                diagnostics.Add(Blocker(
                    RuntimeDiagnosticCodes.InvalidRegistry,
                    "registry",
                    "The generated registry has an empty expected schema identity."));
            }

            bindings = new Dictionary<int, RuntimeTableBinding>();
            var runtimeTypes = new HashSet<Type>();
            foreach (var binding in registry.Bindings ?? throw new InvalidOperationException("Registry bindings are null."))
            {
                if (!bindings.TryAdd(binding.TableId, binding) || !runtimeTypes.Add(binding.RuntimeType))
                {
                    diagnostics.Add(Blocker(
                        RuntimeDiagnosticCodes.InvalidRegistry,
                        "registry",
                        $"Duplicate table/type binding for table {binding.TableId} ({binding.RuntimeType.FullName})."));
                }
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            diagnostics.Add(Blocker(
                RuntimeDiagnosticCodes.InvalidRegistry,
                "registry",
                $"The generated registry is invalid: {exception.Message}"));
        }

        var expected = new RuntimeSchemaIdentity(registry.ExpectedSchemaHash, registry.ExpectedExportTarget);
        var declared = new RuntimeSchemaIdentity(source.SchemaHash, source.ExportTarget);
        var actual = new RuntimeSchemaIdentity(snapshot.SchemaHash, snapshot.ExportTarget);

        if (expected.SchemaHash != declared.SchemaHash
            || expected.SchemaHash != actual.SchemaHash
            || declared.SchemaHash != actual.SchemaHash)
        {
            diagnostics.Add(Blocker(
                RuntimeDiagnosticCodes.SchemaHashMismatch,
                "source",
                $"Schema hash mismatch: expected={expected.SchemaHash:x16}, declared={declared.SchemaHash:x16}, actual={actual.SchemaHash:x16}."));
        }

        if (expected.ExportTarget != declared.ExportTarget
            || expected.ExportTarget != actual.ExportTarget
            || declared.ExportTarget != actual.ExportTarget)
        {
            diagnostics.Add(Blocker(
                RuntimeDiagnosticCodes.ExportTargetMismatch,
                "source",
                $"Export target mismatch: expected={expected.ExportTarget}, declared={declared.ExportTarget}, actual={actual.ExportTarget}."));
        }

        if (snapshot.FormatVersion <= 0
            || sourceInfo.FormatVersion <= 0
            || snapshot.FormatVersion != sourceInfo.FormatVersion)
        {
            diagnostics.Add(Blocker(
                RuntimeDiagnosticCodes.SourceFailure,
                "source",
                $"Source format mismatch: declared={sourceInfo.FormatVersion}, actual={snapshot.FormatVersion}."));
        }

        diagnostics.AddRange(snapshot.Diagnostics);

        var identities = new HashSet<AssetIdentity>();
        var keys = new HashSet<AssetLookupKey>();
        foreach (var record in snapshot.Assets)
        {
            if (record.Identity.TableId <= 0 || record.Identity.RowGuid.IsEmpty)
            {
                diagnostics.Add(Blocker(
                    RuntimeDiagnosticCodes.PendingIdentity,
                    "source",
                    "A runtime-exported row has a pending or empty identity."));
                continue;
            }

            if (!identities.Add(record.Identity))
            {
                diagnostics.Add(Blocker(
                    RuntimeDiagnosticCodes.DuplicateIdentity,
                    record.Identity.ToString(),
                    "The candidate contains a duplicate asset identity."));
            }

            if (string.IsNullOrEmpty(record.Key))
            {
                diagnostics.Add(Blocker(
                    RuntimeDiagnosticCodes.InvalidRow,
                    record.Identity.ToString(),
                    "A runtime asset key must not be empty."));
            }
            else if (!keys.Add(new AssetLookupKey(record.TableId, record.Key)))
            {
                diagnostics.Add(Blocker(
                    RuntimeDiagnosticCodes.DuplicateKey,
                    record.Identity.ToString(),
                    $"Duplicate key '{record.Key}' in table {record.TableId}."));
            }

            if (bindings is null || !bindings.ContainsKey(record.TableId))
            {
                diagnostics.Add(Blocker(
                    RuntimeDiagnosticCodes.InvalidRegistry,
                    record.Identity.ToString(),
                    $"The generated registry does not bind table {record.TableId}."));
            }

            var fieldPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in record.Fields)
            {
                var fieldPath = string.Join('.', field.FieldIdPath);
                if (!fieldPaths.Add(fieldPath))
                {
                    diagnostics.Add(Blocker(
                        RuntimeDiagnosticCodes.InvalidRow,
                        record.Identity.ToString(),
                        $"Duplicate runtime field id path {fieldPath}."));
                }
            }
        }

        foreach (var record in snapshot.Assets)
        {
            foreach (var dependency in record.Dependencies)
            {
                if (!identities.Contains(dependency))
                {
                    diagnostics.Add(Blocker(
                        RuntimeDiagnosticCodes.DanglingReference,
                        record.Identity.ToString(),
                        $"Hard reference target {dependency} is absent from the candidate."));
                }
            }
        }

        return diagnostics.Any(static item => item.IsFailure) ? null : bindings;
    }

    private static bool ValidateModeAndCapabilities(
        IDataSource source,
        SourceInfo sourceInfo,
        in RuntimeBootstrapOptions options,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Enum.IsDefined(typeof(RuntimeMode), options.Mode))
        {
            diagnostics.Add(Error(
                RuntimeDiagnosticCodes.SourceRejected,
                "runtime",
                $"Unknown runtime mode value {(byte)options.Mode}."));
        }

        if (options.PublishPoint is { IsOwnerContext: false })
        {
            diagnostics.Add(Error(
                RuntimeDiagnosticCodes.SourceRejected,
                "runtime",
                "Runtime Open must execute on the configured host publish-point owner context."));
        }

        if (!sourceInfo.Capabilities.HasFlag(RuntimeSourceCapabilities.Read))
        {
            diagnostics.Add(Error(
                RuntimeDiagnosticCodes.SourceRejected,
                "source",
                "The source does not declare read capability."));
        }

        if (options.Mode == RuntimeMode.Release && sourceInfo.Kind == RuntimeSourceKind.Excel)
        {
            diagnostics.Add(Error(
                RuntimeDiagnosticCodes.SourceRejected,
                "source",
                "ExcelDataSource is forbidden in Release mode."));
        }

        if (sourceInfo.Capabilities.HasFlag(RuntimeSourceCapabilities.Refresh)
            && source is not IRefreshableDataSource)
        {
            diagnostics.Add(Error(
                RuntimeDiagnosticCodes.SourceRejected,
                "source",
                "The source declares refresh capability but does not implement IRefreshableDataSource."));
        }

        if (sourceInfo.Capabilities.HasFlag(RuntimeSourceCapabilities.Watch)
            && source is not IWatchableDataSource)
        {
            diagnostics.Add(Error(
                RuntimeDiagnosticCodes.SourceRejected,
                "source",
                "The source declares watch capability but does not implement IWatchableDataSource."));
        }

        if (options.EnableHotReload
            && (options.Mode == RuntimeMode.Release
                || source is not IWatchableDataSource
                || !sourceInfo.Capabilities.HasFlag(RuntimeSourceCapabilities.Watch)))
        {
            diagnostics.Add(Error(
                RuntimeDiagnosticCodes.HotReloadRejected,
                "source",
                "Requested hot reload is not available for this mode/source."));
        }

        return !diagnostics.Any(static item => item.IsFailure);
    }

    private static SourceInfo InspectSource(IDataSource source, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            return source.Inspect();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            diagnostics.Add(Error(
                RuntimeDiagnosticCodes.SourceFailure,
                "source",
                $"Source inspection failed: {exception.Message}"));
            return default;
        }
    }

    private static SourceInfo TryInspectNoThrow(IDataSource source)
    {
        try
        {
            return source.Inspect();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return default;
        }
    }

    private static SourceSnapshot? ReadSnapshot(
        Func<SourceSnapshot> read,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            return read() ?? throw new InvalidOperationException("The source returned a null snapshot.");
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            diagnostics.Add(Error(
                RuntimeDiagnosticCodes.SourceFailure,
                "source",
                $"Source open/refresh failed: {exception.Message}"));
            return null;
        }
    }

    private static IDisposable StartWatching(IDataSource source)
    {
        if (source is not IWatchableDataSource watchable)
            throw new InvalidOperationException("The source is not watchable.");

        return watchable.Watch(() =>
        {
            IRuntimePublishPoint? publishPoint = null;
            lock (Sync)
            {
                if (_session is not null && ReferenceEquals(_session.Source, source) && _session.HotReloadEnabled)
                {
                    _hotReloadPending = true;
                    publishPoint = _session.PublishPoint;
                }
            }

            publishPoint?.RequestPublish();
        });
    }

    private static bool TryBeginOpen()
    {
        lock (Sync)
        {
            RejectConcurrentOrReentrantWrite();
            if (_session is not null)
            {
                _lastReport = new RuntimeOperationReport(
                    "open",
                    false,
                    false,
                    _session.Identity,
                    null,
                    null,
                    null,
                    [Error(RuntimeDiagnosticCodes.AlreadyOpen, "runtime", "RuntimeDatabase is already open.")]);
                return false;
            }

            _writeInProgress = true;
            return true;
        }
    }

    private static Session? TryBeginActiveWrite(string operation)
    {
        lock (Sync)
        {
            RejectConcurrentOrReentrantWrite();
            if (_session is null)
            {
                _lastReport = ClosedReport(operation);
                return null;
            }

            if (_session.OwnerThreadId != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Runtime writes must execute on the session owner context.");

            _writeInProgress = true;
            return _session;
        }
    }

    private static void RejectConcurrentOrReentrantWrite()
    {
        if (_publishing)
            throw new RuntimeReentrancyException();
        if (_writeInProgress)
            throw new InvalidOperationException("Another runtime write transaction is already in progress.");
    }

    private static void EndWrite()
    {
        lock (Sync)
            _writeInProgress = false;
    }

    private static bool Fail(
        string operation,
        RuntimeSchemaRegistry registry,
        IDataSource source,
        SourceInfo sourceInfo,
        SourceSnapshot? snapshot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var report = new RuntimeOperationReport(
            operation,
            false,
            false,
            new RuntimeSchemaIdentity(registry.ExpectedSchemaHash, registry.ExpectedExportTarget),
            new RuntimeSchemaIdentity(source.SchemaHash, source.ExportTarget),
            snapshot is null ? null : new RuntimeSchemaIdentity(snapshot.SchemaHash, snapshot.ExportTarget),
            sourceInfo,
            diagnostics.ToImmutable());
        lock (Sync)
            _lastReport = report;
        return false;
    }

    private static RuntimeOperationReport SuccessReport(
        string operation,
        bool changedValue,
        Session session,
        IDataSource source,
        SourceSnapshot? snapshot,
        IEnumerable<Diagnostic> diagnostics) =>
        new(
            operation,
            true,
            changedValue,
            session.Identity,
            new RuntimeSchemaIdentity(source.SchemaHash, source.ExportTarget),
            snapshot is null ? session.Identity : new RuntimeSchemaIdentity(snapshot.SchemaHash, snapshot.ExportTarget),
            session.SourceInfo,
            diagnostics.ToImmutableArray());

    private static RuntimeOperationReport SuccessReportWithoutDiagnostics(
        string operation,
        bool changedValue,
        Session session,
        IDataSource source,
        SourceSnapshot? snapshot) =>
        new(
            operation,
            true,
            changedValue,
            session.Identity,
            new RuntimeSchemaIdentity(source.SchemaHash, source.ExportTarget),
            snapshot is null ? session.Identity : new RuntimeSchemaIdentity(snapshot.SchemaHash, snapshot.ExportTarget),
            session.SourceInfo,
            ImmutableArray<Diagnostic>.Empty);

    private static RuntimeOperationReport ClosedReport(string operation) =>
        new(
            operation,
            false,
            false,
            null,
            null,
            null,
            null,
            [Error(RuntimeDiagnosticCodes.Closed, "runtime", "RuntimeDatabase is closed.")]);

    private static void Publish(
        ImmutableArray<ChangeEvent> events,
        uint version,
        RuntimeOperationReport report,
        IRuntimeProfiler? profiler) =>
        Publish(new ChangeEventList(events), events.Length, version, report, profiler);

    private static void Publish(
        ChangeEvent[] events,
        int eventCount,
        uint version,
        RuntimeOperationReport report,
        IRuntimeProfiler? profiler) =>
        Publish(new ChangeEventList(events, eventCount), eventCount, version, report, profiler);

    private static void Publish(
        ChangeEventList events,
        int eventCount,
        uint version,
        RuntimeOperationReport report,
        IRuntimeProfiler? profiler)
    {
        profiler?.Begin(RuntimeProfileMarker.Publish);
        try
        {
            if (eventCount == 0)
            {
                lock (Sync)
                    _lastReport = report;
                return;
            }

            ImmutableArray<Action<ChangeSet>> handlers;
            lock (Sync)
            {
                _publishing = true;
                _lastReport = report;
                handlers = _changedHandlers;
            }

            ImmutableArray<Diagnostic>.Builder? callbackDiagnostics = null;
            profiler?.Begin(RuntimeProfileMarker.ChangedDispatch);
            try
            {
                var changeSet = new ChangeSet(version, events);
                foreach (var handler in handlers)
                {
                    try
                    {
                        handler(changeSet);
                    }
                    catch (RuntimeReentrancyException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (!IsFatal(exception))
                    {
                        callbackDiagnostics ??= ImmutableArray.CreateBuilder<Diagnostic>();
                        callbackDiagnostics.Add(new Diagnostic(
                            "runtime.changed.callback-failed",
                            DiagnosticSeverity.Warning,
                            "runtime",
                            exception.Message));
                    }
                }
            }
            finally
            {
                profiler?.End(RuntimeProfileMarker.ChangedDispatch);
                lock (Sync)
                {
                    _publishing = false;
                    if (callbackDiagnostics is { Count: > 0 })
                        _lastReport = report with { Diagnostics = report.Diagnostics.AddRange(callbackDiagnostics) };
                }
            }
        }
        finally
        {
            profiler?.End(RuntimeProfileMarker.Publish);
        }
    }

    private static void SetObjectState(object instance, AssetIdentity identity, RuntimeAssetState state)
    {
        var holder = ObjectStates.GetOrCreateValue(instance);
        holder.Identity = identity;
        holder.State = state;
    }

    private static uint AllocateGeneration()
    {
        var generation = unchecked((uint)Interlocked.Increment(ref _generationCounter));
        if (generation == 0)
            throw new InvalidOperationException("AssetKey generation space is exhausted.");
        return generation;
    }

    private static ChangeEvent ToEvent(ChangeKind kind, Entry entry) =>
        new(kind, entry.Handle, entry.Record.Identity, entry.Binding.RuntimeType);

    private static ChangeEvent ToEvent(
        ChangeKind kind,
        Entry entry,
        RuntimeAssetRecord candidate) =>
        new(kind, entry.Handle, candidate.Identity, entry.Binding.RuntimeType);

    private static Diagnostic Error(string code, string location, string message) =>
        new(code, DiagnosticSeverity.Error, location, message);

    private static Diagnostic Blocker(string code, string location, string message) =>
        new(code, DiagnosticSeverity.Blocker, location, message);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed class ObjectState
    {
        public AssetIdentity Identity { get; set; }

        public RuntimeAssetState State { get; set; }
    }

    private sealed class RuntimeReentrancyException : InvalidOperationException
    {
        public RuntimeReentrancyException()
            : base("Runtime write transactions cannot be re-entered from Changed callbacks.")
        {
        }
    }

    private sealed class Session
    {
        public Session(
            IDataSource source,
            SourceInfo sourceInfo,
            RuntimeSchemaRegistry registry,
            RuntimeMode mode,
            bool hotReloadEnabled,
            IDisposable? watchSubscription,
            IRuntimePublishPoint? publishPoint,
            IRuntimeProfiler? profiler,
            RuntimeReferenceServices referenceServices,
            string sourceRevision,
            string sourceContentHash,
            uint version,
            int ownerThreadId,
            Dictionary<AssetIdentity, Entry> entries,
            Dictionary<AssetLookupKey, Entry> keyIndex,
            Dictionary<Type, int> tableByType,
            Dictionary<int, Entry?[]> slots)
        {
            Source = source;
            SourceInfo = sourceInfo;
            Registry = registry;
            Mode = mode;
            HotReloadEnabled = hotReloadEnabled;
            WatchSubscription = watchSubscription;
            PublishPoint = publishPoint;
            Profiler = profiler;
            ReferenceServices = referenceServices;
            SourceRevision = sourceRevision;
            SourceContentHash = sourceContentHash;
            Version = version;
            OwnerThreadId = ownerThreadId;
            Entries = entries;
            KeyIndex = keyIndex;
            TableByType = tableByType;
            Slots = slots;
            Workspace = new SteadyRefreshWorkspace(entries.Count);
        }

        public IDataSource Source { get; set; }

        public SourceInfo SourceInfo { get; set; }

        public RuntimeSchemaRegistry Registry { get; }

        public RuntimeMode Mode { get; }

        public bool HotReloadEnabled { get; set; }

        public IDisposable? WatchSubscription { get; set; }

        public IRuntimePublishPoint? PublishPoint { get; }

        public IRuntimeProfiler? Profiler { get; }

        public RuntimeReferenceServices ReferenceServices { get; }

        public string SourceRevision { get; set; }

        public string SourceContentHash { get; set; }

        public uint Version { get; set; }

        public int OwnerThreadId { get; }

        public Dictionary<AssetIdentity, Entry> Entries { get; }

        public Dictionary<AssetLookupKey, Entry> KeyIndex { get; }

        public Dictionary<Type, int> TableByType { get; }

        public Dictionary<int, Entry?[]> Slots { get; }

        public SteadyRefreshWorkspace Workspace { get; }

        public RuntimeSchemaIdentity Identity =>
            new(Registry.ExpectedSchemaHash, Registry.ExpectedExportTarget);

        public Session WithWatch(IDisposable subscription)
        {
            WatchSubscription = subscription;
            HotReloadEnabled = true;
            return this;
        }

        public Session WithHotReload(IDisposable subscription) => WithWatch(subscription);

        public Session WithoutHotReload()
        {
            WatchSubscription = null;
            HotReloadEnabled = false;
            return this;
        }
    }

    private sealed class Entry
    {
        public Entry(
            RuntimeAssetRecord record,
            RuntimeTableBinding binding,
            object instance,
            RuntimeAssetState state,
            AssetKey handle)
        {
            Record = record;
            Binding = binding;
            Instance = instance;
            State = state;
            Handle = handle;
            ReusableRollbackState = binding.SupportsReusableRollbackState
                ? binding.CreateReusableRollbackState(instance)
                : null;
        }

        public RuntimeAssetRecord Record { get; set; }

        public RuntimeTableBinding Binding { get; set; }

        public object Instance { get; }

        public RuntimeAssetState State { get; set; }

        public AssetKey Handle { get; }

        public object? ReusableRollbackState { get; }

        public Entry Copy(
            RuntimeAssetRecord record,
            RuntimeTableBinding binding,
            RuntimeAssetState state) =>
            new(record, binding, Instance, state, Handle);
    }

    private sealed class SteadyRefreshWorkspace
    {
        private Entry?[] _entries = Array.Empty<Entry?>();
        private RuntimeAssetRecord?[] _candidates = Array.Empty<RuntimeAssetRecord?>();
        private object?[] _capturedStates = Array.Empty<object?>();
        private ChangeEvent[] _events = Array.Empty<ChangeEvent>();
        private AssetIdentity[] _dependencyQueue = Array.Empty<AssetIdentity>();

        public SteadyRefreshWorkspace(int assetCapacity) => EnsureCapacity(assetCapacity);

        public HashSet<AssetIdentity> Identities { get; } = new();

        public HashSet<AssetLookupKey> Keys { get; } = new();

        public HashSet<AssetIdentity> DependencyVisited { get; } = new();

        public int MutationCount { get; private set; }

        public int EventCount { get; private set; }

        public int DependencyQueueCount { get; private set; }

        public ChangeEvent[] Events => _events;

        public void EnsureCapacity(int assetCapacity)
        {
            var assets = Math.Max(1, assetCapacity);
            EnsureArrayCapacity(ref _entries, assets);
            EnsureArrayCapacity(ref _candidates, assets);
            EnsureArrayCapacity(ref _capturedStates, assets);
            EnsureArrayCapacity(ref _dependencyQueue, assets);
            EnsureArrayCapacity(ref _events, checked((assets * 7) + 1));
            Identities.EnsureCapacity(assets);
            Keys.EnsureCapacity(assets);
            DependencyVisited.EnsureCapacity(assets);
        }

        public void BeginValidation()
        {
            Identities.Clear();
            Keys.Clear();
        }

        public void BeginPlan()
        {
            MutationCount = 0;
            EventCount = 0;
            DependencyQueueCount = 0;
            DependencyVisited.Clear();
        }

        public void AddMutation(Entry entry, RuntimeAssetRecord candidate)
        {
            var index = MutationCount++;
            _entries[index] = entry;
            _candidates[index] = candidate;
            _capturedStates[index] = null;
        }

        public Entry GetMutationEntry(int index) => _entries[index]!;

        public RuntimeAssetRecord GetMutationCandidate(int index) => _candidates[index]!;

        public object? GetCapturedState(int index) => _capturedStates[index];

        public void SetCapturedState(int index, object state) => _capturedStates[index] = state;

        public void AddEvent(in ChangeEvent value)
        {
            for (var index = 0; index < EventCount; index++)
            {
                if (_events[index].Kind == value.Kind
                    && _events[index].AssetIdentity == value.AssetIdentity)
                {
                    return;
                }
            }

            _events[EventCount++] = value;
        }

        public bool EnqueueDependency(AssetIdentity identity)
        {
            if (!DependencyVisited.Add(identity))
                return false;
            _dependencyQueue[DependencyQueueCount++] = identity;
            return true;
        }

        public AssetIdentity GetDependency(int index) => _dependencyQueue[index];

        public void SortEvents()
        {
            for (var index = 1; index < EventCount; index++)
            {
                var value = _events[index];
                var insertion = index - 1;
                while (insertion >= 0
                    && ChangeEventComparer.Instance.Compare(_events[insertion], value) > 0)
                {
                    _events[insertion + 1] = _events[insertion];
                    insertion--;
                }
                _events[insertion + 1] = value;
            }
        }

        private static void EnsureArrayCapacity<T>(ref T[] buffer, int capacity)
        {
            if (buffer.Length >= capacity)
                return;
            var next = Math.Max(capacity, buffer.Length == 0 ? 4 : checked(buffer.Length * 2));
            Array.Resize(ref buffer, next);
        }
    }

    private sealed record Mutation(Entry Entry, MutationKind Kind, RuntimeAssetRecord? Candidate = null);

    private sealed record CapturedState(Entry Entry, object Value);

    private sealed record UpdatePlan(
        Session Next,
        IReadOnlyList<Mutation> Mutations,
        ImmutableArray<ChangeEvent> Events,
        IReadOnlyList<Entry> RecreatedPrevious);

    private enum MutationKind : byte
    {
        Apply = 0,
        Reset = 1,
    }

    private readonly record struct AssetLookupKey(int TableId, string Key);

    private readonly record struct ChangeEventIdentity(ChangeKind Kind, AssetIdentity Identity);

    private sealed class ChangeEventComparer : IComparer<ChangeEvent>
    {
        public static ChangeEventComparer Instance { get; } = new();

        public int Compare(ChangeEvent left, ChangeEvent right)
        {
            var leftKind = (byte)left.Kind;
            var rightKind = (byte)right.Kind;
            var kindComparison = leftKind < rightKind ? -1 : leftKind > rightKind ? 1 : 0;
            return kindComparison != 0
                ? kindComparison
                : AssetIdentityComparer.Instance.Compare(left.AssetIdentity, right.AssetIdentity);
        }
    }
}
