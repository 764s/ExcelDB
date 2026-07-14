using System.Collections.Immutable;
using ExcelDb.Core.Identity;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Model;

namespace ExcelDb.Workbooks.Tests;

internal static class TestData
{
    public const ulong SchemaHash = 0x0123_4567_89ab_cdefUL;

    public static CanonicalSchemaDescriptor Schema()
    {
        var fields = ImmutableArray.Create(
            Field(1, "id", "id", "string", required: true, keyOrder: 1),
            Field(2, "count", "count", "int32", defaultValue: "7"),
            Field(3, "note", "note", "string"),
            Field(4, "ratio", "ratio", "double"));
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

    public static CanonicalFieldDescriptor Field(
        int id,
        string name,
        string propertyPath,
        string typeName,
        bool required = false,
        string? defaultValue = null,
        bool unique = false,
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
            unique,
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

    public static WorkbookDefinition Workbook(params WorkbookRow[] rows)
    {
        var schema = Schema();
        var table = WorkbookLayout.CreateTable(schema.Tables[0]) with { Rows = rows.ToImmutableArray() };
        return new WorkbookDefinition(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            schema.SchemaHash,
            DateTimeOffset.Parse("2026-07-14T00:00:00+00:00"),
            [table]);
    }

    public static WorkbookRow Row(
        string? guid,
        uint revision,
        string id,
        WorkbookCell? count = null,
        WorkbookCell? note = null,
        WorkbookCell? ratio = null,
        string? key = null,
        string? rawGuid = null,
        string? rawRevision = null,
        int? sourceRow = null)
    {
        var cells = ImmutableDictionary.CreateBuilder<string, WorkbookCell>(StringComparer.Ordinal);
        cells["id"] = new WorkbookCell(id);
        if (count is not null)
            cells["count"] = count;
        if (note is not null)
            cells["note"] = note;
        if (ratio is not null)
            cells["ratio"] = ratio;
        return new WorkbookRow(
            guid is null ? null : RowGuid.Parse(guid),
            revision,
            cells.ToImmutable(),
            key ?? $"{id.Length}:{id}",
            rawGuid,
            rawRevision,
            sourceRow);
    }

    public static string GuidText(int suffix) => $"000000000000000000000000{suffix:x8}";
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"exceldb-workbooks-tests-{Guid.NewGuid():N}");
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
