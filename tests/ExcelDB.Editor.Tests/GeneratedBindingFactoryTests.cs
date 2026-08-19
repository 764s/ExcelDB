using ExcelDb.Editor.Model;

namespace ExcelDb.Editor.Tests;

public sealed class GeneratedBindingFactoryTests
{
    [Fact]
    public void FaithfulGeneratedContractBuildsCachedScalarGroupObjectListAndRowRefBindings()
    {
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        var characterTable = CharacterTable();
        var enemyTable = Table(2, "game.Enemy", typeof(GeneratedEnemy), ["damageable"]);
        var propTable = Table(3, "game.Prop", typeof(GeneratedProp), ["damageable"]);
        var catalog = new[] { characterTable, enemyTable, propTable };
        var factory = new GeneratedBindingFactory();

        var binding = factory.Create(characterTable, catalog);
        var cached = factory.Create(characterTable, catalog);

        Assert.Same(binding, cached);
        Assert.Equal(1, factory.CachedBindingCount);
        Assert.Equal(
            ["id", "stats", "stats.hp", "levels", "skills", "profile", "variant_text", "primary_target", "any_target"],
            binding.Properties.Select(item => item.PropertyPath));
        Assert.False(binding.TryGetProperty("skills.power", out _));

        var id = binding.GetProperty("id");
        Assert.True(id.IsReadOnly);
        Assert.Equal(1, id.KeyOrder);
        Assert.True(id.Required);

        var stats = binding.GetProperty("stats");
        Assert.Equal(EditorPropertyKind.Group, stats.Kind);
        Assert.True(stats.IsGroupContainer);
        Assert.True(stats.IsReadOnly);
        Assert.Equal("Expanded stats", stats.Tooltip);
        Assert.Equal(EditorPropertyKind.Scalar, binding.GetProperty("stats.hp").Kind);
        Assert.Equal(EditorPropertyKind.List, binding.GetProperty("levels").Kind);
        Assert.Equal(EditorPropertyKind.List, binding.GetProperty("skills").Kind);
        Assert.Equal(EditorPropertyKind.Object, binding.GetProperty("profile").Kind);
        Assert.Equal(EditorPropertyKind.Scalar, binding.GetProperty("variant_text").Kind);

        var resident = new GeneratedCharacter
        {
            Id = "hero",
            Stats = new GeneratedStats { Hp = 10 },
            Levels = [1, 2],
            Skills = [new GeneratedSkill { Power = 4 }],
            Profile = new GeneratedProfile { Note = "starter" },
            VariantText = "alpha",
            PrimaryTarget = new GeneratedRowRef(2, firstTarget),
            AnyTarget = new GeneratedRowRef(2, firstTarget),
        };
        var history = new EditorUndoHistory();
        using var serialized = new EditorSerializedObject(
            binding,
            [new EditorEditTarget("hero", resident, static () => "r1")],
            history);
        Assert.False(serialized.TryFindProperty("skills.power", out _));
        Assert.Equal("skills.power", binding.GetSchemaProperty("skills.power").PropertyPath);

        var statsProperty = serialized.FindProperty("stats");
        Assert.True(statsProperty.IsGroupContainer);
        Assert.Equal(["stats.hp"], statsProperty.Children.Select(item => item.PropertyPath));
        serialized.FindProperty("stats.hp").BoxedValue = 25;
        Assert.Equal(EditorApplyStatus.Applied, serialized.ApplyModifiedProperties("Edit group leaf").Status);
        Assert.Equal(25, resident.Stats!.Hp);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal(10, resident.Stats!.Hp);

        var skills = serialized.FindProperty("skills");
        var skillElement = skills.GetArrayElementAtIndex(0);
        Assert.Contains(skillElement.Children, item => item.SchemaPropertyPath == "skills.power");
        var levels = serialized.FindProperty("levels");
        levels.InsertArrayElementAtIndex(2, 3);

        var primary = serialized.FindProperty("primary_target");
        Assert.Equal("game.Enemy", primary.ReferenceConstraint!.ReferenceTable);
        Assert.Null(primary.ReferenceConstraint.ReferenceGroup);
        Assert.False(primary.ReferenceConstraint.AllowNull);
        Assert.Equal(["game.Enemy"], primary.ReferenceConstraint.AllowedTables);
        Assert.Equal(
            new EditorReferenceValue(2, "game.Enemy", firstTarget.ToString("N")),
            primary.ReferenceValue);
        Assert.Throws<ArgumentException>(
            () => primary.ReferenceValue = new EditorReferenceValue(
                3,
                "game.Prop",
                secondTarget.ToString("N")));

        var any = serialized.FindProperty("any_target");
        Assert.Null(any.ReferenceConstraint!.ReferenceTable);
        Assert.Equal("damageable", any.ReferenceConstraint.ReferenceGroup);
        Assert.True(any.ReferenceConstraint.AllowNull);
        Assert.Equal(["game.Enemy", "game.Prop"], any.ReferenceConstraint.AllowedTables);
        any.ReferenceValue = new EditorReferenceValue(
            3,
            "game.Prop",
            secondTarget.ToString("D"),
            "barrel");

        Assert.Equal(EditorApplyStatus.Applied, serialized.ApplyModifiedProperties("Lists and refs").Status);
        Assert.Equal([1, 2, 3], resident.Levels);
        Assert.Equal(3, resident.AnyTarget.Table);
        Assert.Equal(secondTarget, resident.AnyTarget.RowGuid);
    }

