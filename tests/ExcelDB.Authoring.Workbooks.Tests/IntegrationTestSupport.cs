using System.Collections.Immutable;
using System.Globalization;
using ExcelDb.Authoring.Workbooks;
using ExcelDb.Core.Identity;
using ExcelDb.Core.Values;
using ExcelDb.Runtime;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Model;
using ExcelDbEditor;

namespace ExcelDb.Authoring.Workbooks.Tests;

internal static class IntegrationTestSupport
{
    public const ulong SchemaHash = 0x1000_2000_3000_4000UL;

    public static CanonicalSchemaDescriptor Schema()
    {
        var fields = ImmutableArray.Create(
            Field(1, "id", "id", "string", required: true, keyOrder: 1),
            Field(2, "count", "count", "int32", defaultValue: "7"),
            Field(3, "note", "note", "string"));
        return new CanonicalSchemaDescriptor(
            [new CanonicalTableDescriptor(
                1,
                "Item",
                "game.Item",
                CanonicalTableKind.Asset,
                "Items",
                [],
                [],
                ["client", "server"],
                [],
                fields,
                [1])],
            [],
            [],
            SchemaHash);
    }

    public static WorkbookDefinition Workbook(
        string guid,
        uint revision = 3,
        string id = "sword",
        string count = "5",
        string note = "old")
    {
        var schema = Schema();
        var layout = WorkbookLayout.CreateTable(schema.Tables[0]);
        var cells = ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
        {
            new KeyValuePair<string, WorkbookCell>("id", new WorkbookCell(id)),
            new KeyValuePair<string, WorkbookCell>("count", new WorkbookCell(count)),
            new KeyValuePair<string, WorkbookCell>("note", new WorkbookCell(note)),
        });
        var row = new WorkbookRow(
            RowGuid.Parse(guid),
            revision,
            cells,
            $"{id.Length.ToString(CultureInfo.InvariantCulture)}:{id}");
        return new WorkbookDefinition(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            SchemaHash,
            new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero),
            [layout with { Rows = [row] }]);
    }

    public static XlsxAuthoringWorkbookAdapter Adapter(RecordingOpener? opener = null)
    {
        var schema = Schema();
        return new XlsxAuthoringWorkbookAdapter(
            schema,
            new TestRegistry(),
            opener);
    }

    public static AuthoringTableRegistration Registration() => new(
        1,
        "Item",
        typeof(Item),
        static value => ((Item)value).Id,
        static (value, key) => ((Item)value).Id = key,
        static value => ((Item)value).Clone());

    private static CanonicalFieldDescriptor Field(
        int id,
        string name,
        string propertyPath,
        string typeName,
        bool required = false,
        string? defaultValue = null,
        int keyOrder = 0) =>
        new(
            id,
            name,
            propertyPath,
            CanonicalFieldShape.Scalar,
            typeName,
            false,
            null,
            null,
            required,
            defaultValue,
            null,
            null,
            null,
            false,
            keyOrder,
            null,
            null,
            CanonicalDeletePolicy.Block,
            CanonicalExpandMode.SingleCell,
            false,
            null,
            null,
            null,
            null,
            ["client", "server"],
            [],
            []);
}

internal sealed class Item
{
    public string Id { get; set; } = string.Empty;

    public int Count { get; set; }

    public string? Note { get; set; }

    public Item Clone() => new() { Id = Id, Count = Count, Note = Note };
}

internal sealed class TestRegistry : RuntimeSchemaRegistry
{
    public TestRegistry()
        : base(
            IntegrationTestSupport.SchemaHash,
            new ExportTargetId("client"),
            [CreateBinding()])
    {
    }

    private static RuntimeTableBinding CreateBinding() => new RuntimeTableBinding<Item>(
        1,
        static () => new Item(),
        Apply,
        static item =>
        {
            item.Id = string.Empty;
            item.Count = 0;
            item.Note = null;
        },
        static item => item.Clone(),
        static (item, state) =>
        {
            var saved = (Item)state;
            item.Id = saved.Id;
            item.Count = saved.Count;
            item.Note = saved.Note;
        });

    private static void Apply(Item item, RuntimeAssetRecord record)
    {
        foreach (var field in record.Fields)
        {
            var value = CanonicalRuntimeFieldEncoding.Decode(field.Data.Span);
            switch (field.FieldNumber)
            {
                case 1:
                    item.Id = value.Text ?? string.Empty;
                    break;
                case 2:
                    item.Count = value.State == CanonicalValueState.Missing
                        ? 0
                        : int.Parse(value.Text ?? "0", CultureInfo.InvariantCulture);
                    break;
                case 3:
                    item.Note = value.State is CanonicalValueState.Missing or CanonicalValueState.Null
                        ? null
                        : value.Text;
                    break;
            }
        }
    }
}

internal sealed class RecordingOpener : IWorkbookLocationOpener
{
    public (string Path, string Sheet, int Row)? LastOpen { get; private set; }

    public bool Open(string workbookPath, string sheetName, int rowNumber)
    {
        LastOpen = (workbookPath, sheetName, rowNumber);
        return true;
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"exceldb-authoring-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
