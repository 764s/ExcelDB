using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Runtime;
using ExcelDbEditor;

namespace ExcelDb.Authoring.Tests;

public sealed class AssetDatabaseTests
{
    [Fact]
    public void MountLoadFindGuidAndContainersFollowAssetDatabaseShape()
    {
        var fireball = new Skill { Id = "fireball", Labels = ["boss", "fire"] };
        var adapter = new MemoryAdapter([Import("Data/game.xlsx", fireball)]);
        using var session = CreateSession(adapter);
        using var activation = session.Activate();

        var eventCount = 0;
        AssetDatabase.workbookImported += _ => eventCount++;
        AssetDatabase.MountWorkbook("Data/game.xlsx");

        Assert.Same(fireball, AssetDatabase.LoadAssetAtPath<Skill>("Data/game.xlsx/Skill/fireball"));
        Assert.Null(AssetDatabase.LoadAssetAtPath<Other>("Data/game.xlsx/Skill/fireball"));
        Assert.True(AssetDatabase.Contains(fireball));
        Assert.Equal("Data/game.xlsx/Skill/fireball", AssetDatabase.GetAssetPath(fireball));
        Assert.True(AssetDatabase.IsValidFolder("Data/game.xlsx"));
        Assert.True(AssetDatabase.IsValidFolder("Data/game.xlsx/Skill"));
        Assert.False(AssetDatabase.IsValidFolder("Data/game.xlsx/Skill/fireball"));
        Assert.Single(AssetDatabase.FindAssets("fire t:Skill l:boss"));
        Assert.Equal(AssetDatabase.AssetPathToGUID("Data/game.xlsx/Skill/fireball"), AssetDatabase.FindAssets("t:Skill")[0]);
        Assert.Equal("Data/game.xlsx/Skill/fireball", AssetDatabase.GUIDToAssetPath(AssetDatabase.GUIDFromAssetPath("Data/game.xlsx/Skill/fireball")));
        Assert.Equal(1, eventCount);
    }