    [Fact]
    public void GeneratedReferenceMetadataRequiresExactlyOneResolvableTableOrGroup()
    {
        var both = Table(
            1,
            "game.Invalid",
            typeof(GeneratedInvalidReference),
            [],
            Field(
                1,
                [1],
                "target",
                nameof(GeneratedInvalidReference.Target),
                FakeGeneratedFieldShape.Message,
                referenceTable: "game.Enemy",
                referenceGroup: "damageable"));
        var enemy = Table(2, "game.Enemy", typeof(GeneratedEnemy), ["damageable"]);
        var factory = new GeneratedBindingFactory();

        var bothError = Assert.Throws<InvalidOperationException>(
            () => factory.Create(both, new[] { both, enemy }));
        Assert.Contains("exactly one", bothError.Message, StringComparison.Ordinal);

        var unknownGroup = Table(
            3,
            "game.Unknown",
            typeof(GeneratedInvalidReference),
            [],
            Field(
                1,
                [1],
                "target",
                nameof(GeneratedInvalidReference.Target),
                FakeGeneratedFieldShape.Message,
                referenceGroup: "missing"));
        var groupError = Assert.Throws<InvalidOperationException>(
            () => factory.Create(unknownGroup, new[] { unknownGroup, enemy }));
        Assert.Contains("no implementing target tables", groupError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedPresenceCanClearApplyUndoRedoAndMaterializeWithoutMakingGroupWritable()
    {
        var characterTable = CharacterTable();
        var binding = new GeneratedBindingFactory().Create(
            characterTable,
            ReferenceCatalog(characterTable));
        var resident = new GeneratedCharacter
        {
            Id = "hero",
            Stats = new GeneratedStats { Hp = 12 },
        };
        var history = new EditorUndoHistory();
        using var serialized = new EditorSerializedObject(
            binding,
            [new EditorEditTarget("hero", resident, static () => "r1")],
            history);
        var stats = serialized.FindProperty("stats");

        Assert.True(stats.HasPresence);
        Assert.True(stats.HasPresenceValue);
        Assert.False(stats.HasMultipleDifferentPresenceValues);
        Assert.True(stats.IsReadOnly);

        stats.SetPresence(false);

        Assert.False(stats.HasPresenceValue);
        Assert.NotNull(resident.Stats);
        Assert.Equal(EditorApplyStatus.Applied, serialized.ApplyModifiedProperties("Clear details").Status);
        Assert.Null(resident.Stats);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal(12, resident.Stats!.Hp);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryRedo().Status);
        Assert.Null(resident.Stats);

        stats.SetPresence(true);

        Assert.True(stats.HasPresenceValue);
        Assert.Equal(0, serialized.FindProperty("stats.hp").BoxedValue);
        Assert.Null(resident.Stats);
        Assert.Equal(EditorApplyStatus.Applied, serialized.ApplyModifiedProperties("Materialize details").Status);
        Assert.NotNull(resident.Stats);
        Assert.Equal(0, resident.Stats!.Hp);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Null(resident.Stats);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryRedo().Status);
        Assert.NotNull(resident.Stats);
    }

    [Fact]
    public void MultiTargetPresenceMaterializationPreservesExistingValuesAndStagesAtomically()
    {
        var characterTable = CharacterTable();
        var binding = new GeneratedBindingFactory().Create(
            characterTable,
            ReferenceCatalog(characterTable));
        var missing = new GeneratedCharacter { Id = "missing", Stats = null };
        var existing = new GeneratedCharacter
        {
            Id = "existing",
            Stats = new GeneratedStats { Hp = 9 },
        };
        var history = new EditorUndoHistory();
        using var serialized = new EditorSerializedObject(
            binding,
            [
                new EditorEditTarget("missing", missing, static () => "r1"),
                new EditorEditTarget("existing", existing, static () => "r1"),
            ],
            history);
        var stats = serialized.FindProperty("stats");

        Assert.False(stats.HasPresenceValue);
        Assert.True(stats.HasMultipleDifferentPresenceValues);

        stats.SetPresence(true);

        Assert.False(stats.HasMultipleDifferentPresenceValues);
        Assert.Null(missing.Stats);
        Assert.Equal(9, existing.Stats!.Hp);
        Assert.Equal(EditorApplyStatus.Applied, serialized.ApplyModifiedProperties("Materialize selection").Status);
        Assert.Equal(0, missing.Stats!.Hp);
        Assert.Equal(9, existing.Stats!.Hp);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Null(missing.Stats);
        Assert.Equal(9, existing.Stats!.Hp);
    }

