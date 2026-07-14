using ExcelDb.Runtime.References;

namespace ExcelDb.Runtime.Tests;

public sealed class RuntimeAllocationWaterlineTests : IDisposable
{
    private static readonly ExcelDb.Core.Identity.AssetIdentity TargetIdentity = RuntimeTestData.Identity(1);
    private static readonly ExcelDb.Core.Identity.AssetIdentity DependentIdentity = RuntimeTestData.Identity(2);

    public RuntimeAllocationWaterlineTests() => RuntimeDatabase.Close();

    public void Dispose() => RuntimeDatabase.Close();

    [Fact]
    public void PrewarmedReadPaths_DoNotGrowAllocationWithOperationCount()
    {
        var profiler = new CountingProfiler();
        var source = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "value")]));
        Assert.True(RuntimeDatabase.Open(
            source,
            new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client),
            new RuntimeBootstrapOptions(RuntimeMode.Release, false, profiler: profiler)));
        RuntimeDatabase.Prewarm();
        Assert.True(RuntimeDatabase.TryGetAssetKey<TestAsset>("one", out var handle));
        var buffer = new TestAsset[1];

        RunReads(handle, buffer, 2_048);
        var shortBatch = MeasureReads(handle, buffer, 10_000);
        var longBatch = MeasureReads(handle, buffer, 20_000);

        Assert.Equal(0, shortBatch);
        Assert.Equal(shortBatch, longBatch);
        Assert.Equal(profiler.BeginCount, profiler.EndCount);
        Assert.True(profiler.BeginCount > 0);
    }

    [Fact]
    public void PreboundProviderHandles_DoNotAllocateWhileResolving()
    {
        var target = new object();
        var unityProvider = new AllocationUnityProvider(target);
        Assert.True(BoundUnityAsset.TryBind(
            unityProvider,
            new UnityAssetReference("0123456789abcdef0123456789abcdef"),
            out var unity));

        var family = new OrdinalReferenceFamily<object>(
            "allocation.test",
            1,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["target"] = 7 },
            new Dictionary<int, object> { [7] = target });
        var families = new ReferenceFamilyRegistry([family]);
        Assert.True(families.TryBind("allocation.test", "target", out var custom, out var error), error);

        RunProviderResolves(unity, custom, target, 2_048);
        var shortBatch = MeasureProviderResolves(unity, custom, target, 10_000);
        var longBatch = MeasureProviderResolves(unity, custom, target, 20_000);

        Assert.Equal(0, shortBatch);
        Assert.Equal(shortBatch, longBatch);
    }

    [Fact]
    public void PrewarmedNoOpRefresh_HasZeroAllocation()
    {
        var source = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "value")]));
        Assert.True(RuntimeDatabase.Open(
            source,
            new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client),
            new RuntimeBootstrapOptions(RuntimeMode.Development, false)));

        RunRefreshes(8);
        var shortBatch = MeasureRefreshes(8);
        var longBatch = MeasureRefreshes(16);
        Assert.Equal(0, shortBatch);
        Assert.Equal(shortBatch, longBatch);
    }

    [Fact]
    public void SteadyRealChange_RefreshCommitChangeSetAndDispatch_HaveZeroAllocation()
    {
        var first = NoGcSnapshot(1, "allocation-a", "allocation-a");
        var second = NoGcSnapshot(2, "allocation-b", "allocation-b");
        var source = new AlternatingSource(first, second);
        var observer = new NoGcChangeObserver();
        var profiler = new AllocationProfiler();
        Assert.True(RuntimeDatabase.Open(
            source,
            new NoGcRegistry(),
            new RuntimeBootstrapOptions(RuntimeMode.Development, false, profiler: profiler)));
        RuntimeDatabase.Changed += observer.OnChanged;
        try
        {
            RuntimeDatabase.Prewarm();

            RunRealChanges(2_048);
            profiler.Reset();
            var shortBatch = MeasureRealChanges(10_000);
            var longBatch = MeasureRealChanges(20_000);

            Assert.True(
                shortBatch == 0,
                $"Allocated {shortBatch}: candidate={profiler[RuntimeProfileMarker.CandidateRead]}, "
                + $"validate={profiler[RuntimeProfileMarker.Validate]}, diff={profiler[RuntimeProfileMarker.Diff]}, "
                + $"commit={profiler[RuntimeProfileMarker.Commit]}, publish={profiler[RuntimeProfileMarker.Publish]}, "
                + $"dispatch={profiler[RuntimeProfileMarker.ChangedDispatch]}; "
                + $"gaps candidate={profiler.Gap(RuntimeProfileMarker.CandidateRead)}, "
                + $"validate={profiler.Gap(RuntimeProfileMarker.Validate)}, diff={profiler.Gap(RuntimeProfileMarker.Diff)}, "
                + $"commit={profiler.Gap(RuntimeProfileMarker.Commit)}, publish={profiler.Gap(RuntimeProfileMarker.Publish)}, "
                + $"dispatch={profiler.Gap(RuntimeProfileMarker.ChangedDispatch)}.");
            Assert.Equal(shortBatch, longBatch);
            Assert.Equal(32_048, observer.Count);
            Assert.Equal(ChangeKind.PropertyChanged, observer.LastKind);
        }
        finally
        {
            RuntimeDatabase.Changed -= observer.OnChanged;
        }
    }

    [Fact]
    public void SteadyRealChange_SourceSwitchCommitAndDispatch_HaveZeroAllocation()
    {
        var first = new StaticSource(NoGcSnapshot(1, "switch-a", "switch-a"));
        var second = new StaticSource(NoGcSnapshot(2, "switch-b", "switch-b"));
        var observer = new NoGcChangeObserver();
        Assert.True(RuntimeDatabase.Open(
            first,
            new NoGcRegistry(),
            new RuntimeBootstrapOptions(RuntimeMode.Development, false)));
        RuntimeDatabase.Changed += observer.OnChanged;
        try
        {
            RuntimeDatabase.Prewarm();
            RunSwitches(first, second, 2_048);
            var shortBatch = MeasureSwitches(first, second, 10_000);
            var longBatch = MeasureSwitches(first, second, 20_000);

            Assert.Equal(0, shortBatch);
            Assert.Equal(shortBatch, longBatch);
            Assert.Equal(32_048, observer.Count);
            Assert.Equal(ChangeKind.PropertyChanged, observer.LastKind);
        }
        finally
        {
            RuntimeDatabase.Changed -= observer.OnChanged;
        }
    }

    [Fact]
    public void SteadyRealChange_HotReloadPatchAndDispatch_HaveZeroAllocation()
    {
        var source = new AlternatingWatchSource(
            NoGcSnapshot(1, "watch-a", "watch-a"),
            NoGcSnapshot(2, "watch-b", "watch-b"));
        var observer = new NoGcChangeObserver();
        Assert.True(RuntimeDatabase.Open(
            source,
            new NoGcRegistry(),
            new RuntimeBootstrapOptions(RuntimeMode.Development, true)));
        RuntimeDatabase.Changed += observer.OnChanged;
        try
        {
            RuntimeDatabase.Prewarm();
            RunHotReloads(source, 2_048);
            var shortBatch = MeasureHotReloads(source, 10_000);
            var longBatch = MeasureHotReloads(source, 20_000);

            Assert.Equal(0, shortBatch);
            Assert.Equal(shortBatch, longBatch);
            Assert.Equal(32_048, observer.Count);
            Assert.Equal(ChangeKind.PropertyChanged, observer.LastKind);
        }
        finally
        {
            RuntimeDatabase.Changed -= observer.OnChanged;
        }
    }

    [Fact]
    public void SteadyDependencyClosureChangeSet_HasZeroAllocationAndStableOrder()
    {
        var source = new AlternatingSource(
            NoGcDependencySnapshot(1, "dependency-a", "dependency-a"),
            NoGcDependencySnapshot(2, "dependency-b", "dependency-b"));
        var observer = new NoGcDependencyObserver();
        var profiler = new AllocationProfiler();
        Assert.True(RuntimeDatabase.Open(
            source,
            new NoGcRegistry(),
            new RuntimeBootstrapOptions(RuntimeMode.Development, false, profiler: profiler)));
        RuntimeDatabase.Changed += observer.OnChanged;
        try
        {
            RuntimeDatabase.Prewarm();
            RunRealChanges(2_048);
            profiler.Reset();
            var shortBatch = MeasureRealChanges(10_000);
            var longBatch = MeasureRealChanges(20_000);

            Assert.True(
                shortBatch == 0,
                $"Allocated {shortBatch}: candidate={profiler[RuntimeProfileMarker.CandidateRead]}, "
                + $"validate={profiler[RuntimeProfileMarker.Validate]}, diff={profiler[RuntimeProfileMarker.Diff]}, "
                + $"commit={profiler[RuntimeProfileMarker.Commit]}, publish={profiler[RuntimeProfileMarker.Publish]}, "
                + $"dispatch={profiler[RuntimeProfileMarker.ChangedDispatch]}; "
                + $"gaps candidate={profiler.Gap(RuntimeProfileMarker.CandidateRead)}, "
                + $"validate={profiler.Gap(RuntimeProfileMarker.Validate)}, diff={profiler.Gap(RuntimeProfileMarker.Diff)}, "
                + $"commit={profiler.Gap(RuntimeProfileMarker.Commit)}, publish={profiler.Gap(RuntimeProfileMarker.Publish)}, "
                + $"dispatch={profiler.Gap(RuntimeProfileMarker.ChangedDispatch)}.");
            Assert.Equal(shortBatch, longBatch);
            Assert.Equal(32_048, observer.Count);
        }
        finally
        {
            RuntimeDatabase.Changed -= observer.OnChanged;
        }
    }

    private static long MeasureReads(
        ExcelDb.Core.Identity.AssetKey handle,
        TestAsset[] buffer,
        int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        RunReads(handle, buffer, iterations);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void RunReads(
        ExcelDb.Core.Identity.AssetKey handle,
        TestAsset[] buffer,
        int iterations)
    {
        for (var index = 0; index < iterations; index++)
        {
            if (!RuntimeDatabase.TryGetAssetKey<TestAsset>("one", out var lookup)
                || lookup != handle
                || !RuntimeDatabase.TryGetAsset(handle, out TestAsset? asset)
                || asset is null
                || RuntimeDatabase.GetAssets(buffer, out var count) != RuntimeQueryStatus.Success
                || count != 1)
            {
                throw new InvalidOperationException("The allocation probe observed an invalid runtime read.");
            }
        }
    }

    private static long MeasureProviderResolves(
        BoundUnityAsset unity,
        BoundReference custom,
        object expected,
        int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        RunProviderResolves(unity, custom, expected, iterations);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void RunProviderResolves(
        BoundUnityAsset unity,
        BoundReference custom,
        object expected,
        int iterations)
    {
        for (var index = 0; index < iterations; index++)
        {
            if (!unity.TryResolve<object>(out var unityValue)
                || !ReferenceEquals(unityValue, expected)
                || !custom.TryResolve<object>(out var customValue)
                || !ReferenceEquals(customValue, expected))
            {
                throw new InvalidOperationException("The allocation probe observed an invalid provider result.");
            }
        }
    }

    private static long MeasureRefreshes(int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        RunRefreshes(iterations);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void RunRefreshes(int iterations)
    {
        for (var index = 0; index < iterations; index++)
        {
            RuntimeDatabase.Refresh();
            if (!RuntimeDatabase.LastReport.Succeeded)
                throw new InvalidOperationException("The allocation probe observed a failed refresh.");
        }
    }

    private static long MeasureRealChanges(int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        RunRealChanges(iterations);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void RunRealChanges(int iterations)
    {
        for (var index = 0; index < iterations; index++)
        {
            if (!RuntimeDatabase.Refresh())
                throw new InvalidOperationException("The real-change allocation probe observed a no-op or failure.");
        }
    }

    private static long MeasureSwitches(
        IDataSource first,
        IDataSource second,
        int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        RunSwitches(first, second, iterations);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void RunSwitches(IDataSource first, IDataSource second, int iterations)
    {
        for (var index = 0; index < iterations; index++)
        {
            var source = (index & 1) == 0 ? second : first;
            if (!RuntimeDatabase.SwitchDataSource(source))
                throw new InvalidOperationException("The source-switch allocation probe failed.");
        }
    }

    private static long MeasureHotReloads(AlternatingWatchSource source, int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        RunHotReloads(source, iterations);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void RunHotReloads(AlternatingWatchSource source, int iterations)
    {
        for (var index = 0; index < iterations; index++)
        {
            source.Signal();
            if (!RuntimeDatabase.ProcessPendingHotReload())
                throw new InvalidOperationException("The hot-reload allocation probe failed.");
        }
    }

    private static SourceSnapshot NoGcSnapshot(byte value, string revision, string contentHash) =>
        new(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            revision,
            contentHash,
            [
                new RuntimeAssetRecord(
                    TargetIdentity,
                    "one",
                    [new RuntimeFieldValue(1, [value])])
            ]);

    private static SourceSnapshot NoGcDependencySnapshot(
        byte targetValue,
        string revision,
        string contentHash) =>
        new(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            revision,
            contentHash,
            [
                new RuntimeAssetRecord(
                    TargetIdentity,
                    "target",
                    [new RuntimeFieldValue(1, [targetValue])]),
                new RuntimeAssetRecord(
                    DependentIdentity,
                    "dependent",
                    [new RuntimeFieldValue(1, [9])],
                    [TargetIdentity])
            ]);

    private sealed class CountingProfiler : IRuntimeProfiler
    {
        public int BeginCount { get; private set; }

        public int EndCount { get; private set; }

        public void Begin(RuntimeProfileMarker marker) => BeginCount++;

        public void End(RuntimeProfileMarker marker) => EndCount++;
    }

    private sealed class AllocationUnityProvider(object target) : IUnityAssetProvider
    {
        public string Id => "allocation.unity";

        public bool TryBind(in UnityAssetReference reference, out UnityAssetProviderKey key)
        {
            key = new UnityAssetProviderKey(7);
            return true;
        }

        public bool TryResolve<T>(in UnityAssetProviderKey key, out T? asset)
            where T : class
        {
            asset = key.Value == 7 ? target as T : null;
            return asset is not null;
        }
    }

    private sealed class NoGcAsset
    {
        public byte Value { get; set; }
    }

    private sealed class NoGcState
    {
        public byte Value { get; set; }
    }

    private sealed class NoGcRegistry : RuntimeSchemaRegistry
    {
        public NoGcRegistry()
            : base(
                RuntimeTestData.SchemaHash,
                RuntimeTestData.Client,
                [
                    new RuntimeTableBinding<NoGcAsset>(
                        RuntimeTestData.TableId,
                        static () => new NoGcAsset(),
                        static (asset, record) => asset.Value = record.Fields[0].Data.Span[0],
                        static asset => asset.Value = 0,
                        static _ => new NoGcState(),
                        static (asset, state) => ((NoGcState)state).Value = asset.Value,
                        static (asset, state) => asset.Value = ((NoGcState)state).Value)
                ])
        {
        }
    }

    private sealed class AlternatingSource : IRefreshableDataSource
    {
        private readonly SourceSnapshot[] _snapshots;
        private int _next;

        public AlternatingSource(SourceSnapshot first, SourceSnapshot second)
        {
            _snapshots = [first, second];
        }

        public ulong SchemaHash => RuntimeTestData.SchemaHash;

        public ExcelDb.Core.Identity.ExportTargetId ExportTarget => RuntimeTestData.Client;

        public SourceInfo Inspect()
        {
            var snapshot = _snapshots[_next];
            return new SourceInfo(
                RuntimeSourceKind.Custom,
                RuntimeSourceCapabilities.Read | RuntimeSourceCapabilities.Refresh,
                snapshot.FormatVersion,
                snapshot.Revision,
                snapshot.ContentHash);
        }

        public SourceSnapshot Open()
        {
            var snapshot = _snapshots[0];
            _next = 1;
            return snapshot;
        }

        public SourceSnapshot Refresh()
        {
            var snapshot = _snapshots[_next];
            _next ^= 1;
            return snapshot;
        }
    }

    private sealed class StaticSource(SourceSnapshot snapshot) : IDataSource
    {
        public ulong SchemaHash => RuntimeTestData.SchemaHash;

        public ExcelDb.Core.Identity.ExportTargetId ExportTarget => RuntimeTestData.Client;

        public SourceInfo Inspect() => new(
            RuntimeSourceKind.Custom,
            RuntimeSourceCapabilities.Read,
            snapshot.FormatVersion,
            snapshot.Revision,
            snapshot.ContentHash);

        public SourceSnapshot Open() => snapshot;
    }

    private sealed class AlternatingWatchSource : IWatchableDataSource
    {
        private readonly SourceSnapshot[] _snapshots;
        private Action? _changed;
        private int _next;

        public AlternatingWatchSource(SourceSnapshot first, SourceSnapshot second)
        {
            _snapshots = [first, second];
        }

        public ulong SchemaHash => RuntimeTestData.SchemaHash;

        public ExcelDb.Core.Identity.ExportTargetId ExportTarget => RuntimeTestData.Client;

        public SourceInfo Inspect()
        {
            var snapshot = _snapshots[_next];
            return new SourceInfo(
                RuntimeSourceKind.Custom,
                RuntimeSourceCapabilities.Read
                    | RuntimeSourceCapabilities.Refresh
                    | RuntimeSourceCapabilities.Watch,
                snapshot.FormatVersion,
                snapshot.Revision,
                snapshot.ContentHash);
        }

        public SourceSnapshot Open()
        {
            var snapshot = _snapshots[0];
            _next = 1;
            return snapshot;
        }

        public SourceSnapshot Refresh()
        {
            var snapshot = _snapshots[_next];
            _next ^= 1;
            return snapshot;
        }

        public IDisposable Watch(Action sourceChanged)
        {
            _changed = sourceChanged;
            return new WatchSubscription(this);
        }

        public void Signal() => _changed?.Invoke();

        private sealed class WatchSubscription(AlternatingWatchSource owner) : IDisposable
        {
            public void Dispose() => owner._changed = null;
        }
    }

    private sealed class NoGcChangeObserver
    {
        public int Count { get; private set; }

        public ChangeKind LastKind { get; private set; }

        public void OnChanged(ChangeSet changeSet)
        {
            if (changeSet.Events.Count != 1)
                throw new InvalidOperationException("Expected exactly one coalesced property event.");
            LastKind = changeSet.Events[0].Kind;
            Count++;
        }
    }

    private sealed class NoGcDependencyObserver
    {
        public int Count { get; private set; }

        public void OnChanged(ChangeSet changeSet)
        {
            if (changeSet.Events.Count != 2
                || changeSet.Events[0].Kind != ChangeKind.PropertyChanged
                || changeSet.Events[0].AssetIdentity != TargetIdentity
                || changeSet.Events[1].Kind != ChangeKind.DependencyChanged
                || changeSet.Events[1].AssetIdentity != DependentIdentity)
            {
                throw new InvalidOperationException("The dependency closure ChangeSet is invalid or unsorted.");
            }
            Count++;
        }
    }

    private sealed class AllocationProfiler : IRuntimeProfiler
    {
        private readonly long[] _began = new long[Enum.GetValues<RuntimeProfileMarker>().Length];
        private readonly long[] _allocated = new long[Enum.GetValues<RuntimeProfileMarker>().Length];
        private readonly long[] _gaps = new long[Enum.GetValues<RuntimeProfileMarker>().Length];
        private long _lastBoundary;

        public long this[RuntimeProfileMarker marker] => _allocated[(int)marker];

        public long Gap(RuntimeProfileMarker marker) => _gaps[(int)marker];

        public void Begin(RuntimeProfileMarker marker)
        {
            var now = GC.GetAllocatedBytesForCurrentThread();
            _gaps[(int)marker] += now - _lastBoundary;
            _began[(int)marker] = now;
            _lastBoundary = now;
        }

        public void End(RuntimeProfileMarker marker)
        {
            var now = GC.GetAllocatedBytesForCurrentThread();
            _allocated[(int)marker] += now - _began[(int)marker];
            _lastBoundary = now;
        }

        public void Reset()
        {
            Array.Clear(_allocated);
            Array.Clear(_gaps);
            _lastBoundary = GC.GetAllocatedBytesForCurrentThread();
        }
    }
}