    [Fact]
    public void StructuralChangesAreImmediateAndSavedAsOneWorkbookRequest()
    {
        var adapter = new MemoryAdapter([EmptyImport("Data/game.xlsx")]);
        using var session = CreateSession(adapter);
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook("Data/game.xlsx");

        var skill = new Skill { Id = "draft", Labels = [] };
        AssetDatabase.CreateAsset(skill, "Data/game.xlsx/Skill/ice_nova");
        var originalGuid = AssetDatabase.GUIDFromAssetPath("Data/game.xlsx/Skill/ice_nova");
        Assert.Equal("ice_nova", skill.Id);
        Assert.Same(skill, AssetDatabase.LoadAssetAtPath<Skill>("Data/game.xlsx/Skill/ice_nova"));
        Assert.Equal(string.Empty, AssetDatabase.RenameAsset("Data/game.xlsx/Skill/ice_nova", "frost_nova"));
        Assert.Equal(originalGuid, AssetDatabase.GUIDFromAssetPath("Data/game.xlsx/Skill/frost_nova"));
        Assert.True(AssetDatabase.CopyAsset("Data/game.xlsx/Skill/frost_nova", "Data/game.xlsx/Skill/frost_copy"));
        Assert.NotEqual(originalGuid, AssetDatabase.GUIDFromAssetPath("Data/game.xlsx/Skill/frost_copy"));
        Assert.Equal("Data/game.xlsx/Skill/frost_copy_1", AssetDatabase.GenerateUniqueAssetPath("Data/game.xlsx/Skill/frost_copy"));

        AssetDatabase.SetLabels(skill, ["z", "a", "a"]);
        Assert.Equal(["a", "z"], AssetDatabase.GetLabels(skill));
        AssetDatabase.SaveAssets();

        Assert.Single(adapter.Saves);
        Assert.Equal(2, adapter.Saves[0].Items.Length);
        Assert.All(adapter.Saves[0].Items, static item => Assert.False(item.Deleted));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void DeleteIsBlockedByReferencesAndDependencyQueriesAreDeterministic()
    {
        var target = new Skill { Id = "target" };
        var source = new Skill { Id = "source" };
        var targetGuid = new GUID(RowGuid.New());
        source.References = [targetGuid];
        var adapter = new MemoryAdapter([
            new AuthoringWorkbookImport(
                "Data/game.xlsx",
                [new(targetGuid, "Skill", "target", target), new(new GUID(RowGuid.New()), "Skill", "source", source)],
                Ok("import"))]);
        using var session = CreateSession(adapter);
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook("Data/game.xlsx");

        Assert.False(AssetDatabase.DeleteAsset("Data/game.xlsx/Skill/target"));
        Assert.Equal(["Data/game.xlsx/Skill/target"], AssetDatabase.GetDependencies("Data/game.xlsx/Skill/source", false));
        Assert.Equal([AssetDatabase.AssetPathToGUID("Data/game.xlsx/Skill/source")], AssetDatabase.FindAssets("ref:Skill/target"));
        Assert.Contains(session.Diagnostics, static item => item.Diagnostic.Code == "EXAD0104");
    }

    [Fact]
    public void ConflictBufferReportsTruncationAndResolution()
    {
        var adapter = new MemoryAdapter([]);
        using var session = CreateSession(adapter);
        using var activation = session.Activate();
        var id = ConflictId.New();
        session.AddConflict(new ConflictRecord(id, default, "hp", "1", "2", "3"));

        Assert.Equal(RuntimeQueryStatus.Truncated, AssetDatabase.GetConflicts([], out var count));
        Assert.Equal(1, count);
        Assert.True(AssetDatabase.ResolveConflict(id, ConflictResolutionAction.KeepMine));
        var buffer = new ConflictRecord[1];
        Assert.Equal(RuntimeQueryStatus.Success, AssetDatabase.GetConflicts(buffer, out count));
        Assert.Equal(0, count);
    }

    [Fact]
    public void DeletePolicyPlansWholeClosureAndBlockWinsBeforeAnyMutation()
    {
        var targetGuid = new GUID(RowGuid.New());
        var cascadeGuid = new GUID(RowGuid.New());
        var nullGuid = new GUID(RowGuid.New());
        var blockerGuid = new GUID(RowGuid.New());
        var target = new Skill { Id = "target" };
        var cascade = new Skill
        {
            Id = "cascade",
            ReferenceSlots = [new(targetGuid, AuthoringReferenceDeletePolicy.Cascade, "101:target")],
        };
        var nullOwner = new Skill
        {
            Id = "null_owner",
            ReferenceSlots = [new(targetGuid, AuthoringReferenceDeletePolicy.SetNull, "101:target")],
        };
        var blocker = new Skill
        {
            Id = "blocker",
            ReferenceSlots = [new(cascadeGuid, AuthoringReferenceDeletePolicy.Block, "101:cascade")],
        };
        var adapter = new MemoryAdapter([
            Import(
                "Data/game.xlsx",
                (targetGuid, target),
                (cascadeGuid, cascade),
                (nullGuid, nullOwner),
                (blockerGuid, blocker))]);
        using var session = CreateSession(adapter);
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook("Data/game.xlsx");

        Assert.False(AssetDatabase.DeleteAsset("Data/game.xlsx/Skill/target"));
        Assert.Equal(targetGuid, nullOwner.ReferenceSlots[0].Target);
        Assert.NotNull(AssetDatabase.LoadAssetAtPath<Skill>("Data/game.xlsx/Skill/cascade"));

        blocker.ReferenceSlots = [];
        Assert.True(AssetDatabase.DeleteAsset("Data/game.xlsx/Skill/target"));
        Assert.True(nullOwner.ReferenceSlots[0].Target.Empty());
        Assert.Null(AssetDatabase.LoadAssetAtPath<Skill>("Data/game.xlsx/Skill/target"));
        Assert.Null(AssetDatabase.LoadAssetAtPath<Skill>("Data/game.xlsx/Skill/cascade"));

        AssetDatabase.SaveAssets();
        var items = Assert.Single(adapter.Saves).Items;
        Assert.Equal(3, items.Length);
        Assert.Equal(2, items.Count(static item => item.Deleted));
        Assert.Contains(items, item => item.Guid == nullGuid && !item.Deleted);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void MoveRewritesReferenceProjectionAndSavesCrossWorkbookAsOneTransaction()
    {
        var targetGuid = new GUID(RowGuid.New());
        var ownerGuid = new GUID(RowGuid.New());
        var target = new Skill { Id = "target" };
        var owner = new Skill
        {
            Id = "owner",
            ReferenceSlots = [new(targetGuid, AuthoringReferenceDeletePolicy.Block, "101:target")],
        };
        var adapter = new MemoryAdapter([
            Import("Data/game.xlsx", (targetGuid, target)),
            Import("Data/dlc.xlsx", (ownerGuid, owner))]);
        using var session = CreateSession(adapter);
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook("Data/game.xlsx");
        AssetDatabase.MountWorkbook("Data/dlc.xlsx");

        Assert.Equal(
            string.Empty,
            AssetDatabase.MoveAsset(
                "Data/game.xlsx/Skill/target",
                "Data/dlc.xlsx/Skill/renamed"));
        Assert.Equal(targetGuid, AssetDatabase.GUIDFromAssetPath("Data/dlc.xlsx/Skill/renamed"));
        Assert.Equal("Data/dlc.xlsx/Skill/renamed", owner.ReferenceSlots[0].Token);

        AssetDatabase.SaveAssetIfDirty(targetGuid);

        var batch = Assert.Single(adapter.TransactionSaves);
        Assert.Equal(2, batch.Length);
        Assert.Contains(batch.Single(request => request.WorkbookPath == "Data/game.xlsx").Items,
            item => item.Guid == targetGuid && item.Deleted);
        var targetRequest = batch.Single(request => request.WorkbookPath == "Data/dlc.xlsx");
        Assert.Contains(targetRequest.Items, item => item.Guid == targetGuid && !item.Deleted && item.Key == "renamed");
        Assert.Contains(targetRequest.Items, item => item.Guid == ownerGuid && !item.Deleted);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void EditingBatchCoalescesSaveAndWatcherUntilOutermostStop()
    {
        var adapter = new MemoryAdapter([EmptyImport("Data/game.xlsx")]);
        using var session = CreateSession(adapter);
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook("Data/game.xlsx");
        var importsAfterMount = adapter.ImportCount;

        AssetDatabase.StartAssetEditing();
        AssetDatabase.StartAssetEditing();
        AssetDatabase.CreateAsset(new Skill(), "Data/game.xlsx/Skill/generated");
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        session.NotifyWorkbookChanged("Data/game.xlsx");
        session.NotifyWorkbookChanged("Data/game.xlsx");

        AssetDatabase.StopAssetEditing();
        Assert.Empty(adapter.Saves);
        Assert.Equal(importsAfterMount, adapter.ImportCount);
        AssetDatabase.StopAssetEditing();

        Assert.Single(adapter.Saves);
        Assert.Equal(importsAfterMount + 1, adapter.ImportCount);
        Assert.False(session.ProcessPendingChanges());
        AssetDatabase.StopAssetEditing();
        Assert.Contains(session.Diagnostics, static item => item.Diagnostic.Code == "EXAD0105");
    }

    [Fact]
    public void WatcherRaisedByImportEventQueuesTheNextOwnerPublishPoint()
    {
        var adapter = new MemoryAdapter([EmptyImport("Data/game.xlsx")]);
        using var session = CreateSession(adapter);
        using var activation = session.Activate();
        var events = 0;
        AssetDatabase.workbookImported += _ =>
        {
            events++;
            if (events == 1)
                session.NotifyWorkbookChanged("Data/game.xlsx");
        };

        AssetDatabase.MountWorkbook("Data/game.xlsx");
        Assert.Equal(1, events);
        Assert.True(session.ProcessPendingChanges());
        Assert.Equal(2, events);
    }

    [Fact]
    public void ModeMatrixRejectsExcelAndWritebackAtTheFacadeBoundary()
    {
        var adapter = new MemoryAdapter([EmptyImport("Data/game.xlsx")]);
        using (var release = new AssetDatabaseSession(adapter, RuntimeMode.Release))
        using (release.Activate())
            Assert.Throws<InvalidOperationException>(() => AssetDatabase.MountWorkbook("Data/game.xlsx"));

        using var development = CreateSession(adapter, RuntimeMode.Development, enableExcel: true);
        using var activation = development.Activate();
        AssetDatabase.MountWorkbook("Data/game.xlsx");
        Assert.Throws<InvalidOperationException>(() => AssetDatabase.CreateAsset(new Skill(), "Data/game.xlsx/Skill/x"));
        Assert.Throws<InvalidOperationException>(() => development.NotifyWorkbookChanged("Data/game.xlsx"));
    }

    [Fact]
    public void FindUsesOrWithinTypeLabelAndReferenceKeysIncludingTypeAliases()
    {
        var aGuid = new GUID(RowGuid.New());
        var bGuid = new GUID(RowGuid.New());
        var a = new Skill { Id = "a", Labels = ["fire"] };
        var b = new Skill { Id = "b", Labels = ["ice"], References = [aGuid] };
        var adapter = new MemoryAdapter([Import("Data/game.xlsx", (aGuid, a), (bGuid, b))]);
        using var session = CreateSession(adapter);
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook("Data/game.xlsx");

        Assert.Equal(2, AssetDatabase.FindAssets("t:combat t:missing").Length);
        Assert.Equal(2, AssetDatabase.FindAssets("l:fire l:ice").Length);
        Assert.Single(AssetDatabase.FindAssets("ref:Skill/a ref:Skill/nope"));
    }

    [Fact]
    public void CreateRequiresGeneratedSheetAndProjectDomainKeyUniqueness()
    {
        var existing = new Skill { Id = "shared" };
        var adapter = new MemoryAdapter([
            Import("Data/game.xlsx", existing),
            EmptyImport("Data/dlc.xlsx"),
            EmptyImport("Data/no-sheet.xlsx") with { AvailableTables = [] }]);
        using var session = CreateSession(adapter);
        using var activation = session.Activate();
        AssetDatabase.MountWorkbook("Data/game.xlsx");
        AssetDatabase.MountWorkbook("Data/dlc.xlsx");
        AssetDatabase.MountWorkbook("Data/no-sheet.xlsx");
        var duplicate = new Skill { Id = "draft" };
        var noSheet = new Skill { Id = "draft" };

        AssetDatabase.CreateAsset(duplicate, "Data/dlc.xlsx/Skill/shared");
        AssetDatabase.CreateAsset(noSheet, "Data/no-sheet.xlsx/Skill/new");

        Assert.False(AssetDatabase.Contains(duplicate));
        Assert.Equal("draft", duplicate.Id);
        Assert.False(AssetDatabase.Contains(noSheet));
        Assert.False(AssetDatabase.IsValidFolder("Data/no-sheet.xlsx/Skill"));
        Assert.Contains(session.Diagnostics, static item => item.Diagnostic.Code == "EXAD0103");
        Assert.Contains(session.Diagnostics, static item => item.Diagnostic.Code == "EXAD0102");
    }

    private static AssetDatabaseSession CreateSession(
        MemoryAdapter adapter,
        RuntimeMode mode = RuntimeMode.EditorAuthoring,
        bool enableExcel = false) =>
        new AssetDatabaseSession(adapter, mode, enableExcel).RegisterTable(new AuthoringTableRegistration(
            101,
            "Skill",
            typeof(Skill),
            static value => ((Skill)value).Id,
            static (value, key) => ((Skill)value).Id = key,
            static value => ((Skill)value).Copy(),
            static value => ((Skill)value).References,
            static value => ((Skill)value).Labels,
            static (value, labels) => ((Skill)value).Labels = [.. labels],
            static value => GetReferences((Skill)value),
            ["combat"]));

    private static IEnumerable<AuthoringReference> GetReferences(Skill skill)
    {
        foreach (var guid in skill.References)
            yield return new AuthoringReference(guid, "references");
        foreach (var slot in skill.ReferenceSlots)
        {
            yield return new AuthoringReference(
                slot.Target,
                "reference_slots",
                slot.Policy,
                () => slot.Target = default,
                target =>
                {
                    slot.Target = target.Guid;
                    slot.Token = target.AssetPath;
                });
        }
    }

    private static AuthoringWorkbookImport Import(string path, Skill asset) => new(
        path,
        [new(new GUID(RowGuid.New()), "Skill", asset.Id, asset)],
        Ok("import"),
        "v1");

    private static AuthoringWorkbookImport Import(
        string path,
        params (GUID Guid, Skill Asset)[] assets) => new(
            path,
            assets.Select(static item => new AuthoringImportedAsset(item.Guid, "Skill", item.Asset.Id, item.Asset)).ToImmutableArray(),
            Ok("import"),
            "v1");

    private static AuthoringWorkbookImport EmptyImport(string path) => new(path, [], Ok("import"), "v1");
    private static OperationReport Ok(string operation) => OperationReport.Success(operation, "test", applied: true);

    private sealed class Skill
    {
        public string Id { get; set; } = string.Empty;
        public string[] Labels { get; set; } = [];
        public GUID[] References { get; set; } = [];
        public ReferenceSlot[] ReferenceSlots { get; set; } = [];
        public Skill Copy() => new()
        {
            Id = Id,
            Labels = [.. Labels],
            References = [.. References],
            ReferenceSlots = ReferenceSlots.Select(static slot => slot.Copy()).ToArray(),
        };
    }

    private sealed class ReferenceSlot(
        GUID target,
        AuthoringReferenceDeletePolicy policy,
        string token)
    {
        public GUID Target { get; set; } = target;
        public AuthoringReferenceDeletePolicy Policy { get; } = policy;
        public string Token { get; set; } = token;
        public ReferenceSlot Copy() => new(Target, Policy, Token);
    }

    private sealed class Other;

    private sealed class MemoryAdapter(IEnumerable<AuthoringWorkbookImport> imports) : ITransactionalAuthoringWorkbookAdapter
    {
        private readonly Dictionary<string, AuthoringWorkbookImport> _imports = imports.ToDictionary(static item => item.WorkbookPath, StringComparer.Ordinal);
        public List<AuthoringSaveRequest> Saves { get; } = [];
        public List<ImmutableArray<AuthoringSaveRequest>> TransactionSaves { get; } = [];
        public int ImportCount { get; private set; }
        public AuthoringWorkbookImport Import(string workbookPath, bool forceUpdate)
        {
            ImportCount++;
            return _imports.TryGetValue(workbookPath, out var value) ? value : EmptyImport(workbookPath);
        }
        public OperationReport Save(AuthoringSaveRequest request) { Saves.Add(request); return Ok("save"); }
        public OperationReport Save(ImmutableArray<AuthoringSaveRequest> requests)
        {
            TransactionSaves.Add(requests);
            return new OperationReport(
                "save",
                "test",
                true,
                [],
                requests.Select(static request => new ArtifactRecord("workbook", request.WorkbookPath)).ToImmutableArray());
        }
        public bool Open(string workbookPath, string tableName, GUID guid) => true;
    }
}