    private static FakeGeneratedTableBinding CharacterTable()
    {
        return Table(
            1,
            "game.Character",
            typeof(GeneratedCharacter),
            [],
            Field(
                1,
                [1],
                "id",
                nameof(GeneratedCharacter.Id),
                FakeGeneratedFieldShape.Scalar,
                required: true,
                keyOrder: 1),
            Field(
                2,
                [2],
                "stats",
                nameof(GeneratedCharacter.Stats),
                FakeGeneratedFieldShape.Message,
                physicalKind: FakeGeneratedPhysicalFieldKind.PropertyGroup,
                hasPresence: true,
                headerComment: "Expanded stats"),
            Field(
                3,
                [2, 1],
                "stats.hp",
                $"{nameof(GeneratedCharacter.Stats)}.{nameof(GeneratedStats.Hp)}",
                FakeGeneratedFieldShape.Scalar,
                physicalKind: FakeGeneratedPhysicalFieldKind.ExpandedColumn),
            Field(
                4,
                [4],
                "levels",
                nameof(GeneratedCharacter.Levels),
                FakeGeneratedFieldShape.RepeatedScalar),
            Field(
                5,
                [5],
                "skills",
                nameof(GeneratedCharacter.Skills),
                FakeGeneratedFieldShape.RepeatedMessage,
                physicalKind: FakeGeneratedPhysicalFieldKind.RepeatedMessageChildTable),
            // Generator flattening emits this child-table leaf even though MemberPath cannot cross IList.
            // The factory must bind the complete list and omit this overlapping metadata leaf.
            Field(
                6,
                [5, 1],
                "skills.power",
                $"{nameof(GeneratedCharacter.Skills)}.{nameof(GeneratedSkill.Power)}",
                FakeGeneratedFieldShape.Scalar,
                physicalKind: FakeGeneratedPhysicalFieldKind.ExpandedColumn),
            Field(
                7,
                [7],
                "profile",
                nameof(GeneratedCharacter.Profile),
                FakeGeneratedFieldShape.Message),
            Field(
                10,
                [10],
                "variant_text",
                nameof(GeneratedCharacter.VariantText),
                FakeGeneratedFieldShape.OneOfVariant),
            Field(
                8,
                [8],
                "primary_target",
                nameof(GeneratedCharacter.PrimaryTarget),
                FakeGeneratedFieldShape.Message,
                required: true,
                referenceTable: "game.Enemy"),
            Field(
                9,
                [9],
                "any_target",
                nameof(GeneratedCharacter.AnyTarget),
                FakeGeneratedFieldShape.Message,
                hasPresence: true,
                referenceGroup: "damageable"));
    }

    private static FakeGeneratedTableBinding[] ReferenceCatalog(FakeGeneratedTableBinding characterTable)
    {
        return
        [
            characterTable,
            Table(2, "game.Enemy", typeof(GeneratedEnemy), ["damageable"]),
            Table(3, "game.Prop", typeof(GeneratedProp), ["damageable"]),
        ];
    }

    private static FakeGeneratedTableBinding Table(
        int tableId,
        string fullName,
        Type clrType,
        string[] implements,
        params FakeGeneratedFieldBinding[] fields)
    {
        return new FakeGeneratedTableBinding(
            tableId,
            fullName,
            fullName,
            clrType,
            "factory",
            "accessor",
            "patcher",
            implements,
            Array.AsReadOnly(fields.Length == 0
                ? [Field(100, [100], "placeholder", "Placeholder", FakeGeneratedFieldShape.Scalar)]
                : fields));
    }

    private static FakeGeneratedFieldBinding Field(
        int fieldId,
        int[] fieldIdPath,
        string propertyPath,
        string memberPath,
        FakeGeneratedFieldShape shape,
        FakeGeneratedPhysicalFieldKind physicalKind = FakeGeneratedPhysicalFieldKind.Cell,
        bool hasPresence = false,
        bool required = false,
        int keyOrder = 0,
        string? referenceTable = null,
        string? referenceGroup = null,
        string? headerComment = null)
    {
        return new FakeGeneratedFieldBinding(
            fieldId,
            fieldIdPath,
            propertyPath,
            memberPath,
            propertyPath,
            headerComment,
            [],
            physicalKind,
            shape,
            hasPresence,
            required,
            keyOrder,
            referenceTable,
            referenceGroup);
    }

