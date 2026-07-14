using System.Collections.Immutable;
using System.Text;
using ExcelDb.Core.Identity;
using ExcelDb.Runtime.Bytes;
using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;

namespace ExcelDb.Runtime.Excel.Tests;

public sealed class ExcelDataSourceTests : IDisposable
{
    private const ulong SchemaHash = 0x1020304050607080;
    private static readonly ExportTargetId Client = new("client");
    private static readonly ExportTargetId Server = new("server");

    public ExcelDataSourceTests() => RuntimeDatabase.Close();

    public void Dispose() => RuntimeDatabase.Close();

    [Fact]
    public void SourceSet_IsOrderIndependent_AndProjectsOnlyTheSelectedTarget()
    {
        var table = Table(
            101,
            "Item",
            "Items",
            [
                Field(1, "key", "string", ["client", "server"]),
                Field(2, "client_value", "string", ["client"]),
                Field(3, "server_value", "string", ["server"]),
            ],
            [1],
            ["client", "server"]);
        var schema = Schema([table]);
        var store = new MemoryWorkbookStore();
        store.Set("a.xlsx", WorkbookBytes(
            table,
            Row(1, ("key", "hero"), ("client_value", "visible-a"), ("server_value", "secret-a")),
            workbookId: Guid.Parse("10000000-0000-0000-0000-000000000001")));
        store.Set("b.xlsx", WorkbookBytes(
            table,
            Row(2, ("key", "mage"), ("client_value", "visible-b"), ("server_value", "secret-b")),
            workbookId: Guid.Parse("10000000-0000-0000-0000-000000000002")));

        var forward = new ExcelDataSource(["a.xlsx", "b.xlsx"], schema, Client, store).Open();
        var reverse = new ExcelDataSource(["b.xlsx", "a.xlsx"], schema, Client, store).Open();
        var server = new ExcelDataSource(["a.xlsx", "b.xlsx"], schema, Server, store).Open();

        Assert.Equal(forward.ContentHash, reverse.ContentHash);
        Assert.Equal(
            forward.Assets.Select(static asset => asset.Identity),
            reverse.Assets.Select(static asset => asset.Identity));
        Assert.All(forward.Assets, asset => Assert.Equal([1, 2], asset.Fields.Select(static field => field.FieldNumber)));
        Assert.All(server.Assets, asset => Assert.Equal([1, 3], asset.Fields.Select(static field => field.FieldNumber)));
        Assert.DoesNotContain(
            forward.Assets.SelectMany(static asset => asset.Fields),
            field => Encoding.UTF8.GetString(field.Data.Span).StartsWith("secret", StringComparison.Ordinal));

        var package = ConvertedBytesWriter.Build(forward, "test");
        using var roundTripped = ConvertedBytesReader.Read(package.Bytes, package.ManifestJson).Snapshot;
        Assert.Equal(
            forward.Assets.Select(CanonicalAssetText),
            roundTripped.Assets.Select(CanonicalAssetText));
    }

    [Fact]
    public void ExcelSourceUsesInjectedPureCSharpCellFormatRegistry()
    {
        var formatted = Field(2, "display", "string", ["client"]) with
        {
            CellFormat = new CanonicalCellFormat("codec", ["angle:v1"], []),
        };
        var table = Table(
            101,
            "Item",
            "Items",
            [Field(1, "key", "string", ["client"]), formatted],
            [1],
            ["client"]);
        var schema = Schema([table]);
        var store = new MemoryWorkbookStore();
        store.Set("codec.xlsx", WorkbookBytes(table, Row(1, ("key", "hero"), ("display", "<visible>"))));

        using var missing = new ExcelDataSource(["codec.xlsx"], schema, Client, store).Open();
        Assert.Contains(missing.Diagnostics, static diagnostic => diagnostic.IsFailure);

        var formats = new CellFormatRegistry([new AngleCellFormat()]);
        using var accepted = new ExcelDataSource(
            ["codec.xlsx"], schema, Client, store, formats).Open();

        Assert.DoesNotContain(accepted.Diagnostics, static diagnostic => diagnostic.IsFailure);
        var record = Assert.Single(accepted.Assets);
        Assert.Equal(
            "visible",
            Encoding.UTF8.GetString(Assert.Single(record.Fields, static field => field.FieldNumber == 2).Data.Span));
    }

