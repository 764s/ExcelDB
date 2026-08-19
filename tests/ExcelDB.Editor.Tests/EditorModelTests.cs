using ExcelDb.Editor.Model;

namespace ExcelDb.Editor.Tests;

public sealed class EditorModelTests
{
    [Fact]
    public void ScalarNestedListAndReferenceValuesStageApplyDiscardAndTrackDirty()
    {
        var revision = "r1";
        var resident = Character("hero", 10, new Skill("base", 1), new RowReference("unit", "enemy"));
        var history = new EditorUndoHistory();
        var serialized = Serialized(Binding(), history, Target("hero", resident, () => revision));

        serialized.FindProperty("stats.hp").BoxedValue = 20;
        var stagedSkills = new List<Skill> { new Skill("slash", 2) };
        serialized.FindProperty("skills").BoxedValue = stagedSkills;
        serialized.FindProperty("target").BoxedValue = new RowReference("unit", "boss");
        stagedSkills[0].Level = 99;

        Assert.True(serialized.HasModifiedProperties);
        Assert.False(serialized.IsDirty);
        Assert.Equal(10, resident.Stats.Hp);
        Assert.Equal("base", Assert.Single(resident.Skills).Id);

        serialized.DiscardModifiedProperties();

        Assert.False(serialized.HasModifiedProperties);
        Assert.Equal(10, serialized.FindProperty("stats.hp").BoxedValue);

        serialized.FindProperty("stats.hp").BoxedValue = 20;
        serialized.FindProperty("skills").BoxedValue = new List<Skill> { new Skill("slash", 2) };
        serialized.FindProperty("target").BoxedValue = new RowReference("unit", "boss");
        var detachedRead = Assert.IsType<List<Skill>>(serialized.FindProperty("skills").BoxedValue);
        detachedRead[0].Level = 50;

        var result = serialized.ApplyModifiedProperties("Edit combat fields");

        Assert.Equal(EditorApplyStatus.Applied, result.Status);
        Assert.Equal(["hero"], result.ChangedTargets);
        Assert.False(serialized.HasModifiedProperties);
        Assert.True(serialized.IsDirty);
        Assert.Equal(20, resident.Stats.Hp);
        Assert.Equal(2, Assert.Single(resident.Skills).Level);
        Assert.Equal(new RowReference("unit", "boss"), resident.Target);
        Assert.Equal(1, history.UndoCount);

        serialized.MarkSaved();

        Assert.False(serialized.IsDirty);
        Assert.Equal(EditorApplyStatus.NoChanges, serialized.ApplyModifiedProperties("No-op").Status);
        Assert.Equal(1, history.UndoCount);

        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal(10, resident.Stats.Hp);
        Assert.Equal("base", Assert.Single(resident.Skills).Id);
        Assert.True(serialized.IsDirty);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryRedo().Status);
        Assert.Equal(2, Assert.Single(resident.Skills).Level);
    }

    [Fact]
    public void GeneratedMemberPathAdapterEditsNestedPocoWithoutUnityOrGeneratedTypeDependency()
    {
        var resident = Character("hero", 10);
        var hp = EditorPropertyBinding.CreateFromMemberPath(
            2,
            new[] { 2, 1 },
            "stats.hp",
            "Stats.Hp",
            typeof(CharacterAsset),
            EditorPropertyKind.Scalar,
            "Hit Points");
        var binding = EditorObjectBinding.Create<CharacterAsset>(new[] { hp });
        var serialized = Serialized(binding, new EditorUndoHistory(), Target("hero", resident));

        var property = serialized.FindProperty("stats.hp");

        Assert.Equal([2, 1], property.FieldIdPath);
        Assert.Equal("Stats.Hp", property.MemberPath);
        Assert.Equal("Hit Points", property.DisplayName);
        property.BoxedValue = 25;
        Assert.Equal(EditorApplyStatus.Applied, serialized.ApplyModifiedProperties("Nested edit").Status);
        Assert.Equal(25, resident.Stats.Hp);
    }

    [Fact]
    public void MultiTargetPropertyReportsMixedValueAndOneApplyIsOneUndoUnit()
    {
        var first = Character("one", 10);
        var second = Character("two", 20);
        var history = new EditorUndoHistory();
        var serialized = Serialized(
            Binding(),
            history,
            Target("one", first),
            Target("two", second));
        var name = serialized.FindProperty("name");

        Assert.True(name.HasMultipleDifferentValues);
        Assert.Equal("one", name.BoxedValue);

        name.BoxedValue = "shared";

        Assert.False(name.HasMultipleDifferentValues);
        Assert.Equal(EditorApplyStatus.Applied, serialized.ApplyModifiedProperties("Rename selection").Status);
        Assert.Equal("shared", first.Name);
        Assert.Equal("shared", second.Name);
        Assert.Equal(1, history.UndoCount);

        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal("one", first.Name);
        Assert.Equal("two", second.Name);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryRedo().Status);
        Assert.Equal("shared", first.Name);
        Assert.Equal("shared", second.Name);
    }

    [Fact]
    public void MultipleAppliesUndoAndRedoInOrder()
    {
        var resident = Character("hero", 10);
        var history = new EditorUndoHistory();
        var serialized = Serialized(Binding(), history, Target("hero", resident));
        var hp = serialized.FindProperty("stats.hp");

        hp.BoxedValue = 20;
        serialized.ApplyModifiedProperties("Twenty");
        hp.BoxedValue = 30;
        serialized.ApplyModifiedProperties("Thirty");

        Assert.Equal(2, history.UndoCount);
        Assert.Equal("Thirty", history.UndoLabel);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal(20, resident.Stats.Hp);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal(10, resident.Stats.Hp);
        Assert.False(serialized.IsDirty);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryRedo().Status);
        Assert.Equal(20, resident.Stats.Hp);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryRedo().Status);
        Assert.Equal(30, resident.Stats.Hp);
    }

    [Fact]
    public void ExplicitGroupCollapsesSeveralAppliesToAbsoluteBeforeAndAfter()
    {
        var resident = Character("hero", 10);
        var history = new EditorUndoHistory();
        var serialized = Serialized(Binding(), history, Target("hero", resident));

        using (history.BeginGroup("Character preset"))
        {
            serialized.FindProperty("name").BoxedValue = "champion";
            serialized.ApplyModifiedProperties("Rename");
            serialized.FindProperty("stats.hp").BoxedValue = 50;
            serialized.ApplyModifiedProperties("Health");
        }

        Assert.Equal(1, history.UndoCount);
        Assert.Equal("Character preset", history.UndoLabel);
        Assert.Equal("champion", resident.Name);
        Assert.Equal(50, resident.Stats.Hp);

        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal("hero", resident.Name);
        Assert.Equal(10, resident.Stats.Hp);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryRedo().Status);
        Assert.Equal("champion", resident.Name);
        Assert.Equal(50, resident.Stats.Hp);
    }

    [Fact]
    public void ChangedExternalRevisionRejectsApplyAndUndoThenUpdatePrunesHistory()
    {
        var revision = "r1";
        var resident = Character("hero", 10);
        var history = new EditorUndoHistory();
        var serialized = Serialized(Binding(), history, Target("hero", resident, () => revision));
        var hp = serialized.FindProperty("stats.hp");

        hp.BoxedValue = 20;
        serialized.ApplyModifiedProperties("Local edit");
        hp.BoxedValue = 30;
        revision = "r2";

        var apply = serialized.ApplyModifiedProperties("Stale edit");

        Assert.Equal(EditorApplyStatus.RevisionConflict, apply.Status);
        Assert.Equal(["hero"], apply.ConflictingTargets);
        Assert.Equal(20, resident.Stats.Hp);
        Assert.Equal(1, history.UndoCount);

        var undo = history.TryUndo();

        Assert.Equal(EditorHistoryStatus.RevisionConflict, undo.Status);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);

        resident.Stats.Hp = 40;
        var update = serialized.Update();

        Assert.True(update.ObservedExternalRevision);
        Assert.Equal(1, update.RefreshedTargets);
        Assert.Equal(1, update.PrunedHistoryEntries);
        Assert.False(history.CanUndo);
        Assert.False(serialized.HasModifiedProperties);
        Assert.False(serialized.IsDirty);
        Assert.Equal(40, serialized.FindProperty("stats.hp").BoxedValue);
    }

    [Fact]
    public void UndoRefusesUnversionedResidentMutationWithoutMovingStacks()
    {
        var resident = Character("hero", 10);
        var history = new EditorUndoHistory();
        var serialized = Serialized(Binding(), history, Target("hero", resident));
        serialized.FindProperty("stats.hp").BoxedValue = 20;
        serialized.ApplyModifiedProperties("Local edit");
        resident.Stats.Hp = 99;

        var undo = history.TryUndo();

        Assert.Equal(EditorHistoryStatus.StateConflict, undo.Status);
        Assert.Equal(["hero"], undo.ConflictingTargets);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
        Assert.Equal(99, resident.Stats.Hp);
    }

    [Fact]
    public void HistoryIsBoundedAndPrunableByStableIdentity()
    {
        var resident = Character("hero", 0);
        var history = new EditorUndoHistory(depth: 2);
        var serialized = Serialized(Binding(), history, Target("hero", resident));
        var hp = serialized.FindProperty("stats.hp");
        for (var value = 1; value <= 2; value++)
        {
            hp.BoxedValue = value;
            serialized.ApplyModifiedProperties("Set " + value);
        }
        var beforeTruncation = history.CaptureToken();
        hp.BoxedValue = 3;
        serialized.ApplyModifiedProperties("Set 3");

        Assert.Equal(2, history.UndoCount);
        Assert.True(history.Generation > beforeTruncation.Generation);
        Assert.Equal(EditorHistoryStatus.InvalidToken, history.TryMoveTo(beforeTruncation).Status);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal(2, resident.Stats.Hp);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal(1, resident.Stats.Hp);
        Assert.Equal(EditorHistoryStatus.Empty, history.TryUndo().Status);
        Assert.Equal(2, history.RedoCount);
        var beforePrune = history.CaptureToken();
        Assert.Equal(2, history.Prune("hero"));
        Assert.True(history.Generation > beforePrune.Generation);
        Assert.Equal(EditorHistoryStatus.InvalidToken, history.TryMoveTo(beforePrune).Status);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void PropertiesAreEnumerableAndListElementChildrenStayStagedThroughStructuralEdits()
    {
        var resident = Character("hero", 10, new Skill("base", 1));
        var history = new EditorUndoHistory();
        using var serialized = Serialized(Binding(), history, Target("hero", resident));

        Assert.Equal(serialized.Properties, serialized.ToArray());
        Assert.Equal(["name", "stats.hp", "skills", "target", "code"], serialized.Properties.Select(item => item.PropertyPath));
        Assert.Equal("Immutable authoring code.", serialized.FindProperty("code").Tooltip);
        Assert.True(serialized.FindProperty("code").IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => serialized.FindProperty("code").BoxedValue = "changed");

        var skills = serialized.FindProperty("skills");
        Assert.True(skills.IsArray);
        Assert.Equal(1, skills.ArraySize);
        skills.InsertArrayElementAtIndex(1, new Skill("slash", 2));
        skills.MoveArrayElement(1, 0);
        var first = skills.GetArrayElementAtIndex(0);
        Assert.True(first.HasChildren);
        Assert.Equal(["Id", "Level"], first.Children.Select(item => item.DisplayName));
        var level = Assert.IsType<EditorSerializedProperty>(first.FindRelativeProperty("Level"));
        Assert.Equal("skills[0].Level", level.PropertyPath);
        level.BoxedValue = 3;
        skills.DeleteArrayElementAtIndex(1);
        skills.ArraySize = 2;
        var added = skills.GetArrayElementAtIndex(1);
        added.FindRelativeProperty("Id")!.BoxedValue = "guard";
        added.FindRelativeProperty("Level")!.BoxedValue = 1;

        Assert.Equal("base", Assert.Single(resident.Skills).Id);
        Assert.Equal(EditorApplyStatus.Applied, serialized.ApplyModifiedProperties("Edit skill list").Status);
        Assert.Equal(["slash", "guard"], resident.Skills.Select(item => item.Id));
        Assert.Equal([3, 1], resident.Skills.Select(item => item.Level));

        var undo = history.TryUndo();

        Assert.Equal(EditorHistoryStatus.Applied, undo.Status);
        Assert.Equal(["hero"], undo.AppliedTargets.Select(item => item.Identity));
        Assert.Equal("base", Assert.Single(resident.Skills).Id);
        Assert.False(serialized.HasModifiedProperties);
        Assert.Equal("base", Assert.Single(Assert.IsType<List<Skill>>(skills.BoxedValue)).Id);
    }

    [Fact]
    public void ReferenceAdapterValidatesConstraintAndOnlyWritesThroughApply()
    {
        var resident = Character("hero", 10, target: new RowReference("unit", "enemy"));
        using var serialized = Serialized(Binding(), new EditorUndoHistory(), Target("hero", resident));
        var reference = serialized.FindProperty("target");

        Assert.True(reference.HasReferenceAdapter);
        Assert.Equal("unit", reference.ReferenceConstraint!.Table);
        Assert.Null(reference.ReferenceConstraint.Group);
        Assert.Equal(["unit"], reference.ReferenceConstraint.AllowedTables);
        Assert.Equal(new EditorReferenceValue("unit", "enemy"), reference.ReferenceValue);

        reference.ReferenceValue = new EditorReferenceValue("unit", "boss");

        Assert.Equal(new RowReference("unit", "enemy"), resident.Target);
        Assert.Equal(new EditorReferenceValue("unit", "boss"), reference.ReferenceValue);
        Assert.Throws<ArgumentException>(
            () => reference.ReferenceValue = new EditorReferenceValue("skill", "slash"));
        Assert.Equal(EditorApplyStatus.Applied, serialized.ApplyModifiedProperties("Retarget").Status);
        Assert.Equal(new RowReference("unit", "boss"), resident.Target);
    }

    [Fact]
    public void HistoryTokensMoveSafelyAndBranchingInvalidatesOldCoordinates()
    {
        var resident = Character("hero", 0);
        var history = new EditorUndoHistory();
        using var serialized = Serialized(Binding(), history, Target("hero", resident));
        var hp = serialized.FindProperty("stats.hp");
        var zero = history.CaptureToken();

        hp.BoxedValue = 1;
        serialized.ApplyModifiedProperties("One");
        var one = history.CaptureToken();
        hp.BoxedValue = 2;
        serialized.ApplyModifiedProperties("Two");
        var two = history.CaptureToken();

        Assert.Equal(zero.Generation, two.Generation);
        var toZero = history.TryMoveTo(zero);
        Assert.Equal(EditorHistoryStatus.Applied, toZero.Status);
        Assert.Equal(["hero"], toZero.AppliedTargets.Select(item => item.Identity));
        Assert.Equal(0, resident.Stats.Hp);
        Assert.False(serialized.HasModifiedProperties);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryRestore(two).Status);
        Assert.Equal(2, resident.Stats.Hp);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryMoveTo(one).Status);
        Assert.Equal(1, resident.Stats.Hp);

        hp.BoxedValue = 9;
        serialized.ApplyModifiedProperties("Branch");

        Assert.True(history.Generation > two.Generation);
        Assert.Equal(EditorHistoryStatus.InvalidToken, history.TryMoveTo(two).Status);
        Assert.Equal(9, resident.Stats.Hp);
    }

    [Fact]
    public void GroupedMultiTargetUndoReportsEveryResidentThatChanged()
    {
        var first = Character("one", 1);
        var second = Character("two", 2);
        var history = new EditorUndoHistory();
        using var serialized = Serialized(
            Binding(),
            history,
            Target("one", first),
            Target("two", second));

        using (history.BeginGroup("Batch"))
        {
            serialized.FindProperty("name").BoxedValue = "shared";
            serialized.ApplyModifiedProperties("Rename");
            serialized.FindProperty("stats.hp").BoxedValue = 5;
            serialized.ApplyModifiedProperties("Health");
        }

        var undo = history.TryUndo();

        Assert.Equal(EditorHistoryStatus.Applied, undo.Status);
        Assert.Equal(["one", "two"], undo.AppliedTargets.Select(item => item.Identity));
        Assert.Equal("one", first.Name);
        Assert.Equal("two", second.Name);
        Assert.Equal(1, first.Stats.Hp);
        Assert.Equal(2, second.Stats.Hp);
        Assert.False(serialized.HasModifiedProperties);
    }

    [Fact]
    public void OverlappingContainerAndLeafBindingsAreRejected()
    {
        var container = EditorPropertyBinding.Create<CharacterAsset, CharacterStats>(
            10, [10], "stats", "Stats", EditorPropertyKind.Object,
            static value => value.Stats,
            static (value, next) => value.Stats = next);
        var leaf = EditorPropertyBinding.CreateFromMemberPath(
            11, [10, 1], "stats.hp", "Stats.Hp", typeof(CharacterAsset), EditorPropertyKind.Scalar);

        var error = Assert.Throws<ArgumentException>(
            () => EditorObjectBinding.Create<CharacterAsset>([container, leaf]));

        Assert.Contains("patch the same object graph twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleTargetApplyRollsBackEarlierPropertiesWhenALaterSetterThrows()
    {
        var resident = Character("hero", 10);
        var history = new EditorUndoHistory();
        var binding = FaultBinding((_, next) => next == 99);
        using var serialized = Serialized(binding, history, Target("hero", resident));
        serialized.FindProperty("name").BoxedValue = "changed";
        serialized.FindProperty("stats.hp").BoxedValue = 99;

        Assert.Throws<FaultingSetterException>(
            () => serialized.ApplyModifiedProperties("Faulting apply"));

        Assert.Equal("hero", resident.Name);
        Assert.Equal(10, resident.Stats.Hp);
        Assert.True(serialized.HasModifiedProperties);
        Assert.False(serialized.IsDirty);
        Assert.Equal(0, history.UndoCount);
    }

    [Fact]
    public void MultiTargetApplyRollsBackPriorTargetsWhenALaterTargetSetterThrows()
    {
        var first = Character("first", 10);
        var second = Character("second", 20);
        second.Code = "fail";
        var history = new EditorUndoHistory();
        var binding = FaultBinding((asset, next) => asset.Code == "fail" && next == 99);
        using var serialized = Serialized(
            binding,
            history,
            Target("first", first),
            Target("second", second));
        serialized.FindProperty("name").BoxedValue = "changed";
        serialized.FindProperty("stats.hp").BoxedValue = 99;

        Assert.Throws<FaultingSetterException>(
            () => serialized.ApplyModifiedProperties("Faulting multi-edit"));

        Assert.Equal("first", first.Name);
        Assert.Equal(10, first.Stats.Hp);
        Assert.Equal("second", second.Name);
        Assert.Equal(20, second.Stats.Hp);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(0, history.RedoCount);
    }

    [Fact]
    public void UndoAndRedoKeepResidentAndStacksUnchangedWhenASetterThrows()
    {
        var resident = Character("hero", 10);
        int? rejectedValue = null;
        var binding = FaultBinding((_, next) => rejectedValue == next);
        var history = new EditorUndoHistory();
        using var serialized = Serialized(binding, history, Target("hero", resident));
        serialized.FindProperty("name").BoxedValue = "changed";
        serialized.FindProperty("stats.hp").BoxedValue = 20;
        serialized.ApplyModifiedProperties("Successful edit");

        rejectedValue = 10;
        Assert.Throws<FaultingSetterException>(() => history.TryUndo());
        Assert.Equal("changed", resident.Name);
        Assert.Equal(20, resident.Stats.Hp);
        Assert.Equal(1, history.UndoCount);
        Assert.Equal(0, history.RedoCount);

        rejectedValue = null;
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal("hero", resident.Name);
        Assert.Equal(10, resident.Stats.Hp);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(1, history.RedoCount);

        rejectedValue = 20;
        Assert.Throws<FaultingSetterException>(() => history.TryRedo());
        Assert.Equal("hero", resident.Name);
        Assert.Equal(10, resident.Stats.Hp);
        Assert.Equal(0, history.UndoCount);
        Assert.Equal(1, history.RedoCount);
    }

    [Fact]
    public void RollbackSetterFailureProducesExplicitUncertainStateException()
    {
        var resident = Character("hero", 10);
        var binding = FaultBinding((_, _) => true);
        using var serialized = Serialized(binding, new EditorUndoHistory(), Target("hero", resident));
        serialized.FindProperty("name").BoxedValue = "changed";
        serialized.FindProperty("stats.hp").BoxedValue = 20;

        var error = Assert.Throws<EditorAtomicApplyException>(
            () => serialized.ApplyModifiedProperties("Unrecoverable setter"));

        Assert.IsType<FaultingSetterException>(error.ApplyException);
        Assert.Contains(error.RollbackExceptions, item => item is FaultingSetterException);
        Assert.Contains("state may be uncertain", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondSerializedObjectCannotOverwriteAResidentChangedByFirstInspector()
    {
        var resident = Character("hero", 10);
        var binding = Binding();
        var target = Target("hero", resident);
        using var first = Serialized(binding, new EditorUndoHistory(), target);
        using var second = Serialized(binding, new EditorUndoHistory(), target);
        second.FindProperty("stats.hp").BoxedValue = 30;

        first.FindProperty("stats.hp").BoxedValue = 20;
        Assert.Equal(EditorApplyStatus.Applied, first.ApplyModifiedProperties("First inspector").Status);
        var result = second.ApplyModifiedProperties("Stale second inspector");

        Assert.Equal(EditorApplyStatus.StateConflict, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(["hero"], result.ConflictingTargets);
        Assert.Equal(20, resident.Stats.Hp);
        Assert.True(second.HasModifiedProperties);
        Assert.Equal(0, second.History.UndoCount);
    }

    [Fact]
    public void DirectResidentMutationProducesStateConflictWithoutTreatingItAsAStagedEdit()
    {
        var resident = Character("hero", 10);
        using var serialized = Serialized(Binding(), new EditorUndoHistory(), Target("hero", resident));

        resident.Stats.Hp = 77;

        Assert.False(serialized.HasModifiedProperties);
        serialized.FindProperty("name").BoxedValue = "staged";
        var result = serialized.ApplyModifiedProperties("Reject external overwrite");

        Assert.Equal(EditorApplyStatus.StateConflict, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal("hero", resident.Name);
        Assert.Equal(77, resident.Stats.Hp);
        Assert.Equal(0, serialized.History.UndoCount);
    }

    [Fact]
    public void MultiTargetStateConflictPreflightRejectsEveryWriteAtomically()
    {
        var first = Character("one", 1);
        var second = Character("two", 2);
        using var serialized = Serialized(
            Binding(),
            new EditorUndoHistory(),
            Target("one", first),
            Target("two", second));
        serialized.FindProperty("name").BoxedValue = "shared";
        second.Stats.Hp = 99;

        var result = serialized.ApplyModifiedProperties("Atomic conflict");

        Assert.Equal(EditorApplyStatus.StateConflict, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(["two"], result.ConflictingTargets);
        Assert.Equal("one", first.Name);
        Assert.Equal("two", second.Name);
        Assert.Equal(1, first.Stats.Hp);
        Assert.Equal(99, second.Stats.Hp);
        Assert.Equal(0, serialized.History.UndoCount);
    }

    [Fact]
    public void StructsContainingListsAndShallowICloneableObjectsNeverAliasSnapshots()
    {
        var resident = new StructAsset
        {
            Payload = new MutableStructPayload { Values = [1] },
        };
        var binding = EditorObjectBinding.Create<StructAsset>(
        [
            EditorPropertyBinding.Create<StructAsset, MutableStructPayload>(
                201,
                [201],
                "payload",
                nameof(StructAsset.Payload),
                EditorPropertyKind.Object,
                static value => value.Payload,
                static (value, next) => value.Payload = next),
        ]);
        var history = new EditorUndoHistory();
        using var serialized = new EditorSerializedObject(
            binding,
            [new EditorEditTarget("struct", resident, static () => "r1")],
            history);

        var detached = Assert.IsType<MutableStructPayload>(serialized.FindProperty("payload").BoxedValue);
        Assert.NotSame(resident.Payload.Values, detached.Values);
        Assert.True(EditorValueComparer.StructuralEquals(resident.Payload, detached));
        detached.Values.Add(99);
        Assert.Equal([1], resident.Payload.Values);

        var stagedValues = new List<int> { 2 };
        serialized.FindProperty("payload").BoxedValue = new MutableStructPayload { Values = stagedValues };
        stagedValues.Add(88);
        Assert.Equal(EditorApplyStatus.Applied, serialized.ApplyModifiedProperties("Edit mutable struct").Status);
        Assert.Equal([2], resident.Payload.Values);
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal([1], resident.Payload.Values);

        var shallow = new ShallowClonePayload { Values = [3] };
        var cloned = Assert.IsType<ShallowClonePayload>(EditorValueCloner.Clone(shallow));
        Assert.NotSame(shallow.Values, cloned.Values);
        cloned.Values.Add(4);
        Assert.Equal([3], shallow.Values);

        var readonlyStruct = new ReadonlyMutableStructPayload([5]);
        var readonlyClone = Assert.IsType<ReadonlyMutableStructPayload>(
            EditorValueCloner.Clone(readonlyStruct));
        Assert.NotSame(readonlyStruct.Values, readonlyClone.Values);
        readonlyClone.Values.Add(6);
        Assert.Equal([5], readonlyStruct.Values);
    }

    private static EditorSerializedObject Serialized(
        EditorObjectBinding binding,
        EditorUndoHistory history,
        params EditorEditTarget[] targets)
    {
        return new EditorSerializedObject(binding, targets, history);
    }

    private static EditorEditTarget Target(
        string identity,
        CharacterAsset resident,
        Func<string>? revision = null)
    {
        return new EditorEditTarget(identity, resident, revision ?? (() => "r1"));
    }

    private static EditorObjectBinding Binding()
    {
        return EditorObjectBinding.Create<CharacterAsset>(
        [
            EditorPropertyBinding.Create<CharacterAsset, string>(
                1, [1], "name", "Name", EditorPropertyKind.Scalar,
                static value => value.Name,
                static (value, next) => value.Name = next),
            EditorPropertyBinding.CreateFromMemberPath(
                2, [2, 1], "stats.hp", "Stats.Hp", typeof(CharacterAsset), EditorPropertyKind.Scalar),
            EditorPropertyBinding.Create<CharacterAsset, List<Skill>>(
                3, [3], "skills", "Skills", EditorPropertyKind.List,
                static value => value.Skills,
                static (value, next) => value.Skills = next),
            EditorPropertyBinding.Create<CharacterAsset, RowReference>(
                4, [4], "target", "Target", EditorPropertyKind.Reference,
                static value => value.Target,
                static (value, next) => value.Target = next,
                reference: new EditorReferenceAdapter(
                    new EditorReferenceConstraint("unit", allowNull: false),
                    static value =>
                    {
                        var row = (RowReference)value!;
                        return string.IsNullOrEmpty(row.Table) || string.IsNullOrEmpty(row.Key)
                            ? null
                            : new EditorReferenceValue(row.Table, row.Key);
                    },
                    static value => value == null
                        ? default(RowReference)
                        : new RowReference(value.Table, value.TargetIdentity))),
            EditorPropertyBinding.Create<CharacterAsset, string>(
                5, [5], "code", "Code", EditorPropertyKind.Scalar,
                static value => value.Code,
                static (value, next) => value.Code = next,
                tooltip: "Immutable authoring code.",
                isReadOnly: true),
        ]);
    }

    private static EditorObjectBinding FaultBinding(Func<CharacterAsset, int, bool> shouldThrow)
    {
        return EditorObjectBinding.Create<CharacterAsset>(
        [
            EditorPropertyBinding.Create<CharacterAsset, string>(
                101, [101], "name", "Name", EditorPropertyKind.Scalar,
                static value => value.Name,
                static (value, next) => value.Name = next),
            EditorPropertyBinding.Create<CharacterAsset, int>(
                102, [102], "stats.hp", "Stats.Hp", EditorPropertyKind.Scalar,
                static value => value.Stats.Hp,
                (value, next) =>
                {
                    if (shouldThrow(value, next))
                        throw new FaultingSetterException();
                    value.Stats.Hp = next;
                }),
        ]);
    }

    private static CharacterAsset Character(
        string name,
        int hp,
        Skill? skill = null,
        RowReference target = default)
    {
        return new CharacterAsset
        {
            Name = name,
            Stats = new CharacterStats { Hp = hp },
            Skills = skill == null ? [] : [skill],
            Target = target,
        };
    }

    public sealed class CharacterAsset
    {
        public string Name { get; set; } = string.Empty;

        public CharacterStats Stats { get; set; } = new();

        public List<Skill> Skills { get; set; } = [];

        public RowReference Target { get; set; }

        public string Code { get; set; } = "fixed";
    }

    public sealed class CharacterStats
    {
        public int Hp { get; set; }
    }

    public sealed class Skill
    {
        public Skill()
        {
        }

        public Skill(string id, int level)
        {
            Id = id;
            Level = level;
        }

        public string Id { get; set; } = string.Empty;

        public int Level { get; set; }
    }

    public sealed class StructAsset
    {
        public MutableStructPayload Payload { get; set; }
    }

    public struct MutableStructPayload
    {
        public List<int> Values { get; set; }
    }

    public sealed class ShallowClonePayload : ICloneable
    {
        public List<int> Values { get; set; } = [];

        public object Clone() => MemberwiseClone();
    }

    public readonly struct ReadonlyMutableStructPayload
    {
        public ReadonlyMutableStructPayload(List<int> values)
        {
            Values = values;
        }

        public List<int> Values { get; }
    }

    public readonly struct RowReference : IEquatable<RowReference>
    {
        public RowReference(string table, string key)
        {
            Table = table;
            Key = key;
        }

        public string? Table { get; }

        public string? Key { get; }

        public bool Equals(RowReference other)
        {
            return string.Equals(Table, other.Table, StringComparison.Ordinal)
                && string.Equals(Key, other.Key, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj) => obj is RowReference other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Table, Key);
    }

    private sealed class FaultingSetterException : InvalidOperationException
    {
    }
}
