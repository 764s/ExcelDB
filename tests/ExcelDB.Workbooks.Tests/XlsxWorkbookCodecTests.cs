using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;

namespace ExcelDb.Workbooks.Tests;

public sealed class XlsxWorkbookCodecTests
{
    [Fact]
    public void V1_roundtrip_has_three_headers_hidden_protocol_sheets_and_trailing_system_columns()
    {
        var formula = WorkbookCell.FormulaCell("1+2", "3");
        var workbook = TestData.Workbook(TestData.Row(
            TestData.GuidText(1),
            9,
            "sword",
            formula,
            new WorkbookCell(WorkbookProtocol.ExplicitNullToken)));

        var bytes = XlsxWorkbookCodec.Write(workbook);

        Assert.Equal("id", XlsxWorkbookCodec.ReadCell(bytes, "Items", 1, 1)!.Text);
        Assert.Equal("id", XlsxWorkbookCodec.ReadCell(bytes, "Items", 2, 1)!.Text);
        Assert.Equal("string", XlsxWorkbookCodec.ReadCell(bytes, "Items", 3, 1)!.Text);
        Assert.Equal(WorkbookProtocol.GuidColumnName, XlsxWorkbookCodec.ReadCell(bytes, "Items", 2, 5)!.Text);
        Assert.Equal(WorkbookProtocol.RevisionColumnName, XlsxWorkbookCodec.ReadCell(bytes, "Items", 2, 6)!.Text);
        Assert.True(XlsxWorkbookCodec.IsSheetHidden(bytes, WorkbookProtocol.MetadataSheetName));
        Assert.True(XlsxWorkbookCodec.IsSheetHidden(bytes, WorkbookProtocol.KeySheetName));

        var read = XlsxWorkbookCodec.Read(bytes);
        var row = Assert.Single(Assert.Single(read.Tables).Rows);
        Assert.Equal(TestData.GuidText(1), row.RowGuid!.Value.ToString());
        Assert.Equal((uint)9, row.Revision);
        Assert.Equal("1+2", row.Cells["count"].Formula);
        Assert.Equal("3", row.Cells["count"].Text);
        Assert.Equal(WorkbookProtocol.ExplicitNullToken, row.Cells["note"].Text);
    }

    [Fact]
    public void Cell_patch_preserves_unknown_parts_and_unplanned_formula_text()
    {
        var workbook = TestData.Workbook(TestData.Row(
            TestData.GuidText(2),
            1,
            "old",
            WorkbookCell.FormulaCell("40+2", "42")));
        var unknown = new byte[] { 1, 3, 3, 7, 9 };
        var source = AddPart(XlsxWorkbookCodec.Write(workbook), "custom/opaque.bin", unknown);

        var patched = XlsxWorkbookCodec.PatchCells(
            source,
            [new CellPatch("Items", 4, 1, new WorkbookCell("new"))]);

        Assert.Equal(unknown, ReadPart(patched, "custom/opaque.bin"));
        Assert.Equal("new", XlsxWorkbookCodec.ReadCell(patched, "Items", 4, 1)!.Text);
        var formula = XlsxWorkbookCodec.ReadCell(patched, "Items", 4, 2)!;
        Assert.Equal("40+2", formula.Formula);
        Assert.Equal("42", formula.Text);
        var fingerprint = XlsxWorkbookCodec.Fingerprint(patched);
        Assert.Equal(patched.Length, fingerprint.Length);
        Assert.Equal(64, fingerprint.Sha256.Length);
    }

