using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;

namespace ExcelDb.Cli;

public static class ExcelDbToolHost
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static Task<int> RunAsync(
        string[] args,
        Action<ToolBuilder>? configure = null,
        IToolConsole? console = null,
        string? currentDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = new ToolBuilder();
        configure?.Invoke(builder);
        return Task.FromResult(Run(args, builder, console ?? new SystemToolConsole(), currentDirectory ?? Environment.CurrentDirectory));
    }

    private static int Run(string[] args, ToolBuilder builder, IToolConsole console, string currentDirectory)
    {
        try
        {
            if (args.Length == 0)
            {
                if (!console.IsInteractive)
                {
                    WriteUsage(console);
                    return (int)OperationExitCode.UsageOrEnvironment;
                }

                return RunGuide(builder, console, currentDirectory);
            }

            if (args is ["--help"] or ["-h"] or ["help"])
            {
                WriteUsage(console);
                return 0;
            }

            if (args is ["--version"] or ["-v"])
            {
                console.WriteLine(ToolVersion);
                return 0;
            }

            return args[0] switch
            {
                "init" => RunInit(args[1..], console, currentDirectory, implicitInteractive: false),
                "schema" or "table" or "generate" or "normalize" or "check" or "convert" or "diff" or "data" =>
                    new PipelineCommandDispatcher(builder, console, currentDirectory, ToolVersion).Run(args[0], args[1..]),
                _ => UsageError(console, $"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or InvalidDataException
                                           or JsonException
                                           or ArgumentException
                                           or InvalidOperationException)
        {
            console.WriteLine($"error: {exception.Message}");
            return (int)OperationExitCode.UsageOrEnvironment;
        }
    }

    private static int RunGuide(ToolBuilder builder, IToolConsole console, string currentDirectory)
    {
        console.WriteLine($"ExcelDB Project guide  {Path.GetFullPath(currentDirectory)}");
        console.WriteLine("[1/3] Project initialization");
        var projectFile = ProjectLocator.FindNearest(currentDirectory);
        if (projectFile is null)
        {
            var init = RunInit([currentDirectory], console, currentDirectory, implicitInteractive: true);
            if (init != 0)
                return init;
            projectFile = Path.Combine(Path.GetFullPath(currentDirectory), ExcelDbProject.FileName);
            if (!File.Exists(projectFile))
                return 0;
        }
        else
        {
            console.WriteLine($"  Project: {projectFile}");
        }

        var project = ExcelDbProject.Load(projectFile).Resolve(projectFile);
        var dispatcher = new PipelineCommandDispatcher(builder, console, project.RootDirectory, ToolVersion);
        console.WriteLine("[2/3] Create or synchronize table structure and generated C#");
        var hasProto = Directory.Exists(project.SchemaDirectory)
            && Directory.EnumerateFiles(project.SchemaDirectory, "*.proto", SearchOption.AllDirectories).Any();
        if (!hasProto)
        {
            console.Write("Table name [Hero]: ");
            var table = console.ReadLine();
            if (string.IsNullOrWhiteSpace(table))
                table = "Hero";
            console.Write("Workbook [Data/game.xlsx]: ");
            var workbook = console.ReadLine();
            if (string.IsNullOrWhiteSpace(workbook))
                workbook = "Data/game.xlsx";
            console.Write("Simple fields, comma-separated [name:string,hp:int32]: ");
            var fieldText = console.ReadLine();
            if (string.IsNullOrWhiteSpace(fieldText))
                fieldText = "name:string,hp:int32";
            var createArgs = new List<string> { "create", table, "--workbook", workbook, "--auto-key" };
            foreach (var field in fieldText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                createArgs.Add("--field");
                createArgs.Add(field);
            }
            console.WriteLine($"> exceldb table create {table} --workbook {workbook}");
            var create = dispatcher.Run("table", [.. createArgs]);
            if (create != 0)
                return create;
        }
        else
        {
            var checkBuild = dispatcher.Run("schema", ["build", "--check"]);
            if (checkBuild != 0)
            {
                console.WriteLine("> exceldb schema build");
                var build = dispatcher.Run("schema", ["build"]);
                if (build != 0)
                    return build;
            }
        }

        console.WriteLine("[3/3] Fill Excel, prepare identities, validate, and convert client data");
        console.WriteLine("Save the workbook in Excel, then press Enter to continue.");
        _ = console.ReadLine();
        console.WriteLine("> exceldb data prepare");
        var prepare = dispatcher.Run("data", ["prepare"]);
        if (prepare != 0)
            return prepare;
        console.WriteLine("> exceldb check");
        var check = dispatcher.Run("check", []);
        if (check != 0)
            return check;
        console.WriteLine("> exceldb convert");
        var convert = dispatcher.Run("convert", []);
        if (convert == 0)
            console.WriteLine("Guide complete. Runtime, application write-back, Play switching, Git review, CI and publishing remain explicit entry points.");
        return convert;
    }

    private static int RunInit(
        string[] args,
        IToolConsole console,
        string currentDirectory,
        bool implicitInteractive)
    {
        string? target = null;
        string? planOutput = null;
        string? applyPlan = null;
        string? jsonOutput = null;
        var dryRun = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--plan":
                    planOutput = ReadValue(args, ref index, "--plan");
                    break;
                case "--apply-plan":
                    applyPlan = ReadValue(args, ref index, "--apply-plan");
                    break;
                case "--json":
                    jsonOutput = ReadValue(args, ref index, "--json");
                    break;
                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal))
                        return UsageError(console, $"Unknown init option '{args[index]}'.");
                    if (target is not null)
                        return UsageError(console, "init accepts at most one target path.");
                    target = args[index];
                    break;
            }
        }

        if (applyPlan is not null)
        {
            if (target is not null || dryRun || planOutput is not null || jsonOutput is not null)
                return UsageError(console, "--apply-plan cannot be combined with target, --dry-run, --plan or --json.");

            var planPath = Path.GetFullPath(applyPlan, currentDirectory);
            var frozenPlan = MutationPlanCodec.Deserialize(File.ReadAllBytes(planPath));
            if (!string.Equals(frozenPlan.Operation, "init", StringComparison.Ordinal))
                return UsageError(console, "The supplied plan is not an init plan.");
            return PresentReport(console, new MutationPlanApplier().Apply(frozenPlan), null);
        }

        if (dryRun != (planOutput is not null))
            return UsageError(console, "--dry-run and --plan must be supplied together.");
        if (dryRun && jsonOutput is not null)
            return UsageError(console, "--dry-run cannot be combined with --json.");

        target ??= currentDirectory;
        var fullTarget = Path.GetFullPath(target, currentDirectory);
        var initializer = new ProjectInitializer(ToolVersion);
        var plan = initializer.Plan(fullTarget);

        if (dryRun)
        {
            var planPath = Path.GetFullPath(planOutput!, currentDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            plan = initializer.Plan(fullTarget);
            AtomicFile.WriteAllBytes(planPath, MutationPlanCodec.Serialize(plan));
            PresentPlan(console, plan);
            console.WriteLine($"plan: {planPath}");
            return plan.HasBlockers ? (int)OperationExitCode.Blocker : 0;
        }

        PresentPlan(console, plan);
        var shouldConfirm = implicitInteractive || (args.Length == 0 && console.IsInteractive);
        if (shouldConfirm)
        {
            console.Write("Apply? [y/N] ");
            var answer = console.ReadLine();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                console.WriteLine("Cancelled; no project files were written.");
                return 0;
            }
        }

        var report = new MutationPlanApplier().Apply(plan);
        return PresentReport(console, report, jsonOutput is null ? null : Path.GetFullPath(jsonOutput, currentDirectory));
    }

    private static int PresentReport(IToolConsole console, OperationReport report, string? jsonPath)
    {
        foreach (var diagnostic in report.Diagnostics)
            console.WriteLine($"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}");

        if (jsonPath is not null)
        {
            AtomicFile.WriteAllBytes(jsonPath, [.. JsonSerializer.SerializeToUtf8Bytes(report, ReportJsonOptions), (byte)'\n']);
            console.WriteLine($"report: {jsonPath}");
        }

        console.WriteLine(report.Succeeded
            ? report.Applied ? "Project initialized." : "No changes required."
            : "Operation failed; no project was initialized.");
        return (int)report.ExitCode;
    }

    private static void PresentPlan(IToolConsole console, MutationPlan plan)
    {
        console.WriteLine($"Operation: {plan.Operation}");
        console.WriteLine($"Project: {plan.ProjectRoot}");
        console.WriteLine($"Plan: {plan.PlanHash}");
        foreach (var mutation in plan.Mutations)
            console.WriteLine($"  {mutation.Kind}: {mutation.RelativePath}");
        foreach (var diagnostic in plan.Diagnostics)
            console.WriteLine($"  {diagnostic.Severity}: {diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}");
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"Option {option} requires a value.");
        return args[index];
    }

    private static int UsageError(IToolConsole console, string message)
    {
        console.WriteLine($"usage error: {message}");
        WriteUsage(console);
        return (int)OperationExitCode.UsageOrEnvironment;
    }

    private static void WriteUsage(IToolConsole console)
    {
        console.WriteLine("ExcelDB project tool");
        console.WriteLine("usage:");
        console.WriteLine("  exceldb init [path]");
        console.WriteLine("  exceldb init [path] --dry-run --plan <file>");
        console.WriteLine("  exceldb init --apply-plan <file>");
        console.WriteLine("  exceldb schema build [--check]");
        console.WriteLine("  exceldb schema compatibility [--json <report>]");
        console.WriteLine("  exceldb schema publish [--host-migrated <target>] [--dry-run --plan <file> | --apply-plan <file>]");
        console.WriteLine("  exceldb table create <name> --workbook <xlsx> [--auto-key] [--field <name:type>]");
        console.WriteLine("  exceldb table edit <full-name> [--add <name:type>] [--rename <number:name>] [--remove <number>] [--retire]");
        console.WriteLine("  exceldb generate [--workbook <xlsx>] [--purge|--rekey]");
        console.WriteLine("  exceldb normalize [--workbook <xlsx>]");
        console.WriteLine("  exceldb data prepare [--workbook <xlsx>]");
        console.WriteLine("  exceldb check [--workbook <xlsx>] [--json <report>]");
        console.WriteLine("  exceldb convert [--target client|server] [--out <bytes>] [--json <report>]");
        console.WriteLine("  exceldb diff <base.xlsx> <target.xlsx> [--json <report>]");
        console.WriteLine("  exceldb --help | --version");
    }

    public static string ToolVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.1.0";
}
