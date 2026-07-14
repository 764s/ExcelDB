using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Runtime;
using ExcelDb.Runtime.Bytes;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;
using ExcelDbEditor;

namespace ExcelDb.Editor;

public sealed class EditorReportRing
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
    private readonly int _capacity;
    private readonly Queue<EditorReportEntry> _reports;

    public EditorReportRing(int capacity = 100)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _reports = new Queue<EditorReportEntry>(capacity);
    }

    public ImmutableArray<EditorReportEntry> Entries => _reports.Reverse().ToImmutableArray();

    public void Add(OperationReport report, string? summary = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        while (_reports.Count >= _capacity)
            _reports.Dequeue();
        _reports.Enqueue(new EditorReportEntry(
            DateTimeOffset.UtcNow,
            report,
            summary ?? $"{report.Operation}: {(report.Succeeded ? "ok" : "failed")}"));
    }

    public void ProjectToConsole(OperationReport report, IEditorConsole console)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(console);
        foreach (var diagnostic in report.Diagnostics.Where(static item => item.Severity >= DiagnosticSeverity.Error))
            console.Error($"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}");
        var warnings = report.Diagnostics.Count(static item => item.Severity == DiagnosticSeverity.Warning);
        if (warnings != 0)
            console.Warning($"{report.Operation}: {warnings} warning(s); open the ExcelDB report for details.");
    }

    public static string ToJson(OperationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions) + "\n";
    }
}

public sealed class EditorProjectMountService(string toolVersion)
{
    public MutationPlan PlanInitialize(string targetDirectory) => new ProjectInitializer(toolVersion).Plan(targetDirectory);

    public OperationReport Initialize(string targetDirectory) => new MutationPlanApplier().Apply(PlanInitialize(targetDirectory));

    public EditorProjectMountState Inspect(string projectFile)
    {
        var fullProject = Path.GetFullPath(projectFile);
        if (!File.Exists(fullProject))
            return new EditorProjectMountState(false, fullProject, [], [], []);

        var context = ExcelDbProject.Load(fullProject).Resolve(fullProject);
        var mounted = new SortedSet<string>(StringComparer.Ordinal);
        var missing = ImmutableArray.CreateBuilder<string>();
        foreach (var glob in context.Project.Workbooks)
        {
            var normalized = glob.Replace('/', Path.DirectorySeparatorChar);
            var directoryPart = Path.GetDirectoryName(normalized) ?? ".";
            var pattern = Path.GetFileName(normalized);
            var directory = context.ResolvePath(directoryPart);
            var matches = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Select(Path.GetFullPath).ToArray()
                : [];
            if (matches.Length == 0)
                missing.Add(glob);
            foreach (var match in matches)
                mounted.Add(match);
        }

        return new EditorProjectMountState(
            true,
            fullProject,
            context.Project.Workbooks,
            mounted.ToImmutableArray(),
            missing.ToImmutable());
    }

    public EditorProjectMountState MountConfigured(string projectFile)
    {
        var state = Inspect(projectFile);
        foreach (var workbook in state.MountedWorkbooks)
            AssetDatabase.MountWorkbook(workbook);
        return state;
    }
}

public sealed class EditorBrowserService(IEditorAssetStatusProvider? statusProvider = null)
{
    private readonly IEditorAssetStatusProvider _status = statusProvider ?? EmptyStatusProvider.Instance;

