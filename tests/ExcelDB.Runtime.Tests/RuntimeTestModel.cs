using System.Globalization;
using System.Text;
using ExcelDb.Core.Identity;

namespace ExcelDb.Runtime.Tests;

internal sealed class TestAsset
{
    public string Name { get; set; } = string.Empty;

    public int Value { get; set; }
}

internal sealed record TestAssetState(string Name, int Value);

internal sealed class TestRegistry : RuntimeSchemaRegistry
{
    public TestRegistry(ulong schemaHash, ExportTargetId target)
        : base(schemaHash, target, CreateBindings())
    {
    }

    private static IEnumerable<RuntimeTableBinding> CreateBindings() =>
        [
            new RuntimeTableBinding<TestAsset>(
                RuntimeTestData.TableId,
                static () => new TestAsset(),
                RuntimeTestData.Apply,
                static asset =>
                {
                    asset.Name = string.Empty;
                    asset.Value = 0;
                },
                static asset => new TestAssetState(asset.Name, asset.Value),
                static (asset, state) =>
                {
                    var typed = Assert.IsType<TestAssetState>(state);
                    asset.Name = typed.Name;
                    asset.Value = typed.Value;
                },
                static (_, candidate) => RuntimeTestData.ReadText(candidate, 99) != "recreate")
        ];
}

internal class TestSource : IRefreshableDataSource
{
    private SourceSnapshot _snapshot;

    public TestSource(
        ulong declaredHash,
        ExportTargetId declaredTarget,
        SourceSnapshot snapshot,
        RuntimeSourceKind kind = RuntimeSourceKind.Custom)
    {
        SchemaHash = declaredHash;
        ExportTarget = declaredTarget;
        _snapshot = snapshot;
        Kind = kind;
    }

    public ulong SchemaHash { get; }

    public ExportTargetId ExportTarget { get; }

    public RuntimeSourceKind Kind { get; }

    public void SetSnapshot(SourceSnapshot snapshot) => _snapshot = snapshot;

    public virtual SourceInfo Inspect() => new(
        Kind,
        RuntimeSourceCapabilities.Read | RuntimeSourceCapabilities.Refresh,
        _snapshot.FormatVersion,
        _snapshot.Revision,
        _snapshot.ContentHash);

    public SourceSnapshot Open() => _snapshot;

    public SourceSnapshot Refresh() => _snapshot;
}

internal sealed class TestWatchSource : TestSource, IWatchableDataSource
{
    private Action? _callback;

    public TestWatchSource(
        ulong declaredHash,
        ExportTargetId declaredTarget,
        SourceSnapshot snapshot)
        : base(declaredHash, declaredTarget, snapshot)
    {
    }

    public override SourceInfo Inspect()
    {
        var info = base.Inspect();
        return info with { Capabilities = info.Capabilities | RuntimeSourceCapabilities.Watch };
    }

    public IDisposable Watch(Action sourceChanged)
    {
        ArgumentNullException.ThrowIfNull(sourceChanged);
        _callback = sourceChanged;
        return new CallbackDisposable(() =>
        {
            if (ReferenceEquals(_callback, sourceChanged))
                _callback = null;
        });
    }

    public void Signal() => _callback?.Invoke();

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

internal static class RuntimeTestData
{
    public const int TableId = 101;
    public const ulong SchemaHash = 0x1122334455667788;

    public static ExportTargetId Client { get; } = new("client");

    public static ExportTargetId Server { get; } = new("server");

    public static AssetIdentity Identity(int value) => new(
        TableId,
        RowGuid.Parse(value.ToString("x32", CultureInfo.InvariantCulture)));

    public static RuntimeFieldValue Field(int number, string value) =>
        new(number, Encoding.UTF8.GetBytes(value));

    public static RuntimeAssetRecord Asset(
        int identity,
        string key,
        string name,
        int value = 0,
        string? path = null,
        IEnumerable<AssetIdentity>? dependencies = null,
        bool recreate = false)
    {
        var fields = new List<RuntimeFieldValue>
        {
            Field(1, name),
            Field(2, value.ToString(CultureInfo.InvariantCulture)),
        };
        if (recreate)
            fields.Add(Field(99, "recreate"));
        return new RuntimeAssetRecord(Identity(identity), key, fields, dependencies, path);
    }

    public static SourceSnapshot Snapshot(
        IEnumerable<RuntimeAssetRecord> assets,
        ulong schemaHash = SchemaHash,
        ExportTargetId? target = null,
        string revision = "r1",
        string contentHash = "content-1") =>
        new(schemaHash, target ?? Client, revision, contentHash, assets);

    public static string? ReadText(RuntimeAssetRecord record, int fieldNumber)
    {
        var field = record.Fields.FirstOrDefault(item => item.FieldNumber == fieldNumber);
        return field is null ? null : Encoding.UTF8.GetString(field.Data.Span);
    }

    public static void Apply(TestAsset asset, RuntimeAssetRecord record)
    {
        var name = ReadText(record, 1) ?? string.Empty;
        asset.Name = name;
        if (name == "throw")
            throw new InvalidOperationException("Injected patch failure.");

        asset.Value = int.Parse(ReadText(record, 2) ?? "0", CultureInfo.InvariantCulture);
    }
}
