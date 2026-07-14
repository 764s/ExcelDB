namespace ExcelDb.Runtime.Tests;

public sealed class RuntimeHostingTests : IDisposable
{
    public RuntimeHostingTests() => RuntimeDatabase.Close();

    public void Dispose() => RuntimeDatabase.Close();

    [Fact]
    public void Watcher_RequestsHostPublishPoint_AndOnlyOwnerTickCommits()
    {
        var source = new TestWatchSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "before")]));
        var publishPoint = new TestPublishPoint();
        var options = new RuntimeBootstrapOptions(
            RuntimeMode.Development,
            enableHotReload: true,
            publishPoint);

        Assert.True(RuntimeDatabase.Open(
            source,
            new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client),
            options));
        source.SetSnapshot(RuntimeTestData.Snapshot(
            [RuntimeTestData.Asset(1, "one", "after")],
            revision: "r2",
            contentHash: "content-2"));

        source.Signal();

        Assert.Equal(1, publishPoint.RequestCount);
        Assert.True(RuntimeDatabase.HasPendingHotReload);
        Assert.Equal("before", RuntimeDatabase.LoadAsset<TestAsset>("one")!.Name);

        publishPoint.IsOwnerContext = false;
        Assert.Throws<InvalidOperationException>(() => RuntimeDatabase.ProcessPendingHotReload());
        Assert.True(RuntimeDatabase.HasPendingHotReload);

        publishPoint.IsOwnerContext = true;
        Assert.True(RuntimeDatabase.ProcessPendingHotReload());
        Assert.Equal("after", RuntimeDatabase.LoadAsset<TestAsset>("one")!.Name);
    }

    [Fact]
    public void Profiler_ReceivesBalancedReadCommitAndDispatchMarkers()
    {
        var profiler = new TestProfiler();
        var source = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "before")]));
        Action<ChangeSet> handler = static _ => { };
        RuntimeDatabase.Changed += handler;
        try
        {
            Assert.True(RuntimeDatabase.Open(
                source,
                new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client),
                new RuntimeBootstrapOptions(RuntimeMode.Development, false, profiler: profiler)));
            Assert.NotNull(RuntimeDatabase.LoadAsset<TestAsset>("one"));

            source.SetSnapshot(RuntimeTestData.Snapshot(
                [RuntimeTestData.Asset(1, "one", "after")],
                revision: "r2",
                contentHash: "content-2"));
            Assert.True(RuntimeDatabase.Refresh());
        }
        finally
        {
            RuntimeDatabase.Changed -= handler;
        }

        Assert.Contains(RuntimeProfileMarker.CandidateRead, profiler.Begun);
        Assert.Contains(RuntimeProfileMarker.Validate, profiler.Begun);
        Assert.Contains(RuntimeProfileMarker.Diff, profiler.Begun);
        Assert.Contains(RuntimeProfileMarker.Commit, profiler.Begun);
        Assert.Contains(RuntimeProfileMarker.Publish, profiler.Begun);
        Assert.Contains(RuntimeProfileMarker.ChangedDispatch, profiler.Begun);
        Assert.Contains(RuntimeProfileMarker.Read, profiler.Begun);
        foreach (var marker in Enum.GetValues<RuntimeProfileMarker>())
        {
            Assert.Equal(
                profiler.Begun.Count(item => item == marker),
                profiler.Ended.Count(item => item == marker));
        }
    }

    private sealed class TestPublishPoint : IRuntimePublishPoint
    {
        public bool IsOwnerContext { get; set; } = true;

        public int RequestCount { get; private set; }

        public void RequestPublish() => RequestCount++;
    }

    private sealed class TestProfiler : IRuntimeProfiler
    {
        public List<RuntimeProfileMarker> Begun { get; } = [];

        public List<RuntimeProfileMarker> Ended { get; } = [];

        public void Begin(RuntimeProfileMarker marker) => Begun.Add(marker);

        public void End(RuntimeProfileMarker marker) => Ended.Add(marker);
    }
}