    public EditorBrowserSnapshot Search(string filter = "", string? displayNameTerm = null, string[]? folders = null)
    {
        var guids = folders is null ? AssetDatabase.FindAssets(filter) : AssetDatabase.FindAssets(filter, folders);
        var rows = new List<EditorBrowserRow>();
        foreach (var text in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(text);
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type is null)
                continue;
            var asset = AssetDatabase.LoadAssetAtPath(path, type);
            if (asset is null)
                continue;
            var displayName = _status.GetDisplayName(asset);
            if (!string.IsNullOrEmpty(displayNameTerm)
                && !(displayName?.Contains(displayNameTerm, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;
            var guid = AssetDatabase.GUIDFromAssetPath(path);
            rows.Add(new EditorBrowserRow(guid, path, KeyFromPath(path), displayName, type, asset, _status.GetBadges(asset)));
        }

        var tables = rows
            .GroupBy(static row => Split(row.Path))
            .OrderBy(static group => group.Key.Workbook, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Table, StringComparer.Ordinal)
            .Select(group => new EditorBrowserTable(
                group.Key.Workbook,
                group.Key.Table,
                group.OrderBy(static row => row.Key, StringComparer.Ordinal).ToImmutableArray()))
            .ToImmutableArray();
        return new EditorBrowserSnapshot(
            tables,
            rows.Count(static row => row.Badges.Contains(EditorAssetBadge.Dirty)),
            rows.Count(static row => row.Badges.Contains(EditorAssetBadge.Conflicted)),
            rows.Count(static row => row.Badges.Contains(EditorAssetBadge.Error)));
    }

    private static string KeyFromPath(string path)
    {
        var split = Split(path);
        return path[(split.Workbook.Length + split.Table.Length + 2)..];
    }

    private static (string Workbook, string Table) Split(string path)
    {
        var marker = path.IndexOf(".xlsx/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return (path, string.Empty);
        var workbookEnd = marker + 5;
        var tableEnd = path.IndexOf('/', workbookEnd + 1);
        return tableEnd < 0
            ? (path[..workbookEnd], path[(workbookEnd + 1)..])
            : (path[..workbookEnd], path[(workbookEnd + 1)..tableEnd]);
    }

    private sealed class EmptyStatusProvider : IEditorAssetStatusProvider
    {
        public static EmptyStatusProvider Instance { get; } = new();
        public ImmutableArray<EditorAssetBadge> GetBadges(object asset) => [];
        public string? GetDisplayName(object asset) => null;
    }
}

public sealed class EditorRowReferencePickerService(EditorBrowserService browser)
{
    public EditorBrowserSnapshot Search(
        EditorRowReferenceConstraint constraint,
        string filter = "",
        string? displayNameTerm = null,
        string[]? folders = null)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        ArgumentException.ThrowIfNullOrWhiteSpace(constraint.RefTable);
        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter))
            terms.Add(filter.Trim());
        terms.Add($"t:{constraint.RefTable}");
        if (!string.IsNullOrWhiteSpace(constraint.RefGroup))
            terms.Add($"l:{constraint.RefGroup}");
        return browser.Search(string.Join(' ', terms), displayNameTerm, folders);
    }
}

public sealed class EditorWorkbookSummaryService(IEditorWorkbookSummaryProvider provider)
{
    public EditorWorkbookSummary? Inspect(string workbookPath, ulong? expectedSchemaHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        return provider.TryGetSummary(Path.GetFullPath(workbookPath), expectedSchemaHash);
    }
}

public sealed class EditorProjectSettingsService(string toolVersion)
{
    public ProjectSettingsProjection Load(string projectFile, ulong? schemaHash = null)
    {
        var project = ExcelDbProject.Load(projectFile);
        return ProjectSettingsProjection.FromProject(project, schemaHash, toolVersion);
    }

    public OperationReport Save(string projectFile, ProjectSettingsProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var fullProject = Path.GetFullPath(projectFile);
        var root = Path.GetDirectoryName(fullProject) ?? throw new InvalidDataException("Project file has no parent.");
        var project = new ExcelDbProject(
            projection.SchemaDir,
            projection.GeneratedDir,
            projection.Workbooks,
            projection.BytesOutput,
            projection.CacheDir);
        var currentHash = File.Exists(fullProject)
            ? ContentFingerprint.FromFile(fullProject).Sha256
            : ContentFingerprint.FromBytes(project.ToCanonicalJson()).Sha256;
        var plan = new MutationPlanBuilder("settings-save", toolVersion, root, currentHash, projection.SchemaHash ?? 0)
            .Observe(fullProject)
            .WriteFile(fullProject, project.ToCanonicalJson())
            .Build();
        return new MutationPlanApplier().Apply(plan);
    }
}

public sealed class EditorDirtyGuard(IPendingChangeSource changes)
{
    public bool HasPendingChanges => !changes.GetPendingChanges().IsEmpty;

