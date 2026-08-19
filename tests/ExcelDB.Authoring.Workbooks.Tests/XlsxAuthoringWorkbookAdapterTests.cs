using System.Collections.Immutable;
using ExcelDb.Authoring.Workbooks;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.OpenXml;
using ExcelDbEditor;

namespace ExcelDb.Authoring.Workbooks.Tests;

public sealed class XlsxAuthoringWorkbookAdapterTests
{
    private const string GuidText = "00000000000000000000000000000101";

    [Fact]
    public void Real_xlsx_import_materializes_registered_runtime_poco_and_open_resolves_exact_row()
    {
        using var directory = new TempDirectory();
        var path = directory.File("items.xlsx");
        File.WriteAllBytes(path, XlsxWorkbookCodec.Write(IntegrationTestSupport.Workbook(GuidText)));
        var opener = new RecordingOpener();
        var adapter = IntegrationTestSupport.Adapter(opener);

        var imported = adapter.Import(path, forceUpdate: true);

        Assert.True(imported.Report.Succeeded);
        Assert.NotNull(imported.Fingerprint);
        var asset = Assert.IsType<Item>(Assert.Single(imported.Assets).Asset);
        Assert.Equal("sword", asset.Id);
        Assert.Equal(5, asset.Count);
        Assert.Equal("old", asset.Note);
        Assert.True(adapter.Open(path, "Item", GUID.Parse(GuidText)));
        Assert.Equal((Path.GetFullPath(path), "Items", 4), opener.LastOpen);
    }

    [Fact]
    public void AdapterUsesInjectedPureCSharpCellFormatRegistry()
    {
        using var directory = new TempDirectory();
        var path = directory.File("custom-codec.xlsx");
        var baseSchema = IntegrationTestSupport.Schema();
        var table = Assert.Single(baseSchema.Tables);
        var schema = baseSchema with
        {
            Tables =
            [
                table with
                {
                    Fields = table.Fields.Select(field => field.Name == "count"
                        ? field with { CellFormat = new CanonicalCellFormat("codec", ["bracket-int:v1"], []) }
                        : field).ToImmutableArray(),
                },
            ],
        };
        File.WriteAllBytes(
            path,
            XlsxWorkbookCodec.Write(IntegrationTestSupport.Workbook(GuidText, count: "[5]")));
        var formats = new CellFormatRegistry([new BracketIntCellFormat()]);
        var adapter = new XlsxAuthoringWorkbookAdapter(
            schema,
            new TestRegistry(),
            cellFormats: formats);

        var imported = adapter.Import(path, forceUpdate: true);

        Assert.True(imported.Report.Succeeded);
        Assert.Equal(5, Assert.IsType<Item>(Assert.Single(imported.Assets).Asset).Count);
    }

