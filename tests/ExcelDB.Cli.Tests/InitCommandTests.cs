using System.Text;
using ExcelDb.Cli;
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
        Assert.Contains("Project initialized", console.Output);
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
    public async Task NoCommand_InteractivelyInitializesTheCurrentDirectory()
    {
        var console = new TestConsole(isInteractive: true, "y", "", "", "", "y", "");

        var exitCode = await ExcelDbToolHost.RunAsync([], console: console, currentDirectory: _root);

        Assert.True(exitCode == 0, console.Output);
        Assert.True(File.Exists(Path.Combine(_root, ExcelDbProject.FileName)));
        Assert.True(File.Exists(Path.Combine(_root, "Schema", "Hero.proto")));
        Assert.True(File.Exists(Path.Combine(_root, "Data", "game.xlsx")));
        Assert.Contains("Guide complete", console.Output);
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
            Directory.Delete(_root, recursive: true);
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
