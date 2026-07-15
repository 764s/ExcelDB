using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;
using ExcelDb.Pipeline;
using ExcelDb.ProjectHub.Windows;
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
        string? currentDirectory = null,
        Action<IExcelDbProjectService, string>? runProjectHub = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = new ToolBuilder();
        configure?.Invoke(builder);
        return Task.FromResult(Run(
            args,
            builder,
            console ?? new SystemToolConsole(),
            currentDirectory ?? Environment.CurrentDirectory,
            runProjectHub));
    }

    private static int Run(
        string[] args,
        ToolBuilder builder,
        IToolConsole console,
        string currentDirectory,
        Action<IExcelDbProjectService, string>? runProjectHub)
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

                var launcher = runProjectHub ?? RunProjectHub;
                launcher(builder.CreateProjectService(ToolVersion), Path.GetFullPath(currentDirectory));
                return 0;
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
                "init" => RunInit(builder, args[1..], console, currentDirectory, implicitInteractive: false),
                "project" or "schema" or "table" or "generate" or "normalize" or "check" or "convert" or "diff" or "data" =>
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

    private static int RunInit(
        ToolBuilder builder,
        string[] args,
        IToolConsole console,
        string currentDirectory,
        bool implicitInteractive)
    {
        string? target = null;
        string? planOutput = null;
        string? applyPlan = null;
        string? jsonOutput = null;
        string? generatedCSharpDirectory = null;
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
                case "--generated-csharp-dir":
                    generatedCSharpDirectory = ReadValue(args, ref index, "--generated-csharp-dir");
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
            if (target is not null || generatedCSharpDirectory is not null || dryRun || planOutput is not null || jsonOutput is not null)
                return UsageError(console, "--apply-plan cannot be combined with target, --generated-csharp-dir, --dry-run, --plan or --json.");

            var planPath = Path.GetFullPath(applyPlan, currentDirectory);
            var frozenPlan = MutationPlanCodec.Deserialize(File.ReadAllBytes(planPath));
            if (!string.Equals(frozenPlan.Operation, "init", StringComparison.Ordinal))
                return UsageError(console, "The supplied plan is not an init plan.");
            return PresentReport(console, builder.CreateProjectService(ToolVersion).Apply(frozenPlan), null);
        }

        if (dryRun != (planOutput is not null))
            return UsageError(console, "--dry-run and --plan must be supplied together.");
        if (dryRun && jsonOutput is not null)
            return UsageError(console, "--dry-run cannot be combined with --json.");

        target ??= currentDirectory;
        var fullTarget = Path.GetFullPath(target, currentDirectory);
        var projects = builder.CreateProjectService(ToolVersion);
        var plan = projects.PlanInitialize(new ExcelDb.Pipeline.InitializeProjectRequest(
            ExcelDb.Pipeline.ProjectLocation.From(fullTarget),
            generatedCSharpDirectory is null ? null : Path.GetFullPath(generatedCSharpDirectory, currentDirectory)));

        if (dryRun)
        {
            var planPath = Path.GetFullPath(planOutput!, currentDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            plan = projects.PlanInitialize(new ExcelDb.Pipeline.InitializeProjectRequest(
                ExcelDb.Pipeline.ProjectLocation.From(fullTarget),
                generatedCSharpDirectory is null ? null : Path.GetFullPath(generatedCSharpDirectory, currentDirectory)));
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

        var report = projects.Apply(plan);
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
        if (IsExternalRoot(plan.ProjectRoot, plan.GeneratedCSharpRoot))
            console.WriteLine($"Generated C#: {plan.GeneratedCSharpRoot} (external root)");
        console.WriteLine($"Plan: {plan.PlanHash}");
        foreach (var mutation in plan.Mutations)
            console.WriteLine($"  [{mutation.Root}] {mutation.Kind}: {mutation.RelativePath}");
        foreach (var diagnostic in plan.Diagnostics)
            console.WriteLine($"  {diagnostic.Severity}: {diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}");
    }

    private static void RunProjectHub(IExcelDbProjectService service, string initialDirectory)
    {
        WindowsConsoleWindow.DetachIfOwned();
        ProjectHubApplication.Run(service, initialDirectory);
    }

    private static bool IsExternalRoot(string projectRoot, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        var relative = Path.GetRelativePath(Path.GetFullPath(projectRoot), Path.GetFullPath(candidate));
        return Path.IsPathFullyQualified(relative)
               || relative == ".."
               || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
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
        console.WriteLine("  exceldb                         # open Project Hub (interactive Windows)");
        console.WriteLine("  exceldb init [path] [--generated-csharp-dir <path>]");
        console.WriteLine("  exceldb init [path] --dry-run --plan <file>");
        console.WriteLine("  exceldb init --apply-plan <file>");
        console.WriteLine("  exceldb project inspect");
        console.WriteLine("  exceldb project configure [--schema-dir <path>] [--excel-dir <path>] [--generated-csharp-dir <path>] [--generated-bytes-dir <path>]");
        console.WriteLine("  exceldb project repair-imports");
        console.WriteLine("  exceldb project clean-cache");
        console.WriteLine("  exceldb schema build [--check]");
        console.WriteLine("  exceldb schema compatibility [--json <report>]");
        console.WriteLine("  exceldb schema publish [--host-migrated <target>] [--dry-run --plan <file> | --apply-plan <file>]");
        console.WriteLine("  exceldb table create <name> --workbook <xlsx> [--auto-key | --key <name:type> | --field <name:type:key>] [--field <name:type>]");
        console.WriteLine("  exceldb table edit <full-name> [--add <name:type>] [--rename <number:name>] [--remove <number>] [--retire]");
        console.WriteLine("  exceldb generate");
        console.WriteLine("  exceldb normalize [--workbook <xlsx>]");
        console.WriteLine("  exceldb data prepare [--workbook <xlsx>]");
        console.WriteLine("  exceldb check [--workbook <xlsx>] [--json <report>]");
        console.WriteLine("  exceldb convert [--target <target-id>] [--out <bytes>] [--json <report>]");
        console.WriteLine("  exceldb diff <base.xlsx> <target.xlsx> [--json <report>]");
        console.WriteLine("  exceldb --help | --version");
    }

    public static string ToolVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.1.0";
}
