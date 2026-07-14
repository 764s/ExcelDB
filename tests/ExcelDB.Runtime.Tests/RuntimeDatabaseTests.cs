using ExcelDb.Core.Identity;

namespace ExcelDb.Runtime.Tests;

public sealed class RuntimeDatabaseTests : IDisposable
{
    public RuntimeDatabaseTests()
    {
        if (RuntimeDatabase.IsOpen)
            RuntimeDatabase.Close();
    }

    public void Dispose()
    {
        if (RuntimeDatabase.IsOpen)
            RuntimeDatabase.Close();
    }

    [Fact]
    public void Open_ReportsExpectedDeclaredAndActualHashTargetMismatches()
    {
        var registry = new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client);
        var snapshot = RuntimeTestData.Snapshot(
            [RuntimeTestData.Asset(1, "one", "one")],
            schemaHash: RuntimeTestData.SchemaHash + 2,
            target: RuntimeTestData.Client);
        var source = new TestSource(
            RuntimeTestData.SchemaHash + 1,
            RuntimeTestData.Server,
            snapshot);

        var opened = RuntimeDatabase.Open(
            source,
            registry,
            new RuntimeBootstrapOptions(RuntimeMode.EditorAuthoring, false));

        Assert.False(opened);
        Assert.False(RuntimeDatabase.IsOpen);
        var report = RuntimeDatabase.LastReport;
        Assert.Equal(RuntimeTestData.SchemaHash, report.Expected!.Value.SchemaHash);
        Assert.Equal(RuntimeTestData.SchemaHash + 1, report.Declared!.Value.SchemaHash);
        Assert.Equal(RuntimeTestData.SchemaHash + 2, report.Actual!.Value.SchemaHash);
        Assert.Contains(report.Diagnostics, item => item.Code == RuntimeDiagnosticCodes.SchemaHashMismatch);
        Assert.Contains(report.Diagnostics, item => item.Code == RuntimeDiagnosticCodes.ExportTargetMismatch);
    }

    [Fact]
    public void SwitchRefreshAndResurrection_PreserveCanonicalInstanceAndHandle()
    {
        var registry = new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client);
        var initialSource = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "initial", 1)]));
        Assert.True(RuntimeDatabase.Open(
            initialSource,
            registry,
            new RuntimeBootstrapOptions(RuntimeMode.EditorAuthoring, false)));
        Assert.True(RuntimeDatabase.TryGetAssetKey<TestAsset>("one", out var originalHandle));
        Assert.True(RuntimeDatabase.TryGetAsset(originalHandle, out TestAsset? original));

        var switchedSource = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot(
                [RuntimeTestData.Asset(1, "one", "switched", 2)],
                revision: "r2",
                contentHash: "content-2"));
        Assert.True(RuntimeDatabase.SwitchDataSource(switchedSource));
        Assert.Same(original, RuntimeDatabase.LoadAsset<TestAsset>("one"));
        Assert.Equal("switched", original!.Name);

        switchedSource.SetSnapshot(RuntimeTestData.Snapshot([], revision: "r3", contentHash: "content-3"));
        Assert.True(RuntimeDatabase.Refresh());
        Assert.False(RuntimeDatabase.TryGetAsset<TestAsset>(originalHandle, out _));
        Assert.True(RuntimeDatabase.TryGetAssetState(original, out var missingState));
        Assert.Equal(RuntimeAssetState.Missing, missingState);
        Assert.Equal(string.Empty, original.Name);

        switchedSource.SetSnapshot(RuntimeTestData.Snapshot(
            [RuntimeTestData.Asset(1, "one", "returned", 3)],
            revision: "r4",
            contentHash: "content-4"));
        Assert.True(RuntimeDatabase.Refresh());
        Assert.True(RuntimeDatabase.TryGetAssetKey<TestAsset>("one", out var returnedHandle));
        Assert.Equal(originalHandle, returnedHandle);
        Assert.Same(original, RuntimeDatabase.LoadAsset<TestAsset>("one"));
        Assert.Equal("returned", original.Name);
        Assert.True(RuntimeDatabase.TryGetAssetState(original, out var residentState));
        Assert.Equal(RuntimeAssetState.Resident, residentState);
    }

    [Fact]
    public void CommitFailure_RestoresObjectAndRetainsOldSession()
    {
        var source = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "stable", 7)]));
        Assert.True(RuntimeDatabase.Open(
            source,
            new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client),
            new RuntimeBootstrapOptions(RuntimeMode.EditorAuthoring, false)));
        var original = Assert.IsType<TestAsset>(RuntimeDatabase.LoadAsset<TestAsset>("one"));

        source.SetSnapshot(RuntimeTestData.Snapshot(
            [RuntimeTestData.Asset(1, "one", "throw", 99)],
            revision: "bad",
            contentHash: "bad"));
        Assert.False(RuntimeDatabase.Refresh());

        Assert.Same(original, RuntimeDatabase.LoadAsset<TestAsset>("one"));
        Assert.Equal("stable", original.Name);
        Assert.Equal(7, original.Value);
        Assert.Contains(
            RuntimeDatabase.LastDiagnostics,
            item => item.Code == RuntimeDiagnosticCodes.CommitFailure);

        source.SetSnapshot(RuntimeTestData.Snapshot(
            [RuntimeTestData.Asset(1, "one", "recovered", 8)],
            revision: "good",
            contentHash: "good"));
        Assert.True(RuntimeDatabase.Refresh());
        Assert.Same(original, RuntimeDatabase.LoadAsset<TestAsset>("one"));
        Assert.Equal("recovered", original.Name);
    }

    [Fact]
    public void SwitchHashAndTargetMismatch_PreservesOldSourceObjectAndEvents()
    {
        var source = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "stable", 7)]));
        var registry = new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client);
        Assert.True(RuntimeDatabase.Open(
            source,
            registry,
            new RuntimeBootstrapOptions(RuntimeMode.EditorAuthoring, false)));
        var original = Assert.IsType<TestAsset>(RuntimeDatabase.LoadAsset<TestAsset>("one"));

        var published = false;
        Action<ChangeSet> handler = _ => published = true;
        RuntimeDatabase.Changed += handler;
        try
        {
            var mismatched = new TestSource(
                RuntimeTestData.SchemaHash + 1,
                RuntimeTestData.Server,
                RuntimeTestData.Snapshot(
                    [RuntimeTestData.Asset(1, "one", "rejected", 99)],
                    schemaHash: RuntimeTestData.SchemaHash + 1,
                    target: RuntimeTestData.Server));
            Assert.False(RuntimeDatabase.SwitchDataSource(mismatched));
        }
        finally
        {
            RuntimeDatabase.Changed -= handler;
        }

        Assert.False(published);
        Assert.Same(original, RuntimeDatabase.LoadAsset<TestAsset>("one"));
        Assert.Equal("stable", original.Name);
        Assert.Equal(RuntimeTestData.Client, RuntimeDatabase.ExportTarget);
        Assert.Contains(
            RuntimeDatabase.LastDiagnostics,
            item => item.Code == RuntimeDiagnosticCodes.SchemaHashMismatch);
        Assert.Contains(
            RuntimeDatabase.LastDiagnostics,
            item => item.Code == RuntimeDiagnosticCodes.ExportTargetMismatch);
    }

    [Fact]
    public void RecreatedAsset_IncrementsGenerationAndInvalidatesOldObject()
    {
        var source = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "before")]));
        Assert.True(RuntimeDatabase.Open(
            source,
            new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client),
            new RuntimeBootstrapOptions(RuntimeMode.EditorAuthoring, false)));
        Assert.True(RuntimeDatabase.TryGetAssetKey<TestAsset>("one", out var oldHandle));
        var oldObject = Assert.IsType<TestAsset>(RuntimeDatabase.LoadAsset<TestAsset>("one"));

        ChangeSet observed = default;
        Action<ChangeSet> handler = value => observed = value;
        RuntimeDatabase.Changed += handler;
        try
        {
            source.SetSnapshot(RuntimeTestData.Snapshot(
                [RuntimeTestData.Asset(1, "one", "after", recreate: true)],
                revision: "r2",
                contentHash: "c2"));
            Assert.True(RuntimeDatabase.Refresh());
        }
        finally
        {
            RuntimeDatabase.Changed -= handler;
        }

        Assert.True(RuntimeDatabase.TryGetAssetKey<TestAsset>("one", out var newHandle));
        Assert.Equal(oldHandle.Slot, newHandle.Slot);
        Assert.NotEqual(oldHandle.Generation, newHandle.Generation);
        Assert.False(RuntimeDatabase.TryGetAsset<TestAsset>(oldHandle, out _));
        Assert.NotSame(oldObject, RuntimeDatabase.LoadAsset<TestAsset>("one"));
        Assert.True(RuntimeDatabase.TryGetAssetState(oldObject, out var oldState));
        Assert.Equal(RuntimeAssetState.Missing, oldState);
        Assert.Contains(observed.Events, item => item.Kind == ChangeKind.Recreated);
    }

    [Fact]
    public void ChangeSet_IsDeterministicallyOrderedAndRejectsReentrantWrites()
    {
        var source = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot(
            [
                RuntimeTestData.Asset(1, "one", "old", path: "book/a/one"),
                RuntimeTestData.Asset(2, "two", "two"),
            ]));
        Assert.True(RuntimeDatabase.Open(
            source,
            new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client),
            new RuntimeBootstrapOptions(RuntimeMode.EditorAuthoring, false)));

        ChangeSet observed = default;
        Action<ChangeSet> handler = value =>
        {
            observed = value;
            Assert.Equal("new", RuntimeDatabase.LoadAsset<TestAsset>("renamed")!.Name);
            Assert.Throws<InvalidOperationException>(() => RuntimeDatabase.Refresh());
        };
        RuntimeDatabase.Changed += handler;
        try
        {
            source.SetSnapshot(RuntimeTestData.Snapshot(
            [
                RuntimeTestData.Asset(1, "renamed", "new", path: "book/b/renamed"),
                RuntimeTestData.Asset(3, "three", "three"),
            ], revision: "r2", contentHash: "c2"));
            Assert.True(RuntimeDatabase.Refresh());
        }
        finally
        {
            RuntimeDatabase.Changed -= handler;
        }

        Assert.Equal(
            [
                ChangeKind.Removed,
                ChangeKind.Added,
                ChangeKind.Moved,
                ChangeKind.Renamed,
                ChangeKind.PropertyChanged,
            ],
            observed.Events.Select(static item => item.Kind).ToArray());
    }

    [Fact]
    public void Release_RejectsExcelSourceWithoutChangingState()
    {
        var source = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "one")]),
            RuntimeSourceKind.Excel);

        Assert.False(RuntimeDatabase.Open(
            source,
            new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client),
            new RuntimeBootstrapOptions(RuntimeMode.Release, false)));
        Assert.False(RuntimeDatabase.IsOpen);
        Assert.Contains(
            RuntimeDatabase.LastDiagnostics,
            item => item.Code == RuntimeDiagnosticCodes.SourceRejected);
    }

    [Fact]
    public void Watcher_OnlyQueuesWorkUntilOwnerProcessesPendingHotReload()
    {
        var source = new TestWatchSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "initial")]));
        Assert.True(RuntimeDatabase.Open(
            source,
            new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client),
            new RuntimeBootstrapOptions(RuntimeMode.EditorAuthoring, true)));

        source.SetSnapshot(RuntimeTestData.Snapshot(
            [RuntimeTestData.Asset(1, "one", "updated")],
            revision: "r2",
            contentHash: "c2"));
        source.Signal();

        Assert.True(RuntimeDatabase.HasPendingHotReload);
        Assert.Equal("initial", RuntimeDatabase.LoadAsset<TestAsset>("one")!.Name);
        Assert.True(RuntimeDatabase.ProcessPendingHotReload());
        Assert.False(RuntimeDatabase.HasPendingHotReload);
        Assert.Equal("updated", RuntimeDatabase.LoadAsset<TestAsset>("one")!.Name);

        Assert.True(RuntimeDatabase.TryDisableHotReload());
        source.SetSnapshot(RuntimeTestData.Snapshot(
            [RuntimeTestData.Asset(1, "one", "ignored")],
            revision: "r3",
            contentHash: "c3"));
        source.Signal();
        Assert.False(RuntimeDatabase.HasPendingHotReload);
    }

    [Fact]
    public void Close_InvalidatesOldHandlesAcrossTheNextSession()
    {
        var registry = new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client);
        var firstSource = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "first")]));
        Assert.True(RuntimeDatabase.Open(
            firstSource,
            registry,
            new RuntimeBootstrapOptions(RuntimeMode.EditorAuthoring, false)));
        Assert.True(RuntimeDatabase.TryGetAssetKey<TestAsset>("one", out var oldHandle));
        var oldObject = Assert.IsType<TestAsset>(RuntimeDatabase.LoadAsset<TestAsset>("one"));

        RuntimeDatabase.Close();
        Assert.False(RuntimeDatabase.TryGetAsset<TestAsset>(oldHandle, out _));
        Assert.True(RuntimeDatabase.TryGetAssetState(oldObject, out var closedState));
        Assert.Equal(RuntimeAssetState.Missing, closedState);

        var secondSource = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(2, "two", "second")]));
        Assert.True(RuntimeDatabase.Open(
            secondSource,
            registry,
            new RuntimeBootstrapOptions(RuntimeMode.EditorAuthoring, false)));

        Assert.False(RuntimeDatabase.TryGetAsset<TestAsset>(oldHandle, out _));
        Assert.True(RuntimeDatabase.TryGetAssetKey<TestAsset>("two", out var newHandle));
        Assert.NotEqual(oldHandle.Generation, newHandle.Generation);
    }

    [Fact]
    public void PendingIdentity_IsAlwaysABlocker()
    {
        var source = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([new RuntimeAssetRecord(default, "pending")]));

        Assert.False(RuntimeDatabase.Open(
            source,
            new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client),
            new RuntimeBootstrapOptions(RuntimeMode.EditorAuthoring, false)));
        Assert.Contains(
            RuntimeDatabase.LastDiagnostics,
            item => item.Code == RuntimeDiagnosticCodes.PendingIdentity && item.IsBlocker);
    }
}