    [Fact]
    public void AssetDatabase_save_uses_draft_write_plan_and_journal_and_supports_repeated_saves()
    {
        using var directory = new TempDirectory();
        var path = directory.File("items.xlsx");
        File.WriteAllBytes(path, XlsxWorkbookCodec.Write(IntegrationTestSupport.Workbook(GuidText)));
        var adapter = IntegrationTestSupport.Adapter();
        using var session = new AssetDatabaseSession(adapter).RegisterTable(IntegrationTestSupport.Registration());
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook(path);
        var assetPath = $"{path.Replace('\\', '/')}/Item/sword";
        var asset = AssetDatabase.LoadAssetAtPath<Item>(assetPath);
        Assert.NotNull(asset);

        asset.Count = 12;
        asset.Note = "saved";
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);
        asset.Count = 13;
        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);

        Assert.DoesNotContain(session.Diagnostics, diagnostic => diagnostic.Diagnostic.IsFailure);
        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(path));
        var row = Assert.Single(Assert.Single(workbook.Tables).Rows);
        Assert.Equal("13", row.Cells["count"].Text);
        Assert.Equal("saved", row.Cells["note"].Text);
        Assert.Equal((uint)5, row.Revision);
        Assert.Empty(Directory.EnumerateFiles(directory.Path, ".exceldb-*.journal.json"));
    }

    [Fact]
    public void Save_three_way_merges_non_conflicting_external_cells_and_updates_submitted_poco()
    {
        using var directory = new TempDirectory();
        var path = directory.File("items.xlsx");
        var original = XlsxWorkbookCodec.Write(IntegrationTestSupport.Workbook(GuidText));
        File.WriteAllBytes(path, original);
        var adapter = IntegrationTestSupport.Adapter();
        using var session = new AssetDatabaseSession(adapter).RegisterTable(IntegrationTestSupport.Registration());
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook(path);
        var assetPath = $"{path.Replace('\\', '/')}/Item/sword";
        var asset = AssetDatabase.LoadAssetAtPath<Item>(assetPath)!;
        asset.Note = "mine";
        var external = XlsxWorkbookCodec.PatchCells(
            original,
            [new CellPatch("Items", 4, 2, new ExcelDb.Workbooks.Model.WorkbookCell("9"))]);
        File.WriteAllBytes(path, external);

        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);

        Assert.DoesNotContain(session.Diagnostics, diagnostic => diagnostic.Diagnostic.IsFailure);
        Assert.Equal(9, asset.Count);
        Assert.Equal("mine", asset.Note);
        var row = XlsxWorkbookCodec.Read(File.ReadAllBytes(path)).Tables[0].Rows[0];
        Assert.Equal("9", row.Cells["count"].Text);
        Assert.Equal("mine", row.Cells["note"].Text);
        Assert.Equal((uint)4, row.Revision);
    }

    [Fact]
    public void Save_reports_same_field_external_conflict_without_overwriting_their_cell()
    {
        using var directory = new TempDirectory();
        var path = directory.File("items.xlsx");
        var original = XlsxWorkbookCodec.Write(IntegrationTestSupport.Workbook(GuidText));
        File.WriteAllBytes(path, original);
        var adapter = IntegrationTestSupport.Adapter();
        using var session = new AssetDatabaseSession(adapter).RegisterTable(IntegrationTestSupport.Registration());
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook(path);
        var assetPath = $"{path.Replace('\\', '/')}/Item/sword";
        var asset = AssetDatabase.LoadAssetAtPath<Item>(assetPath)!;
        asset.Note = "mine";
        var external = XlsxWorkbookCodec.PatchCells(
            original,
            [new CellPatch("Items", 4, 3, new ExcelDb.Workbooks.Model.WorkbookCell("theirs"))]);
        File.WriteAllBytes(path, external);

        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);

        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Diagnostic.Code == "EXAW0102");
        var conflict = Assert.Single(adapter.LastConflicts);
        Assert.Equal("note", conflict.PropertyPath);
        var row = XlsxWorkbookCodec.Read(File.ReadAllBytes(path)).Tables[0].Rows[0];
        Assert.Equal("theirs", row.Cells["note"].Text);
        Assert.Equal((uint)3, row.Revision);
    }

    [Theory]
    [InlineData(ConflictResolutionAction.KeepEditorValue, "mine")]
    [InlineData(ConflictResolutionAction.ReloadFromExcel, "theirs")]
    public void Facade_conflict_resolution_is_applied_by_the_next_save_preflight(
        ConflictResolutionAction action,
        string expected)
    {
        using var directory = new TempDirectory();
        var path = directory.File($"conflict-{action}.xlsx");
        var original = XlsxWorkbookCodec.Write(IntegrationTestSupport.Workbook(GuidText));
        File.WriteAllBytes(path, original);
        var adapter = IntegrationTestSupport.Adapter();
        using var session = new AssetDatabaseSession(adapter).RegisterTable(IntegrationTestSupport.Registration());
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook(path);
        var assetPath = $"{path.Replace('\\', '/')}/Item/sword";
        var asset = AssetDatabase.LoadAssetAtPath<Item>(assetPath)!;
        asset.Note = "mine";
        File.WriteAllBytes(
            path,
            XlsxWorkbookCodec.PatchCells(
                original,
                [new CellPatch("Items", 4, 3, new ExcelDb.Workbooks.Model.WorkbookCell("theirs"))]));

        EditorUtility.SetDirty(asset);
        AssetDatabase.SaveAssetIfDirty(asset);
        var conflicts = new ConflictRecord[1];
        Assert.Equal(ExcelDb.Runtime.RuntimeQueryStatus.Success, AssetDatabase.GetConflicts(conflicts, out var count));
        Assert.Equal(1, count);
        Assert.True(AssetDatabase.ResolveConflict(conflicts[0].Id, action));
        AssetDatabase.SaveAssets();

        var row = XlsxWorkbookCodec.Read(File.ReadAllBytes(path)).Tables[0].Rows[0];
        Assert.Equal(expected, row.Cells["note"].Text);
        Assert.Equal(expected, asset.Note);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void Newly_created_row_starts_at_zero_and_can_be_saved_again_from_the_same_session()
    {
        using var directory = new TempDirectory();
        var path = directory.File("items.xlsx");
        File.WriteAllBytes(path, XlsxWorkbookCodec.Write(IntegrationTestSupport.Workbook(GuidText)));
        var adapter = IntegrationTestSupport.Adapter();
        using var session = new AssetDatabaseSession(adapter).RegisterTable(IntegrationTestSupport.Registration());
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook(path);
        var workbookPath = path.Replace('\\', '/');
        var created = new Item { Id = "shield", Count = 2, Note = "new" };
        AssetDatabase.CreateAsset(created, $"{workbookPath}/Item/shield");

        AssetDatabase.SaveAssets();
        var first = XlsxWorkbookCodec.Read(File.ReadAllBytes(path)).Tables[0].Rows.Single(row => row.Cells["id"].Text == "shield");
        Assert.Equal((uint)0, first.Revision);

        created.Count = 3;
        EditorUtility.SetDirty(created);
        AssetDatabase.SaveAssetIfDirty(created);

        Assert.DoesNotContain(session.Diagnostics, diagnostic => diagnostic.Diagnostic.IsFailure);
        var second = XlsxWorkbookCodec.Read(File.ReadAllBytes(path)).Tables[0].Rows.Single(row => row.Cells["id"].Text == "shield");
        Assert.Equal((uint)1, second.Revision);
        Assert.Equal("3", second.Cells["count"].Text);
    }

    [Fact]
    public void Rename_is_a_key_edit_and_delete_removes_the_managed_row()
    {
        using var directory = new TempDirectory();
        var path = directory.File("items.xlsx");
        File.WriteAllBytes(path, XlsxWorkbookCodec.Write(IntegrationTestSupport.Workbook(GuidText)));
        var adapter = IntegrationTestSupport.Adapter();
        using var session = new AssetDatabaseSession(adapter).RegisterTable(IntegrationTestSupport.Registration());
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook(path);
        var workbookPath = path.Replace('\\', '/');
        var oldPath = $"{workbookPath}/Item/sword";

        Assert.Equal(string.Empty, AssetDatabase.RenameAsset(oldPath, "axe"));
        AssetDatabase.SaveAssets();
        var renamed = Assert.Single(XlsxWorkbookCodec.Read(File.ReadAllBytes(path)).Tables[0].Rows);
        Assert.Equal("axe", renamed.Cells["id"].Text);
        Assert.Equal("3:axe", renamed.Key);
        Assert.Equal((uint)4, renamed.Revision);

        Assert.True(AssetDatabase.DeleteAsset($"{workbookPath}/Item/axe"));
        AssetDatabase.SaveAssets();

        Assert.DoesNotContain(session.Diagnostics, diagnostic => diagnostic.Diagnostic.IsFailure);
        Assert.Empty(XlsxWorkbookCodec.Read(File.ReadAllBytes(path)).Tables[0].Rows);
    }

    [Fact]
    public void Cross_workbook_move_prepares_all_candidates_and_commits_once_with_stable_guid()
    {
        using var directory = new TempDirectory();
        var sourcePath = directory.File("source.xlsx");
        var targetPath = directory.File("target.xlsx");
        const string targetGuid = "00000000000000000000000000000102";
        File.WriteAllBytes(
            sourcePath,
            XlsxWorkbookCodec.Write(IntegrationTestSupport.Workbook(GuidText)));
        File.WriteAllBytes(
            targetPath,
            XlsxWorkbookCodec.Write(
                IntegrationTestSupport.Workbook(targetGuid, id: "shield") with
                {
                    WorkbookGuid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                }));
        var adapter = IntegrationTestSupport.Adapter();
        using var session = new AssetDatabaseSession(adapter).RegisterTable(IntegrationTestSupport.Registration());
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook(sourcePath);
        AssetDatabase.MountWorkbook(targetPath);
        var sourceContainer = sourcePath.Replace('\\', '/');
        var targetContainer = targetPath.Replace('\\', '/');
        var guid = AssetDatabase.GUIDFromAssetPath($"{sourceContainer}/Item/sword");

        Assert.Equal(
            string.Empty,
            AssetDatabase.MoveAsset(
                $"{sourceContainer}/Item/sword",
                $"{targetContainer}/Item/sword_moved"));
        AssetDatabase.SaveAssetIfDirty(guid);

        Assert.DoesNotContain(session.Diagnostics, diagnostic => diagnostic.Diagnostic.IsFailure);
        Assert.Empty(XlsxWorkbookCodec.Read(File.ReadAllBytes(sourcePath)).Tables[0].Rows);
        var targetRows = XlsxWorkbookCodec.Read(File.ReadAllBytes(targetPath)).Tables[0].Rows;
        Assert.Equal(2, targetRows.Length);
        var moved = targetRows.Single(row => row.Cells["id"].Text == "sword_moved");
        Assert.Equal(GuidText, moved.RowGuid?.ToString());
        Assert.Equal($"{targetContainer}/Item/sword_moved", AssetDatabase.GUIDToAssetPath(guid));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, ".exceldb-*.journal.json"));
    }

    private sealed class BracketIntCellFormat : ICellFormat
    {
        public string Identity => "bracket-int:v1";

        public string Describe(CellFormatContext context) => "[integer]";

        public bool TryParse(
            string physicalText,
            CellFormatContext context,
            out string canonicalValue,
            out string? error)
        {
            if (physicalText.Length >= 2 && physicalText[0] == '[' && physicalText[^1] == ']')
            {
                canonicalValue = physicalText[1..^1];
                error = null;
                return true;
            }

            canonicalValue = string.Empty;
            error = "Expected [integer].";
            return false;
        }

        public bool TryWrite(
            string canonicalValue,
            CellFormatContext context,
            out string physicalText,
            out string? error)
        {
            physicalText = $"[{canonicalValue}]";
            error = null;
            return true;
        }
    }
}
