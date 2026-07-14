using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Workbooks.Identity;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;

namespace ExcelDb.Workbooks.Tests;

public sealed class ProjectIdentityScannerTests
{
    [Fact]
    public void Duplicate_guid_is_a_project_domain_blocker_with_no_implicit_survivor()
    {
        var guid = TestData.GuidText(11);
        var first = Source("a.xlsx", TestData.Workbook(TestData.Row(guid, 0, "a")));
        var second = Source("b.xlsx", TestData.Workbook(TestData.Row(guid, 0, "b")));

        var result = new ProjectIdentityScanner().Scan([first, second]);

        Assert.True(result.HasBlockers);
        Assert.All(result.Observations, observation =>
        {
            Assert.Equal(IdentityClassification.Duplicated, observation.Classification);
            Assert.Null(observation.CandidateGuid);
        });
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "EXWB1001" && diagnostic.IsBlocker);
    }

    [Fact]
    public void Pending_rows_get_unique_candidates_but_recovered_rows_require_explicit_repair()
    {
        var recoveredGuid = RowGuid.Parse(TestData.GuidText(20));
        var pendingGuid = RowGuid.Parse(TestData.GuidText(21));
        var workbook = TestData.Workbook(
            TestData.Row(null, 3, "old", key: "3:old"),
            TestData.Row(null, 0, "new", key: "3:new"));
        var source = Source("items.xlsx", workbook);
        var result = new ProjectIdentityScanner(new QueueGuidGenerator(pendingGuid)).Scan(
            [source],
            [new KnownIdentity(new AssetIdentity(1, recoveredGuid), "3:old")]);

        Assert.False(result.HasBlockers);
        var recovered = result.Observations[0];
        Assert.Equal(IdentityClassification.Recovered, recovered.Classification);
        Assert.Equal(recoveredGuid, recovered.CandidateGuid);
        var pending = result.Observations[1];
        Assert.Equal(IdentityClassification.PendingNew, pending.Classification);
        Assert.Equal(pendingGuid, pending.CandidateGuid);

        var prepare = IdentityWritePlan.CreateDataPrepare(result, source);
        Assert.Single(prepare.Assignments);
        Assert.Equal(pendingGuid, prepare.Assignments[0].CandidateGuid);
        var repair = IdentityWritePlan.CreateIdentityRepair(result, source, [recovered.Location]);
        Assert.Single(repair.Assignments);
        Assert.Equal(recoveredGuid, repair.Assignments[0].CandidateGuid);
    }

    [Fact]
    public void Malformed_guid_without_unique_recovery_is_a_blocker()
    {
        var row = TestData.Row(null, 0, "bad", rawGuid: "NOT-A-GUID");
        var result = new ProjectIdentityScanner().Scan([Source("bad.xlsx", TestData.Workbook(row))]);

        var observation = Assert.Single(result.Observations);
        Assert.Equal(IdentityClassification.Invalid, observation.Classification);
        Assert.True(result.HasBlockers);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "EXWB1005");
    }

    [Fact]
    public void Hidden_key_projection_is_not_identity_recovery_evidence()
    {
        var recoveredGuid = RowGuid.Parse(TestData.GuidText(22));
        var candidate = RowGuid.Parse(TestData.GuidText(23));
        var workbook = TestData.Workbook(TestData.Row(null, 0, "actual", key: "6:forged"));
        var bytes = XlsxWorkbookCodec.Write(workbook);
        var untrusted = XlsxWorkbookCodec.Read(bytes);
        Assert.True(untrusted.Tables[0].Rows[0].KeyIsProjection);

        var scan = new ProjectIdentityScanner(new QueueGuidGenerator(candidate)).Scan(
            [new WorkbookSource("items.xlsx", untrusted, ContentFingerprint.FromBytes(bytes))],
            [new KnownIdentity(new AssetIdentity(1, recoveredGuid), "6:forged")]);

        var observation = Assert.Single(scan.Observations);
        Assert.Equal(IdentityClassification.PendingNew, observation.Classification);
        Assert.Equal(candidate, observation.CandidateGuid);
    }

    [Fact]
    public void Data_prepare_is_stale_safe_atomic_and_changes_only_identity_cells()
    {
        using var directory = new TempDirectory();
        var path = directory.File("items.xlsx");
        var candidate = RowGuid.Parse(TestData.GuidText(30));
        var initial = TestData.Workbook(TestData.Row(
            null,
            7,
            "alpha",
            WorkbookCell.FormulaCell("6*7", "42"),
            key: "5:alpha"));
        var originalBytes = XlsxWorkbookCodec.Write(initial);
        File.WriteAllBytes(path, originalBytes);

        var source = ReadSource(path);
        var scan = new ProjectIdentityScanner(new QueueGuidGenerator(candidate)).Scan([source]);
        var plan = IdentityWritePlan.CreateDataPrepare(scan, source);

        var changedBytes = XlsxWorkbookCodec.PatchCells(
            originalBytes,
            [new CellPatch("Items", 4, 3, new WorkbookCell("external"))]);
        File.WriteAllBytes(path, changedBytes);
        var stale = IdentityWriteService.Apply(plan);
        Assert.False(stale.Applied);
        Assert.Equal(changedBytes, File.ReadAllBytes(path));

        File.WriteAllBytes(path, originalBytes);
        source = ReadSource(path);
        scan = new ProjectIdentityScanner(new QueueGuidGenerator(candidate)).Scan([source]);
        plan = IdentityWritePlan.CreateDataPrepare(scan, source);
        var applied = IdentityWriteService.Apply(plan);

        Assert.True(applied.Applied);
        var after = XlsxWorkbookCodec.Read(File.ReadAllBytes(path));
        var row = Assert.Single(Assert.Single(after.Tables).Rows);
        Assert.Equal(candidate, row.RowGuid);
        Assert.Equal((uint)7, row.Revision);
        Assert.Equal("6*7", row.Cells["count"].Formula);
        Assert.Equal("42", row.Cells["count"].Text);
        Assert.Equal("5:alpha", row.Key);
    }

    private static WorkbookSource Source(string path, WorkbookDefinition workbook)
    {
        var bytes = XlsxWorkbookCodec.Write(workbook);
        var read = XlsxWorkbookCodec.Read(bytes);
        var imported = WorkbookImporter.Import(path, bytes, read, TestData.Schema());
        return new WorkbookSource(
            path,
            WorkbookIdentityProjection.BindCanonicalKeys(read, imported),
            ContentFingerprint.FromBytes(bytes));
    }

    private static WorkbookSource ReadSource(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var read = XlsxWorkbookCodec.Read(bytes);
        var imported = WorkbookImporter.Import(path, bytes, read, TestData.Schema());
        return new WorkbookSource(
            path,
            WorkbookIdentityProjection.BindCanonicalKeys(read, imported),
            ContentFingerprint.FromBytes(bytes));
    }

    private sealed class QueueGuidGenerator(params RowGuid[] values) : IRowGuidGenerator
    {
        private readonly Queue<RowGuid> _values = new(values);

        public RowGuid Next() => _values.Dequeue();
    }
}