    public enum FakeGeneratedPhysicalFieldKind
    {
        Cell = 0,
        ExpandedColumn = 1,
        RepeatedMessageChildTable = 2,
        MessageMapChildTable = 3,
        PropertyGroup = 4,
    }

    public enum FakeGeneratedFieldShape
    {
        Scalar = 1,
        Enum = 2,
        Message = 3,
        RepeatedScalar = 4,
        RepeatedEnum = 5,
        RepeatedMessage = 6,
        Map = 7,
        OneOfVariant = 8,
    }

    public sealed class FakeGeneratedFieldBinding
    {
        public FakeGeneratedFieldBinding(
            int fieldId,
            int[] fieldIdPath,
            string propertyPath,
            string memberPath,
            string? displayName,
            string? headerComment,
            string[] aliases,
            FakeGeneratedPhysicalFieldKind physicalKind,
            FakeGeneratedFieldShape shape,
            bool hasPresence,
            bool required,
            int keyOrder,
            string? referenceTable,
            string? referenceGroup)
        {
            FieldId = fieldId;
            FieldIdPath = Array.AsReadOnly(fieldIdPath);
            PropertyPath = propertyPath;
            MemberPath = memberPath;
            DisplayName = displayName;
            HeaderComment = headerComment;
            Aliases = Array.AsReadOnly(aliases);
            PhysicalKind = physicalKind;
            Shape = shape;
            HasPresence = hasPresence;
            Required = required;
            KeyOrder = keyOrder;
            ReferenceTable = referenceTable;
            ReferenceGroup = referenceGroup;
        }

        public int FieldId { get; }
        public IReadOnlyList<int> FieldIdPath { get; }
        public string PropertyPath { get; }
        public string MemberPath { get; }
        public string? DisplayName { get; }
        public string? HeaderComment { get; }
        public IReadOnlyList<string> Aliases { get; }
        public FakeGeneratedPhysicalFieldKind PhysicalKind { get; }
        public FakeGeneratedFieldShape Shape { get; }
        public bool HasPresence { get; }
        public bool Required { get; }
        public int KeyOrder { get; }
        public string? ReferenceTable { get; }
        public string? ReferenceGroup { get; }
    }

    public sealed class FakeGeneratedTableBinding
    {
        public FakeGeneratedTableBinding(
            int tableId,
            string fullName,
            string? displayName,
            Type clrType,
            string factoryId,
            string accessorId,
            string patcherId,
            string[] implements,
            IReadOnlyList<FakeGeneratedFieldBinding> fields)
        {
            TableId = tableId;
            FullName = fullName;
            DisplayName = displayName;
            ClrType = clrType;
            FactoryId = factoryId;
            AccessorId = accessorId;
            PatcherId = patcherId;
            Implements = Array.AsReadOnly(implements);
            Fields = fields;
        }

        public int TableId { get; }
        public string FullName { get; }
        public string? DisplayName { get; }
        public Type ClrType { get; }
        public string FactoryId { get; }
        public string AccessorId { get; }
        public string PatcherId { get; }
        public IReadOnlyList<string> Implements { get; }
        public IReadOnlyList<FakeGeneratedFieldBinding> Fields { get; }
    }

    public sealed class GeneratedCharacter
    {
        public string Id { get; set; } = string.Empty;
        public GeneratedStats? Stats { get; set; } = new();
        public List<int> Levels { get; set; } = [];
        public List<GeneratedSkill> Skills { get; set; } = [];
        public GeneratedProfile Profile { get; set; } = new();
        public string VariantText { get; set; } = string.Empty;
        public GeneratedRowRef PrimaryTarget { get; set; }
        public GeneratedRowRef AnyTarget { get; set; }
    }

    public sealed class GeneratedStats
    {
        public int Hp { get; set; }
    }

    public sealed class GeneratedSkill
    {
        public int Power { get; set; }
    }

    public sealed class GeneratedProfile
    {
        public string Note { get; set; } = string.Empty;
    }

    public sealed class GeneratedEnemy
    {
        public string Placeholder { get; set; } = string.Empty;
    }

    public sealed class GeneratedProp
    {
        public string Placeholder { get; set; } = string.Empty;
    }

    public sealed class GeneratedInvalidReference
    {
        public GeneratedRowRef Target { get; set; }
    }

    public readonly struct GeneratedRowRef
    {
        public GeneratedRowRef(int table, Guid rowGuid)
        {
            Table = table;
            RowGuid = rowGuid;
        }

        public int Table { get; }
        public Guid RowGuid { get; }
    }
}
