using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExcelDb.Core.Identity;
using ExcelDb.Core.Values;
using ExcelDb.Workbooks.Authoring;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;
using ExcelDb.Workbooks.Transactions;

namespace ExcelDb.Workbooks.Tests;

public sealed class WritePlanAndTransactionTests
{
    [Fact]
    public void Single_row_save_expands_impact_closure_preserves_unplanned_formula_and_increments_revision_once()
    {
        using var directory = new TempDirectory();
        var path = directory.File("items.xlsx");
        var guid = TestData.GuidText(50);
        var workbook = TestData.Workbook(TestData.Row(
            guid,
            5,
            "alpha",
            WorkbookCell.FormulaCell("20+22", "42"),
            new WorkbookCell("before")));
        var bytes = XlsxWorkbookCodec.Write(workbook);
        File.WriteAllBytes(path, bytes);
        var read = XlsxWorkbookCodec.Read(bytes);
        var imported = WorkbookImporter.Import(path, bytes, read, TestData.Schema());
        var identity = imported.Snapshot.Rows.Keys.Single();
        var drafts = new AuthoringDraftStore(imported.Snapshot);
        drafts.Edit(identity, "note", CanonicalValue.FromValue("middle"));
        drafts.Edit(identity, "note", CanonicalValue.FromValue("after"));
        var extra = new AssetIdentity(1, RowGuid.Parse(TestData.GuidText(51)));
        var closure = new RecordingClosure(extra);

        var plan = WorkbookWritePlan.Create(
            path,
            read,
            imported.Snapshot,
            drafts.Changes,
            closure);

        Assert.True(plan.CanApply);
        Assert.True(closure.Called);
        Assert.Contains(identity, plan.AffectedIdentities);
        Assert.Contains(extra, plan.AffectedIdentities);
        var revision = Assert.Single(plan.Revisions);
        Assert.Equal((uint)5, revision.Before);
        Assert.Equal((uint)6, revision.After);
        Assert.Single(plan.Patches.Where(patch => patch.Column == 6 && patch.Row == 4));

        var report = WorkbookWriteService.Apply(plan);

        Assert.True(report.Applied);
        var after = XlsxWorkbookCodec.Read(File.ReadAllBytes(path));
        var row = Assert.Single(Assert.Single(after.Tables).Rows);
        Assert.Equal((uint)6, row.Revision);
        Assert.Equal("after", row.Cells["note"].Text);
        Assert.Equal("20+22", row.Cells["count"].Formula);
        Assert.Equal("42", row.Cells["count"].Text);
    }

    [Fact]
    public void Revision_overflow_is_a_plan_blocker_and_new_rows_start_at_zero()
    {
        using var directory = new TempDirectory();
        var overflowPath = directory.File("overflow.xlsx");
        var overflowWorkbook = TestData.Workbook(TestData.Row(TestData.GuidText(52), uint.MaxValue, "overflow"));
        var overflowBytes = XlsxWorkbookCodec.Write(overflowWorkbook);
        File.WriteAllBytes(overflowPath, overflowBytes);
        var overflowRead = XlsxWorkbookCodec.Read(overflowBytes);
        var overflowImport = WorkbookImporter.Import(
            overflowPath,
            overflowBytes,
            overflowRead,
            TestData.Schema());
        var drafts = new AuthoringDraftStore(overflowImport.Snapshot);
        drafts.Edit(overflowImport.Snapshot.Rows.Keys.Single(), "note", CanonicalValue.FromValue("x"));

        var overflowPlan = WorkbookWritePlan.Create(
            overflowPath,
            overflowRead,
            overflowImport.Snapshot,
            drafts.Changes);

        Assert.False(overflowPlan.CanApply);
        Assert.Contains(overflowPlan.Diagnostics, diagnostic => diagnostic.Code == "EXWB3005" && diagnostic.IsBlocker);

        var newPath = directory.File("new.xlsx");
        var empty = TestData.Workbook();
        var emptyBytes = XlsxWorkbookCodec.Write(empty);
        File.WriteAllBytes(newPath, emptyBytes);
        var emptyRead = XlsxWorkbookCodec.Read(emptyBytes);
        var emptyImport = WorkbookImporter.Import(newPath, emptyBytes, emptyRead, TestData.Schema());
        var newIdentity = new AssetIdentity(1, RowGuid.Parse(TestData.GuidText(53)));
        var newRow = new SnapshotRow(
            newIdentity,
            "3:new",
            0,
            ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
            {
                new KeyValuePair<string, CanonicalValue>("id", CanonicalValue.FromValue("new")),
                new KeyValuePair<string, CanonicalValue>("count", CanonicalValue.FromDefault("7")),
                new KeyValuePair<string, CanonicalValue>("note", CanonicalValue.Missing),
                new KeyValuePair<string, CanonicalValue>("ratio", CanonicalValue.Missing),
            }),
            ImmutableDictionary<string, WorkbookCell>.Empty);
        var newDrafts = new AuthoringDraftStore(emptyImport.Snapshot);
        newDrafts.Add(newRow);
        var newPlan = WorkbookWritePlan.Create(
            newPath,
            emptyRead,
            emptyImport.Snapshot,
            newDrafts.Changes);