    public DirtyGuardState Inspect(bool hasUnresolvedConflicts)
    {
        var pending = HasPendingChanges;
        var actions = !pending
            ? ImmutableArray<DirtyGuardAction>.Empty
            : hasUnresolvedConflicts
                ? [DirtyGuardAction.ResolveConflicts, DirtyGuardAction.Cancel]
                : [DirtyGuardAction.Save, DirtyGuardAction.Discard, DirtyGuardAction.Cancel];
        return new DirtyGuardState(pending, hasUnresolvedConflicts, actions);
    }

    public bool TryContinue(DirtyGuardAction action, bool hasUnresolvedConflicts)
    {
        if (hasUnresolvedConflicts)
            return false;
        if (!HasPendingChanges)
            return true;
        return action switch
        {
            DirtyGuardAction.Save => Save(),
            DirtyGuardAction.Discard => changes.DiscardAll(),
            DirtyGuardAction.Cancel or DirtyGuardAction.ResolveConflicts => false,
            _ => false,
        };
    }

    private static bool Save()
    {
        AssetDatabase.SaveAssets();
        return true;
    }
}

public sealed class EditorConflictService
{
    public EditorConflictSnapshot Inspect()
    {
        AssetDatabase.GetConflicts(Span<ConflictRecord>.Empty, out var count);
        if (count == 0)
            return new EditorConflictSnapshot([], true);
        var buffer = new ConflictRecord[count];
        AssetDatabase.GetConflicts(buffer, out var actual);
        return new EditorConflictSnapshot(buffer.AsSpan(0, Math.Min(actual, buffer.Length)).ToArray().ToImmutableArray(), actual == 0);
    }

    public bool Resolve(ConflictId id, EditorConflictAction action) => AssetDatabase.ResolveConflict(
        id,
        action == EditorConflictAction.ReloadFromExcel
            ? ConflictResolutionAction.TakeTheirs
            : ConflictResolutionAction.KeepMine);

    public int ResolveAll(EditorConflictAction action)
    {
        var snapshot = Inspect();
        var resolved = 0;
        foreach (var conflict in snapshot.Conflicts)
        {
            if (Resolve(conflict.Id, action))
                resolved++;
        }
        return resolved;
    }
}

public static class EditorFreshnessService
{
    public static EditorFreshnessState Evaluate(
        string manifestPath,
        ulong expectedSchemaHash,
        string expectedContentHash,
        string expectedTarget = "client")
    {
        if (!File.Exists(manifestPath))
            return new EditorFreshnessState(false, "Client manifest is missing.", expectedTarget, null, expectedContentHash, null);
        try
        {
            var manifest = ConvertedBytesManifest.Parse(File.ReadAllText(manifestPath));
            if (!string.Equals(manifest.ExportTarget.Value, expectedTarget, StringComparison.Ordinal))
                return new EditorFreshnessState(false, "Manifest belongs to another export target.", expectedTarget, manifest.ExportTarget.Value, expectedContentHash, manifest.SourceContentHash);
            if (manifest.SchemaHash != expectedSchemaHash)
                return new EditorFreshnessState(false, "Manifest schema hash is stale.", expectedTarget, manifest.ExportTarget.Value, expectedContentHash, manifest.SourceContentHash);
            if (!string.Equals(manifest.SourceContentHash, expectedContentHash, StringComparison.Ordinal))
                return new EditorFreshnessState(false, "Client data content hash is stale.", expectedTarget, manifest.ExportTarget.Value, expectedContentHash, manifest.SourceContentHash);
            return new EditorFreshnessState(true, "Client bytes are fresh.", expectedTarget, manifest.ExportTarget.Value, expectedContentHash, manifest.SourceContentHash);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or JsonException
            or KeyNotFoundException
            or InvalidOperationException
            or FormatException
            or OverflowException)
        {
            return new EditorFreshnessState(false, exception.Message, expectedTarget, null, expectedContentHash, null);
        }
    }


    public static EditorFreshnessDecision Decide(
        EditorFreshnessState state,
        FreshnessAction action,
        Func<EditorFreshnessState>? convertClient = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsFresh)
            return new EditorFreshnessDecision(true, false, state);
        return action switch
        {
            FreshnessAction.ContinueStale => new EditorFreshnessDecision(true, false, state),
            FreshnessAction.Cancel => new EditorFreshnessDecision(false, false, state),
            FreshnessAction.ConvertClientAndContinue when convertClient is not null => ConvertAndEvaluate(convertClient),
            _ => new EditorFreshnessDecision(false, false, state),
        };
    }

    private static EditorFreshnessDecision ConvertAndEvaluate(Func<EditorFreshnessState> convertClient)
    {
        var refreshed = convertClient();
        return new EditorFreshnessDecision(refreshed.IsFresh, true, refreshed);
    }
}

