using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ExcelDb;
using ExcelDb.Database;
using ExcelDb.Excel;
using Game.Configs;
using Xunit;

namespace ExcelDb.Core.Tests;

public class ExcelTableLoaderTests
{
    [Fact]
    public void WriteRead_RoundTripsRealXlsxWorkbook()
    {
        var path = TempWorkbookPath();
        try
        {
            var registry = Fixture.NewRegistry();
            var source = Fixture.StandardLoader();
            var writer = new ExcelTableLoader(path, registry, new ExcelTableLoaderOptions { CreateIfMissing = true });
            foreach (var table in writer.Tables)
                writer.Write(table, source.Load(table));

            using (var archive = ZipFile.OpenRead(path))
            {
                Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
                Assert.NotNull(archive.GetEntry("xl/worksheets/sheet2.xml"));
            }

            var reader = new ExcelTableLoader(path, registry);
            var skills = reader.Load(new TableId(Fixture.SkillTable));
            var fireball = (SkillConfig)skills.Rows.Single(row => row.Id == 1).Row;

            Assert.Equal("fireball", fireball.Key);
            Assert.Equal(10, fireball.Cost);
            Assert.Equal(Fixture.BuffTable, fireball.Payload.Table);
            Assert.Equal(Fixture.Burn.Id, fireball.Payload.Id);
            Assert.Equal(Fixture.Icenova.Id, fireball.Chains[0].Id);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public void DatabaseEdit_SaveAssets_WritesBackToWorkbook()
    {
        var path = TempWorkbookPath();
        try
        {
            var registry = Fixture.NewRegistry();
            var source = Fixture.StandardLoader();
            var writer = new ExcelTableLoader(path, registry, new ExcelTableLoaderOptions { CreateIfMissing = true });
            foreach (var table in writer.Tables)
                writer.Write(table, source.Load(table));

            var loader = new ExcelTableLoader(path, registry);
            var db = ConfigDatabase.Open(registry, packs => packs.Mount("excel", loader));

            using (var tx = db.BeginEdit("fireball cost"))
            {
                tx.GetMutable<SkillConfig>(Fixture.Fireball).Cost = 77;
                tx.Commit();
            }

            Assert.Contains("SkillConfig", db.Info.DirtyTables);
            db.SaveAssets();

            var reread = new ExcelTableLoader(path, registry);
            var skill = (SkillConfig)reread.Load(new TableId(Fixture.SkillTable)).Rows.Single(row => row.Id == 1).Row;

            Assert.Equal(77, skill.Cost);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public void SaveAssets_CreatesBackupWorkbook()
    {
        var path = TempWorkbookPath();
        try
        {
            var registry = Fixture.NewRegistry();
            var source = Fixture.StandardLoader();
            var writer = new ExcelTableLoader(path, registry, new ExcelTableLoaderOptions { CreateIfMissing = true });
            foreach (var table in writer.Tables)
                writer.Write(table, source.Load(table));

            var db = ConfigDatabase.Open(registry, packs => packs.Mount("excel", new ExcelTableLoader(path, registry)));
            using (var tx = db.BeginEdit("fireball cost"))
            {
                tx.GetMutable<SkillConfig>(Fixture.Fireball).Cost = 88;
                tx.Commit();
            }

            db.SaveAssets();

            Assert.True(File.Exists(path + ".bak"));
            var reread = new ExcelTableLoader(path, registry);
            var skill = (SkillConfig)reread.Load(new TableId(Fixture.SkillTable)).Rows.Single(row => row.Id == 1).Row;
            Assert.Equal(88, skill.Cost);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public void SaveAssets_RejectsExternallyModifiedWorkbook()
    {
        var path = TempWorkbookPath();
        try
        {
            var registry = Fixture.NewRegistry();
            var source = Fixture.StandardLoader();
            var writer = new ExcelTableLoader(path, registry, new ExcelTableLoaderOptions { CreateIfMissing = true });
            foreach (var table in writer.Tables)
                writer.Write(table, source.Load(table));

            var db = ConfigDatabase.Open(registry, packs => packs.Mount("excel", new ExcelTableLoader(path, registry)));
            using (var tx = db.BeginEdit("fireball cost"))
            {
                tx.GetMutable<SkillConfig>(Fixture.Fireball).Cost = 99;
                tx.Commit();
            }

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

            Assert.Throws<IOException>(() => db.SaveAssets());
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public void FixedRowRef_CanBeEditedWithBareId()
    {
        var path = TempWorkbookPath();
        try
        {
            var registry = Fixture.NewRegistry();
            var source = Fixture.StandardLoader();
            var writer = new ExcelTableLoader(path, registry, new ExcelTableLoaderOptions { CreateIfMissing = true });
            foreach (var table in writer.Tables)
                writer.Write(table, source.Load(table));

            var db = ConfigDatabase.Open(registry, packs => packs.Mount("excel", new ExcelTableLoader(path, registry)));
            using (var tx = db.BeginEdit("retarget chain"))
            {
                var draft = tx.GetMutable<SkillConfig>(Fixture.Fireball);
                var field = ExcelFieldCodec.FindField(SkillConfig.Descriptor, "chains")!;
                ExcelFieldCodec.SetField(draft, field, "1", registry);
                tx.Commit();
            }

            Assert.Equal(Fixture.Fireball.Id, db.LoadAsset<SkillConfig>(Fixture.Fireball).Chains[0].Id);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    static string TempWorkbookPath() =>
        Path.Combine(Path.GetTempPath(), "exceldb-" + Guid.NewGuid().ToString("N") + ".xlsx");

    static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        if (File.Exists(path + ".bak"))
            File.Delete(path + ".bak");
        if (File.Exists(path + ".tmp"))
            File.Delete(path + ".tmp");
    }
}
