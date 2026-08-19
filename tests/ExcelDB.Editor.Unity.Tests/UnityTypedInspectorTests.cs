using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Editor.Model;
using ExcelDb.Editor.Unity;
using ExcelDb.Runtime;
using ExcelDbEditor;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ExcelDb.Editor.Unity.Tests;

public sealed class UnityTypedInspectorTests
{
    [Fact]
    public void TypedHostPathEditsScalarListAndReferenceThenSavesAndUsesUnityUndoRedo()
    {
        var resident = new TestAsset
        {
            Id = "hero",
            Hp = 10,
            Tags = ["base"],
            Target = new RowLink("unit", "old"),
        };
        var assetGuid = new GUID(RowGuid.New());
        var adapter = new MemoryAdapter(Import(assetGuid, resident));
        using var authoring = CreateAuthoring(adapter, RuntimeMode.EditorAuthoring);
        using var activation = authoring.Activate();
        AssetDatabase.MountWorkbook(WorkbookPath);

        var guid = AssetDatabase.AssetPathToGUID(AssetPath);
        var host = new FakeHost(guid, resident, Binding());
        var factory = new ExcelDbAuthoringPropertyFactory();
        Assert.True(ExcelDbSerializedInspectorSession.TryCreate(host, guid, factory, out var created));
        using var inspector = Assert.IsType<ExcelDbSerializedInspectorSession>(created);

        inspector.SerializedObject.FindProperty("hp").BoxedValue = 25;
        var tags = inspector.SerializedObject.FindProperty("tags");
        tags.InsertArrayElementAtIndex(tags.ArraySize);
        tags.GetArrayElementAtIndex(1).BoxedValue = "burst";
        inspector.QueueReferenceChange(
            inspector.SerializedObject.FindProperty("target"),
            new EditorReferenceValue("unit", "boss"));

        var apply = Assert.IsType<EditorApplyResult>(inspector.SynchronizeBeforeDraw());
        Assert.Equal(EditorApplyStatus.Applied, apply.Status);
        Assert.Equal(25, resident.Hp);
        Assert.Equal(["base", "burst"], resident.Tags);
        Assert.Equal(new RowLink("unit", "boss"), resident.Target);
        Assert.True(EditorUtility.IsDirty(resident));

        Assert.True(inspector.Save());
        Assert.Equal(1, host.SaveCount);
        Assert.False(EditorUtility.IsDirty(resident));
        Assert.False(inspector.SerializedObject.IsDirty);
        Assert.True(inspector.CanUndo);

        // Save advanced the Authoring revision token. MarkSaved must have accepted it, otherwise
        // this native Unity Undo would be rejected as a revision conflict.
        inspector.PerformUndo();
        Assert.Equal(10, resident.Hp);
        Assert.Equal(["base"], resident.Tags);
        Assert.Equal(new RowLink("unit", "old"), resident.Target);
        Assert.True(EditorUtility.IsDirty(resident));
        Assert.True(inspector.SerializedObject.IsDirty);
        Assert.True(inspector.CanRedo);

        inspector.PerformRedo();
        Assert.Equal(25, resident.Hp);
        Assert.Equal(["base", "burst"], resident.Tags);
        Assert.Equal(new RowLink("unit", "boss"), resident.Target);
    }