public sealed class EditorPlaySourceRequestStore
{
    private EditorPlaySourceRequest? _pending;

    public bool HasPending => _pending is not null;

    public void SetNext(EditorPlaySourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Source.ExportTarget.Value, "client", StringComparison.Ordinal)
            || !string.Equals(request.Registry.ExpectedExportTarget.Value, "client", StringComparison.Ordinal))
            throw new InvalidOperationException("Unity Play sessions only accept matching client sources and registries.");
        if (request.Source.SchemaHash != request.Registry.ExpectedSchemaHash)
            throw new InvalidOperationException("The Play source schema does not match the generated client registry.");
        _pending = request;
    }

    public EditorPlaySourceRequest? ConsumeNext()
    {
        var result = _pending;
        _pending = null;
        return result;
    }

    public void Cancel() => _pending = null;
}

public sealed class EditorPlayRuntimeController
{
    public EditorPlayRuntimeState Inspect()
    {
        var report = RuntimeDatabase.LastReport;
        return new EditorPlayRuntimeState(
            RuntimeDatabase.IsOpen,
            RuntimeDatabase.ExportTarget.Value,
            report.Source?.Kind.ToString() ?? "none",
            report.Source?.Revision ?? string.Empty,
            RuntimeDatabase.IsHotReloadEnabled,
            report.Diagnostics);
    }

    public EditorRuntimeActionResult SwitchDataSource(IDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var activeTarget = RuntimeDatabase.ExportTarget;
        if (!RuntimeDatabase.IsOpen)
            return new EditorRuntimeActionResult(false, "Runtime database is closed.", Inspect());
        if (!string.Equals(activeTarget.Value, "client", StringComparison.Ordinal))
            return new EditorRuntimeActionResult(false, "Unity Play tools only operate on the client registry.", Inspect());
        if (source.ExportTarget != activeTarget)
            return new EditorRuntimeActionResult(false, "The selected source belongs to another export target.", Inspect());
        var succeeded = RuntimeDatabase.SwitchDataSource(source);
        return new EditorRuntimeActionResult(
            succeeded,
            succeeded ? "Data source switched." : LastRuntimeReason(),
            Inspect());
    }

    public EditorRuntimeActionResult SetHotReload(bool enabled)
    {
        var succeeded = enabled ? RuntimeDatabase.TryEnableHotReload() : RuntimeDatabase.TryDisableHotReload();
        return new EditorRuntimeActionResult(
            succeeded,
            succeeded ? enabled ? "Hot reload enabled." : "Hot reload disabled." : LastRuntimeReason(),
            Inspect());
    }

    private static string LastRuntimeReason() => RuntimeDatabase.LastDiagnostics.FirstOrDefault()?.Message
        ?? "The runtime operation was rejected.";
}

public sealed class EditorPendingChangesService(IPendingChangeSource changes)
{
    public ImmutableArray<PendingAssetChange> Inspect() => changes.GetPendingChanges();

    public void Save(GUID guid) => AssetDatabase.SaveAssetIfDirty(guid);

    public bool DiscardAll() => changes.DiscardAll();
}

public sealed class EditorPlanService(EditorReportRing reports)
{
    private MutationPlan? _displayed;

    public EditorPlanPreview Preview(MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _displayed = plan;
        return EditorPlanPreview.FromPlan(plan);
    }

    public OperationReport Apply(MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!ReferenceEquals(_displayed, plan))
            throw new InvalidOperationException("Only the exact in-memory MutationPlan displayed to the user may be applied.");
        _displayed = null;
        var report = new MutationPlanApplier().Apply(plan);
        reports.Add(report);
        return report;
    }

    public OperationReport ApplyDisplayed(string planHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);
        var plan = _displayed ?? throw new InvalidOperationException("No MutationPlan is currently displayed.");
        if (!string.Equals(plan.PlanHash, planHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The displayed MutationPlan hash does not match the confirmation request.");
        return Apply(plan);
    }
}