    [Fact]
    public async Task HardReference_IsResolvedToStableIdentity()
    {
        using var proto = TemporaryProtoDirectory.Create();
        proto.Write("refs.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";

            message Item {
              option (exceldb.table) = { kind: ASSET, id: 101 };
              string key = 1 [(exceldb.field) = { key: 1 }];
            }

            message Holder {
              option (exceldb.table) = { kind: ASSET, id: 202 };
              string key = 1 [(exceldb.field) = { key: 1 }];
              exceldb.RowRef item = 2 [(exceldb.field) = { ref_table: "Item" }];
            }
            """);
        var compilation = await new SchemaCompiler().CompileAsync(proto.Path);
        Assert.True(
            compilation.Succeeded,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(static item => item.Message)));
        var schema = compilation.Descriptor!;
        var item = Assert.Single(schema.Tables, static table => table.Name == "Item");
        var holder = Assert.Single(schema.Tables, static table => table.Name == "Holder");
        var rowReference = Assert.Single(holder.Fields, static field => field.Name == "item");
        Assert.Equal("exceldb.RowRef", rowReference.TypeName);
        Assert.Equal(CanonicalFieldShape.Message, rowReference.Shape);
        Assert.Equal("Item", rowReference.ReferenceTable);
        var workbook = new WorkbookDefinition(
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            schema.SchemaHash,
            DateTimeOffset.UnixEpoch,
            [
                WorkbookTable(item, Row(1, ("key", "sword"))),
                WorkbookTable(holder, Row(2, ("key", "slot"), ("item", "101:sword"))),
            ]);
        var store = new MemoryWorkbookStore();
        store.Set("refs.xlsx", XlsxWorkbookCodec.Write(workbook));

        var snapshot = new ExcelDataSource(["refs.xlsx"], schema, Client, store).Open();

        Assert.DoesNotContain(snapshot.Diagnostics, static diagnostic => diagnostic.IsFailure);
        var holderRecord = Assert.Single(snapshot.Assets, static asset => asset.TableId == 202);
        var expectedIdentity = Identity(101, 1);
        Assert.Equal(expectedIdentity, Assert.Single(holderRecord.Dependencies));
        Assert.Equal(
            expectedIdentity.ToString(),
            Encoding.UTF8.GetString(Assert.Single(holderRecord.Fields, static field => field.FieldNumber == 2).Data.Span));

        var registry = new ReferenceRegistry(schema.SchemaHash);
        Assert.True(RuntimeDatabase.Open(
            new ExcelDataSource(["refs.xlsx"], schema, Client, store),
            registry,
            new RuntimeBootstrapOptions(RuntimeMode.Development, false)));
        var holderAsset = RuntimeDatabase.LoadAsset<HolderAsset>("4:slot");
        Assert.NotNull(holderAsset);
        Assert.Equal(expectedIdentity, holderAsset.Target);

        var package = ConvertedBytesWriter.Build(snapshot, "test");
        Assert.True(RuntimeDatabase.SwitchDataSource(
            new ConvertedBytesDataSource(package.Bytes, package.ManifestJson)));
        Assert.Same(holderAsset, RuntimeDatabase.LoadAsset<HolderAsset>("4:slot"));
        Assert.Equal(expectedIdentity, holderAsset.Target);
        RuntimeDatabase.Close();
    }

    [Theory]
    [InlineData(RuntimeMode.EditorAuthoring)]
    [InlineData(RuntimeMode.EditorPlayDebug)]
    [InlineData(RuntimeMode.Development)]
    public void ConcreteExcelSource_OpensOnlyThroughExplicitNonReleaseMode(RuntimeMode mode)
    {
        var table = Table(
            101,
            "Item",
            "Items",
            [Field(1, "key", "string", ["client"])],
            [1],
            ["client"]);
        var schema = Schema([table]);
        var store = new MemoryWorkbookStore();
        store.Set("mode.xlsx", WorkbookBytes(table, Row(1, ("key", "hero"))));
        var source = new ExcelDataSource(["mode.xlsx"], schema, Client, store);

        Assert.True(RuntimeDatabase.Open(
            source,
            new ExcelRegistry(),
            new RuntimeBootstrapOptions(mode, false)));
        Assert.Equal("hero", RuntimeDatabase.LoadAsset<ExcelAsset>("4:hero")!.Key);
        RuntimeDatabase.Close();
    }

    [Fact]
    public void PendingIdentity_IsABlocker_AndReleaseRejectsTheConcreteExcelSource()
    {
        var table = Table(
            101,
            "Item",
            "Items",
            [Field(1, "key", "string", ["client"])],
            [1],
            ["client"]);
        var schema = Schema([table]);
        var pending = new WorkbookRow(
            null,
            0,
            new Dictionary<string, WorkbookCell>(StringComparer.Ordinal)
            {
                ["key"] = new("pending"),
            }.ToImmutableDictionary(StringComparer.Ordinal));
        var store = new MemoryWorkbookStore();
        store.Set("pending.xlsx", WorkbookBytes(table, pending));
        var source = new ExcelDataSource(["pending.xlsx"], schema, Client, store);

        using var snapshot = source.Open();
        Assert.Contains(snapshot.Diagnostics, diagnostic =>
            diagnostic.Code == RuntimeDiagnosticCodes.PendingIdentity && diagnostic.IsBlocker);
        Assert.False(RuntimeDatabase.Open(
            source,
            new EmptyRegistry(SchemaHash, Client),
            new RuntimeBootstrapOptions(RuntimeMode.Development, false)));
        Assert.Contains(RuntimeDatabase.LastDiagnostics, diagnostic =>
            diagnostic.Code == RuntimeDiagnosticCodes.PendingIdentity);

        RuntimeDatabase.Close();
        Assert.False(RuntimeDatabase.Open(
            source,
            new EmptyRegistry(SchemaHash, Client),
            new RuntimeBootstrapOptions(RuntimeMode.Release, false)));
        Assert.Contains(RuntimeDatabase.LastDiagnostics, diagnostic =>
            diagnostic.Code == RuntimeDiagnosticCodes.SourceRejected);
    }

    [Fact]
    public void RefreshAndWatch_ExposeAReadOnlyChangeSignalWithNewRevision()
    {
        var table = Table(
            101,
            "Item",
            "Items",
            [Field(1, "key", "string", ["client"])],
            [1],
            ["client"]);
        var schema = Schema([table]);
        var store = new MemoryWorkbookStore();
        store.Set("watch.xlsx", WorkbookBytes(table, Row(1, ("key", "before"))));
        var source = new ExcelDataSource(["watch.xlsx"], schema, Client, store);
        using var before = source.Open();
        var ownerThread = Environment.CurrentManagedThreadId;
        var signals = 0;
        using var prepared = new ManualResetEventSlim();
        using var watch = source.Watch(() =>
        {
            Interlocked.Increment(ref signals);
            prepared.Set();
        });

        store.Set("watch.xlsx", WorkbookBytes(table, Row(1, ("key", "after"))));
        store.Signal();
        Assert.True(prepared.Wait(TimeSpan.FromSeconds(10)), "Background Excel candidate was not prepared.");
        Assert.NotEqual(ownerThread, store.LastReadThreadId);
        var readsAfterPrepare = store.ReadCount;
        using var after = source.Refresh();

        Assert.Equal(1, signals);
        Assert.Equal(readsAfterPrepare, store.ReadCount);
        Assert.NotEqual(before.Revision, after.Revision);
        Assert.Equal(RuntimeSourceKind.Excel, source.Inspect().Kind);
        Assert.True(source.Inspect().Capabilities.HasFlag(RuntimeSourceCapabilities.Watch));
    }

    private static CanonicalSchemaDescriptor Schema(ImmutableArray<CanonicalTableDescriptor> tables) =>
        new(tables, [], [], SchemaHash);

    private static string CanonicalAssetText(RuntimeAssetRecord asset) =>
        string.Join(
            '|',
            asset.Identity.ToString(),
            asset.Key,
            asset.Path,
            string.Join(',', asset.Fields.Select(field =>
                $"{field.FieldNumber}:{Convert.ToHexString(field.Data.Span)}")),
            string.Join(',', asset.Dependencies.Select(static dependency => dependency.ToString())));

    private static CanonicalTableDescriptor Table(
        int id,
        string name,
        string sheetName,
        ImmutableArray<CanonicalFieldDescriptor> fields,
        ImmutableArray<int> keyFields,
        ImmutableArray<string> targets) =>
        new(
            id,
            name,
            $"Game.{name}",
            CanonicalTableKind.Asset,
            sheetName,
            [],
            [],
            targets,
            [],
            fields,
            keyFields);

    private static CanonicalFieldDescriptor Field(
        int id,
        string name,
        string type,
        ImmutableArray<string> targets,
        string? referenceTable = null,
        CanonicalFieldShape shape = CanonicalFieldShape.Scalar,
        bool hasPresence = false,
        bool required = true) =>
        new(
            Id: id,
            Name: name,
            PropertyPath: name,
            Shape: shape,
            TypeName: type,
            HasPresence: hasPresence,
            OneOfGroup: null,
            Map: null,
            Required: required,
            DefaultValue: null,
            Minimum: null,
            Maximum: null,
            Regex: null,
            Unique: false,
            KeyOrder: 0,
            ReferenceTable: referenceTable,
            ReferenceGroup: null,
            DeletePolicy: CanonicalDeletePolicy.Block,
            ExpandMode: CanonicalExpandMode.SingleCell,
            Labels: false,
            ChildTableSheetName: null,
            CellFormat: null,
            Expression: null,
            Weighted: null,
            ExportTargets: targets,
            ReservedChildFieldNumbers: [],
            Children: []);

    private static WorkbookRow Row(int id, params (string Name, string Value)[] cells) =>
        WorkbookRow.Create(
            Identity(1, id).RowGuid,
            1,
            cells.Select(static pair =>
                new KeyValuePair<string, WorkbookCell>(pair.Name, new WorkbookCell(pair.Value))));

    private static AssetIdentity Identity(int tableId, int id) =>
        new(tableId, RowGuid.Parse(id.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)));

    private static WorkbookTable WorkbookTable(
        CanonicalTableDescriptor table,
        params WorkbookRow[] rows) =>
        WorkbookLayout.CreateTable(table) with { Rows = rows.ToImmutableArray() };

    private static byte[] WorkbookBytes(
        CanonicalTableDescriptor table,
        WorkbookRow row,
        Guid? workbookId = null) =>
        XlsxWorkbookCodec.Write(new WorkbookDefinition(
            workbookId ?? Guid.Parse("30000000-0000-0000-0000-000000000001"),
            SchemaHash,
            DateTimeOffset.UnixEpoch,
            [WorkbookTable(table, row)]));

    private sealed class EmptyRegistry(ulong hash, ExportTargetId target)
        : RuntimeSchemaRegistry(hash, target, [])
    {
    }

    private sealed class AngleCellFormat : ICellFormat
    {
        public string Identity => "angle:v1";

        public string Describe(CellFormatContext context) => "<value>";

        public bool TryParse(
            string physicalText,
            CellFormatContext context,
            out string canonicalValue,
            out string? error)
        {
            if (physicalText.Length >= 2 && physicalText[0] == '<' && physicalText[^1] == '>')
            {
                canonicalValue = physicalText[1..^1];
                error = null;
                return true;
            }

            canonicalValue = string.Empty;
            error = "Expected <value>.";
            return false;
        }

        public bool TryWrite(
            string canonicalValue,
            CellFormatContext context,
            out string physicalText,
            out string? error)
        {
            physicalText = $"<{canonicalValue}>";
            error = null;
            return true;
        }
    }

    private sealed class ExcelAsset
    {
        public string Key { get; set; } = string.Empty;
    }

    private sealed class ExcelRegistry : RuntimeSchemaRegistry
    {
        public ExcelRegistry()
            : base(
                SchemaHash,
                Client,
                [
                    new RuntimeTableBinding<ExcelAsset>(
                        101,
                        static () => new ExcelAsset(),
                        static (asset, record) =>
                            asset.Key = Encoding.UTF8.GetString(record.Fields[0].Data.Span),
                        static asset => asset.Key = string.Empty,
                        static asset => asset.Key,
                        static (asset, state) => asset.Key = (string)state)
                ])
        {
        }
    }

    private sealed class ItemAsset
    {
        public string Key { get; set; } = string.Empty;
    }

    private sealed class HolderAsset
    {
        public string Key { get; set; } = string.Empty;

        public AssetIdentity Target { get; set; }
    }

    private sealed record HolderState(string Key, AssetIdentity Target);

    private sealed class ReferenceRegistry : RuntimeSchemaRegistry
    {
        public ReferenceRegistry(ulong schemaHash)
            : base(
                schemaHash,
                Client,
                [
                    new RuntimeTableBinding<ItemAsset>(
                        101,
                        static () => new ItemAsset(),
                        static (asset, record) =>
                            asset.Key = Encoding.UTF8.GetString(record.Fields[0].Data.Span),
                        static asset => asset.Key = string.Empty,
                        static asset => asset.Key,
                        static (asset, state) => asset.Key = (string)state),
                    new RuntimeTableBinding<HolderAsset>(
                        202,
                        static () => new HolderAsset(),
                        static (asset, record) =>
                        {
                            foreach (var field in record.Fields)
                            {
                                if (field.FieldNumber == 1)
                                {
                                    asset.Key = Encoding.UTF8.GetString(field.Data.Span);
                                }
                                else if (field.FieldNumber == 2
                                    && !field.Data.IsEmpty
                                    && AssetIdentity.TryParse(Encoding.UTF8.GetString(field.Data.Span), out var target))
                                {
                                    asset.Target = target;
                                }
                            }
                        },
                        static asset =>
                        {
                            asset.Key = string.Empty;
                            asset.Target = default;
                        },
                        static asset => new HolderState(asset.Key, asset.Target),
                        static (asset, state) =>
                        {
                            var typed = (HolderState)state;
                            asset.Key = typed.Key;
                            asset.Target = typed.Target;
                        })
                ])
        {
        }
    }

    private sealed class TemporaryProtoDirectory : IDisposable
    {
        private TemporaryProtoDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryProtoDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"exceldb-runtime-excel-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryProtoDirectory(path);
        }

        public void Write(string relativePath, string content)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class MemoryWorkbookStore : IExcelWorkbookStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private Action? _changed;
        private int _readCount;

        public bool SupportsWatch => true;

        public int ReadCount => Volatile.Read(ref _readCount);

        public int LastReadThreadId { get; private set; }

        public void Set(string path, byte[] bytes) =>
            _files[Path.GetFullPath(path)] = bytes;

        public byte[] ReadAllBytes(string path)
        {
            LastReadThreadId = Environment.CurrentManagedThreadId;
            Interlocked.Increment(ref _readCount);
            return _files[Path.GetFullPath(path)].ToArray();
        }

        public IDisposable Watch(IReadOnlyList<string> paths, Action changed)
        {
            Assert.All(paths, path => Assert.True(_files.ContainsKey(Path.GetFullPath(path))));
            _changed += changed;
            return new Subscription(() => _changed -= changed);
        }

        public void Signal() => _changed?.Invoke();

        private sealed class Subscription(Action dispose) : IDisposable
        {
            private Action? _dispose = dispose;

            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
