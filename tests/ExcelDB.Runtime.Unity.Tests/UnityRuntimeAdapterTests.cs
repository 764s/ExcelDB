using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using ExcelDb.Core.Identity;
using ExcelDb.Runtime.References;

namespace ExcelDb.Runtime.Unity.Tests;

public sealed class UnityRuntimeAdapterTests : IDisposable
{
    private const ulong SchemaHash = 0x9988776655443322;
    private const int TableId = 707;
    private static readonly ExportTargetId Client = new("client");

    public UnityRuntimeAdapterTests() => RuntimeDatabase.Close();

    public void Dispose() => RuntimeDatabase.Close();

    [Fact]
    public void WatcherWork_IsCommittedOnlyAtUnityMainThreadPublishTick()
    {
        var loop = new TestLoop();
        var profiler = new TestProfilerBackend();
        var source = new WatchSource(Snapshot("before", "r1"));
        using var host = new UnityRuntimeHost(loop, profiler);
        Assert.True(host.Open(source, new GeneratedRegistry(), RuntimeMode.Development, enableHotReload: true));

        source.SetSnapshot(Snapshot("after", "r2"));
        source.Signal();

        Assert.Equal(1, loop.RequestCount);
        Assert.Equal("before", host.LoadAsset<UnityTestAsset>("4:item")!.Value);

        loop.Tick();

        Assert.Equal("after", host.LoadAsset<UnityTestAsset>("4:item")!.Value);
        Assert.Contains(RuntimeProfileMarker.Commit, profiler.Begun);
        Assert.Contains(RuntimeProfileMarker.Publish, profiler.Begun);
        foreach (var marker in Enum.GetValues<RuntimeProfileMarker>())
        {
            Assert.Equal(
                profiler.Begun.Count(item => item == marker),
                profiler.Ended.Count(item => item == marker));
        }

        host.Close();
    }

    [Fact]
    public void BakedUnityProvider_UsesGuidAndNeverDisplayPathAsFallback()
    {
        var asset = new object();
        var assetHost = new TestAssetHost(asset);
        const string guid = "0123456789abcdef0123456789abcdef";
        var provider = new BakedUnityAssetProvider(
            "unity.resources",
            assetHost,
            [new UnityAssetBakeEntry(guid, 19)]);
        var reference = new UnityAssetReference(guid, "Assets/Renamed/DisplayOnly.asset");

        Assert.True(BoundUnityAsset.TryBind(provider, reference, out var binding));
        Assert.True(binding.TryResolve<object>(out var resolved));
        Assert.Same(asset, resolved);
        Assert.Equal(19, assetHost.LastKey);

        var pathAsIdentity = new UnityAssetReference("fedcba9876543210fedcba9876543210", guid);
        Assert.False(BoundUnityAsset.TryBind(provider, pathAsIdentity, out _));
    }

