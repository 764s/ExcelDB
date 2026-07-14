using ExcelDb.Core.Values;
using ExcelDb.Core.IO;
using ExcelDb.Workbooks.Authoring;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;

namespace ExcelDb.Workbooks.Tests;

public sealed class SnapshotPersistenceTests
{
    [Fact]
    public void Snapshot_store_roundtrips_raw_and_effective_states_deterministically()
    {
        using var directory = new TempDirectory();
        var workbook = TestData.Workbook(TestData.Row(
            TestData.GuidText(60),
            3,
            "snapshot",
            ratio: WorkbookCell.FormulaCell("1+1", "2")));
        var bytes = XlsxWorkbookCodec.Write(workbook);
        var imported = WorkbookImporter.Import(
            "items.xlsx",
            bytes,
            XlsxWorkbookCodec.Read(bytes),
            TestData.Schema());
        var path = directory.File("snapshot.json");

        ImportSnapshotStore.Save(path, imported.Snapshot);
        var firstBytes = File.ReadAllBytes(path);
        ImportSnapshotStore.Save(path, imported.Snapshot);
        Assert.Equal(firstBytes, File.ReadAllBytes(path));

        var loaded = ImportSnapshotStore.Load(path, "items.xlsx", TestData.SchemaHash);

        Assert.False(loaded.IsDegraded);
        var row = Assert.Single(loaded.Snapshot!.Rows.Values);
        Assert.Equal(CanonicalValueState.Defaulted, row.Values["count"].State);
        Assert.Equal(CanonicalValueState.Missing, row.RawValues["count"].State);
        Assert.Equal(CanonicalValue.FromValue("=1+1"), row.RawValues["ratio"]);
        Assert.Equal("1+1", row.RawCells["ratio"].Formula);
    }

    [Fact]
    public void Missing_corrupt_and_mismatched_snapshots_degrade_without_guessing_a_base()
    {
        using var directory = new TempDirectory();
        var path = directory.File("snapshot.json");

        var missing = ImportSnapshotStore.Load(path, "items.xlsx", TestData.SchemaHash);
        Assert.True(missing.IsDegraded);
        Assert.Contains(missing.Diagnostics, static diagnostic => diagnostic.Code == "EXWB2200");

        File.WriteAllText(path, "{not-json");
        var corrupt = ImportSnapshotStore.Load(path, "items.xlsx", TestData.SchemaHash);
        Assert.True(corrupt.IsDegraded);
        Assert.Contains(corrupt.Diagnostics, static diagnostic => diagnostic.Code == "EXWB2201");

        var workbook = TestData.Workbook(TestData.Row(TestData.GuidText(61), 1, "mismatch"));
        var bytes = XlsxWorkbookCodec.Write(workbook);
        var imported = WorkbookImporter.Import(
            "items.xlsx",
            bytes,
            XlsxWorkbookCodec.Read(bytes),
            TestData.Schema());
        ImportSnapshotStore.Save(path, imported.Snapshot);
        var mismatch = ImportSnapshotStore.Load(path, "other.xlsx", TestData.SchemaHash);
        Assert.True(mismatch.IsDegraded);
        Assert.Contains(mismatch.Diagnostics, static diagnostic => diagnostic.Code == "EXWB2202");
    }

    [Fact]
    public void Real_file_watcher_debounces_bursts_waits_for_stability_and_suppresses_self_writes()
    {
        using var directory = new TempDirectory();
        var path = directory.File("watched.xlsx");
        File.WriteAllText(path, "initial");
        var refreshes = 0;
        var scheduler = new AuthoringRefreshScheduler(() => refreshes++);
        using var watcher = new WorkbookFileWatcher(
            path,
            scheduler,
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(30));

        File.WriteAllText(path, "external-1");
        File.WriteAllText(path, "external-2");
        File.WriteAllText(path, "external-3");
        Assert.True(SpinWait.SpinUntil(
            () => scheduler.Pending.HasFlag(AuthoringRefreshTrigger.Watcher),
            TimeSpan.FromSeconds(5)));
        Assert.True(scheduler.Drain());
        Thread.Sleep(200);
        Assert.Equal(AuthoringRefreshTrigger.None, scheduler.Pending);
        Assert.Equal(1, refreshes);

        var selfBytes = System.Text.Encoding.UTF8.GetBytes("self-write");
        watcher.RecordSelfWrite(ContentFingerprint.FromBytes(selfBytes));
        File.WriteAllBytes(path, selfBytes);
        Thread.Sleep(250);
        Assert.Equal(AuthoringRefreshTrigger.None, scheduler.Pending);
        Assert.Equal(1, refreshes);

        File.WriteAllText(path, "external-after-self");
        Assert.True(SpinWait.SpinUntil(
            () => scheduler.Pending.HasFlag(AuthoringRefreshTrigger.Watcher),
            TimeSpan.FromSeconds(5)));
        Assert.True(scheduler.Drain());
        Assert.Equal(2, refreshes);
    }
}
