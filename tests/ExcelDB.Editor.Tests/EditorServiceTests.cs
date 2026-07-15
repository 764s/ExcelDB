using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Editor;
using ExcelDb.Runtime;
using ExcelDb.Runtime.Bytes;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;
using ExcelDbEditor;

namespace ExcelDb.Editor.Tests;

public sealed class EditorServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exceldb-editor-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReportRingIsBoundedAndConsoleProjectionFollowsSeverityRules()
    {
        var ring = new EditorReportRing(2);
        var console = new RecordingConsole();
        for (var index = 0; index < 3; index++)
        {
            var report = new OperationReport(
                $"op-{index}",
                "test",
                false,
                [new Diagnostic("w", DiagnosticSeverity.Warning, ".", "warning"), new Diagnostic("e", DiagnosticSeverity.Error, "cell", "error")],
                []);
            ring.Add(report);
            ring.ProjectToConsole(report, console);
        }

        Assert.Equal(2, ring.Entries.Length);
        Assert.Equal("op-2", ring.Entries[0].Report.Operation);
        Assert.Equal(3, console.Errors.Count);
        Assert.Equal(3, console.Warnings.Count);
        using var json = System.Text.Json.JsonDocument.Parse(EditorReportRing.ToJson(ring.Entries[0].Report));
        Assert.Equal("op-2", json.RootElement.GetProperty("operation").GetString());
    }

    [Fact]
    public void FreshnessRejectsServerManifestForClientPlay()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "config.bytes.manifest.json");
        var manifest = new ConvertedBytesManifest(1, 7, new ExportTargetId("server"), "data", 1, 1, "test", new string('a', 64));
        File.WriteAllText(path, manifest.ToJson());

        var result = EditorFreshnessService.Evaluate(path, 7, "data");

        Assert.False(result.IsFresh);
        Assert.Equal("client", result.ExpectedTarget);
        Assert.Equal("server", result.ActualTarget);
    }

    [Fact]
    public void PlaySourceRequestIsClientOnlyOneShotAndNeverPersisted()
    {
        var store = new EditorPlaySourceRequestStore();
        var registry = new TestRegistry("client");
        var source = new TestSource("client");
        store.SetNext(new EditorPlaySourceRequest(source, registry, new RuntimeBootstrapOptions(RuntimeMode.EditorPlayDebug, true)));
        Assert.True(store.HasPending);
        Assert.NotNull(store.ConsumeNext());
        Assert.False(store.HasPending);
        Assert.Null(store.ConsumeNext());
        Assert.Throws<InvalidOperationException>(() => store.SetNext(new EditorPlaySourceRequest(new TestSource("server"), registry, default)));
        Assert.Throws<InvalidOperationException>(() => store.SetNext(new EditorPlaySourceRequest(new TestSource("client", 8), registry, default)));
    }

    [Fact]
    public void SettingsProjectsExactlyFourArtifactFieldsWithoutEditorPrefsLayer()
    {
        var init = new ProjectInitializer("test").Plan(_root);
        new MutationPlanApplier().Apply(init);
        var file = Path.Combine(_root, ExcelDbProject.FileName);
        var service = new EditorProjectSettingsService("test");
        var before = service.Load(file, 10);
        var changed = before with { SchemaDir = "Proto", ExcelDir = "Sheets" };

        var report = service.Save(file, changed);

        Assert.True(report.Succeeded);
        var project = ExcelDbProject.Load(file);
        Assert.Equal("Proto", project.SchemaDir);
        Assert.Equal("Sheets", project.ExcelDir);
        Assert.Equal("Generated/CSharp", project.GeneratedCSharpDir);
        Assert.Equal("Generated/Bytes", project.GeneratedBytesDir);
        Assert.Equal(5, System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file)).RootElement.EnumerateObject().Count());
        Assert.False(File.Exists(Path.Combine(_root, ".exceldb", ExcelDbProject.FileName)));
    }

    [Fact]
    public void PlanPreviewAppliesTheExactHashBoundObject()
    {
        var init = new ProjectInitializer("test").Plan(_root);
        var reports = new EditorReportRing();
        var service = new EditorPlanService(reports);
        var preview = service.Preview(init);

        Assert.Equal(init.PlanHash, preview.PlanHash);
        Assert.True(preview.CanApply);
        Assert.Throws<InvalidOperationException>(() => service.Apply(init with { }));
        var report = service.ApplyDisplayed(preview.PlanHash);
        Assert.True(report.Succeeded);
        Assert.Equal(init.PlanHash, reports.Entries[0].Report.PlanHash);
    }

    [Fact]
    public void ProjectMountUsesSharedInitializerAndRecursivelyDiscoversExcelDirectory()
    {
        var service = new EditorProjectMountService("test");
        var plan = service.PlanInitialize(_root);
        Assert.Equal("init", plan.Operation);
        Assert.Equal(ExcelDbProject.Default.ToCanonicalJson(), Convert.FromBase64String(Assert.Single(plan.Mutations, item => item.RelativePath == ExcelDbProject.FileName).ContentBase64!));
        Assert.True(service.Initialize(_root).Succeeded);
        var excel = Path.Combine(_root, "Excel");
        var nested = Path.Combine(excel, "Nested");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(excel, "b.xlsx"), []);
        File.WriteAllBytes(Path.Combine(nested, "a.xlsx"), []);
        File.WriteAllBytes(Path.Combine(excel, "~$temporary.xlsx"), []);

        var state = service.Inspect(Path.Combine(_root, ExcelDbProject.FileName));

        Assert.True(state.HasProject);
        Assert.Equal(excel, state.ExcelDirectory);
        Assert.Equal(
            new[] { Path.Combine(nested, "a.xlsx"), Path.Combine(excel, "b.xlsx") }.OrderBy(static path => path, StringComparer.Ordinal),
            state.MountedWorkbooks);
        Assert.Empty(state.Diagnostics);
        if (OperatingSystem.IsWindows())
            Assert.True(File.GetAttributes(Path.Combine(_root, ".exceldb")).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public void ConflictsExposeThreeValuesAndBlockSaveUntilResolved()
    {
        using var session = new AssetDatabaseSession(new EmptyWorkbookAdapter());
        using var activation = session.Activate();
        session.AddConflict(new ConflictRecord(ConflictId.New(), default, "stats.hp", "10", "20", "30"));
        var service = new EditorConflictService();

        var before = service.Inspect();

        Assert.False(before.CanSave);
        Assert.Equal("10", Assert.Single(before.Conflicts).BaseValue);
        Assert.Equal(1, service.ResolveAll(EditorConflictAction.KeepEditorValue));
        Assert.True(service.Inspect().CanSave);
    }

    [Fact]
    public void DirtyGuardOnlyOffersResolveOrCancelWhileConflicted()
    {
        var changes = new PendingChanges();
        var guard = new EditorDirtyGuard(changes);

        var conflicted = guard.Inspect(hasUnresolvedConflicts: true);

        Assert.Equal([DirtyGuardAction.ResolveConflicts, DirtyGuardAction.Cancel], conflicted.AllowedActions.ToArray());
        Assert.False(guard.TryContinue(DirtyGuardAction.Save, hasUnresolvedConflicts: true));
        Assert.True(guard.TryContinue(DirtyGuardAction.Discard, hasUnresolvedConflicts: false));
    }

    [Fact]
    public void FreshnessThreeWayDecisionRechecksAfterClientConvert()
    {
        var stale = new EditorFreshnessState(false, "stale", "client", "client", "new", "old");

        var converted = EditorFreshnessService.Decide(
            stale,
            FreshnessAction.ConvertClientAndContinue,
            () => new EditorFreshnessState(true, "fresh", "client", "client", "new", "new"));

        Assert.True(converted.Continue);
        Assert.True(converted.Converted);
        Assert.True(EditorFreshnessService.Decide(stale, FreshnessAction.ContinueStale).Continue);
        Assert.False(EditorFreshnessService.Decide(stale, FreshnessAction.Cancel).Continue);
    }

    [Fact]
    public void PlayRuntimeControllerRejectsCrossTargetBeforeSwitching()
    {
        RuntimeDatabase.Close();
        var registry = new TestRegistry("client");
        Assert.True(RuntimeDatabase.Open(new TestSource("client"), registry, new RuntimeBootstrapOptions(RuntimeMode.EditorPlayDebug, false)));
        try
        {
            var result = new EditorPlayRuntimeController().SwitchDataSource(new TestSource("server"));

            Assert.False(result.Succeeded);
            Assert.Contains("another export target", result.Reason, StringComparison.Ordinal);
            Assert.Equal("client", result.State.Target);
        }
        finally
        {
            RuntimeDatabase.Close();
        }
    }

    [Fact]
    public void RowReferencePickerAddsSchemaTableConstraintWithoutChangingFindGrammar()
    {
        var asset = new HeroAsset { Key = "hero" };
        using var session = new AssetDatabaseSession(new PickerWorkbookAdapter(asset));
        session.RegisterTable(new AuthoringTableRegistration(
            10,
            "Hero",
            typeof(HeroAsset),
            value => ((HeroAsset)value).Key,
            (value, key) => ((HeroAsset)value).Key = key,
            value => new HeroAsset { Key = ((HeroAsset)value).Key }));
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook("Data/game.xlsx");

        var result = new EditorRowReferencePickerService(new EditorBrowserService()).Search(
            new EditorRowReferenceConstraint("Hero"),
            "hero");

        Assert.Equal("hero", Assert.Single(Assert.Single(result.Tables).Rows).Key);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingConsole : IEditorConsole
    {
        public List<string> Errors { get; } = [];
        public List<string> Warnings { get; } = [];
        public void Error(string message) => Errors.Add(message);
        public void Warning(string message) => Warnings.Add(message);
    }

    private sealed class TestRegistry(string target) : RuntimeSchemaRegistry(7, new ExportTargetId(target), []);

    private sealed class TestSource(string target, ulong schemaHash = 7) : IDataSource
    {
        public ulong SchemaHash => schemaHash;
        public ExportTargetId ExportTarget { get; } = new(target);
        public SourceInfo Inspect() => new(RuntimeSourceKind.Custom, RuntimeSourceCapabilities.Read, 1, "r", "h");
        public SourceSnapshot Open() => new(schemaHash, ExportTarget, "r", "h", []);
    }

    private sealed class PendingChanges : IPendingChangeSource
    {
        private bool _hasPending = true;

        public ImmutableArray<PendingAssetChange> GetPendingChanges() => _hasPending
            ? [new PendingAssetChange(default, "Data/game.xlsx/Hero/one", "modified", [new PendingFieldChange("hp", "1", "2")])]
            : [];

        public bool DiscardAll()
        {
            _hasPending = false;
            return true;
        }
    }

    private sealed class EmptyWorkbookAdapter : IAuthoringWorkbookAdapter
    {
        public AuthoringWorkbookImport Import(string workbookPath, bool forceUpdate) => new(
            workbookPath,
            [],
            OperationReport.Success("import", "test"));

        public OperationReport Save(AuthoringSaveRequest request) => OperationReport.Success("save", "test", true);

        public bool Open(string workbookPath, string tableName, GUID guid) => true;
    }

    private sealed class PickerWorkbookAdapter(HeroAsset asset) : IAuthoringWorkbookAdapter
    {
        public AuthoringWorkbookImport Import(string workbookPath, bool forceUpdate) => new(
            workbookPath,
            [new AuthoringImportedAsset(new GUID(RowGuid.New()), "Hero", asset.Key, asset)],
            OperationReport.Success("import", "test"));

        public OperationReport Save(AuthoringSaveRequest request) => OperationReport.Success("save", "test", true);

        public bool Open(string workbookPath, string tableName, GUID guid) => true;
    }

    private sealed class HeroAsset
    {
        public string Key { get; set; } = string.Empty;
    }
}
