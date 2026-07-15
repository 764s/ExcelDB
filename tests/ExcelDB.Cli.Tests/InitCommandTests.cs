using System.Text;
using System.Text.Json;
using ExcelDb.Cli;
using ExcelDb.Pipeline;
using ExcelDb.Tooling.Project;

namespace ExcelDb.Cli.Tests;

public sealed class InitCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exceldb-cli-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExplicitInit_InitializesEmptyDirectoryWithoutPrompt()
    {
        var console = new TestConsole(isInteractive: false);

        var exitCode = await ExcelDbToolHost.RunAsync(["init", _root], console: console, currentDirectory: Path.GetTempPath());

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(Path.Combine(_root, ExcelDbProject.FileName)));
        Assert.True(Directory.Exists(Path.Combine(_root, "Schema")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Excel")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Generated", "CSharp")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Generated", "Bytes")));
        Assert.True(File.Exists(Path.Combine(_root, "Schema", "exceldb", "options.proto")));
        Assert.True(File.Exists(Path.Combine(_root, "Schema", "google", "protobuf", "descriptor.proto")));
        Assert.True(File.Exists(Path.Combine(_root, ".exceldb", "system-imports.json")));
        Assert.Contains("Project initialized", console.Output);
        Assert.DoesNotContain("external root", console.Output, StringComparison.OrdinalIgnoreCase);

        using var config = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_root, ExcelDbProject.FileName)));
        Assert.Equal(2, config.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal("Schema", config.RootElement.GetProperty("schemaDir").GetString());
        Assert.Equal("Excel", config.RootElement.GetProperty("excelDir").GetString());
        Assert.Equal("Generated/CSharp", config.RootElement.GetProperty("generatedCSharpDir").GetString());
        Assert.Equal("Generated/Bytes", config.RootElement.GetProperty("generatedBytesDir").GetString());
        Assert.Equal(5, config.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task NoCommand_InBatchModeReturnsUsageError()
    {
        var console = new TestConsole(isInteractive: false);

        var exitCode = await ExcelDbToolHost.RunAsync([], console: console, currentDirectory: _root);

        Assert.Equal(3, exitCode);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task NoCommand_InteractivelyRoutesToProjectHubWithoutRunningCliGuide()
    {
        var console = new TestConsole(isInteractive: true);
        IExcelDbProjectService? receivedService = null;
        string? receivedDirectory = null;

        var exitCode = await ExcelDbToolHost.RunAsync(
            [],
            console: console,
            currentDirectory: _root,
            runProjectHub: (service, directory) =>
            {
                receivedService = service;
                receivedDirectory = directory;
            });

        Assert.Equal(0, exitCode);
        Assert.NotNull(receivedService);
        Assert.Equal(Path.GetFullPath(_root), receivedDirectory);
        Assert.False(Directory.Exists(_root));
        Assert.DoesNotContain("Guide", console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitInit_CanWriteGeneratedCSharpDirectlyOutsideTheProject()
    {
        var external = Path.Combine(Path.GetTempPath(), "exceldb-cli-external", Guid.NewGuid().ToString("N"));
        try
        {
            var console = new TestConsole(isInteractive: false);

            var exitCode = await ExcelDbToolHost.RunAsync(
                ["init", _root, "--generated-csharp-dir", external],
                console: console,
                currentDirectory: Path.GetTempPath());

            Assert.Equal(0, exitCode);
            Assert.True(Directory.Exists(external));
            Assert.False(Directory.Exists(Path.Combine(_root, "Generated", "CSharp")));
            var project = ExcelDbProject.Load(Path.Combine(_root, ExcelDbProject.FileName));
            Assert.Equal(Path.GetFullPath(external), project.GeneratedCSharpDir);
        }
        finally
        {
            if (Directory.Exists(external))
                DeleteDirectory(external);
        }
    }

    [Fact]
    public async Task DryRunAndApplyPlan_ReplaysTheFrozenPlan()
    {
        Directory.CreateDirectory(_root);
        var planPath = Path.Combine(Path.GetTempPath(), $"init-{Guid.NewGuid():N}.plan.json");
        try
        {
            var preview = await ExcelDbToolHost.RunAsync(
                ["init", _root, "--dry-run", "--plan", planPath],
                console: new TestConsole(false),
                currentDirectory: _root);
            var apply = await ExcelDbToolHost.RunAsync(
                ["init", "--apply-plan", planPath],
                console: new TestConsole(false),
                currentDirectory: _root);

            Assert.Equal(0, preview);
            Assert.Equal(0, apply);
            Assert.True(File.Exists(Path.Combine(_root, ExcelDbProject.FileName)));
        }
        finally
        {
            if (File.Exists(planPath))
                File.Delete(planPath);
        }
    }

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

    private sealed class TestConsole : IToolConsole
    {
        private readonly StringBuilder _output = new();
        private readonly Queue<string> _input;

        public TestConsole(bool isInteractive, params string[] input)
        {
            IsInteractive = isInteractive;
            _input = new Queue<string>(input);
        }

        public bool IsInteractive { get; }

        public string Output => _output.ToString();

        public void Write(string text) => _output.Append(text);

        public void WriteLine(string text = "") => _output.AppendLine(text);

        public string? ReadLine() => _input.Count == 0 ? "y" : _input.Dequeue();
    }
}
