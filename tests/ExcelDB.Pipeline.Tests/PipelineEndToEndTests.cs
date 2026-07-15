using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ExcelDb.Core.IO;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Values;
using ExcelDb.Compatibility.Migrations;
using ExcelDb.Pipeline;
using ExcelDb.Runtime.Bytes;
using ExcelDb.Schema.Authoring;
using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Mutation;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.OpenXml;
using ExcelDb.Workbooks.References;

namespace ExcelDb.Pipeline.Tests;

public sealed class PipelineEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exceldb-pipeline-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EmptyProjectCompletesCreatePrepareCheckAndConvertLoop()
    {
        Initialize();
        var project = Load();
        var schemaPipeline = new SchemaPipeline("test-v1");
        var workbookPipeline = new WorkbookPipeline("test-v1", schemaPipeline);

        var create = await schemaPipeline.CreateTableAsync(project, new TableCreateIntent(
            "Hero",
            "Excel/game.xlsx",
            [new SimpleFieldDefinition("name", SimpleFieldType.String), new SimpleFieldDefinition("hp", SimpleFieldType.Int32)]));
        Assert.False(create.HasBlockers);
        Assert.True(MutationPlanCodec.Validate(create));
        Assert.Contains(create.InputSets, item => item.Kind == InputSetKind.SchemaProto);
        Assert.Contains(create.InputSets, item => item.Kind == InputSetKind.ExcelWorkbook);
        Assert.Contains(create.InputSets, item => item.Kind == InputSetKind.SystemProtoMirror);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(create).ExitCode);
        Assert.True(File.Exists(Path.Combine(_root, "Schema", "Hero.proto")));
        Assert.True(File.Exists(Path.Combine(_root, "Generated", "CSharp", "Authoring", "ExcelDbSchema.Authoring.g.cs")));

        var workbookPath = Path.Combine(_root, "Excel", "game.xlsx");
        AddPendingRow(workbookPath, "hero_1", "First", "10");
        var beforeCheck = ContentFingerprint.FromFile(workbookPath);
        var pendingCheck = await workbookPipeline.CheckAsync(project);
        Assert.Equal(1, pendingCheck.PendingIdentityCount);
        Assert.Equal(1, (int)pendingCheck.Report.ExitCode);
        Assert.Equal(beforeCheck, ContentFingerprint.FromFile(workbookPath));

        var stale = await workbookPipeline.DataPrepareAsync(project);
        var independentlyPlanned = await workbookPipeline.DataPrepareAsync(project);
        Assert.False(stale.Diagnostics.Any(static item => item.IsFailure),
            string.Join(Environment.NewLine, stale.Diagnostics.Select(static item => $"{item.Code}: {item.Message}")));
        Assert.Single(stale.Mutations);
        Assert.Single(independentlyPlanned.Mutations);
        Assert.NotEqual(stale.Mutations[0].ContentBase64, independentlyPlanned.Mutations[0].ContentBase64);
        ReplaceBusinessCell(workbookPath, "name", "Changed");
        var changedFingerprint = ContentFingerprint.FromFile(workbookPath);
        var staleReport = new MutationPlanApplier().Apply(stale);
        Assert.Equal(2, (int)staleReport.ExitCode);
        Assert.Equal(changedFingerprint, ContentFingerprint.FromFile(workbookPath));

        var prepare = await workbookPipeline.DataPrepareAsync(project);
        var serialized = MutationPlanCodec.Serialize(prepare);
        var replay = MutationPlanCodec.Deserialize(serialized);
        Assert.Equal(prepare.PlanHash, replay.PlanHash);
        Assert.Contains(replay.InputSets, item => item.Kind == InputSetKind.SchemaProto);
        Assert.Contains(replay.InputSets, item => item.Kind == InputSetKind.ExcelWorkbook);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(replay).ExitCode);
        var prepared = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        var row = Assert.Single(Assert.Single(prepared.Tables).Rows);
        Assert.NotNull(row.RowGuid);
        Assert.Equal(
            row.RowGuid.Value.ToString(),
            XlsxWorkbookCodec.ReadCell(File.ReadAllBytes(workbookPath), WorkbookProtocol.KeySheetName, 2, 2)!.Text);
        Assert.Equal("Changed", row.Cells["name"].Text);
        Assert.Equal("10", row.Cells["hp"].Text);

        var check = await workbookPipeline.CheckAsync(Load());
        Assert.Equal(0, check.PendingIdentityCount);
        Assert.Equal(0, (int)check.Report.ExitCode);

        var bytesPath = WorkbookPipeline.DefaultOutputPath(Load(), "client");
        var convert = await workbookPipeline.ConvertAsync(Load(), "client", bytesPath);
        Assert.Equal(0, (int)convert.ExitCode);
        var manifestPath = bytesPath + ".manifest.json";
        Assert.True(File.Exists(bytesPath));
        var manifest = ConvertedBytesManifest.Parse(File.ReadAllText(manifestPath));
        Assert.Equal("client", manifest.ExportTarget.Value);
        Assert.Equal(1, manifest.RowCount);
        var read = ConvertedBytesReader.Read(File.ReadAllBytes(bytesPath), File.ReadAllText(manifestPath));
        Assert.Single(read.Snapshot.Assets);
        read.Snapshot.Dispose();
    }

    [Fact]
    public async Task PlansAreDeterministicAndTamperingIsRejected()
    {
        Initialize();
        var project = Load();
        var pipeline = new SchemaPipeline("test-v1");
        var intent = new TableCreateIntent("Hero", "Excel/game.xlsx", [new SimpleFieldDefinition("name", SimpleFieldType.String)]);
        var first = await pipeline.CreateTableAsync(project, intent);
        var second = await pipeline.CreateTableAsync(project, intent);
        Assert.Equal(MutationPlanCodec.Serialize(first), MutationPlanCodec.Serialize(second));

        var tampered = first with { Operation = "table-edit" };
        Assert.False(MutationPlanCodec.Validate(tampered));
        Assert.Throws<InvalidDataException>(() => new MutationPlanApplier().Apply(tampered));
    }

    [Fact]
    public async Task Generate_without_workbooks_creates_the_shared_default_workbook_for_live_tables()
    {
        Initialize();
        var schemaPath = Path.Combine(_root, "Schema", "catalog.proto");
        File.WriteAllText(schemaPath, """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            message Hero {
              option (exceldb.table) = { kind: ASSET, id: 101 };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }
            message Item {
              option (exceldb.table) = { kind: ASSET, id: 102 };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }
            """, new UTF8Encoding(false));
        var schemas = new SchemaPipeline("test-v1");
        var build = await schemas.BuildAsync(Load(), checkOnly: false);
        Assert.NotNull(build.Plan);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(build.Plan).ExitCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "Excel"), "*.xlsx", SearchOption.AllDirectories));
        var schemaBefore = File.ReadAllBytes(schemaPath);
        var pipeline = new WorkbookPipeline("test-v1", schemas);

        var generate = await pipeline.GenerateAsync(Load());

        Assert.False(generate.HasBlockers, string.Join(
            Environment.NewLine,
            generate.Diagnostics.Select(static item => $"{item.Code}: {item.Message}")));
        Assert.Contains(generate.Mutations, mutation =>
            mutation.Root == PlanRootKind.Project
            && mutation.Kind == FileMutationKind.WriteFile
            && mutation.RelativePath.Replace('\\', '/') == "Excel/game.xlsx");
        Assert.DoesNotContain(generate.Mutations, mutation =>
            mutation.Root == PlanRootKind.Project
            && mutation.RelativePath.Replace('\\', '/').StartsWith("Generated/Bytes/", StringComparison.Ordinal));
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(generate).ExitCode);

        var workbookPath = Path.Combine(_root, "Excel", ProjectArtifactPaths.DefaultWorkbookFileName);
        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        Assert.Equal([101, 102], workbook.Tables.Select(static table => table.TableId).ToArray());
        Assert.All(workbook.Tables, static table => Assert.Empty(table.Rows));
        Assert.Equal(schemaBefore, File.ReadAllBytes(schemaPath));
        Assert.False(File.Exists(Path.Combine(_root, "Generated", "Bytes", "client", "config.bytes")));
    }

    [Fact]
    public async Task ExternalConvert_RevalidatesSystemImportsManifestBeforeWriting()
    {
        Initialize();
        var schemas = new SchemaPipeline("test-v1");
        var create = await schemas.CreateTableAsync(
            Load(),
            new TableCreateIntent(
                "Hero",
                "Excel/game.xlsx",
                [new SimpleFieldDefinition("name", SimpleFieldType.String)]));
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(create).ExitCode);
        var external = Path.Combine(Path.GetTempPath(), $"exceldb-input-race-{Guid.NewGuid():N}");
        var output = Path.Combine(external, "config.bytes");
        var pipeline = new WorkbookPipeline("test-v1", schemas)
        {
            BeforeExternalInputRevalidation = () =>
                File.AppendAllText(
                    Path.Combine(_root, ".exceldb", "system-imports.json"),
                    " ",
                    new UTF8Encoding(false)),
        };

        try
        {
            var report = await pipeline.ConvertAsync(Load(), "client", output);

            Assert.False(report.Succeeded);
            Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "plan.stale-input-set");
            Assert.False(File.Exists(output));
            Assert.False(File.Exists(output + ".manifest.json"));
        }
        finally
        {
            if (Directory.Exists(external))
                Directory.Delete(external, recursive: true);
        }
    }

    [Fact]
    public async Task DiffReportsAddedRemovedRenamedMovedAndFieldChanges()
    {
        Initialize();
        var project = Load();
        var schemas = new SchemaPipeline("test-v1");
        var create = await schemas.CreateTableAsync(project, new TableCreateIntent("Hero", "Excel/game.xlsx", [new SimpleFieldDefinition("name", SimpleFieldType.String)]));
        new MutationPlanApplier().Apply(create);
        var leftPath = Path.Combine(_root, "Excel", "left.xlsx");
        var rightPath = Path.Combine(_root, "Excel", "right.xlsx");
        var baseline = XlsxWorkbookCodec.Read(File.ReadAllBytes(Path.Combine(_root, "Excel", "game.xlsx")));
        var guidA = ExcelDb.Core.Identity.RowGuid.New();
        var guidB = ExcelDb.Core.Identity.RowGuid.New();
        var table = Assert.Single(baseline.Tables);
        var left = baseline with
        {
            Tables = [table with { Rows = [Row(guidA, "a", "old"), Row(guidB, "b", "gone")] }],
        };
        var right = baseline with
        {
            Tables = [table with { Rows = [Row(guidA, "a2", "new"), Row(ExcelDb.Core.Identity.RowGuid.New(), "c", "added")] }],
        };
        File.WriteAllBytes(leftPath, XlsxWorkbookCodec.Write(left));
        File.WriteAllBytes(rightPath, XlsxWorkbookCodec.Write(right));

        var diff = new WorkbookPipeline("test-v1", schemas).Diff(leftPath, rightPath);
        Assert.Contains(diff.Entries, static entry => entry.Kind == "added");
        Assert.Contains(diff.Entries, static entry => entry.Kind == "removed");
        Assert.Contains(diff.Entries, static entry => entry.Kind == "renamed");
        Assert.Contains(diff.Entries, static entry => entry.Kind == "modified" && entry.PropertyPath == "name");
    }

    [Fact]
    public async Task RealProtoRowReferenceResolvesFromHumanTokenIntoStableBytesIdentity()
    {
        Initialize();
        var schemaPath = Path.Combine(_root, "Schema", "references.proto");
        File.WriteAllText(schemaPath, """
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
            """, new UTF8Encoding(false));
        var schemas = new SchemaPipeline("test-v1");
        var build = await schemas.BuildAsync(Load(), checkOnly: false);
        Assert.DoesNotContain(build.Report.Diagnostics, static diagnostic => diagnostic.IsBlocker);
        Assert.NotNull(build.Plan);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(build.Plan).ExitCode);

        var workbookPath = Path.Combine(_root, "Excel", "references.xlsx");
        var workbooks = new WorkbookPipeline("test-v1", schemas);
        var generate = await workbooks.GenerateAsync(Load(), workbookPath);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(generate).ExitCode);
        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        var itemIdentity = new ExcelDb.Core.Identity.AssetIdentity(
            101,
            ExcelDb.Core.Identity.RowGuid.Parse("00000000000000000000000000000001"));
        var holderIdentity = new ExcelDb.Core.Identity.AssetIdentity(
            202,
            ExcelDb.Core.Identity.RowGuid.Parse("00000000000000000000000000000002"));
        var tables = workbook.Tables.Select(table => table.TableId switch
        {
            101 => table with
            {
                Rows = [WorkbookRow.Create(itemIdentity.RowGuid, 1, [new KeyValuePair<string, WorkbookCell>("key", new WorkbookCell("sword"))])],
            },
            202 => table with
            {
                Rows = [WorkbookRow.Create(holderIdentity.RowGuid, 1,
                [
                    new KeyValuePair<string, WorkbookCell>("key", new WorkbookCell("slot")),
                    new KeyValuePair<string, WorkbookCell>("item", new WorkbookCell("101:sword")),
                ])],
            },
            _ => table,
        }).ToImmutableArray();
        File.WriteAllBytes(workbookPath, XlsxWorkbookCodec.Write(workbook with { Tables = tables }));

        var check = await workbooks.CheckAsync(Load());
        Assert.False(check.Report.Diagnostics.Any(static item => item.IsFailure),
            string.Join(Environment.NewLine, check.Report.Diagnostics.Select(static item => $"{item.Code}: {item.Message}")));
        Assert.Equal(0, (int)check.Report.ExitCode);
        var bytesPath = WorkbookPipeline.DefaultOutputPath(Load(), "client");
        var convert = await workbooks.ConvertAsync(Load(), "client", bytesPath);
        Assert.Equal(0, (int)convert.ExitCode);
        using (var snapshot = ConvertedBytesReader.Read(
                   File.ReadAllBytes(bytesPath),
                   File.ReadAllText(bytesPath + ".manifest.json")).Snapshot)
        {
            var holder = Assert.Single(snapshot.Assets, static asset => asset.TableId == 202);
            Assert.Equal(itemIdentity, Assert.Single(holder.Dependencies));
            Assert.Equal(
                itemIdentity.ToString(),
                Encoding.UTF8.GetString(Assert.Single(holder.Fields, static field => field.FieldNumber == 2).Data.Span));
        }

        var beforeFailure = ContentFingerprint.FromFile(bytesPath);
        var changed = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        var changedTables = changed.Tables.Select(table => table.TableId == 202
            ? table with
            {
                Rows = [table.Rows[0] with
                {
                    Cells = table.Rows[0].Cells.SetItem("item", new WorkbookCell("101:missing")),
                }],
            }
            : table).ToImmutableArray();
        File.WriteAllBytes(workbookPath, XlsxWorkbookCodec.Write(changed with { Tables = changedTables }));
        var dangling = await workbooks.ConvertAsync(Load(), "client", bytesPath);
        Assert.Equal(2, (int)dangling.ExitCode);
        Assert.Contains(dangling.Diagnostics, static diagnostic => diagnostic.Code == "ref.unresolved");
        Assert.Equal(beforeFailure, ContentFingerprint.FromFile(bytesPath));
    }

    [Fact]
    public async Task RemovingAnExportTargetFlagsAndDeletesOwnedGhostArtifacts()
    {
        Initialize();
        var protoPath = Path.Combine(_root, "Schema", "targets.proto");
        File.WriteAllText(protoPath, """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            message Hero {
              option (exceldb.table) = { kind: ASSET, id: 90 };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }
            """, new UTF8Encoding(false));

        var pipeline = new SchemaPipeline("test-v1");
        var initial = await pipeline.BuildAsync(Load(), checkOnly: false);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(initial.Plan!).ExitCode);
        var serverDirectory = Path.Combine(_root, "Generated", "CSharp", "Runtime", "server");
        Assert.NotEmpty(Directory.GetFiles(serverDirectory, "*.g.cs", SearchOption.AllDirectories));

        File.WriteAllText(protoPath, """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            message Hero {
              option (exceldb.table) = { kind: ASSET, id: 90, export_targets: { ids: "client" } };
              string id = 1 [(exceldb.field) = { key: 1, export_targets: { ids: "client" } }];
            }
            """, new UTF8Encoding(false));

        var check = await pipeline.BuildAsync(Load(), checkOnly: true);
        Assert.Contains(check.Report.Diagnostics, static diagnostic => diagnostic.Code == "codegen.ghost");
        Assert.NotEmpty(Directory.GetFiles(serverDirectory, "*.g.cs", SearchOption.AllDirectories));

        var rebuild = await pipeline.BuildAsync(Load(), checkOnly: false);
        Assert.Contains(rebuild.Plan!.Mutations, mutation =>
            mutation.Kind == FileMutationKind.DeleteFile
            && mutation.RelativePath.Contains("Runtime/server", StringComparison.Ordinal));
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(rebuild.Plan).ExitCode);
        Assert.Empty(Directory.GetFiles(serverDirectory, "*.g.cs", SearchOption.AllDirectories));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, "Generated", "CSharp", "Runtime", "client"), "*.g.cs", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RegisteredProtoValidatorGatesCheckAndConvertThroughPipelineComposition()
    {
        Initialize();
        File.WriteAllText(Path.Combine(_root, "Schema", "validated.proto"), """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            message Hero {
              option (exceldb.table) = { kind: ASSET, id: 91, validators: "reject-key" };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }
            """, new UTF8Encoding(false));
        var schemas = new SchemaPipeline("test-v1");
        var build = await schemas.BuildAsync(Load(), checkOnly: false);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(build.Plan!).ExitCode);

        var workbookPath = Path.Combine(_root, "Excel", "validated.xlsx");
        var plain = new WorkbookPipeline("test-v1", schemas);
        var generate = await plain.GenerateAsync(Load(), workbookPath);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(generate).ExitCode);
        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        var table = Assert.Single(workbook.Tables);
        var identity = ExcelDb.Core.Identity.RowGuid.Parse("00000000000000000000000000000091");
        var row = WorkbookRow.Create(identity, 1, [new KeyValuePair<string, WorkbookCell>("id", new WorkbookCell("forbidden"))]);
        File.WriteAllBytes(workbookPath, XlsxWorkbookCodec.Write(workbook with { Tables = [table with { Rows = [row] }] }));

        var validators = new WorkbookValidatorRegistry([new RejectKeyValidator()]);
        var pipeline = new WorkbookPipeline("test-v1", schemas, validators: validators);
        var check = await pipeline.CheckAsync(Load());
        Assert.Contains(check.Report.Diagnostics, static diagnostic => diagnostic.Code == "test.reject-key");
        Assert.Equal(1, (int)check.Report.ExitCode);
        var bytesPath = WorkbookPipeline.DefaultOutputPath(Load(), "client");
        var convert = await pipeline.ConvertAsync(Load(), "client", bytesPath);
        Assert.Equal(1, (int)convert.ExitCode);
        Assert.False(File.Exists(bytesPath));
    }

    [Fact]
    public async Task ClientConversionIgnoresServerOnlyCellsAndUnownedPackageParts()
    {
        Initialize();
        File.WriteAllText(Path.Combine(_root, "Schema", "targets.proto"), """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";

            message Hero {
              option (exceldb.table) = { kind: ASSET, id: 301 };
              string key = 1 [(exceldb.field) = { key: 1 }];
              string client_name = 2 [(exceldb.field) = { export_targets: { ids: "client" } }];
              string server_secret = 3 [(exceldb.field) = { export_targets: { ids: "server" } }];
            }
            """, new UTF8Encoding(false));
        var schemas = new SchemaPipeline("test-v1");
        var build = await schemas.BuildAsync(Load(), checkOnly: false);
        Assert.NotNull(build.Plan);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(build.Plan).ExitCode);

        var workbookPath = Path.Combine(_root, "Excel", "targets.xlsx");
        var pipeline = new WorkbookPipeline("test-v1", schemas);
        var generate = await pipeline.GenerateAsync(Load(), workbookPath);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(generate).ExitCode);
        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        var table = Assert.Single(workbook.Tables);
        var keyProperty = table.Columns.Single(static column => column.FieldPath == "1").PropertyPath;
        var clientProperty = table.Columns.Single(static column => column.FieldPath == "2").PropertyPath;
        var serverProperty = table.Columns.Single(static column => column.FieldPath == "3").PropertyPath;
        var rowGuid = ExcelDb.Core.Identity.RowGuid.Parse("00000000000000000000000000000301");
        var row = WorkbookRow.Create(
            rowGuid,
            1,
            [
                new KeyValuePair<string, WorkbookCell>(keyProperty, new WorkbookCell("hero")),
                new KeyValuePair<string, WorkbookCell>(clientProperty, new WorkbookCell("visible")),
                new KeyValuePair<string, WorkbookCell>(serverProperty, new WorkbookCell("first")),
            ],
            CanonicalKeyCodec.Format(["hero"]));
        File.WriteAllBytes(workbookPath, XlsxWorkbookCodec.Write(workbook with
        {
            Tables = [table with { Rows = [row] }],
        }));

        var bytesPath = WorkbookPipeline.DefaultOutputPath(Load(), "client");
        var first = await pipeline.ConvertAsync(Load(), "client", bytesPath);
        Assert.Equal(0, (int)first.ExitCode);
        var firstBytes = File.ReadAllBytes(bytesPath);
        var firstManifest = File.ReadAllBytes(bytesPath + ".manifest.json");

        var serverColumn = table.Columns
            .Select((column, index) => (column, index))
            .Single(item => item.column.PropertyPath == serverProperty)
            .index + 1;
        var changedPackage = XlsxWorkbookCodec.PatchCells(
            File.ReadAllBytes(workbookPath),
            [new CellPatch(table.SheetName, table.DataStartRow, serverColumn, new WorkbookCell("second"))]);
        changedPackage = AddOpaquePart(changedPackage, "custom/free-sheet-content.xml", Encoding.UTF8.GetBytes("<note>unowned</note>"));
        File.WriteAllBytes(workbookPath, changedPackage);

        var second = await pipeline.ConvertAsync(Load(), "client", bytesPath);
        Assert.Equal(0, (int)second.ExitCode);
        Assert.Equal(firstBytes, File.ReadAllBytes(bytesPath));
        Assert.Equal(firstManifest, File.ReadAllBytes(bytesPath + ".manifest.json"));
    }

    [Fact]
    public async Task RegenerateRejectsRekeyAndPreservesBusinessIdentityAndCells()
    {
        Initialize();
        File.WriteAllText(Path.Combine(_root, "Schema", "rekey.proto"), """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            message Item {
              option (exceldb.table) = { kind: ASSET, id: 401 };
              string key = 1 [(exceldb.field) = { key: 1 }];
            }
            message Holder {
              option (exceldb.table) = { kind: ASSET, id: 402 };
              string key = 1 [(exceldb.field) = { key: 1 }];
              exceldb.RowRef item = 2 [(exceldb.field) = { ref_table: "Item" }];
            }
            """, new UTF8Encoding(false));
        var schemas = new SchemaPipeline("test-v1");
        var build = await schemas.BuildAsync(Load(), checkOnly: false);
        Assert.NotNull(build.Plan);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(build.Plan).ExitCode);
        var workbookPath = Path.Combine(_root, "Excel", "rekey.xlsx");
        var pipeline = new WorkbookPipeline("test-v1", schemas);
        var generated = await pipeline.GenerateAsync(Load(), workbookPath);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(generated).ExitCode);

        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        var itemTable = workbook.Tables.Single(static table => table.TableId == 401);
        var holderTable = workbook.Tables.Single(static table => table.TableId == 402);
        var itemGuid = ExcelDb.Core.Identity.RowGuid.Parse("00000000000000000000000000000401");
        var holderGuid = ExcelDb.Core.Identity.RowGuid.Parse("00000000000000000000000000000402");
        var itemKeyProperty = itemTable.Columns.Single(static column => column.FieldPath == "1").PropertyPath;
        var holderKeyProperty = holderTable.Columns.Single(static column => column.FieldPath == "1").PropertyPath;
        var holderRefProperty = holderTable.Columns.Single(static column => column.FieldPath == "2").PropertyPath;
        var populated = workbook with
        {
            Tables = workbook.Tables.Select(table => table.TableId switch
            {
                401 => table with
                {
                    Rows = [WorkbookRow.Create(
                        itemGuid,
                        1,
                        [new KeyValuePair<string, WorkbookCell>(itemKeyProperty, new WorkbookCell("sword"))],
                        CanonicalKeyCodec.Format(["sword"]))],
                },
                402 => table with
                {
                    Rows = [WorkbookRow.Create(
                        holderGuid,
                        1,
                        [
                            new KeyValuePair<string, WorkbookCell>(holderKeyProperty, new WorkbookCell("slot")),
                            new KeyValuePair<string, WorkbookCell>(holderRefProperty, new WorkbookCell("401:sword")),
                        ],
                        CanonicalKeyCodec.Format(["slot"]))],
                },
                _ => table,
            }).ToImmutableArray(),
        };
        File.WriteAllBytes(workbookPath, XlsxWorkbookCodec.Write(populated));
        var businessKeyColumn = itemTable.Columns
            .Select((column, index) => (column, index))
            .Single(item => item.column.FieldPath == "1").index + 1;
        File.WriteAllBytes(workbookPath, XlsxWorkbookCodec.PatchCells(
            File.ReadAllBytes(workbookPath),
            [new CellPatch(itemTable.SheetName, itemTable.DataStartRow, businessKeyColumn, new WorkbookCell("blade"))]));
        var rekey = await pipeline.GenerateAsync(Load(), rekey: true);
        Assert.True(rekey.HasBlockers);
        Assert.Equal(2, (int)new MutationPlanApplier().Apply(rekey).ExitCode);

        var after = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        var item = Assert.Single(after.Tables.Single(static table => table.TableId == 401).Rows);
        var holder = Assert.Single(after.Tables.Single(static table => table.TableId == 402).Rows);
        Assert.Equal(itemGuid, item.RowGuid);
        Assert.Equal(holderGuid, holder.RowGuid);
        Assert.Equal("blade", item.Cells[itemKeyProperty].Text);
        Assert.Equal(CanonicalKeyCodec.Format(["sword"]), item.Key);
        Assert.Equal("401:sword", holder.Cells[holderRefProperty].Text);
    }

    [Fact]
    public async Task ConvertPreservesCompleteNumericPathForExpandedMessageFields()
    {
        Initialize();
        File.WriteAllText(Path.Combine(_root, "Schema", "nested.proto"), """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            message Cost { int32 value = 7; }
            message Hero {
              option (exceldb.table) = { kind: ASSET, id: 510 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              Cost cost = 2 [(exceldb.field) = { expand: EXPANDED_COLUMNS }];
            }
            """, new UTF8Encoding(false));
        var schemas = new SchemaPipeline("test-v1");
        var build = await schemas.BuildAsync(Load(), checkOnly: false);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(build.Plan!).ExitCode);
        var workbooks = new WorkbookPipeline("test-v1", schemas);
        var workbookPath = Path.Combine(_root, "Excel", "nested.xlsx");
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(
            await workbooks.GenerateAsync(Load(), workbookPath)).ExitCode);

        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        var table = Assert.Single(workbook.Tables);
        var idProperty = table.Columns.Single(static column => column.FieldPath == "1").PropertyPath;
        var nestedProperty = table.Columns.Single(static column => column.FieldPath == "2.7").PropertyPath;
        var row = WorkbookRow.Create(
            ExcelDb.Core.Identity.RowGuid.Parse("00000000000000000000000000000510"),
            0,
            [
                new KeyValuePair<string, WorkbookCell>(idProperty, new WorkbookCell("hero")),
                new KeyValuePair<string, WorkbookCell>(nestedProperty, new WorkbookCell("42")),
            ],
            CanonicalKeyCodec.Format(["hero"]));
        File.WriteAllBytes(workbookPath, XlsxWorkbookCodec.Write(workbook with
        {
            Tables = [table with { Rows = [row] }],
        }));

        var output = WorkbookPipeline.DefaultOutputPath(Load(), "client");
        var report = await workbooks.ConvertAsync(Load(), "client", output);
        Assert.Equal(0, (int)report.ExitCode);
        using var snapshot = ConvertedBytesReader.Read(
            File.ReadAllBytes(output),
            File.ReadAllText(output + ".manifest.json")).Snapshot;
        var fields = Assert.Single(snapshot.Assets).Fields;
        Assert.Contains(fields, static field => field.FieldIdPath.SequenceEqual(new[] { 2, 7 })
                                                && Encoding.UTF8.GetString(field.Data.Span) == "42");
    }

    [Fact]
    public async Task ChildWorksheetsAggregateSimpleRowsIntoRepeatedAndMapRuntimeFields()
    {
        Initialize();
        File.WriteAllText(Path.Combine(_root, "Schema", "children.proto"), """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            message Reward { string item_id = 4; int32 count = 9; }
            message Hero {
              option (exceldb.table) = { kind: ASSET, id: 520 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              repeated Reward rewards = 3 [(exceldb.field) = { child_table: { sheet_name: "HeroRewards" } }];
              map<string, Reward> reward_by_slot = 5;
            }
            """, new UTF8Encoding(false));
        var schemas = new SchemaPipeline("test-v1");
        var build = await schemas.BuildAsync(Load(), checkOnly: false);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(build.Plan!).ExitCode);
        var workbooks = new WorkbookPipeline("test-v1", schemas);
        var workbookPath = Path.Combine(_root, "Excel", "children.xlsx");
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(
            await workbooks.GenerateAsync(Load(), workbookPath)).ExitCode);

        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        var parentGuid = ExcelDb.Core.Identity.RowGuid.Parse("00000000000000000000000000000520");
        var table = Assert.Single(workbook.Tables);
        Assert.Single(table.Columns);
        var parent = WorkbookRow.Create(parentGuid, 0,
            [new KeyValuePair<string, WorkbookCell>(table.Columns[0].PropertyPath, new WorkbookCell("hero"))]);
        var repeated = workbook.EffectiveChildTables.Single(static child => child.OwnerFieldIdPath.SequenceEqual(new[] { 3 }));
        var repeatedItem = repeated.Columns.Single(static column => column.FieldPath == "3.4").PropertyPath;
        var repeatedCount = repeated.Columns.Single(static column => column.FieldPath == "3.9").PropertyPath;
        var map = workbook.EffectiveChildTables.Single(static child => child.OwnerFieldIdPath.SequenceEqual(new[] { 5 }));
        var mapItem = map.Columns.Single(static column => column.FieldPath == "5.4").PropertyPath;
        var mapCount = map.Columns.Single(static column => column.FieldPath == "5.9").PropertyPath;
        var children = workbook.EffectiveChildTables.Select(child => child.OwnerFieldIdPath[0] switch
        {
            3 => child with
            {
                Rows =
                [
                    WorkbookChildRow.Create(parentGuid, 2, null,
                    [
                        new KeyValuePair<string, WorkbookCell>(repeatedItem, new WorkbookCell("shield")),
                        new KeyValuePair<string, WorkbookCell>(repeatedCount, new WorkbookCell("1")),
                    ]),
                    WorkbookChildRow.Create(parentGuid, 1, null,
                    [
                        new KeyValuePair<string, WorkbookCell>(repeatedItem, new WorkbookCell("coin")),
                        new KeyValuePair<string, WorkbookCell>(repeatedCount, new WorkbookCell("3")),
                    ]),
                ],
            },
            5 => child with
            {
                Rows =
                [
                    WorkbookChildRow.Create(parentGuid, null, "daily",
                    [
                        new KeyValuePair<string, WorkbookCell>(mapItem, new WorkbookCell("gem")),
                        new KeyValuePair<string, WorkbookCell>(mapCount, new WorkbookCell("2")),
                    ]),
                ],
            },
            _ => child,
        }).ToImmutableArray();
        File.WriteAllBytes(workbookPath, XlsxWorkbookCodec.Write(workbook with
        {
            Tables = [table with { Rows = [parent] }],
            ChildTables = children,
        }));

        var output = WorkbookPipeline.DefaultOutputPath(Load(), "client");
        var report = await workbooks.ConvertAsync(Load(), "client", output);
        Assert.Equal(0, (int)report.ExitCode);
        using var snapshot = ConvertedBytesReader.Read(
            File.ReadAllBytes(output),
            File.ReadAllText(output + ".manifest.json")).Snapshot;
        var fields = Assert.Single(snapshot.Assets).Fields;
        Assert.Equal(
            "[{\"count\":3,\"item_id\":\"coin\"},{\"count\":1,\"item_id\":\"shield\"}]",
            Encoding.UTF8.GetString(Assert.Single(fields, static field => field.FieldIdPath.SequenceEqual(new[] { 3 })).Data.Span));
        Assert.Equal(
            "{\"daily\":{\"count\":2,\"item_id\":\"gem\"}}",
            Encoding.UTF8.GetString(Assert.Single(fields, static field => field.FieldIdPath.SequenceEqual(new[] { 5 })).Data.Span));
    }

    [Fact]
    public async Task PublishedHistorySurvivesCacheDeletionAndBlocksMissingTombstones()
    {
        Initialize();
        var project = Load();
        var schemas = new SchemaPipeline("test-v1");
        var create = await schemas.CreateTableAsync(project, new TableCreateIntent(
            "Hero",
            "Excel/history.xlsx",
            [new SimpleFieldDefinition("name", SimpleFieldType.String)]));
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(create).ExitCode);
        var workbooks = new WorkbookPipeline("test-v1", schemas);
        Assert.Equal(0, (int)(await workbooks.ConvertAsync(
            Load(),
            "client",
            WorkbookPipeline.DefaultOutputPath(Load(), "client"))).ExitCode);
        Assert.Equal(0, (int)(await workbooks.ConvertAsync(
            Load(),
            "server",
            WorkbookPipeline.DefaultOutputPath(Load(), "server"))).ExitCode);

        var compatibility = new CompatibilityPipeline("test-v1", schemas);
        var publish = await compatibility.CreatePublishPlanAsync(Load());
        Assert.False(publish.HasBlockers, string.Join(Environment.NewLine, publish.Diagnostics.Select(static item => item.Message)));
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(publish).ExitCode);
        Assert.True(File.Exists(Path.Combine(_root, ".exceldb", "published", PublishedSchemaHistoryStore.IndexFileName)));
        Assert.False(Directory.Exists(Path.Combine(_root, "Schema", ".exceldb-history")));
        var history = PublishedSchemaHistoryStore.LoadLatest(Load());
        Assert.True(history.IsValid, string.Join(Environment.NewLine, history.Diagnostics.Select(static item => item.Message)));
        Assert.NotNull(history.Latest);

        var cacheDescriptor = Path.Combine(_root, ".exceldb", "cache", "schema", "descriptor.pb");
        File.Delete(cacheDescriptor);
        history = PublishedSchemaHistoryStore.LoadLatest(Load());
        Assert.True(history.IsValid, string.Join(Environment.NewLine, history.Diagnostics.Select(static item => item.Message)));
        Assert.NotNull(history.Latest);

        File.WriteAllText(Path.Combine(_root, "Schema", "Hero.proto"), """
            syntax = "proto3";
            package game.configs;
            import "exceldb/options.proto";
            message Other {
              option (exceldb.table) = { kind: ASSET, id: 2 };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }
            """, new UTF8Encoding(false));

        var analysis = await compatibility.AnalyzeAsync(Load());
        Assert.NotNull(analysis.Report);
        Assert.Contains(
            analysis.Report.Entries,
            static entry => entry.Kind == ExcelDb.Compatibility.CompatibilityChangeKind.TableTombstoneMissing
                            && entry.Severity == ExcelDb.Compatibility.CompatibilitySeverity.Blocker);
        var rejected = await compatibility.CreatePublishPlanAsync(Load());
        Assert.True(rejected.HasBlockers);
        Assert.Empty(rejected.Mutations);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("missing")]
    [InlineData("unknown-ownership")]
    public async Task PublishBlocksGeneratedCSharpThatDoesNotExactlyMatchCurrentGenerator(
        string failure)
    {
        var (schemas, _) = await PreparePublishReadyProjectAsync();
        var generatedPath = Path.Combine(
            _root,
            "Generated",
            "CSharp",
            "Authoring",
            "ExcelDbSchema.Authoring.g.cs");
        var manifestPath = Path.Combine(_root, "Generated", "CSharp", "codegen.manifest.json");
        switch (failure)
        {
            case "tampered":
                File.AppendAllText(generatedPath, "// user edit\n", new UTF8Encoding(false));
                break;
            case "missing":
                File.Delete(generatedPath);
                break;
            case "unknown-ownership":
                var manifest = File.ReadAllText(manifestPath);
                File.WriteAllText(
                    manifestPath,
                    manifest.Replace(
                        "Authoring/ExcelDbSchema.Authoring.g.cs",
                        "Authoring/Unowned.g.cs",
                        StringComparison.Ordinal),
                    new UTF8Encoding(false));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }

        var publish = await new CompatibilityPipeline("test-v1", schemas).CreatePublishPlanAsync(Load());

        Assert.True(publish.HasBlockers);
        Assert.Contains(
            publish.Diagnostics,
            diagnostic => diagnostic.Code is "compat.publish.generated-invalid" or "compat.publish.generated-ownership");
        Assert.DoesNotContain(
            publish.Mutations,
            mutation => NormalizePath(mutation.RelativePath).StartsWith(".exceldb/published/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_blocks_bytes_that_predate_the_current_excel_data()
    {
        var (_, compatibility) = await PreparePublishReadyProjectAsync();
        var workbookPath = Path.Combine(_root, "Excel", "history.xlsx");
        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        var table = Assert.Single(workbook.Tables);
        var idProperty = table.Columns.Single(static column => column.FieldPath == "1").PropertyPath;
        var nameProperty = table.Columns.Single(static column => column.FieldPath == "2").PropertyPath;
        var row = WorkbookRow.Create(
            ExcelDb.Core.Identity.RowGuid.Parse("00000000000000000000000000000101"),
            1,
            [
                new KeyValuePair<string, WorkbookCell>(idProperty, new WorkbookCell("hero")),
                new KeyValuePair<string, WorkbookCell>(nameProperty, new WorkbookCell("Changed after convert")),
            ],
            CanonicalKeyCodec.Format(["hero"]));
        File.WriteAllBytes(
            workbookPath,
            XlsxWorkbookCodec.Write(workbook with { Tables = [table with { Rows = [row] }] }));

        var publish = await compatibility.CreatePublishPlanAsync(Load());

        Assert.True(publish.HasBlockers);
        Assert.Contains(
            publish.Diagnostics,
            diagnostic => diagnostic.Code == "compat.publish.target-artifact"
                          && diagnostic.Message.Contains("stale", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            publish.Mutations,
            mutation => NormalizePath(mutation.RelativePath).StartsWith(".exceldb/published/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("generated-csharp")]
    [InlineData("codegen-manifest")]
    [InlineData("bytes")]
    [InlineData("bytes-manifest")]
    [InlineData("published-index")]
    [InlineData("published-descriptor")]
    public async Task PublishPlanBecomesStaleWhenAnyFrozenGateEvidenceChanges(string evidence)
    {
        var (_, compatibility) = await PrepareInitiallyPublishedEvolutionAsync();
        var project = Load();
        var plan = await compatibility.CreatePublishPlanAsync(project);
        Assert.False(plan.HasBlockers, string.Join(Environment.NewLine, plan.Diagnostics.Select(static item => item.Message)));
        var indexPath = PublishedSchemaHistoryStore.IndexPath(project);
        var entryCountBefore = PublishedEntryCount(indexPath);
        var nextDescriptor = plan.Mutations
            .Where(static mutation => mutation.Kind == FileMutationKind.WriteFile)
            .Select(static mutation => mutation.RelativePath)
            .Single(path => NormalizePath(path).StartsWith(".exceldb/published/descriptor-", StringComparison.Ordinal));
        var nextDescriptorPath = Path.Combine(_root, nextDescriptor.Replace('/', Path.DirectorySeparatorChar));
        Assert.False(File.Exists(nextDescriptorPath));

        var latest = PublishedSchemaHistoryStore.LoadLatest(project).Latest!;
        var path = evidence switch
        {
            "generated-csharp" => Path.Combine(
                _root,
                "Generated",
                "CSharp",
                "Authoring",
                "ExcelDbSchema.Authoring.g.cs"),
            "codegen-manifest" => Path.Combine(_root, "Generated", "CSharp", "codegen.manifest.json"),
            "bytes" => WorkbookPipeline.DefaultOutputPath(project, "client"),
            "bytes-manifest" => WorkbookPipeline.DefaultOutputPath(project, "client") + ".manifest.json",
            "published-index" => indexPath,
            "published-descriptor" => latest.DescriptorPath,
            _ => throw new ArgumentOutOfRangeException(nameof(evidence)),
        };
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            stream.WriteByte((byte)' ');

        var report = new MutationPlanApplier().Apply(plan);

        Assert.Equal(2, (int)report.ExitCode);
        Assert.False(File.Exists(nextDescriptorPath));
        Assert.Equal(entryCountBefore, PublishedEntryCount(indexPath));
    }

    [Fact]
    public async Task RealXlsxMigrationStagesVerifiesCommitsAndPersistsItsMarker()
    {
        Initialize();
        var protoPath = Path.Combine(_root, "Schema", "migration.proto");
        File.WriteAllText(protoPath, MigrationProto(includeTarget: false), new UTF8Encoding(false));
        var schemas = new SchemaPipeline("test-v1");
        var build = await schemas.BuildAsync(Load(), checkOnly: false);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(build.Plan!).ExitCode);
        var sourceCompilation = await new SchemaCompiler().CompileAsync(Path.Combine(_root, "Schema"));
        Assert.True(sourceCompilation.Succeeded);
        var sourceSchema = sourceCompilation.Descriptor!;
        var workbooks = new WorkbookPipeline("test-v1", schemas);
        var workbookPath = Path.Combine(_root, "Excel", "migration.xlsx");
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(
            await workbooks.GenerateAsync(Load(), workbookPath)).ExitCode);
        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        var table = Assert.Single(workbook.Tables);
        var rowGuid = ExcelDb.Core.Identity.RowGuid.Parse("00000000000000000000000000000601");
        var row = WorkbookRow.Create(rowGuid, 1,
        [
            new KeyValuePair<string, WorkbookCell>(table.Columns.Single(static column => column.FieldPath == "1").PropertyPath, new WorkbookCell("hero")),
            new KeyValuePair<string, WorkbookCell>(table.Columns.Single(static column => column.FieldPath == "2").PropertyPath, new WorkbookCell("old")),
        ]);
        File.WriteAllBytes(workbookPath, XlsxWorkbookCodec.Write(workbook with
        {
            Tables = [table with { Rows = [row] }],
        }));

        File.WriteAllText(protoPath, MigrationProto(includeTarget: true), new UTF8Encoding(false));
        var targetCompilation = await new SchemaCompiler().CompileAsync(Path.Combine(_root, "Schema"));
        Assert.True(targetCompilation.Succeeded);
        var targetSchema = targetCompilation.Descriptor!;
        var participant = new XlsxMigrationWorkbookParticipant(workbookPath, sourceSchema, targetSchema);
        var key = new MigrationKey("copy-name", 1);
        var plan = MigrationPlanner.Create(
            sourceSchema.SchemaHash,
            targetSchema.SchemaHash,
            [new MigrationStep(1, key, 601, [2], [3])],
            [participant.ReadCurrent()],
            new MigrationRegistry([new CopyNameMigration()]));

        var report = MigrationCoordinator.Execute(plan, [participant]);

        Assert.True(report.Succeeded, string.Join(Environment.NewLine, report.Diagnostics.Select(static item => item.Message)));
        Assert.True(report.Applied);
        var migrated = XlsxWorkbookCodec.Read(File.ReadAllBytes(workbookPath));
        Assert.Equal(targetSchema.SchemaHash, migrated.SchemaHash);
        Assert.Contains("copy-name@1", migrated.EffectiveMigrationMarkers);
        var migratedRow = Assert.Single(Assert.Single(migrated.Tables).Rows);
        Assert.Equal(rowGuid, migratedRow.RowGuid);
        Assert.Equal("old", migratedRow.Cells["old_name"].Text);
        Assert.Equal("old", migratedRow.Cells["new_name"].Text);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(workbookPath)!, "*.migration-*.stage"));
    }

    private static string MigrationProto(bool includeTarget) => $$"""
        syntax = "proto3";
        package game;
        import "exceldb/options.proto";
        message Hero {
          option (exceldb.table) = { kind: ASSET, id: 601 };
          string id = 1 [(exceldb.field) = { key: 1 }];
          string old_name = 2;
          {{(includeTarget ? "string new_name = 3;" : string.Empty)}}
        }
        """;

    private async Task<(SchemaPipeline Schemas, CompatibilityPipeline Compatibility)> PreparePublishReadyProjectAsync()
    {
        Initialize();
        var schemas = new SchemaPipeline("test-v1");
        var create = await schemas.CreateTableAsync(Load(), new TableCreateIntent(
            "Hero",
            "Excel/history.xlsx",
            [new SimpleFieldDefinition("name", SimpleFieldType.String)]));
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(create).ExitCode);
        var workbooks = new WorkbookPipeline("test-v1", schemas);
        foreach (var target in new[] { "client", "server" })
        {
            var conversion = await workbooks.ConvertAsync(
                Load(),
                target,
                WorkbookPipeline.DefaultOutputPath(Load(), target));
            Assert.Equal(0, (int)conversion.ExitCode);
        }
        return (schemas, new CompatibilityPipeline("test-v1", schemas));
    }

    private async Task<(SchemaPipeline Schemas, CompatibilityPipeline Compatibility)> PrepareInitiallyPublishedEvolutionAsync()
    {
        var (schemas, compatibility) = await PreparePublishReadyProjectAsync();
        var firstPublish = await compatibility.CreatePublishPlanAsync(Load());
        Assert.False(firstPublish.HasBlockers, string.Join(Environment.NewLine, firstPublish.Diagnostics.Select(static item => item.Message)));
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(firstPublish).ExitCode);

        var edit = await schemas.EditTableAsync(Load(), new TableEditIntent(
            "game.configs.Hero",
            [new AddSimpleFieldMutation(new SimpleFieldDefinition("title", SimpleFieldType.String))],
            [],
            []));
        Assert.False(edit.HasBlockers, string.Join(Environment.NewLine, edit.Diagnostics.Select(static item => item.Message)));
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(edit).ExitCode);
        var workbooks = new WorkbookPipeline("test-v1", schemas);
        foreach (var target in new[] { "client", "server" })
        {
            var conversion = await workbooks.ConvertAsync(
                Load(),
                target,
                WorkbookPipeline.DefaultOutputPath(Load(), target));
            Assert.Equal(0, (int)conversion.ExitCode);
        }
        return (schemas, compatibility);
    }

    private static int PublishedEntryCount(string indexPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(indexPath));
        return document.RootElement.GetProperty("entries").GetArrayLength();
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private void Initialize()
    {
        var plan = new ProjectInitializer("test-v1").Plan(_root);
        Assert.Equal(0, (int)new MutationPlanApplier().Apply(plan).ExitCode);
    }

    private PipelineProject Load() => PipelineProject.Load(Path.Combine(_root, ExcelDbProject.FileName));

    private static void AddPendingRow(string path, string id, string name, string hp)
    {
        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(path));
        var table = Assert.Single(workbook.Tables);
        var row = new WorkbookRow(
            null,
            0,
            new Dictionary<string, WorkbookCell>
            {
                ["id"] = new(id),
                ["name"] = new(name),
                ["hp"] = new(hp),
            }.ToImmutableDictionary(StringComparer.Ordinal),
            id);
        var updated = workbook with { Tables = [table with { Rows = [row] }] };
        File.WriteAllBytes(path, XlsxWorkbookCodec.Write(updated));
    }

    private static void ReplaceBusinessCell(string path, string property, string value)
    {
        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(path));
        var table = Assert.Single(workbook.Tables);
        var row = Assert.Single(table.Rows);
        var updatedRow = row with { Cells = row.Cells.SetItem(property, new WorkbookCell(value)) };
        File.WriteAllBytes(path, XlsxWorkbookCodec.Write(workbook with { Tables = [table with { Rows = [updatedRow] }] }));
    }

    private static WorkbookRow Row(ExcelDb.Core.Identity.RowGuid guid, string id, string name) => new(
        guid,
        0,
        new Dictionary<string, WorkbookCell> { ["id"] = new(id), ["name"] = new(name) }.ToImmutableDictionary(StringComparer.Ordinal),
        id);

    private static byte[] AddOpaquePart(byte[] source, string name, byte[] content)
    {
        using var input = new MemoryStream(source, writable: false);
        using var output = new MemoryStream();
        using (var sourceArchive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true))
        using (var targetArchive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in sourceArchive.Entries)
            {
                var copy = targetArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var sourceStream = entry.Open();
                using var targetStream = copy.Open();
                sourceStream.CopyTo(targetStream);
            }
            var added = targetArchive.CreateEntry(name, CompressionLevel.Optimal);
            added.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var addedStream = added.Open();
            addedStream.Write(content);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class RejectKeyValidator : IWorkbookValidator
    {
        public string Id => "reject-key";

        public IEnumerable<Diagnostic> Validate(WorkbookValidationContext context)
        {
            yield return new Diagnostic("test.reject-key", DiagnosticSeverity.Error, context.Row.Location, "Key is forbidden.");
        }
    }

    private sealed class CopyNameMigration : ICanonicalValueMigration
    {
        public string Id => "copy-name";

        public int Version => 1;

        public MigrationResult Transform(in MigrationContext context, CanonicalValue source) =>
            MigrationResult.Converted(source);
    }
}
