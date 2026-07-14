using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExcelDb.Compatibility;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.IO;
using ExcelDb.Pipeline;
using ExcelDb.Schema.Authoring;
using ExcelDb.Schema.Mutation;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;

namespace ExcelDb.Cli;

internal sealed class PipelineCommandDispatcher(
    ToolBuilder builder,
    IToolConsole console,
    string currentDirectory,
    string toolVersion)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly SchemaPipeline _schemas = new(toolVersion, builder.TableInitializer, builder.ExportTargetStrategy);

    public int Run(string command, string[] arguments) => command switch
    {
        "schema" => RunSchema(arguments),
        "table" => RunTable(arguments),
        "generate" => RunGenerate(arguments),
        "normalize" => RunNormalize(arguments),
        "check" => RunCheck(arguments),
        "convert" => RunConvert(arguments),
        "diff" => RunDiff(arguments),
        "data" => RunData(arguments),
        _ => throw new ArgumentException($"Unknown command '{command}'."),
    };

    private int RunSchema(string[] arguments)
    {
        if (arguments.Length == 0)
            throw new ArgumentException("schema requires 'build', 'compatibility' or 'publish'.");
        return arguments[0] switch
        {
            "build" => RunSchemaBuild(arguments[1..]),
            "compatibility" => RunSchemaCompatibility(arguments[1..]),
            "publish" => RunSchemaPublish(arguments[1..]),
            _ => throw new ArgumentException("schema requires 'build', 'compatibility' or 'publish'."),
        };
    }

    private int RunSchemaBuild(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var planOptions = PlanOptions.Parse(args);
        if (planOptions.ApplyPlan is not null)
            return ApplyFrozenPlan("schema-build", args, planOptions);
        var checkOnly = args.TakeFlag("--check");
        var common = ProjectOptions.Parse(args, currentDirectory);
        args.RequireEmpty();
        var outcome = _schemas.BuildAsync(common.Project, checkOnly).GetAwaiter().GetResult();
        if (outcome.Plan is null)
            return PresentReport(outcome.Report, common.JsonPath);
        return ExecutePlan(outcome.Plan, planOptions, common.JsonPath, prompt: !checkOnly);
    }

    private int RunSchemaCompatibility(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var common = ProjectOptions.Parse(args, currentDirectory);
        args.RequireEmpty();
        var outcome = new CompatibilityPipeline(toolVersion, _schemas, builder.CellFormats)
            .AnalyzeAsync(common.Project).GetAwaiter().GetResult();
        if (outcome.Report is null)
        {
            return PresentReport(new OperationReport(
                "schema-compatibility",
                toolVersion,
                false,
                outcome.Diagnostics,
                []), common.JsonPath);
        }

        foreach (var entry in outcome.Report.Entries)
            console.WriteLine($"{entry.Severity.ToString().ToLowerInvariant()} {entry.Kind}: {entry.Message}");
        if (common.JsonPath is not null)
            AtomicFile.WriteAllBytes(common.JsonPath, [.. CompatibilityReportSerializer.SerializeUtf8(outcome.Report), (byte)'\n']);
        return outcome.Report.MaximumSeverity switch
        {
            CompatibilitySeverity.Blocker => (int)OperationExitCode.Blocker,
            CompatibilitySeverity.Error => (int)OperationExitCode.Error,
            _ => 0,
        };
    }

    private int RunSchemaPublish(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var planOptions = PlanOptions.Parse(args);
        if (planOptions.ApplyPlan is not null)
            return ApplyFrozenPlan("schema-publish", args, planOptions);
        var completedHostMigrations = args.TakeOptions("--host-migrated");
        var common = ProjectOptions.Parse(args, currentDirectory);
        args.RequireEmpty();
        var plan = new CompatibilityPipeline(toolVersion, _schemas, builder.CellFormats)
            .CreatePublishPlanAsync(common.Project, completedHostMigrations).GetAwaiter().GetResult();
        return ExecutePlan(plan, planOptions, common.JsonPath, prompt: true);
    }

    private int RunTable(string[] arguments)
    {
        if (arguments.Length == 0)
            throw new ArgumentException("table requires 'create' or 'edit'.");
        return arguments[0] switch
        {
            "create" => RunTableCreate(arguments[1..]),
            "edit" => RunTableEdit(arguments[1..]),
            _ => throw new ArgumentException("table requires 'create' or 'edit'."),
        };
    }

    private int RunTableCreate(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var planOptions = PlanOptions.Parse(args);
        if (planOptions.ApplyPlan is not null)
            return ApplyFrozenPlan("table-create", args, planOptions);
        var common = ProjectOptions.Parse(args, currentDirectory);
        var tableName = args.TakeFirstPositional();
        if (tableName is null)
        {
            if (!console.IsInteractive)
                throw new ArgumentException("table create requires a table name in non-interactive mode.");
            console.Write("Table name: ");
            tableName = RequireAnswer(console.ReadLine(), "table name");
        }
        var workbook = args.TakeOption("--workbook");
        if (workbook is null)
        {
            if (!console.IsInteractive)
                throw new ArgumentException("table create requires --workbook in non-interactive mode.");
            console.Write("Workbook [Data/game.xlsx]: ");
            workbook = console.ReadLine();
            if (string.IsNullOrWhiteSpace(workbook))
                workbook = "Data/game.xlsx";
        }

        var autoKeyFlag = args.TakeFlag("--auto-key");
        var key = args.TakeOption("--key");
        var fields = args.TakeOptions("--field").Select(ParseField).ToList();
        if (key is not null)
        {
            if (autoKeyFlag)
                throw new ArgumentException("--auto-key and --key cannot be combined.");
            fields.Add(ParseField(key) with { IsKey = true });
        }
        var autoKey = key is null;
        var package = args.TakeOption("--package") ?? "game.configs";
        var proto = args.TakeOption("--proto");
        var tableIdText = args.TakeOption("--id");
        int? tableId = tableIdText is null ? null : ParsePositiveInt(tableIdText, "--id");
        var tableTargets = ParseTargetSelection(args.TakeOption("--table-target"));
        var fieldTargets = ImmutableDictionary.CreateBuilder<string, ExportTargetSelection>(StringComparer.Ordinal);
        foreach (var value in args.TakeOptions("--field-target"))
        {
            var (name, selection) = ParseNamedTargetSelection(value);
            if (selection is not null)
                fieldTargets.Add(name, selection);
        }
        args.RequireEmpty();
        var intent = new TableCreateIntent(
            tableName,
            workbook,
            fields.ToImmutableArray(),
            autoKey,
            package,
            tableId,
            fieldTargets.ToImmutable(),
            tableTargets,
            proto);
        var plan = _schemas.CreateTableAsync(common.Project, intent).GetAwaiter().GetResult();
        return ExecutePlan(plan, planOptions, common.JsonPath, prompt: true);
    }

    private int RunTableEdit(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var planOptions = PlanOptions.Parse(args);
        if (planOptions.ApplyPlan is not null)
            return ApplyFrozenPlan(["table-edit", "table-retire"], args, planOptions);
        var common = ProjectOptions.Parse(args, currentDirectory);
        var tableName = args.TakeFirstPositional();
        if (tableName is null)
        {
            if (!console.IsInteractive)
                throw new ArgumentException("table edit requires a table full name in non-interactive mode.");
            console.Write("Table full name: ");
            tableName = RequireAnswer(console.ReadLine(), "table name");
        }
        var additions = args.TakeOptions("--add").Select(value => new AddSimpleFieldMutation(ParseField(value))).ToImmutableArray();
        var renames = args.TakeOptions("--rename").Select(ParseRename).ToImmutableArray();
        var removals = args.TakeOptions("--remove").Select(value => ParsePositiveInt(value, "--remove")).ToImmutableArray();
        var retire = args.TakeFlag("--retire");
        var newName = args.TakeOption("--new-name");
        var workbook = args.TakeOption("--workbook");
        var tableTargets = ParseTargetSelection(args.TakeOption("--table-target"));
        var fieldTargets = ImmutableDictionary.CreateBuilder<int, ExportTargetSelection>();
        foreach (var value in args.TakeOptions("--field-target"))
        {
            var separator = value.IndexOf('=');
            if (separator <= 0)
                throw new ArgumentException("--field-target must be <field-number>=<targets>.");
            var fieldNumber = ParsePositiveInt(value[..separator], "--field-target");
            var selection = ParseTargetSelection(value[(separator + 1)..])
                ?? throw new ArgumentException("The edit target selection cannot be 'auto'; omit it to keep automatic policy.");
            fieldTargets.Add(fieldNumber, selection);
        }
        args.RequireEmpty();
        if (!retire && additions.IsEmpty && renames.IsEmpty && removals.IsEmpty && newName is null && tableTargets is null && fieldTargets.Count == 0)
            throw new ArgumentException("table edit contains no requested change.");
        var intent = new TableEditIntent(tableName, additions, renames, removals, retire, newName, tableTargets, fieldTargets.ToImmutable(), workbook);
        var plan = _schemas.EditTableAsync(common.Project, intent).GetAwaiter().GetResult();
        return ExecutePlan(plan, planOptions, common.JsonPath, prompt: true);
    }

    private int RunGenerate(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var planOptions = PlanOptions.Parse(args);
        if (planOptions.ApplyPlan is not null)
            return ApplyFrozenPlan("generate", args, planOptions);
        var common = ProjectOptions.Parse(args, currentDirectory);
        var workbook = args.TakeOption("--workbook");
        var purge = args.TakeFlag("--purge");
        var rekey = args.TakeFlag("--rekey");
        args.RequireEmpty();
        var plan = new WorkbookPipeline(toolVersion, _schemas, builder.CellFormats, builder.CreateWorkbookValidatorRegistry()).GenerateAsync(common.Project, workbook, purge, rekey).GetAwaiter().GetResult();
        return ExecutePlan(plan, planOptions, common.JsonPath, prompt: true);
    }

    private int RunNormalize(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var planOptions = PlanOptions.Parse(args);
        if (planOptions.ApplyPlan is not null)
            return ApplyFrozenPlan("normalize", args, planOptions);
        var common = ProjectOptions.Parse(args, currentDirectory);
        var workbook = args.TakeOption("--workbook");
        args.RequireEmpty();
        var plan = new WorkbookPipeline(toolVersion, _schemas, builder.CellFormats, builder.CreateWorkbookValidatorRegistry()).NormalizeAsync(common.Project, workbook).GetAwaiter().GetResult();
        return ExecutePlan(plan, planOptions, common.JsonPath, prompt: true);
    }

    private int RunData(string[] arguments)
    {
        if (arguments.Length == 0 || !string.Equals(arguments[0], "prepare", StringComparison.Ordinal))
            throw new ArgumentException("data requires the 'prepare' subcommand.");
        var args = new CommandArguments(arguments[1..]);
        var planOptions = PlanOptions.Parse(args);
        if (planOptions.ApplyPlan is not null)
            return ApplyFrozenPlan("data-prepare", args, planOptions);
        var common = ProjectOptions.Parse(args, currentDirectory);
        var workbook = args.TakeOption("--workbook");
        args.RequireEmpty();
        var plan = new WorkbookPipeline(toolVersion, _schemas, builder.CellFormats, builder.CreateWorkbookValidatorRegistry()).DataPrepareAsync(common.Project, workbook).GetAwaiter().GetResult();
        return ExecutePlan(plan, planOptions, common.JsonPath, prompt: true);
    }

    private int RunCheck(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var common = ProjectOptions.Parse(args, currentDirectory);
        var workbook = args.TakeOption("--workbook");
        args.RequireEmpty();
        var outcome = new WorkbookPipeline(toolVersion, _schemas, builder.CellFormats, builder.CreateWorkbookValidatorRegistry()).CheckAsync(common.Project, workbook).GetAwaiter().GetResult();
        return PresentReport(outcome.Report with { ToolVersion = toolVersion }, common.JsonPath);
    }

    private int RunConvert(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var common = ProjectOptions.Parse(args, currentDirectory);
        var target = args.TakeOption("--target") ?? "client";
        if (target is "all" || target.Contains(','))
            throw new ArgumentException("convert accepts exactly one explicit target; 'all' and target lists are not supported.");
        var output = args.TakeOption("--out");
        var workbook = args.TakeOption("--workbook");
        if (output is null && !string.Equals(target, "client", StringComparison.Ordinal))
            throw new ArgumentException("A non-client convert requires an explicit --out path.");
        output ??= WorkbookPipeline.DefaultOutputPath(common.Project, target);
        args.RequireEmpty();
        var report = new WorkbookPipeline(toolVersion, _schemas, builder.CellFormats, builder.CreateWorkbookValidatorRegistry()).ConvertAsync(common.Project, target, output, workbook).GetAwaiter().GetResult();
        return PresentReport(report, common.JsonPath);
    }

    private int RunDiff(string[] arguments)
    {
        var args = new CommandArguments(arguments);
        var json = args.TakeOption("--json");
        var positions = args.TakeAllPositionals();
        args.RequireEmpty();
        if (positions.Length != 2)
            throw new ArgumentException("diff requires <base.xlsx> <target.xlsx>.");
        var basePath = Path.GetFullPath(positions[0], currentDirectory);
        var targetPath = Path.GetFullPath(positions[1], currentDirectory);
        var report = new WorkbookPipeline(toolVersion, _schemas, builder.CellFormats, builder.CreateWorkbookValidatorRegistry()).Diff(basePath, targetPath);
        foreach (var diagnostic in report.Diagnostics)
            WriteDiagnostic(diagnostic);
        foreach (var entry in report.Entries)
            console.WriteLine($"{entry.Kind} table={entry.TableId} id={entry.Identity} field={entry.PropertyPath ?? "-"}: {entry.Before ?? "<missing>"} -> {entry.After ?? "<missing>"}");
        if (json is not null)
        {
            var path = Path.GetFullPath(json, currentDirectory);
            AtomicFile.WriteAllBytes(path, [.. JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions), (byte)'\n']);
            console.WriteLine($"report: {path}");
        }
        return report.Succeeded ? 0 : 1;
    }

    private int ExecutePlan(MutationPlan plan, PlanOptions options, string? jsonPath, bool prompt)
    {
        if (options.DryRun && jsonPath is not null)
            throw new ArgumentException("--dry-run cannot be combined with --json.");
        PresentPlan(plan);
        if (options.DryRun)
        {
            var path = Path.GetFullPath(options.PlanPath!, currentDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllBytes(path, MutationPlanCodec.Serialize(plan));
            console.WriteLine($"plan: {path}");
            return plan.HasBlockers ? 2 : plan.Diagnostics.Any(static item => item.IsFailure) ? 1 : 0;
        }
        if (prompt && console.IsInteractive)
        {
            console.Write("Apply this exact plan? [y/N] ");
            var answer = console.ReadLine();
            if (!IsYes(answer))
            {
                console.WriteLine("Cancelled; no domain files were written.");
                return 0;
            }
        }
        return PresentReport(new MutationPlanApplier().Apply(plan), jsonPath);
    }

    private int ApplyFrozenPlan(string expectedOperation, CommandArguments args, PlanOptions options) =>
        ApplyFrozenPlan([expectedOperation], args, options);

    private int ApplyFrozenPlan(string[] expectedOperations, CommandArguments args, PlanOptions options)
    {
        args.RequireEmpty();
        var path = Path.GetFullPath(options.ApplyPlan!, currentDirectory);
        var plan = MutationPlanCodec.Deserialize(File.ReadAllBytes(path));
        if (!expectedOperations.Contains(plan.Operation, StringComparer.Ordinal))
            throw new ArgumentException($"Plan operation '{plan.Operation}' does not match this command.");
        return PresentReport(new MutationPlanApplier().Apply(plan), null);
    }

    private int PresentReport(OperationReport report, string? jsonPath)
    {
        foreach (var diagnostic in report.Diagnostics)
            WriteDiagnostic(diagnostic);
        if (jsonPath is not null)
        {
            var path = Path.IsPathRooted(jsonPath) ? jsonPath : Path.GetFullPath(jsonPath, report.Artifacts.FirstOrDefault()?.Path is { } artifact ? Path.GetDirectoryName(artifact)! : currentDirectory);
            AtomicFile.WriteAllBytes(path, [.. JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions), (byte)'\n']);
            console.WriteLine($"report: {path}");
        }
        console.WriteLine(report.Succeeded
            ? report.Applied ? $"{report.Operation} applied." : $"{report.Operation} completed; no domain change required."
            : $"{report.Operation} failed; previous domain artifacts were preserved.");
        return (int)report.ExitCode;
    }

    private void PresentPlan(MutationPlan plan)
    {
        console.WriteLine($"Operation: {plan.Operation}");
        console.WriteLine($"Project: {plan.ProjectRoot}");
        console.WriteLine($"Plan: {plan.PlanHash}");
        foreach (var risk in plan.Risks)
            console.WriteLine($"  risk: {risk}");
        foreach (var mutation in plan.Mutations)
            console.WriteLine($"  {mutation.Kind}: {mutation.RelativePath}");
        foreach (var diagnostic in plan.Diagnostics)
            WriteDiagnostic(diagnostic);
    }

    private void WriteDiagnostic(Diagnostic diagnostic) =>
        console.WriteLine($"{diagnostic.Severity.ToString().ToLowerInvariant()} {diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}");

    private static SimpleFieldDefinition ParseField(string value)
    {
        var pieces = value.Split(':', StringSplitOptions.TrimEntries);
        if (pieces.Length is < 2 or > 3 || string.IsNullOrWhiteSpace(pieces[0]))
            throw new ArgumentException("A field must be <name>:<type>[:key].");
        var isKey = pieces.Length == 3 && string.Equals(pieces[2], "key", StringComparison.OrdinalIgnoreCase);
        if (pieces.Length == 3 && !isKey)
            throw new ArgumentException("The only third field component is ':key'.");
        string? enumType = null;
        SimpleFieldType type;
        if (pieces[1].StartsWith("enum=", StringComparison.OrdinalIgnoreCase))
        {
            type = SimpleFieldType.Enum;
            enumType = pieces[1][5..];
        }
        else if (!Enum.TryParse<SimpleFieldType>(pieces[1], ignoreCase: true, out type))
        {
            throw new ArgumentException($"Unsupported simple field type '{pieces[1]}'.");
        }
        return new SimpleFieldDefinition(pieces[0], type, enumType, isKey);
    }

    private static RenameFieldMutation ParseRename(string value)
    {
        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            throw new ArgumentException("--rename must be <field-number>:<new-name>.");
        return new RenameFieldMutation(ParsePositiveInt(value[..separator], "--rename"), value[(separator + 1)..]);
    }

    private static (string Name, ExportTargetSelection? Selection) ParseNamedTargetSelection(string value)
    {
        var separator = value.IndexOf('=');
        if (separator <= 0)
            throw new ArgumentException("--field-target must be <field-name>=<targets>.");
        return (value[..separator], ParseTargetSelection(value[(separator + 1)..]));
    }

    private static ExportTargetSelection? ParseTargetSelection(string? value)
    {
        if (value is null || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            return ExportTargetSelection.Explicit([]);
        return ExportTargetSelection.Explicit(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static int ParsePositiveInt(string value, string option) =>
        int.TryParse(value, out var result) && result > 0 ? result : throw new ArgumentException($"{option} requires a positive integer.");

    private static string RequireAnswer(string? answer, string name) =>
        !string.IsNullOrWhiteSpace(answer) ? answer : throw new ArgumentException($"Interactive {name} is required.");

    private static bool IsYes(string? value) => string.Equals(value, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private sealed record PlanOptions(bool DryRun, string? PlanPath, string? ApplyPlan)
    {
        public static PlanOptions Parse(CommandArguments args)
        {
            var dryRun = args.TakeFlag("--dry-run");
            var plan = args.TakeOption("--plan");
            var apply = args.TakeOption("--apply-plan");
            if (dryRun != (plan is not null))
                throw new ArgumentException("--dry-run and --plan must be supplied together.");
            if (apply is not null && (dryRun || plan is not null))
                throw new ArgumentException("--apply-plan cannot be combined with --dry-run or --plan.");
            return new PlanOptions(dryRun, plan, apply);
        }
    }

    private sealed record ProjectOptions(PipelineProject Project, string? JsonPath)
    {
        public static ProjectOptions Parse(CommandArguments args, string currentDirectory)
        {
            var projectOption = args.TakeOption("--project");
            var schemaOverride = args.TakeOption("--schema-dir");
            var workbookOverrides = args.TakeOptions("--workbooks");
            var json = args.TakeOption("--json");
            var projectFile = projectOption is null
                ? ProjectLocator.FindNearest(currentDirectory)
                : ResolveProjectFile(Path.GetFullPath(projectOption, currentDirectory));
            if (projectFile is null)
                throw new ArgumentException("No ExcelDb.Project.json was found; run init first or pass --project.");
            var project = PipelineProject.Load(
                projectFile,
                schemaOverride,
                workbookOverrides.Length == 0 ? null : workbookOverrides.ToImmutableArray());
            var jsonPath = json is null ? null : project.Context.ResolvePath(json);
            return new ProjectOptions(project, jsonPath);
        }

        private static string ResolveProjectFile(string path) => Directory.Exists(path) ? Path.Combine(path, ExcelDbProject.FileName) : path;
    }

    private sealed class CommandArguments
    {
        private readonly List<string> _values;
        public CommandArguments(IEnumerable<string> values) => _values = [.. values];

        public bool TakeFlag(string name)
        {
            var indexes = _values.Select((value, index) => (value, index)).Where(item => string.Equals(item.value, name, StringComparison.Ordinal)).Select(static item => item.index).ToArray();
            if (indexes.Length > 1)
                throw new ArgumentException($"Option {name} may be supplied once.");
            if (indexes.Length == 0)
                return false;
            _values.RemoveAt(indexes[0]);
            return true;
        }

        public string? TakeOption(string name)
        {
            var values = TakeOptions(name);
            if (values.Length > 1)
                throw new ArgumentException($"Option {name} may be supplied once.");
            return values.SingleOrDefault();
        }

        public string[] TakeOptions(string name)
        {
            var result = new List<string>();
            for (var index = 0; index < _values.Count;)
            {
                if (!string.Equals(_values[index], name, StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }
                if (index + 1 >= _values.Count || _values[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Option {name} requires a value.");
                result.Add(_values[index + 1]);
                _values.RemoveRange(index, 2);
            }
            return result.ToArray();
        }

        public string? TakeFirstPositional()
        {
            var index = _values.FindIndex(static value => !value.StartsWith("-", StringComparison.Ordinal));
            if (index < 0)
                return null;
            var value = _values[index];
            _values.RemoveAt(index);
            return value;
        }

        public string[] TakeAllPositionals()
        {
            var result = _values.Where(static value => !value.StartsWith("-", StringComparison.Ordinal)).ToArray();
            _values.RemoveAll(static value => !value.StartsWith("-", StringComparison.Ordinal));
            return result;
        }

        public void RequireEmpty()
        {
            if (_values.Count != 0)
                throw new ArgumentException($"Unknown or misplaced argument '{_values[0]}'.");
        }
    }
}