        var newReport = WorkbookWriteService.Apply(newPlan);

        Assert.True(newReport.Applied);
        var written = Assert.Single(Assert.Single(XlsxWorkbookCodec.Read(File.ReadAllBytes(newPath)).Tables).Rows);
        Assert.Equal((uint)0, written.Revision);
        Assert.Equal(newIdentity.RowGuid, written.RowGuid);
        var writtenBytes = File.ReadAllBytes(newPath);
        Assert.Equal("1:new", XlsxWorkbookCodec.ReadCell(writtenBytes, WorkbookProtocol.KeySheetName, 2, 5)!.Text);
        Assert.Equal("1:new", XlsxWorkbookCodec.ReadCell(writtenBytes, WorkbookProtocol.KeySheetName, 2, 6)!.Text);
    }

    [Fact]
    public void Multi_workbook_journal_recovers_a_partial_replacement_to_the_complete_old_set()
    {
        using var directory = new TempDirectory();
        var first = CreatePreparedEdit(directory.File("first.xlsx"), TestData.GuidText(60), "first-new");
        var second = CreatePreparedEdit(directory.File("second.xlsx"), TestData.GuidText(61), "second-new");
        var firstOriginal = File.ReadAllBytes(first.Plan.WorkbookPath);
        var secondOriginal = File.ReadAllBytes(second.Plan.WorkbookPath);

        var staged = MultiWorkbookTransaction.Stage([first, second], directory.Path);
        Assert.True(staged.Report.Succeeded);
        Assert.NotNull(staged.JournalPath);
        var jsonOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var journal = JsonSerializer.Deserialize<WorkbookTransactionJournal>(
            File.ReadAllBytes(staged.JournalPath),
            jsonOptions);
        Assert.NotNull(journal);
        File.Move(journal.Items[0].StagePath, journal.Items[0].TargetPath, overwrite: true);
        Assert.NotEqual(firstOriginal, File.ReadAllBytes(first.Plan.WorkbookPath));

        var recovery = AuthoringStartupRecovery.RecoverWorkbookDirectoryBeforeImport(first.Plan.WorkbookPath);

        Assert.True(recovery.Applied);
        Assert.Equal(firstOriginal, File.ReadAllBytes(first.Plan.WorkbookPath));
        Assert.Equal(secondOriginal, File.ReadAllBytes(second.Plan.WorkbookPath));
        Assert.False(File.Exists(staged.JournalPath));
    }

    [Fact]
    public void Multi_workbook_commit_publishes_all_staged_outputs()
    {
        using var directory = new TempDirectory();
        var first = CreatePreparedEdit(directory.File("first.xlsx"), TestData.GuidText(62), "first-new");
        var second = CreatePreparedEdit(directory.File("second.xlsx"), TestData.GuidText(63), "second-new");

        var result = MultiWorkbookTransaction.Commit([first, second], directory.Path);

        Assert.True(result.Report.Applied);
        Assert.Null(result.JournalPath);
        Assert.Equal(
            "first-new",
            XlsxWorkbookCodec.Read(File.ReadAllBytes(first.Plan.WorkbookPath)).Tables[0].Rows[0].Cells["note"].Text);
        Assert.Equal(
            "second-new",
            XlsxWorkbookCodec.Read(File.ReadAllBytes(second.Plan.WorkbookPath)).Tables[0].Rows[0].Cells["note"].Text);
    }

    private static PreparedWorkbookWrite CreatePreparedEdit(string path, string guid, string note)
    {
        var workbook = TestData.Workbook(TestData.Row(guid, 1, "id", note: new WorkbookCell("old")));
        var bytes = XlsxWorkbookCodec.Write(workbook);
        File.WriteAllBytes(path, bytes);
        var read = XlsxWorkbookCodec.Read(bytes);
        var imported = WorkbookImporter.Import(path, bytes, read, TestData.Schema());
        var drafts = new AuthoringDraftStore(imported.Snapshot);
        drafts.Edit(imported.Snapshot.Rows.Keys.Single(), "note", CanonicalValue.FromValue(note));
        var plan = WorkbookWritePlan.Create(path, read, imported.Snapshot, drafts.Changes);
        return WorkbookWriteService.Prepare(plan);
    }

    private sealed class RecordingClosure(AssetIdentity extra) : IImpactClosureProvider
    {
        public bool Called { get; private set; }

        public ImmutableHashSet<AssetIdentity> Expand(
            IEnumerable<AssetIdentity> seeds,
            ImportSnapshot snapshot)
        {
            Called = true;
            return seeds.Append(extra).ToImmutableHashSet();
        }
    }
}