    [Fact]
    public void RowReferenceColumnsBakeExcelValidationAgainstTheHiddenTokenProjection()
    {
        var workbook = TestData.Workbook(TestData.Row(TestData.GuidText(3), 1, "holder"));
        var table = workbook.Tables[0];
        var rowRefColumn = table.Columns[2] with { TypeName = "exceldb.RowRef" };
        var projected = workbook with
        {
            Tables = [table with { Columns = table.Columns.SetItem(2, rowRefColumn) }],
        };

        var created = XlsxWorkbookCodec.Write(projected);
        var rewritten = XlsxWorkbookCodec.Project(XlsxWorkbookCodec.Write(workbook), projected);

        foreach (var bytes in new[] { created, rewritten })
        {
            var worksheet = Encoding.UTF8.GetString(ReadPart(bytes, "xl/worksheets/sheet1.xml"));
            Assert.Contains("dataValidations", worksheet, StringComparison.Ordinal);
            Assert.Contains("ExcelDB RowRef", worksheet, StringComparison.Ordinal);
            Assert.Contains("C4:C1048576", worksheet, StringComparison.Ordinal);
            Assert.Contains("__exceldb_keys", worksheet, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RowReferenceValidationUsesTheDeclaredTargetTablesProjectionColumn()
    {
        var workbook = TestData.Workbook(TestData.Row(TestData.GuidText(4), 1, "holder"));
        var source = workbook.Tables[0];
        var target = source with
        {
            TableId = 2,
            ProtoName = "Target",
            SheetName = "Targets",
            Rows = [TestData.Row(TestData.GuidText(5), 1, "target")],
        };
        var referenceColumn = source.Columns[2] with
        {
            TypeName = "exceldb.RowRef",
            ReferenceTable = "game.Target",
        };
        workbook = workbook with
        {
            Tables = [source with { Columns = source.Columns.SetItem(2, referenceColumn) }, target],
        };

        var bytes = XlsxWorkbookCodec.Write(workbook);
        var worksheet = Encoding.UTF8.GetString(ReadPart(bytes, "xl/worksheets/sheet1.xml"));

        // F is table 1 and G is table 2 in the hidden projection.
        Assert.Contains("$G$2:$G$1048576", worksheet, StringComparison.Ordinal);
        Assert.DoesNotContain("$E$2:$E$1048576", worksheet, StringComparison.Ordinal);
    }

    [Fact]
    public void Descriptor_aware_inspection_recovers_missing_metadata_without_writing_and_applies_explicit_repair()
    {
        var workbook = TestData.Workbook(TestData.Row(
            TestData.GuidText(6),
            7,
            "recover",
            WorkbookCell.FormulaCell("40+2", "42")));
        var opaque = new byte[] { 2, 7, 1, 8, 2, 8 };
        var source = AddPart(XlsxWorkbookCodec.Write(workbook), "custom/repair-opaque.bin", opaque);
        var tablePart = ReadSheetRegistration(source, "Items").PartName;
        var tableBefore = ReadPart(source, tablePart);
        var missingMetadata = RemoveSheet(source, WorkbookProtocol.MetadataSheetName);
        var beforeInspection = missingMetadata.ToArray();
        Assert.Throws<InvalidDataException>(() => XlsxWorkbookCodec.Read(missingMetadata));

        var inspection = XlsxWorkbookCodec.Inspect(missingMetadata, TestData.Schema());

        Assert.True(inspection.HasDrift);
        Assert.NotNull(inspection.RepairPlan);
        Assert.Contains(inspection.Diagnostics, static diagnostic => diagnostic.Code == "EXWB1200");
        var inspectedRow = Assert.Single(Assert.Single(inspection.Workbook.Tables).Rows);
        Assert.Equal("recover", inspectedRow.Cells["id"].Text);
        Assert.Equal("40+2", inspectedRow.Cells["count"].Formula);
        Assert.Equal(beforeInspection, missingMetadata);

        var repaired = XlsxWorkbookCodec.ApplyProjectionRepair(missingMetadata, inspection.RepairPlan!);

        Assert.True(repaired.Applied);
        var read = XlsxWorkbookCodec.Read(repaired.Bytes);
        var row = Assert.Single(Assert.Single(read.Tables).Rows);
        Assert.Equal(TestData.GuidText(6), row.RowGuid?.ToString());
        Assert.Equal((uint)7, row.Revision);
        Assert.Equal("40+2", row.Cells["count"].Formula);
        Assert.Equal(opaque, ReadPart(repaired.Bytes, "custom/repair-opaque.bin"));
        Assert.Equal(tableBefore, ReadPart(repaired.Bytes, tablePart));
        Assert.False(XlsxWorkbookCodec.Inspect(repaired.Bytes, TestData.Schema()).HasDrift);
    }

    [Fact]
    public void Projection_repair_plan_is_fingerprint_bound_and_stale_apply_is_zero_write()
    {
        var workbook = TestData.Workbook(TestData.Row(TestData.GuidText(7), 1, "stale"));
        var missingMetadata = RemoveSheet(
            XlsxWorkbookCodec.Write(workbook),
            WorkbookProtocol.MetadataSheetName);
        var plan = Assert.IsType<WorkbookProjectionRepairPlan>(
            XlsxWorkbookCodec.Inspect(missingMetadata, TestData.Schema()).RepairPlan);
        var changed = XlsxWorkbookCodec.PatchCells(
            missingMetadata,
            [new CellPatch("Items", 4, 3, new WorkbookCell("external"))]);

        var result = XlsxWorkbookCodec.ApplyProjectionRepair(changed, plan);

        Assert.False(result.Applied);
        Assert.Equal(changed, result.Bytes);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "EXWB1206" && diagnostic.IsBlocker);
    }

    [Fact]
    public void Projection_repair_roundtrips_existing_migration_markers()
    {
        var workbook = TestData.Workbook(TestData.Row(TestData.GuidText(8), 1, "marker")) with
        {
            MigrationMarkers = ["key-rewrite@2", "normalize-cells@1"],
        };
        var missingKeys = RemoveSheet(
            XlsxWorkbookCodec.Write(workbook),
            WorkbookProtocol.KeySheetName);

        var inspection = XlsxWorkbookCodec.Inspect(missingKeys, TestData.Schema());
        Assert.Equal(workbook.MigrationMarkers.Order(), inspection.Workbook.EffectiveMigrationMarkers.Order());
        var repaired = XlsxWorkbookCodec.ApplyProjectionRepair(missingKeys, inspection.RepairPlan!);

        Assert.True(repaired.Applied);
        Assert.Equal(
            workbook.MigrationMarkers.Order(),
            XlsxWorkbookCodec.Read(repaired.Bytes).EffectiveMigrationMarkers.Order());
    }

    [Fact]
    public void Project_preserves_unowned_package_and_worksheet_content_and_is_deterministic()
    {
        var workbook = TestData.Workbook(TestData.Row(
            TestData.GuidText(3),
            4,
            "old",
            WorkbookCell.FormulaCell("40+2", "42")));
        var opaque = new byte[] { 9, 8, 7, 6, 5 };
        var source = AddPart(XlsxWorkbookCodec.Write(workbook), "custom/opaque.bin", opaque);
        source = AddFreeSheet(source, "Notes", "xl/worksheets/notes.xml", "free");
        source = XlsxWorkbookCodec.PatchCells(source,
        [
            new CellPatch("Items", 4, 8, WorkbookCell.FormulaCell("6*7", "42")),
        ]);
        source = TransformXmlPart(source, "xl/worksheets/sheet1.xml", document =>
        {
            var managedCell = document.Descendants(Spreadsheet + "c")
                .Single(element => element.Attribute("r")?.Value == "A4");
            managedCell.SetAttributeValue("s", "0");
            managedCell.SetAttributeValue("preserve-marker", "yes");
            document.Root!.Add(new XElement(
                Spreadsheet + "extLst",
                new XElement(
                    Spreadsheet + "ext",
                    new XAttribute("uri", "urn:exceldb:test"),
                    new XElement(TestExtension + "payload", "keep"))));
        });
        var freeSheetBefore = ReadPart(source, "xl/worksheets/notes.xml");
        var registrationBefore = ReadSheetRegistration(source, "Items");

        var projectedTable = workbook.Tables[0] with
        {
            SheetName = "Renamed Items",
            Rows = [workbook.Tables[0].Rows[0] with
            {
                Cells = workbook.Tables[0].Rows[0].Cells.SetItem("id", new WorkbookCell("new")),
            }],
        };
        var projected = workbook with { Tables = [projectedTable] };

        var rewritten = XlsxWorkbookCodec.Project(source, projected);

        Assert.Equal(opaque, ReadPart(rewritten, "custom/opaque.bin"));
        Assert.Equal(freeSheetBefore, ReadPart(rewritten, "xl/worksheets/notes.xml"));
        Assert.Equal("free", XlsxWorkbookCodec.ReadCell(rewritten, "Notes", 1, 1)!.Text);
        Assert.Equal("new", XlsxWorkbookCodec.ReadCell(rewritten, "Renamed Items", 4, 1)!.Text);
        var helperFormula = XlsxWorkbookCodec.ReadCell(rewritten, "Renamed Items", 4, 8)!;
        Assert.Equal("6*7", helperFormula.Formula);
        Assert.Equal("42", helperFormula.Text);
        var registrationAfter = ReadSheetRegistration(rewritten, "Renamed Items");
        Assert.Equal(registrationBefore, registrationAfter);

        var projectedWorksheet = XDocument.Parse(
            Encoding.UTF8.GetString(ReadPart(rewritten, registrationAfter.PartName)));
        var preservedManagedCell = projectedWorksheet.Descendants(Spreadsheet + "c")
            .Single(element => element.Attribute("r")?.Value == "A4");
        Assert.Equal("0", preservedManagedCell.Attribute("s")?.Value);
        Assert.Equal("yes", preservedManagedCell.Attribute("preserve-marker")?.Value);
        Assert.Equal(
            "keep",
            Assert.Single(projectedWorksheet.Descendants(TestExtension + "payload")).Value);

        Assert.Equal(rewritten, XlsxWorkbookCodec.Rewrite(rewritten, projected));
    }

    [Fact]
    public void Project_blocks_new_managed_column_collision_unless_purge_is_explicit()
    {
        var workbook = TestData.Workbook(TestData.Row(TestData.GuidText(4), 11, "sword"));
        var source = XlsxWorkbookCodec.PatchCells(
            XlsxWorkbookCodec.Write(workbook),
            [
                new CellPatch("Items", 4, 7, new WorkbookCell("collision")),
                new CellPatch("Items", 4, 8, new WorkbookCell("keep")),
            ]);
        var projectedTable = workbook.Tables[0] with
        {
            Columns = workbook.Tables[0].Columns.Add(
                new WorkbookColumn("extra", "extra", "string", "5")),
        };
        var projected = workbook with { Tables = [projectedTable] };

        var error = Assert.Throws<InvalidDataException>(() =>
            XlsxWorkbookCodec.Project(source, projected));

        Assert.Contains("G4", error.Message, StringComparison.Ordinal);
        Assert.Contains("explicit purge", error.Message, StringComparison.OrdinalIgnoreCase);

        var purged = XlsxWorkbookCodec.Project(source, projected, purgeUnownedCells: true);
        Assert.Equal(WorkbookProtocol.RevisionColumnName, XlsxWorkbookCodec.ReadCell(purged, "Items", 2, 7)!.Text);
        Assert.Equal("11", XlsxWorkbookCodec.ReadCell(purged, "Items", 4, 7)!.Text);
        Assert.Equal("keep", XlsxWorkbookCodec.ReadCell(purged, "Items", 4, 8)!.Text);
    }

    [Fact]
    public void Project_supports_table_rename_add_and_remove_with_valid_registrations()
    {
        var baseWorkbook = TestData.Workbook(TestData.Row(TestData.GuidText(5), 1, "base"));
        var firstTable = baseWorkbook.Tables[0];
        var removedTable = firstTable with
        {
            TableId = 3,
            ProtoName = "Legacy",
            SheetName = "Legacy",
            Rows = [],
        };
        var sourceWorkbook = baseWorkbook with { Tables = [firstTable, removedTable] };
        var source = XlsxWorkbookCodec.Write(sourceWorkbook);
        var firstRegistration = ReadSheetRegistration(source, "Items");
        var removedRegistration = ReadSheetRegistration(source, "Legacy");
        var addedTable = firstTable with
        {
            TableId = 2,
            ProtoName = "Added",
            SheetName = "Added",
            Rows = [],
        };
        var projected = sourceWorkbook with
        {
            Tables = [firstTable with { SheetName = "Inventory" }, addedTable],
        };

        var rewritten = XlsxWorkbookCodec.Project(source, projected);

        var read = XlsxWorkbookCodec.Read(rewritten);
        Assert.Equal([1, 2], read.Tables.Select(static table => table.TableId).ToArray());
        Assert.Equal(firstRegistration, ReadSheetRegistration(rewritten, "Inventory"));
        var addedRegistration = ReadSheetRegistration(rewritten, "Added");
        Assert.NotEqual(firstRegistration.PartName, addedRegistration.PartName);
        Assert.NotNull(ReadPartOrNull(rewritten, addedRegistration.PartName));
        Assert.Null(ReadPartOrNull(rewritten, removedRegistration.PartName));
        Assert.Throws<InvalidDataException>(() => XlsxWorkbookCodec.ReadCell(rewritten, "Legacy", 1, 1));

        var contentTypes = XDocument.Parse(Encoding.UTF8.GetString(ReadPart(rewritten, "[Content_Types].xml")));
        Assert.Contains(
            contentTypes.Descendants(ContentTypes + "Override"),
            element => element.Attribute("PartName")?.Value == "/" + addedRegistration.PartName);
        Assert.DoesNotContain(
            contentTypes.Descendants(ContentTypes + "Override"),
            element => element.Attribute("PartName")?.Value == "/" + removedRegistration.PartName);
    }

    private static readonly XNamespace Spreadsheet =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRelationships =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships =
        "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes =
        "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace TestExtension = "urn:exceldb:test-extension";

    private static byte[] AddFreeSheet(byte[] package, string sheetName, string partName, string value)
    {
        package = TransformXmlPart(package, "xl/workbook.xml", document =>
        {
            var sheets = document.Root!.Element(Spreadsheet + "sheets")!;
            sheets.Add(new XElement(
                Spreadsheet + "sheet",
                new XAttribute("name", sheetName),
                new XAttribute("sheetId", 99),
                new XAttribute(OfficeRelationships + "id", "rId99")));
        });
        package = TransformXmlPart(package, "xl/_rels/workbook.xml.rels", document =>
        {
            document.Root!.Add(new XElement(
                PackageRelationships + "Relationship",
                new XAttribute("Id", "rId99"),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target", partName[3..])));
        });
        package = TransformXmlPart(package, "[Content_Types].xml", document =>
        {
            document.Root!.Add(new XElement(
                ContentTypes + "Override",
                new XAttribute("PartName", "/" + partName),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
        });
        var cell = new XElement(
            Spreadsheet + "c",
            new XAttribute("r", "A1"),
            new XAttribute("t", "inlineStr"),
            new XElement(
                Spreadsheet + "is",
                new XElement(Spreadsheet + "t", value)));
        var worksheet = new XDocument(
            new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(
                Spreadsheet + "worksheet",
                new XElement(
                    Spreadsheet + "sheetData",
                    new XElement(
                        Spreadsheet + "row",
                        new XAttribute("r", 1),
                        cell))));
        return AddPart(package, partName, Encoding.UTF8.GetBytes(worksheet.ToString(SaveOptions.DisableFormatting)));
    }

    private static byte[] TransformXmlPart(byte[] package, string path, Action<XDocument> transform)
    {
        using var stream = new MemoryStream();
        stream.Write(package);
        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry(path);
            Assert.NotNull(entry);
            string xml;
            using (var input = entry.Open())
            using (var reader = new StreamReader(input, Encoding.UTF8))
                xml = reader.ReadToEnd();
            entry.Delete();

            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            transform(document);
            var replacement = archive.CreateEntry(path);
            using var output = replacement.Open();
            using var writer = new StreamWriter(output, new UTF8Encoding(false));
            document.Save(writer, SaveOptions.DisableFormatting);
        }

        return stream.ToArray();
    }

    private static byte[] RemoveSheet(byte[] package, string sheetName)
    {
        var registration = ReadSheetRegistration(package, sheetName);
        package = TransformXmlPart(package, "xl/workbook.xml", document =>
        {
            document.Descendants(Spreadsheet + "sheet")
                .Single(element => element.Attribute("name")?.Value == sheetName)
                .Remove();
        });
        package = TransformXmlPart(package, "xl/_rels/workbook.xml.rels", document =>
        {
            document.Descendants(PackageRelationships + "Relationship")
                .Single(element => element.Attribute("Id")?.Value == registration.RelationshipId)
                .Remove();
        });
        package = TransformXmlPart(package, "[Content_Types].xml", document =>
        {
            document.Descendants(ContentTypes + "Override")
                .Single(element => element.Attribute("PartName")?.Value == "/" + registration.PartName)
                .Remove();
        });
        using var stream = new MemoryStream();
        stream.Write(package);
        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
            archive.GetEntry(registration.PartName)!.Delete();
        return stream.ToArray();
    }

    private static (string RelationshipId, string PartName) ReadSheetRegistration(byte[] package, string sheetName)
    {
        var workbook = XDocument.Parse(Encoding.UTF8.GetString(ReadPart(package, "xl/workbook.xml")));
        var sheet = workbook.Descendants(Spreadsheet + "sheet")
            .Single(element => element.Attribute("name")?.Value == sheetName);
        var relationshipId = sheet.Attribute(OfficeRelationships + "id")!.Value;
        var relationships = XDocument.Parse(
            Encoding.UTF8.GetString(ReadPart(package, "xl/_rels/workbook.xml.rels")));
        var target = relationships.Descendants(PackageRelationships + "Relationship")
            .Single(element => element.Attribute("Id")?.Value == relationshipId)
            .Attribute("Target")!.Value;
        var partName = target.StartsWith("/", StringComparison.Ordinal)
            ? target.TrimStart('/')
            : "xl/" + target;
        return (relationshipId, partName);
    }

    private static byte[] AddPart(byte[] package, string path, byte[] content)
    {
        using var stream = new MemoryStream();
        stream.Write(package);
        stream.Position = 0;
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.CreateEntry(path);
            using var output = entry.Open();
            output.Write(content);
        }

        return stream.ToArray();
    }

    private static byte[]? ReadPartOrNull(byte[] package, string path)
    {
        using var stream = new MemoryStream(package, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(path);
        if (entry is null)
            return null;
        using var input = entry.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] ReadPart(byte[] package, string path)
    {
        using var stream = new MemoryStream(package, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(path);
        Assert.NotNull(entry);
        using var input = entry.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
