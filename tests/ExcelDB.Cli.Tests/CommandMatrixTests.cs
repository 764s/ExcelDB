using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ExcelDb.Cli;
using ExcelDb.Core.Identity;
using ExcelDb.Runtime.Bytes;
using ExcelDb.Schema.Authoring;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;

namespace ExcelDb.Cli.Tests;

public sealed class CommandMatrixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exceldb-cli-matrix", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FrozenTablePlanHasZeroDomainWritesAndRejectsStaleProject()
    {
        await Run("init", _root);
        var plan = Path.Combine(_root, ".exceldb", "create.json");
        Assert.Equal(0, await Run("table", "create", "Hero", "--workbook", "Excel/game.xlsx", "--field", "name:string", "--dry-run", "--plan", plan));
        Assert.True(File.Exists(plan));
        Assert.False(File.Exists(Path.Combine(_root, "Schema", "Hero.proto")));
        Assert.False(File.Exists(Path.Combine(_root, "Excel", "game.xlsx")));

        File.AppendAllText(Path.Combine(_root, ".exceldb", "project.json"), " ");
        Assert.Equal(2, await Run("table", "create", "--apply-plan", plan));
        Assert.False(File.Exists(Path.Combine(_root, "Schema", "Hero.proto")));
    }

    [Fact]
    public async Task ConvertDerivesOneOutputDirectoryForEveryTarget()
    {
        await Run("init", _root);
        Assert.Equal(0, await Run("table", "create", "Hero", "--workbook", "Excel/game.xlsx", "--field", "name:string"));

        Assert.Equal(0, await Run("convert", "--target", "server"));
        Assert.Equal(3, await Run("convert", "--target", "all", "--out", "one-off/all.bytes"));
        Assert.Equal(3, await Run("convert", "--target", "../server"));
        Assert.False(File.Exists(Path.Combine(_root, "one-off", "all.bytes")));

        var bytes = Path.Combine(_root, "Generated", "Bytes", "server", "config.bytes");
        Assert.True(File.Exists(bytes));
        var manifest = ConvertedBytesManifest.Parse(File.ReadAllText(bytes + ".manifest.json"));
        Assert.Equal("server", manifest.ExportTarget.Value);
    }

    [Fact]
    public async Task FutureTargetUsesTheSameDerivedBytesLayout()
    {
        Assert.Equal(0, await RunWithFutureTarget("init", _root));
        Assert.Equal(0, await RunWithFutureTarget(
            "table", "create", "Hero", "--workbook", "Excel/game.xlsx", "--field", "name:string"));

        Assert.Equal(0, await RunWithFutureTarget("convert", "--target", "lite-client"));

        var bytes = Path.Combine(_root, "Generated", "Bytes", "lite-client", "config.bytes");
        Assert.True(File.Exists(bytes));
        var manifest = ConvertedBytesManifest.Parse(File.ReadAllText(bytes + ".manifest.json"));
        Assert.Equal("lite-client", manifest.ExportTarget.Value);
    }

    [Fact]
    public async Task FieldKeySyntaxDisablesAutoKeyAndMatchesKeyOption()
    {
        var fieldKeyRoot = Path.Combine(_root, "field-key");
        var keyOptionRoot = Path.Combine(_root, "key-option");
        await RunAt(fieldKeyRoot, "init", fieldKeyRoot);
        await RunAt(keyOptionRoot, "init", keyOptionRoot);

        Assert.Equal(0, await RunAt(
            fieldKeyRoot,
            "table", "create", "Hero", "--workbook", "Excel/game.xlsx", "--field", "name:string", "--field", "id:string:key"));
        Assert.Equal(0, await RunAt(
            keyOptionRoot,
            "table", "create", "Hero", "--workbook", "Excel/game.xlsx", "--key", "id:string", "--field", "name:string"));

        var fieldKeyProto = File.ReadAllText(Path.Combine(fieldKeyRoot, "Schema", "Hero.proto"));
        var keyOptionProto = File.ReadAllText(Path.Combine(keyOptionRoot, "Schema", "Hero.proto"));
        Assert.Equal(keyOptionProto, fieldKeyProto);
        Assert.Equal(1, CountOccurrences(fieldKeyProto, "string id ="));
    }

    [Fact]
    public async Task LegacyRootConfigurationIsReportedAndNeverMigrated()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "ExcelDb.Project.json"),
            "{\"formatVersion\":1}",
            new UTF8Encoding(false));
        var console = new TestConsole();

        var exitCode = await ExcelDbToolHost.RunAsync(
            ["project", "inspect", "--project", _root],
            console: console,
            currentDirectory: _root);

        Assert.Equal(2, exitCode);
        Assert.Contains("Legacy", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, ".exceldb", "project.json")));
    }

    [Fact]
    public async Task ProjectInspectFindsTheNearestProjectFromANestedWorkingDirectory()
    {
        Assert.Equal(0, await Run("init", _root));
        var nested = Path.Combine(_root, "Schema", "nested");
        Directory.CreateDirectory(nested);
        var console = new TestConsole();

        var exitCode = await ExcelDbToolHost.RunAsync(
            ["project", "inspect"],
            console: console,
            currentDirectory: nested);

        Assert.Equal(0, exitCode);
        Assert.Contains($"Project: {Path.GetFullPath(_root)}", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"Project: {Path.GetFullPath(nested)}", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DualLegacyAndV2ConfigurationIsBlockedWithoutDomainWrites()
    {
        Assert.Equal(0, await Run("init", _root));
        File.WriteAllText(Path.Combine(_root, ExcelDb.Tooling.Project.ExcelDbProject.LegacyFileName), "{}");
        var console = new TestConsole();

        var inspectExit = await ExcelDbToolHost.RunAsync(
            ["project", "inspect"],
            console: console,
            currentDirectory: _root);
        var createExit = await Run("table", "create", "ShouldNotExist", "--field", "name:string");

        Assert.Equal(2, inspectExit);
        Assert.Equal(3, createExit);
        Assert.Contains("Dual", console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "Schema", "ShouldNotExist.proto")));
    }

    [Fact]
    public async Task ProjectMaintenanceCommandsExposeFourArtifactsAndRepairOnlyToolState()
    {
        await Run("init", _root);
        var configPath = Path.Combine(_root, ".exceldb", "project.json");
        var configBefore = await File.ReadAllBytesAsync(configPath);
        var descriptor = Path.Combine(_root, "Schema", "google", "protobuf", "descriptor.proto");
        File.SetAttributes(descriptor, FileAttributes.Normal);
        File.Delete(descriptor);
        var cacheSentinel = Path.Combine(_root, ".exceldb", "cache", "sentinel.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheSentinel)!);
        await File.WriteAllTextAsync(cacheSentinel, "local", new UTF8Encoding(false));

        var inspectConsole = new TestConsole();
        Assert.Equal(0, await ExcelDbToolHost.RunAsync(
            ["project", "inspect", "--project", _root],
            console: inspectConsole,
            currentDirectory: _root));
        Assert.Contains("Schema:", inspectConsole.Output, StringComparison.Ordinal);
        Assert.Contains("Excel:", inspectConsole.Output, StringComparison.Ordinal);
        Assert.Contains("Generated C#:", inspectConsole.Output, StringComparison.Ordinal);
        Assert.Contains("Generated Bytes:", inspectConsole.Output, StringComparison.Ordinal);

        Assert.Equal(0, await Run("project", "repair-imports"));
        Assert.True(File.Exists(descriptor));
        Assert.Equal(0, await Run("project", "clean-cache"));
        Assert.True(Directory.Exists(Path.GetDirectoryName(cacheSentinel)));
        Assert.False(File.Exists(cacheSentinel));
        Assert.Equal(configBefore, await File.ReadAllBytesAsync(configPath));
    }

    [Fact]
    public async Task ProjectConfigureInitializesEveryNewArtifactRootAndSystemMirror()
    {
        var external = Path.Combine(Path.GetTempPath(), "exceldb-cli-configure", Guid.NewGuid().ToString("N"));
        try
        {
            await Run("init", _root);

            Assert.Equal(0, await Run(
                "project", "configure",
                "--schema-dir", "SchemaNext",
                "--excel-dir", "ExcelNext",
                "--generated-csharp-dir", external,
                "--generated-bytes-dir", "Generated/BytesNext"));

            using var config = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_root, ".exceldb", "project.json")));
            Assert.Equal(5, config.RootElement.EnumerateObject().Count());
            Assert.Equal("SchemaNext", config.RootElement.GetProperty("schemaDir").GetString());
            Assert.Equal("ExcelNext", config.RootElement.GetProperty("excelDir").GetString());
            Assert.Equal(Path.GetFullPath(external), config.RootElement.GetProperty("generatedCSharpDir").GetString());
            Assert.Equal("Generated/BytesNext", config.RootElement.GetProperty("generatedBytesDir").GetString());
            Assert.True(Directory.Exists(Path.Combine(_root, "SchemaNext")));
            Assert.True(Directory.Exists(Path.Combine(_root, "ExcelNext")));
            Assert.True(Directory.Exists(external));
            Assert.True(Directory.Exists(Path.Combine(_root, "Generated", "BytesNext")));
            Assert.True(File.Exists(Path.Combine(_root, "SchemaNext", "exceldb", "options.proto")));
            Assert.True(File.Exists(Path.Combine(_root, "SchemaNext", "google", "protobuf", "descriptor.proto")));
            Assert.False(Directory.Exists(Path.Combine(_root, "Data")));
            Assert.False(Directory.Exists(Path.Combine(_root, "Build")));
        }
        finally
        {
            if (Directory.Exists(external))
                DeleteDirectory(external);
        }
    }

    [Fact]
    public async Task InteractiveTableCreateDerivesItsDefaultWorkbookFromTheConfiguredExcelDirectory()
    {
        await Run("init", _root);
        Assert.Equal(0, await Run("project", "configure", "--excel-dir", "Tables"));
        var console = new InteractiveConsole("", "y");

        var exitCode = await ExcelDbToolHost.RunAsync(
            ["table", "create", "Hero", "--field", "name:string"],
            console: console,
            currentDirectory: _root);

        Assert.Equal(0, exitCode);
        Assert.Contains("Workbook [Tables/game.xlsx]:", console.Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "Tables", "game.xlsx")));
        Assert.False(File.Exists(Path.Combine(_root, "Excel", "game.xlsx")));
    }

    [Fact]
    public async Task ProjectCommandsOutsideAProjectReturnUsageCode()
    {
        Directory.CreateDirectory(_root);
        Assert.Equal(3, await ExcelDbToolHost.RunAsync(["check"], console: new TestConsole(), currentDirectory: _root));
    }

    [Fact]
    public async Task TableWorkbookCannotEscapeTheExcelArtifactRoot()
    {
        await Run("init", _root);

        Assert.NotEqual(0, await Run(
            "table", "create", "Hero", "--workbook", "Schema/game.xlsx", "--field", "name:string"));
        Assert.False(File.Exists(Path.Combine(_root, "Schema", "game.xlsx")));
        Assert.False(File.Exists(Path.Combine(_root, "Schema", "Hero.proto")));
    }

    [Fact]
    public async Task CustomizedDistributionCellFormatFlowsThroughNormalizeCheckAndConvert()
    {
        await Run("init", _root);
        Assert.Equal(0, await Run(
            "table", "create", "Config", "--package", "custom", "--id", "301",
            "--workbook", "Excel/custom.xlsx", "--key", "id:string"));
        File.WriteAllText(
            Path.Combine(_root, "Schema", "Config.proto"),
            """
            syntax = "proto3";
            package custom;
            import "exceldb/options.proto";

            message Config {
              option (exceldb.table) = { kind: ASSET, id: 301 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              repeated int32 levels = 2 [(exceldb.field) = {
                format: { codec: "pipe-list:v1" }
              }];
            }
            """,
            new UTF8Encoding(false));
        Assert.Equal(0, await Run("schema", "build"));
        Assert.Equal(0, await Run("generate"));

        var path = Path.Combine(_root, "Excel", "custom.xlsx");
        var workbook = XlsxWorkbookCodec.Read(File.ReadAllBytes(path));
        var table = Assert.Single(workbook.Tables);
        var row = WorkbookRow.Create(
            RowGuid.Parse("00000000000000000000000000000301"),
            1,
            [
                new KeyValuePair<string, WorkbookCell>("id", new WorkbookCell("hero")),
                new KeyValuePair<string, WorkbookCell>("levels", new WorkbookCell("(1,2)")),
            ],
            "4:hero");
        File.WriteAllBytes(path, XlsxWorkbookCodec.Write(workbook with
        {
            Tables = [table with { Rows = [row] }],
        }));

        Assert.NotEqual(0, await Run("check"));
        Assert.Equal(0, await RunCustomized("check"));
        Assert.Equal(0, await RunCustomized("normalize"));
        var normalized = XlsxWorkbookCodec.Read(File.ReadAllBytes(path));
        Assert.Equal("[1|2]", Assert.Single(Assert.Single(normalized.Tables).Rows).Cells["levels"].Text);

        Assert.Equal(0, await RunCustomized("convert", "--out", "one-off/custom.bytes"));
        var bytesPath = Path.Combine(_root, "one-off", "custom.bytes");
        using var snapshot = ConvertedBytesReader.Read(
            File.ReadAllBytes(bytesPath),
            File.ReadAllText(bytesPath + ".manifest.json")).Snapshot;
        var asset = Assert.Single(snapshot.Assets);
        Assert.Equal(
            "[1,2]",
            Encoding.UTF8.GetString(Assert.Single(asset.Fields, static field => field.FieldNumber == 2).Data.Span));
    }

    private async Task<int> Run(params string[] args) =>
        await ExcelDbToolHost.RunAsync(args, console: new TestConsole(), currentDirectory: _root);

    private static async Task<int> RunAt(string currentDirectory, params string[] args) =>
        await ExcelDbToolHost.RunAsync(args, console: new TestConsole(), currentDirectory: currentDirectory);

    private async Task<int> RunCustomized(params string[] args) =>
        await ExcelDbToolHost.RunAsync(
            args,
            static builder => builder.UseCellFormat(new PipeListCellFormat()),
            new TestConsole(),
            _root);

    private async Task<int> RunWithFutureTarget(params string[] args) =>
        await ExcelDbToolHost.RunAsync(
            args,
            static builder => builder.UseExportTargetStrategy(new FutureExportTargets()),
            new TestConsole(),
            _root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            DeleteDirectory(_root);
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(entry, FileAttributes.Normal);
        File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private sealed class TestConsole : IToolConsole
    {
        private readonly StringBuilder _output = new();
        public bool IsInteractive => false;
        public string Output => _output.ToString();
        public void Write(string text) => _output.Append(text);
        public void WriteLine(string text = "") => _output.AppendLine(text);
        public string? ReadLine() => null;
    }

    private sealed class InteractiveConsole(params string?[] answers) : IToolConsole
    {
        private readonly Queue<string?> _answers = new(answers);
        private readonly StringBuilder _output = new();
        public bool IsInteractive => true;
        public string Output => _output.ToString();
        public void Write(string text) => _output.Append(text);
        public void WriteLine(string text = "") => _output.AppendLine(text);
        public string? ReadLine() => _answers.Count == 0 ? null : _answers.Dequeue();
    }

    private sealed class PipeListCellFormat : ICellFormat
    {
        public string Identity => "pipe-list:v1";

        public string Describe(CellFormatContext context) => "[1|2] with temporary (1,2) input support";

        public bool TryParse(
            string physicalText,
            CellFormatContext context,
            out string canonicalValue,
            out string? error)
        {
            string body;
            char separator;
            if (physicalText.Length >= 2 && physicalText[0] == '[' && physicalText[^1] == ']')
            {
                body = physicalText[1..^1];
                separator = '|';
            }
            else if (physicalText.Length >= 2 && physicalText[0] == '(' && physicalText[^1] == ')')
            {
                body = physicalText[1..^1];
                separator = ',';
            }
            else
            {
                canonicalValue = string.Empty;
                error = "Expected [1|2].";
                return false;
            }

            var values = new List<int>();
            foreach (var part in body.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                {
                    canonicalValue = string.Empty;
                    error = $"'{part}' is not an integer.";
                    return false;
                }
                values.Add(value);
            }

            canonicalValue = JsonSerializer.Serialize(values);
            error = null;
            return true;
        }

        public bool TryWrite(
            string canonicalValue,
            CellFormatContext context,
            out string physicalText,
            out string? error)
        {
            try
            {
                var values = JsonSerializer.Deserialize<ImmutableArray<int>>(canonicalValue);
                physicalText = $"[{string.Join('|', values)}]";
                error = null;
                return true;
            }
            catch (JsonException exception)
            {
                physicalText = string.Empty;
                error = exception.Message;
                return false;
            }
        }
    }

    private sealed class FutureExportTargets : IExportTargetStrategy
    {
        private static readonly string[] Targets = ["client", "server", "lite-client"];

        public string Id => "tests.future-targets:v1";

        public void Apply(in ExportTargetStrategyContext context, IExportTargetDraft draft)
        {
            draft.TryFillTableExportTargets(Targets);
            var inherited = draft.TableExportTargets!.Targets;
            foreach (var field in draft.Fields)
                draft.TryFillFieldExportTargets(field.Name, inherited);
        }
    }
}
