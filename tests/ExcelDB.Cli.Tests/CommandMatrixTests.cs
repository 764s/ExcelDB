using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ExcelDb.Cli;
using ExcelDb.Core.Identity;
using ExcelDb.Runtime.Bytes;
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
        Assert.Equal(0, await Run("table", "create", "Hero", "--workbook", "Data/game.xlsx", "--field", "name:string", "--dry-run", "--plan", plan));
        Assert.True(File.Exists(plan));
        Assert.False(File.Exists(Path.Combine(_root, "Schema", "Hero.proto")));
        Assert.False(File.Exists(Path.Combine(_root, "Data", "game.xlsx")));

        File.AppendAllText(Path.Combine(_root, "ExcelDb.Project.json"), " ");
        Assert.Equal(2, await Run("table", "create", "--apply-plan", plan));
        Assert.False(File.Exists(Path.Combine(_root, "Schema", "Hero.proto")));
    }

    [Fact]
    public async Task ConvertEnforcesOneTargetAndNonClientOutputRule()
    {
        await Run("init", _root);
        Assert.Equal(0, await Run("table", "create", "Hero", "--workbook", "Data/game.xlsx", "--field", "name:string"));

        Assert.Equal(3, await Run("convert", "--target", "server"));
        Assert.Equal(3, await Run("convert", "--target", "all", "--out", "Build/all.bytes"));
        Assert.False(File.Exists(Path.Combine(_root, "Build", "all.bytes")));

        Assert.Equal(0, await Run("convert", "--target", "server", "--out", "Build/server/config.bytes"));
        var manifest = ConvertedBytesManifest.Parse(File.ReadAllText(Path.Combine(_root, "Build", "server", "config.bytes.manifest.json")));
        Assert.Equal("server", manifest.ExportTarget.Value);
    }

    [Fact]
    public async Task ProjectCommandsOutsideAProjectReturnUsageCode()
    {
        Directory.CreateDirectory(_root);
        Assert.Equal(3, await ExcelDbToolHost.RunAsync(["check"], console: new TestConsole(), currentDirectory: _root));
    }

    [Fact]
    public async Task CustomizedDistributionCellFormatFlowsThroughNormalizeCheckAndConvert()
    {
        await Run("init", _root);
        File.WriteAllText(
            Path.Combine(_root, "Schema", "custom.proto"),
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
        Assert.Equal(0, await Run("generate", "--workbook", "Data/custom.xlsx"));

        var path = Path.Combine(_root, "Data", "custom.xlsx");
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

        Assert.Equal(0, await RunCustomized("convert", "--out", "Build/custom.bytes"));
        var bytesPath = Path.Combine(_root, "Build", "custom.bytes");
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

    private async Task<int> RunCustomized(params string[] args) =>
        await ExcelDbToolHost.RunAsync(
            args,
            static builder => builder.UseCellFormat(new PipeListCellFormat()),
            new TestConsole(),
            _root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class TestConsole : IToolConsole
    {
        private readonly StringBuilder _output = new();
        public bool IsInteractive => false;
        public void Write(string text) => _output.Append(text);
        public void WriteLine(string text = "") => _output.AppendLine(text);
        public string? ReadLine() => null;
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
}