    [Fact]
    public void UpmBoundary_IsUnity2022PackageWithoutHardUnityAssemblyDependency()
    {
        var packageRoot = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "upm",
            "com.exceldb.runtime");
        using var package = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(packageRoot, "package.json")));
        using var asmdef = JsonDocument.Parse(File.ReadAllBytes(
            Path.Combine(packageRoot, "Runtime", "Bridge", "ExcelDb.Runtime.Unity.Engine.asmdef")));

        Assert.Equal("com.exceldb.runtime", package.RootElement.GetProperty("name").GetString());
        Assert.Equal("2022.3", package.RootElement.GetProperty("unity").GetString());
        Assert.Equal("ExcelDb.Runtime.Unity.Engine", asmdef.RootElement.GetProperty("name").GetString());
        Assert.False(asmdef.RootElement.GetProperty("noEngineReferences").GetBoolean());

        var pluginRoot = Path.Combine(packageRoot, "Runtime", "Plugins");
        foreach (var assemblyName in new[]
                 {
                     "ExcelDB.Core.dll",
                     "ExcelDB.Runtime.dll",
                     "ExcelDB.Runtime.Unity.dll",
                     "System.Collections.Immutable.dll",
                     "System.Text.Json.dll",
                 })
        {
            var assemblyPath = Path.Combine(pluginRoot, assemblyName);
            Assert.True(File.Exists(assemblyPath), $"Missing self-contained UPM assembly: {assemblyName}");
        }
        AssertNetStandard21(Path.Combine(pluginRoot, "ExcelDB.Core.dll"));
        AssertNetStandard21(Path.Combine(pluginRoot, "ExcelDB.Runtime.dll"));
        AssertNetStandard21(Path.Combine(pluginRoot, "ExcelDB.Runtime.Unity.dll"));
        foreach (var assemblyPath in Directory.EnumerateFiles(pluginRoot, "*.dll"))
        {
            var targetFramework = ReadTargetFramework(assemblyPath);
            Assert.True(
                targetFramework is null
                    or ".NETStandard,Version=v2.0"
                    or ".NETStandard,Version=v2.1",
                $"UPM dependency '{Path.GetFileName(assemblyPath)}' targets '{targetFramework}'.");
        }

        var bridgeRoot = Path.Combine(packageRoot, "Runtime", "Bridge");
        var loopSource = File.ReadAllText(Path.Combine(bridgeRoot, "UnityRuntimeLoop.Unity.cs"));
        var profilerSource = File.ReadAllText(Path.Combine(bridgeRoot, "UnityProfilerBackend.Unity.cs"));
        Assert.Contains("#if UNITY_2022_3_OR_NEWER", loopSource, StringComparison.Ordinal);
        Assert.Contains("#if UNITY_2022_3_OR_NEWER", profilerSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(packageRoot, "*.cs", SearchOption.AllDirectories),
            static path => Path.GetFileName(path) is "UnityRuntimeHost.cs" or "BakedUnityAssetProvider.cs");
        Assert.DoesNotContain(
            typeof(UnityRuntimeHost).Assembly.GetReferencedAssemblies(),
            static assembly => assembly.Name?.StartsWith("Unity", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void PublishPoint_CoalescedRequestsHaveZeroSteadyStateAllocation()
    {
        var loop = new TestLoop();
        using var publishPoint = new UnityRuntimePublishPoint(loop);
        publishPoint.RequestPublish();

        RunPublishRequests(publishPoint, 2_048);
        var shortBatch = MeasurePublishRequests(publishPoint, 10_000);
        var longBatch = MeasurePublishRequests(publishPoint, 20_000);

        Assert.Equal(1, loop.RequestCount);
        Assert.Equal(0, shortBatch);
        Assert.Equal(shortBatch, longBatch);
    }

    private static SourceSnapshot Snapshot(string value, string revision)
    {
        var identity = new AssetIdentity(
            TableId,
            RowGuid.Parse("00000000000000000000000000000001"));
        return new SourceSnapshot(
            SchemaHash,
            Client,
            revision,
            revision,
            [new RuntimeAssetRecord(
                identity,
                "4:item",
                [new RuntimeFieldValue(1, Encoding.UTF8.GetBytes(value))])]);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ExcelDB.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ExcelDB repository root.");
    }

    private static void AssertNetStandard21(string assemblyPath) =>
        Assert.Equal(".NETStandard,Version=v2.1", ReadTargetFramework(assemblyPath));

    private static string? ReadTargetFramework(string assemblyPath)
    {
        var loadContext = new AssemblyLoadContext(
            $"tfm-{Path.GetFileNameWithoutExtension(assemblyPath)}-{Guid.NewGuid():N}",
            isCollectible: true);
        loadContext.Resolving += (context, identity) =>
        {
            var dependency = Path.Combine(Path.GetDirectoryName(assemblyPath)!, $"{identity.Name}.dll");
            return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
        };
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var targetFramework = assembly.GetCustomAttributesData().FirstOrDefault(attribute =>
                attribute.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
            return targetFramework is null
                ? null
                : Assert.IsType<string>(targetFramework.ConstructorArguments[0].Value);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static long MeasurePublishRequests(UnityRuntimePublishPoint publishPoint, int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        RunPublishRequests(publishPoint, iterations);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void RunPublishRequests(UnityRuntimePublishPoint publishPoint, int iterations)
    {
        for (var index = 0; index < iterations; index++)
            publishPoint.RequestPublish();
    }

    private sealed class UnityTestAsset
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class GeneratedRegistry : RuntimeSchemaRegistry
    {
        public GeneratedRegistry()
            : base(
                SchemaHash,
                Client,
                [
                    new RuntimeTableBinding<UnityTestAsset>(
                        TableId,
                        static () => new UnityTestAsset(),
                        static (asset, record) =>
                            asset.Value = Encoding.UTF8.GetString(record.Fields[0].Data.Span),
                        static asset => asset.Value = string.Empty,
                        static asset => asset.Value,
                        static (asset, state) => asset.Value = (string)state)
                ])
        {
        }
    }

    private sealed class WatchSource(SourceSnapshot snapshot) : IWatchableDataSource
    {
        private SourceSnapshot _snapshot = snapshot;
        private Action? _changed;

        public ulong SchemaHash => UnityRuntimeAdapterTests.SchemaHash;

        public ExportTargetId ExportTarget => Client;

        public SourceInfo Inspect() => new(
            RuntimeSourceKind.ConvertedBytes,
            RuntimeSourceCapabilities.Read | RuntimeSourceCapabilities.Refresh | RuntimeSourceCapabilities.Watch,
            1,
            _snapshot.Revision,
            _snapshot.ContentHash);

        public SourceSnapshot Open() => _snapshot;

        public SourceSnapshot Refresh() => _snapshot;

        public IDisposable Watch(Action sourceChanged)
        {
            _changed += sourceChanged;
            return new Subscription(() => _changed -= sourceChanged);
        }

        public void SetSnapshot(SourceSnapshot next) => _snapshot = next;

        public void Signal() => _changed?.Invoke();

        private sealed class Subscription(Action dispose) : IDisposable
        {
            private Action? _dispose = dispose;

            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }

    private sealed class TestLoop : IUnityRuntimeLoop
    {
        public bool IsMainThread { get; set; } = true;

        public int RequestCount { get; private set; }

        public event Action? PublishTick;

        public void RequestPublishTick() => RequestCount++;

        public void Tick() => PublishTick?.Invoke();
    }

    private sealed class TestProfilerBackend : IUnityProfilerBackend
    {
        public List<RuntimeProfileMarker> Begun { get; } = [];

        public List<RuntimeProfileMarker> Ended { get; } = [];

        public void BeginSample(RuntimeProfileMarker marker) => Begun.Add(marker);

        public void EndSample(RuntimeProfileMarker marker) => Ended.Add(marker);
    }

    private sealed class TestAssetHost(object asset) : IUnityAssetHost
    {
        public int LastKey { get; private set; }

        public bool TryResolve<T>(int providerKey, out T? resolved)
            where T : class
        {
            LastKey = providerKey;
            resolved = providerKey == 19 ? asset as T : null;
            return resolved is not null;
        }
    }
}