    [Fact]
    public void NonAuthoringModeCannotCreateAnEditableInspectorOrMutateResidentState()
    {
        var resident = new TestAsset { Id = "hero", Hp = 10 };
        var assetGuid = new GUID(RowGuid.New());
        var adapter = new MemoryAdapter(Import(assetGuid, resident));
        using var development = CreateAuthoring(adapter, RuntimeMode.Development, enableExcel: true);
        using var activation = development.Activate();
        AssetDatabase.MountWorkbook(WorkbookPath);

        var guid = AssetDatabase.AssetPathToGUID(AssetPath);
        var binding = Binding();
        var host = new FakeHost(guid, resident, binding);
        var factory = new ExcelDbAuthoringPropertyFactory();

        Assert.False(AssetDatabase.IsAuthoringEnabled);
        Assert.False(ExcelDbSerializedInspectorSession.TryCreate(host, guid, factory, out var inspector));
        Assert.Null(inspector);
        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create(binding, resident));
        Assert.Contains("EditorAuthoring", exception.Message, StringComparison.Ordinal);
        Assert.Equal(10, resident.Hp);
        Assert.False(EditorUtility.IsDirty(resident));
    }

    [Fact]
    public void SaveStopsWhenAnotherInspectorChangedTheResidentWithoutARevisionChange()
    {
        var resident = new TestAsset { Id = "hero", Hp = 10 };
        var assetGuid = new GUID(RowGuid.New());
        var adapter = new MemoryAdapter(Import(assetGuid, resident));
        using var authoring = CreateAuthoring(adapter, RuntimeMode.EditorAuthoring);
        using var activation = authoring.Activate();
        AssetDatabase.MountWorkbook(WorkbookPath);

        var guid = AssetDatabase.AssetPathToGUID(AssetPath);
        var host = new FakeHost(guid, resident, Binding());
        var factory = new ExcelDbAuthoringPropertyFactory();
        Assert.True(ExcelDbSerializedInspectorSession.TryCreate(host, guid, factory, out var created));
        using var inspector = Assert.IsType<ExcelDbSerializedInspectorSession>(created);

        inspector.SerializedObject.FindProperty("hp").BoxedValue = 25;
        resident.Hp = 11;

        Assert.False(inspector.Save());
        Assert.Equal(EditorApplyStatus.StateConflict, inspector.LastApplyResult?.Status);
        Assert.Equal(0, host.SaveCount);
        Assert.Equal(11, resident.Hp);
        Assert.False(EditorUtility.IsDirty(resident));
    }

    [Fact]
    public void GroupRowReferenceUsesThePickedRowsActualAllowedTableAndGuid()
    {
        var constraint = EditorReferenceConstraint.ForGroup(
            "actor",
            [
                new EditorReferenceTargetTable(101, "game.Unit"),
                new EditorReferenceTargetTable(205, "game.Boss"),
            ]);
        var selected = new UnityEditorBrowserRow(
            "boss-guid",
            WorkbookPath,
            "game.Boss",
            "dragon",
            "Dragon",
            string.Empty);

        Assert.True(ExcelDbRowReferenceBinding.TryCreateValue(constraint, selected, out var value));
        Assert.NotNull(value);
        Assert.Equal(205, value.TargetTableId);
        Assert.Equal("game.Boss", value.TargetTable);
        Assert.Equal("boss-guid", value.TargetIdentity);
        Assert.Equal("dragon", value.DisplayKey);

        var disallowed = new UnityEditorBrowserRow(
            "bullet-guid",
            WorkbookPath,
            "game.Bullet",
            "fireball",
            "Fireball",
            string.Empty);
        Assert.False(ExcelDbRowReferenceBinding.TryCreateValue(constraint, disallowed, out _));
    }

    [Fact]
    public void GeneratedProjectionEditsNestedAndListRowRefThenSavesUndoesAndDrawsUnsupportedReadonly()
    {
        var resident = new TestAsset
        {
            Id = "hero",
            Stats = new GeneratedStats { Hp = 7 },
            Tuning = new Dictionary<string, int> { ["speed"] = 3 },
        };
        var assetGuid = new GUID(RowGuid.New());
        var adapter = new MemoryAdapter(Import(assetGuid, resident));
        using var authoring = CreateAuthoring(adapter, RuntimeMode.EditorAuthoring);
        using var activation = authoring.Activate();
        AssetDatabase.MountWorkbook(WorkbookPath);

        var catalog = GeneratedCatalog();
        var binding = new GeneratedBindingFactory().Create(catalog[0], catalog);
        var guid = AssetDatabase.AssetPathToGUID(AssetPath);
        var host = new FakeHost(guid, resident, binding);
        var factory = new ExcelDbAuthoringPropertyFactory();
        Assert.True(ExcelDbSerializedInspectorSession.TryCreate(host, guid, factory, out var created));
        using var inspector = Assert.IsType<ExcelDbSerializedInspectorSession>(created);

        var hp = inspector.SerializedObject.FindProperty("stats.hp");
        Assert.Equal(EditorPropertyKind.Scalar, hp.Kind);
        hp.BoxedValue = 42;

        var effects = inspector.SerializedObject.FindProperty("effects");
        effects.ArraySize = 1;
        var effect = effects.GetArrayElementAtIndex(0);
        var groupTarget = Assert.Single(
            effect.Children,
            child => child.SchemaPropertyPath == "effects.group_target");
        Assert.Equal(EditorPropertyKind.Reference, groupTarget.Kind);
        Assert.Equal("damageable", groupTarget.ReferenceConstraint!.ReferenceGroup);

        var targetGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var selected = new UnityEditorBrowserRow(
            targetGuid.ToString("D"),
            WorkbookPath,
            "game.Prop",
            "barrel",
            "Barrel",
            string.Empty);
        Assert.True(ExcelDbRowReferenceBinding.TryCreateValue(
            groupTarget.ReferenceConstraint,
            selected,
            out var reference));
        inspector.QueueReferenceChange(groupTarget, reference);

        var apply = Assert.IsType<EditorApplyResult>(inspector.SynchronizeBeforeDraw());
        Assert.Equal(EditorApplyStatus.Applied, apply.Status);
        Assert.Equal(42, resident.Stats!.Hp);
        var appliedEffect = Assert.Single(resident.Effects);
        Assert.Equal(303, appliedEffect.GroupTarget.Table);
        Assert.Equal(targetGuid, appliedEffect.GroupTarget.RowGuid);
        Assert.True(inspector.Save());

        inspector.PerformUndo();
        Assert.Equal(7, resident.Stats.Hp);
        Assert.Empty(resident.Effects);
        Assert.True(EditorUtility.IsDirty(resident));

        inspector.PerformRedo();
        Assert.Equal(42, resident.Stats.Hp);
        Assert.Equal(targetGuid, Assert.Single(resident.Effects).GroupTarget.RowGuid);

        var unsupported = inspector.SerializedObject.FindProperty("tuning");
        Assert.Equal(EditorPropertyKind.Unsupported, unsupported.Kind);
        Assert.True(unsupported.IsReadOnly);
        Assert.Contains("map editing", unsupported.UnsupportedReason, StringComparison.Ordinal);

        UnityEditor.EditorGUILayout.ResetValidationState();
        Assert.False(ExcelDbPropertyGUI.Draw(
            inspector.SerializedObject,
            new ExcelDbPropertyGUIState(),
            static (_, __) => { }));
        Assert.Equal("tuning", UnityEditor.EditorGUILayout.LastLabel);
        Assert.Contains("speed", UnityEditor.EditorGUILayout.LastLabelValue, StringComparison.Ordinal);
        Assert.Equal(UnityEditor.MessageType.Warning, UnityEditor.EditorGUILayout.LastHelpBoxType);
        Assert.Contains("map editing", UnityEditor.EditorGUILayout.LastHelpBoxMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedOptionalGroupPresenceUiClearsCreatesAndNormalizesMixedTargets()
    {
        var resident = new TestAsset
        {
            Id = "hero",
            Stats = new GeneratedStats { Hp = 9 },
        };
        var assetGuid = new GUID(RowGuid.New());
        var adapter = new MemoryAdapter(Import(assetGuid, resident));
        using var authoring = CreateAuthoring(adapter, RuntimeMode.EditorAuthoring);
        using var activation = authoring.Activate();
        AssetDatabase.MountWorkbook(WorkbookPath);

        var catalog = GeneratedCatalog();
        var binding = new GeneratedBindingFactory().Create(catalog[0], catalog);
        var guid = AssetDatabase.AssetPathToGUID(AssetPath);
        var host = new FakeHost(guid, resident, binding);
        var factory = new ExcelDbAuthoringPropertyFactory();
        Assert.True(ExcelDbSerializedInspectorSession.TryCreate(host, guid, factory, out var created));
        using var inspector = Assert.IsType<ExcelDbSerializedInspectorSession>(created);
        var stats = inspector.SerializedObject.FindProperty("stats");

        Assert.True(stats.HasPresence);
        Assert.True(stats.HasPresenceValue);
        UnityEditor.EditorGUILayout.ResetValidationState();
        UnityEditor.EditorGUILayout.NextToggleValue = false;
        UnityEditor.EditorGUI.NextChangeCheckResult = true;
        Assert.True(ExcelDbPropertyGUI.Draw(
            inspector.SerializedObject,
            new ExcelDbPropertyGUIState(),
            static (_, __) => { }));
        Assert.Equal("Present", UnityEditor.EditorGUILayout.LastToggleLabel);
        Assert.False(stats.HasPresenceValue);
        Assert.NotNull(resident.Stats);

        Assert.Equal(
            EditorApplyStatus.Applied,
            inspector.ApplyModifiedProperties("Clear optional stats").Status);
        Assert.Null(resident.Stats);
        inspector.PerformUndo();
        Assert.Equal(9, resident.Stats!.Hp);
        inspector.PerformRedo();
        Assert.Null(resident.Stats);

        UnityEditor.EditorGUILayout.ResetValidationState();
        UnityEditor.EditorGUILayout.NextToggleValue = true;
        UnityEditor.EditorGUI.NextChangeCheckResult = true;
        Assert.True(ExcelDbPropertyGUI.Draw(
            inspector.SerializedObject,
            new ExcelDbPropertyGUIState(),
            static (_, __) => { }));
        Assert.True(stats.HasPresenceValue);
        Assert.Null(resident.Stats);
        Assert.Equal(
            EditorApplyStatus.Applied,
            inspector.ApplyModifiedProperties("Create optional stats").Status);
        Assert.NotNull(resident.Stats);
        Assert.Equal(0, resident.Stats.Hp);

        var missing = new TestAsset { Id = "missing", Stats = null };
        var existing = new TestAsset
        {
            Id = "existing",
            Stats = new GeneratedStats { Hp = 18 },
        };
        using var multi = new EditorSerializedObject(
            binding,
            [
                new EditorEditTarget("missing", missing, static () => "r1"),
                new EditorEditTarget("existing", existing, static () => "r1"),
            ]);
        var mixedStats = multi.FindProperty("stats");
        Assert.True(mixedStats.HasMultipleDifferentPresenceValues);

        UnityEditor.EditorGUILayout.ResetValidationState();
        UnityEditor.EditorGUILayout.NextToggleValue = true;
        UnityEditor.EditorGUI.NextChangeCheckResult = true;
        Assert.True(ExcelDbPropertyGUI.Draw(
            multi,
            new ExcelDbPropertyGUIState(),
            static (_, __) => { }));
        Assert.True(UnityEditor.EditorGUILayout.LastToggleHadMixedValue);
        Assert.False(mixedStats.HasMultipleDifferentPresenceValues);
        Assert.Null(missing.Stats);
        Assert.Equal(18, existing.Stats!.Hp);

        Assert.Equal(EditorApplyStatus.Applied, multi.ApplyModifiedProperties("Normalize presence").Status);
        Assert.NotNull(missing.Stats);
        Assert.Equal(0, missing.Stats.Hp);
        Assert.Equal(18, existing.Stats.Hp);
    }

    private const string WorkbookPath = "Data/game.xlsx";
    private const string AssetPath = "Data/game.xlsx/TestAsset/hero";

    private static AssetDatabaseSession CreateAuthoring(
        MemoryAdapter adapter,
        RuntimeMode mode,
        bool enableExcel = false)
    {
        return new AssetDatabaseSession(adapter, mode, enableExcel)
            .RegisterTable(new AuthoringTableRegistration(
                101,
                "TestAsset",
                typeof(TestAsset),
                static value => ((TestAsset)value).Id,
                static (value, key) => ((TestAsset)value).Id = key,
                static value => ((TestAsset)value).Copy()));
    }

    private static AuthoringWorkbookImport Import(GUID guid, TestAsset asset)
    {
        return new AuthoringWorkbookImport(
            WorkbookPath,
            [new AuthoringImportedAsset(guid, "TestAsset", asset.Id, asset)],
            OperationReport.Success("import", "test", applied: true),
            "v1");
    }

    private static EditorObjectBinding Binding()
    {
        return EditorObjectBinding.Create<TestAsset>(
        [
            EditorPropertyBinding.Create<TestAsset, int>(
                1, [1], "hp", "Hp", EditorPropertyKind.Scalar,
                static value => value.Hp,
                static (value, next) => value.Hp = next),
            EditorPropertyBinding.Create<TestAsset, List<string>>(
                2, [2], "tags", "Tags", EditorPropertyKind.List,
                static value => value.Tags,
                static (value, next) => value.Tags = next),
            EditorPropertyBinding.Create<TestAsset, RowLink>(
                3, [3], "target", "Target", EditorPropertyKind.Reference,
                static value => value.Target,
                static (value, next) => value.Target = next,
                reference: new EditorReferenceAdapter(
                    new EditorReferenceConstraint("unit"),
                    static value => value is RowLink link && link.Key.Length != 0
                        ? new EditorReferenceValue(link.Table, link.Key)
                        : null,
                    static value => value == null
                        ? default(RowLink)
                        : new RowLink(value.Table, value.TargetIdentity))),
        ]);
    }

    private static IReadOnlyList<GeneratedTableMetadata> GeneratedCatalog()
    {
        var config = new GeneratedTableMetadata(
            101,
            "game.Config",
            typeof(TestAsset),
            [],
            [
                new GeneratedFieldMetadata(
                    10, [10], "stats", nameof(TestAsset.Stats),
                    GeneratedFieldShape.Message,
                    physicalKind: GeneratedPhysicalFieldKind.PropertyGroup,
                    hasPresence: true),
                new GeneratedFieldMetadata(
                    11, [10, 1], "stats.hp",
                    $"{nameof(TestAsset.Stats)}.{nameof(GeneratedStats.Hp)}",
                    GeneratedFieldShape.Scalar,
                    physicalKind: GeneratedPhysicalFieldKind.ExpandedColumn),
                new GeneratedFieldMetadata(
                    20, [20], "effects", nameof(TestAsset.Effects),
                    GeneratedFieldShape.RepeatedMessage,
                    physicalKind: GeneratedPhysicalFieldKind.RepeatedMessageChildTable),
                new GeneratedFieldMetadata(
                    21, [20, 1], "effects.group_target",
                    $"{nameof(TestAsset.Effects)}.{nameof(GeneratedEffect.GroupTarget)}",
                    GeneratedFieldShape.Message,
                    physicalKind: GeneratedPhysicalFieldKind.ExpandedColumn,
                    referenceGroup: "damageable"),
                new GeneratedFieldMetadata(
                    30, [30], "tuning", nameof(TestAsset.Tuning),
                    GeneratedFieldShape.Map,
                    physicalKind: GeneratedPhysicalFieldKind.MessageMapChildTable),
            ]);
        var enemy = new GeneratedTableMetadata(
            202,
            "game.Enemy",
            typeof(GeneratedTarget),
            ["damageable"],
            [GeneratedFieldMetadata.Placeholder()]);
        var prop = new GeneratedTableMetadata(
            303,
            "game.Prop",
            typeof(GeneratedTarget),
            ["damageable"],
            [GeneratedFieldMetadata.Placeholder()]);
        return [config, enemy, prop];
    }

    private sealed class TestAsset
    {
        public string Id { get; set; } = string.Empty;
        public int Hp { get; set; }
        public List<string> Tags { get; set; } = [];
        public RowLink Target { get; set; }
        public GeneratedStats? Stats { get; set; } = new();
        public List<GeneratedEffect> Effects { get; set; } = [];
        public Dictionary<string, int> Tuning { get; set; } = [];

        public TestAsset Copy() => new()
        {
            Id = Id,
            Hp = Hp,
            Tags = [.. Tags],
            Target = Target,
            Stats = Stats == null ? null : new GeneratedStats { Hp = Stats.Hp },
            Effects = Effects.Select(static effect => new GeneratedEffect
            {
                GroupTarget = effect.GroupTarget,
            }).ToList(),
            Tuning = new Dictionary<string, int>(Tuning, StringComparer.Ordinal),
        };
    }

    private readonly record struct RowLink(string Table, string Key);

    private sealed class GeneratedStats
    {
        public int Hp { get; set; }
    }

    private sealed class GeneratedEffect
    {
        public GeneratedRowRef GroupTarget { get; set; }
    }

    private sealed class GeneratedTarget
    {
        public string Placeholder { get; set; } = string.Empty;
    }

    private readonly struct GeneratedRowRef
    {
        public GeneratedRowRef(int table, Guid rowGuid)
        {
            Table = table;
            RowGuid = rowGuid;
        }

        public int Table { get; }
        public Guid RowGuid { get; }
    }

    private enum GeneratedPhysicalFieldKind
    {
        Cell,
        ExpandedColumn,
        RepeatedMessageChildTable,
        MessageMapChildTable,
        PropertyGroup,
    }

    private enum GeneratedFieldShape
    {
        Scalar = 1,
        Enum,
        Message,
        RepeatedScalar,
        RepeatedEnum,
        RepeatedMessage,
        Map,
        OneOfVariant,
    }

    private sealed class GeneratedFieldMetadata
    {
        public GeneratedFieldMetadata(
            int fieldId,
            int[] fieldIdPath,
            string propertyPath,
            string memberPath,
            GeneratedFieldShape shape,
            GeneratedPhysicalFieldKind physicalKind = GeneratedPhysicalFieldKind.Cell,
            bool hasPresence = false,
            string? referenceGroup = null)
        {
            FieldId = fieldId;
            FieldIdPath = fieldIdPath;
            PropertyPath = propertyPath;
            MemberPath = memberPath;
            DisplayName = propertyPath;
            PhysicalKind = physicalKind;
            Shape = shape;
            HasPresence = hasPresence;
            ReferenceGroup = referenceGroup;
        }

        public int FieldId { get; }
        public IReadOnlyList<int> FieldIdPath { get; }
        public string PropertyPath { get; }
        public string MemberPath { get; }
        public string DisplayName { get; }
        public string? HeaderComment => null;
        public GeneratedPhysicalFieldKind PhysicalKind { get; }
        public GeneratedFieldShape Shape { get; }
        public bool HasPresence { get; }
        public bool Required => false;
        public int KeyOrder => 0;
        public string? ReferenceTable => null;
        public string? ReferenceGroup { get; }

        public static GeneratedFieldMetadata Placeholder() => new(
            1,
            [1],
            "placeholder",
            nameof(GeneratedTarget.Placeholder),
            GeneratedFieldShape.Scalar);
    }

    private sealed class GeneratedTableMetadata
    {
        public GeneratedTableMetadata(
            int tableId,
            string fullName,
            Type clrType,
            string[] implements,
            IReadOnlyList<GeneratedFieldMetadata> fields)
        {
            TableId = tableId;
            FullName = fullName;
            ClrType = clrType;
            Implements = implements;
            Fields = fields;
        }

        public int TableId { get; }
        public string FullName { get; }
        public string DisplayName => FullName;
        public Type ClrType { get; }
        public IReadOnlyList<string> Implements { get; }
        public IReadOnlyList<GeneratedFieldMetadata> Fields { get; }
    }

    private sealed class MemoryAdapter(AuthoringWorkbookImport import) : IAuthoringWorkbookAdapter
    {
        public AuthoringWorkbookImport Import(string workbookPath, bool forceUpdate) => import;

        public OperationReport Save(AuthoringSaveRequest request) =>
            OperationReport.Success("save", "test", applied: true);

        public bool Open(string workbookPath, string tableName, GUID guid) => true;
    }

    private sealed class FakeHost(
        string guid,
        object resident,
        EditorObjectBinding binding) : IExcelDbUnityEditorBridge, IExcelDbUnityPropertyBridge
    {
        public int SaveCount { get; private set; }
        public bool HasProject => true;
        public UnityEditorBrowserCounts BrowserCounts => default;
        public IReadOnlyList<string> MountedWorkbooks => [WorkbookPath];
        public IReadOnlyList<UnityEditorReportView> RecentReports => [];
        public IReadOnlyList<UnityEditorConflictView> Conflicts => [];
        public IReadOnlyList<UnityEditorPendingChangeView> PendingChanges => [];

        public bool TryResolveEditorAsset(
            string requestedGuid,
            out object? residentAsset,
            out EditorObjectBinding? editorBinding)
        {
            residentAsset = requestedGuid == guid ? resident : null;
            editorBinding = requestedGuid == guid ? binding : null;
            return residentAsset != null;
        }

        public void SaveAsset(string requestedGuid)
        {
            Assert.Equal(guid, requestedGuid);
            SaveCount++;
            AssetDatabase.SaveAssetIfDirty(resident);
        }

        public void SaveAll() => AssetDatabase.SaveAssets();
        public void RevertAsset(string requestedGuid) => AssetDatabase.DiscardAssetChanges(resident);
        public IReadOnlyList<UnityEditorBrowserRow> Search(string filter, string displayName) => [];
        public UnityEditorProjectSettings ReadProjectSettings() => default;
        public UnityEditorSettingsMetadata ReadProjectSettingsMetadata() => default;
        public void WriteProjectSettings(UnityEditorProjectSettings settings) { }
        public void InitializeProject() { }
        public void Refresh() => AssetDatabase.Refresh();
        public IReadOnlyList<string> InspectAsset(string requestedGuid) => [];
        public void OpenInExcel(string requestedGuid) { }
        public void Create(string table) { }
        public void Delete(string requestedGuid) { }
        public void Rename(string requestedGuid, string newName) { }
        public void Move(string requestedGuid, string newPath) { }
        public void Copy(string requestedGuid, string newPath) { }
        public void MountWorkbook() { }
        public void UnmountWorkbook(string workbook, bool persist) { }
        public void OpenWorkbook(string workbook) { }
        public IReadOnlyList<UnityEditorBrowserRow> SearchRowReferences(string filter, string refTable, string refGroup) => [];
        public void AssignRowReference(string ownerGuid, string propertyPath, string targetGuid) { }
        public UnityEditorWorkbookSummary InspectWorkbook(string workbook) => default;
        public UnityEditorPlanView BuildPlan(string operation) => default;
        public void ApplyDisplayedPlan(string planHash) { }
        public void ConvertClient() { }
        public bool GuardDirty(string trigger) => true;
        public UnityEditorFreshnessView GetClientFreshness() => new(true, string.Empty);
        public void ExportReportJson(int reportIndex, string path) { }
        public void LocateDiagnostic(int reportIndex, int diagnosticIndex) { }
        public void ResolveConflict(string conflictId, bool reloadFromExcel) { }
        public void SavePending(string requestedGuid) => SaveAsset(requestedGuid);
        public void RevertPending(string requestedGuid) => RevertAsset(requestedGuid);
        public bool PrepareNextPlaySource() => true;
        public void ClearPlaySourceRequest() { }
        public UnityEditorPlayView GetPlayState() => default;
        public void SwitchPlaySource() { }
        public void SetHotReload(bool enabled) { }
    }
}
