using System.Collections.Immutable;
using ExcelDb.Compatibility.Migrations;
using ExcelDb.Core.Values;

namespace ExcelDb.Compatibility.Tests;

public sealed class MigrationTests
{
    private const ulong SourceHash = 0x11;
    private const ulong TargetHash = 0x22;

    [Fact]
    public void RegistryRejectsDuplicateVersionedMigrationKeys()
    {
        var migration = new PrefixMigration();

        var exception = Assert.Throws<ArgumentException>(() =>
            new MigrationRegistry([migration, migration]));

        Assert.Contains("prefix@1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlannerConvertsToNewNumericFieldAndRetainsSource()
    {
        var source = Workbook("book", "r1", SourceHash, Cell("row-1", 1, "old"));

        var plan = Plan([source]);

        Assert.True(plan.CanApply);
        Assert.True(MigrationPlanSerializer.Verify(plan));
        Assert.Equal(1, plan.ConvertedCount);
        var candidate = Assert.Single(plan.Workbooks).Candidate;
        Assert.Contains(candidate.Cells, item =>
            item.Address.FieldIdPath.SequenceEqual([1]) && item.Value == CanonicalValue.FromValue("old"));
        Assert.Contains(candidate.Cells, item =>
            item.Address.FieldIdPath.SequenceEqual([2]) && item.Value == CanonicalValue.FromValue("new:old"));
        Assert.Contains(new MigrationKey("prefix", 1), candidate.AppliedMigrations);
        Assert.Equal(TargetHash, candidate.SchemaHash);
    }

    [Fact]
    public void PlannerCanTargetAnotherNumericTableForExplicitSplitOrMerge()
    {
        var source = Workbook("book", "r1", SourceHash, Cell("row-1", 1, "old"));
        var key = new MigrationKey("prefix", 1);
        var step = new MigrationStep(1, key, 101, [1], [1], DataLossRisk: true)
        {
            TargetTableId = 202,
        };

        var plan = MigrationPlanner.Create(
            SourceHash,
            TargetHash,
            [step],
            [source],
            new MigrationRegistry([new PrefixMigration()]));

        Assert.True(plan.CanApply);
        Assert.Contains(Assert.Single(plan.Workbooks).Candidate.Cells, item =>
            item.Address.TableId == 202
            && item.Address.FieldIdPath.SequenceEqual([1])
            && item.Value == CanonicalValue.FromValue("new:old"));
    }

    [Fact]
    public void PlannerFailureRetainsEveryOriginalWorkbookCandidate()
    {
        var good = Workbook("a", "r1", SourceHash, Cell("row-1", 1, "good"));
        var bad = Workbook("b", "r2", SourceHash, Cell("row-2", 1, "bad"));
        var registry = new MigrationRegistry([new SelectiveMigration()]);
        var step = Step(new MigrationKey("selective", 1));

        var plan = MigrationPlanner.Create(SourceHash, TargetHash, [step], [good, bad], registry);

        Assert.False(plan.CanApply);
        Assert.Equal(1, plan.ErrorCount);
        Assert.Contains(plan.Diagnostics, item => item.Code == "migration.transform.error");
        Assert.All(plan.Workbooks, item =>
            Assert.True(MigrationSnapshotComparer.SemanticallyEquals(item.Expected, item.Candidate)));
        Assert.Contains(plan.Workbooks.Single(item => item.Expected.WorkbookId == "b").Expected.Cells,
            item => item.Value == CanonicalValue.FromValue("bad"));
    }

    [Fact]
    public void PlannerPreservesInvalidAndDoesNotImplicitlyMaterializeDefaults()
    {
        var invalid = new MigrationCell(
            new MigrationCellAddress(101, "invalid", [1]),
            CanonicalValue.Invalid("not-parseable"));
        var defaulted = new MigrationCell(
            new MigrationCellAddress(101, "defaulted", [1]),
            CanonicalValue.FromDefault("schema-default"));

        var invalidPlan = Plan([Workbook("book", "r1", SourceHash, invalid)]);
        var defaultPlan = Plan([Workbook("book", "r1", SourceHash, defaulted)]);

        Assert.False(invalidPlan.CanApply);
        Assert.Contains(invalidPlan.Diagnostics, item => item.Code == "migration.source.invalid");
        Assert.True(MigrationSnapshotComparer.SemanticallyEquals(
            Assert.Single(invalidPlan.Workbooks).Expected,
            Assert.Single(invalidPlan.Workbooks).Candidate));
        Assert.True(defaultPlan.CanApply);
        Assert.Equal(0, defaultPlan.ConvertedCount);
        Assert.Equal(1, defaultPlan.SkippedCount);
        Assert.DoesNotContain(Assert.Single(defaultPlan.Workbooks).Candidate.Cells, item =>
            item.Address.FieldIdPath.SequenceEqual([2]));
    }

    [Fact]
    public void PlannerAndMachineReportAreDeterministicAcrossInputOrder()
    {
        var first = Workbook(
            "a",
            "r1",
            SourceHash,
            Cell("row-2", 1, "two"),
            Cell("row-1", 1, "one"));
        var firstReordered = first with { Cells = [.. first.Cells.Reverse()] };
        var second = Workbook("b", "r2", SourceHash, Cell("row-3", 1, "three"));

        var left = Plan([second, first]);
        var right = Plan([firstReordered, second]);

        Assert.Equal(left.PlanHash, right.PlanHash);
        Assert.Equal(MigrationPlanSerializer.Serialize(left), MigrationPlanSerializer.Serialize(right));

        var leftReport = MigrationCoordinator.Execute(
            left,
            left.Workbooks.Select(static item => new FakeParticipant(item.Expected)));
        var rightReport = MigrationCoordinator.Execute(
            right,
            right.Workbooks.Reverse().Select(static item => new FakeParticipant(item.Expected)));

        Assert.True(leftReport.Succeeded);
        Assert.Equal(leftReport.ReportHash, rightReport.ReportHash);
        Assert.Equal(
            MigrationExecutionReportSerializer.Serialize(leftReport),
            MigrationExecutionReportSerializer.Serialize(rightReport));
        Assert.True(MigrationExecutionReportSerializer.Verify(leftReport));
    }

    [Fact]
    public void CrossWorkbookMigrationCommitsAsOneClosedTransactionAndRerunsIdempotently()
    {
        var plan = Plan(
        [
            Workbook("a", "r1", SourceHash, Cell("row-1", 1, "one")),
            Workbook("b", "r2", SourceHash, Cell("row-2", 1, "two")),
        ]);
        var participants = plan.Workbooks
            .Select(static item => new FakeParticipant(item.Expected))
            .ToArray();

        var first = MigrationCoordinator.Execute(plan, participants);

        Assert.True(first.Succeeded);
        Assert.True(first.Applied);
        Assert.All(participants, item =>
        {
            Assert.Equal(1, item.PrepareCount);
            Assert.Equal(1, item.CommitCount);
            Assert.Equal(TargetHash, item.Current.SchemaHash);
        });

        var rerun = Plan(participants.Select(static item => item.Current));
        var prepareCounts = participants.Select(static item => item.PrepareCount).ToArray();
        var second = MigrationCoordinator.Execute(rerun, participants);

        Assert.True(second.Succeeded);
        Assert.False(second.Applied);
        Assert.Equal(prepareCounts, participants.Select(static item => item.PrepareCount));
    }

    [Fact]
    public void StaleWorkbookAbortsBeforeAnyPrepareOrWrite()
    {
        var plan = Plan(
        [
            Workbook("a", "r1", SourceHash, Cell("row-1", 1, "one")),
            Workbook("b", "r2", SourceHash, Cell("row-2", 1, "two")),
        ]);
        var first = new FakeParticipant(plan.Workbooks[0].Expected);
        var second = new FakeParticipant(plan.Workbooks[1].Expected with { Revision = "changed-after-plan" });

        var report = MigrationCoordinator.Execute(plan, [first, second]);

        Assert.False(report.Succeeded);
        Assert.False(report.Applied);
        Assert.Contains(report.Diagnostics, item => item.Code == "migration.apply.stale");
        Assert.Equal(0, first.PrepareCount);
        Assert.Equal(0, second.PrepareCount);
        Assert.True(MigrationSnapshotComparer.SemanticallyEquals(plan.Workbooks[0].Expected, first.Current));
    }

    [Fact]
    public void LaterCommitFailureRollsBackEveryWorkbookIncludingEarlierCommit()
    {
        var plan = Plan(
        [
            Workbook("a", "r1", SourceHash, Cell("row-1", 1, "one")),
            Workbook("b", "r2", SourceHash, Cell("row-2", 1, "two")),
        ]);
        var first = new FakeParticipant(plan.Workbooks[0].Expected);
        var second = new FakeParticipant(plan.Workbooks[1].Expected) { FailCommit = true };

        var report = MigrationCoordinator.Execute(plan, [first, second]);

        Assert.False(report.Succeeded);
        Assert.False(report.Applied);
        Assert.True(report.RollbackAttempted);
        Assert.True(report.RolledBack);
        Assert.Equal(1, first.RollbackCount);
        Assert.Equal(1, second.RollbackCount);
        Assert.True(MigrationSnapshotComparer.SemanticallyEquals(plan.Workbooks[0].Expected, first.Current));
        Assert.True(MigrationSnapshotComparer.SemanticallyEquals(plan.Workbooks[1].Expected, second.Current));
    }

    [Fact]
    public void DataLossPlanRequiresBoundExplicitConfirmation()
    {
        var source = Workbook("book", "r1", SourceHash, Cell("row-1", 1, "old"));
        var registry = new MigrationRegistry([new PrefixMigration()]);
        var plan = MigrationPlanner.Create(
            SourceHash,
            TargetHash,
            [Step(new MigrationKey("prefix", 1)) with { DataLossRisk = true }],
            [source],
            registry);
        var participant = new FakeParticipant(Assert.Single(plan.Workbooks).Expected);

        var blocked = MigrationCoordinator.Execute(plan, [participant]);
        var allowed = MigrationCoordinator.Execute(
            plan,
            [participant],
            new MigrationExecutionOptions(ConfirmDataLoss: true));

        Assert.False(blocked.Succeeded);
        Assert.Contains(blocked.Diagnostics, item => item.Code == "migration.apply.data-loss-confirmation");
        Assert.Equal(1, participant.PrepareCount);
        Assert.True(allowed.Succeeded);
        Assert.True(allowed.Applied);
    }

    [Fact]
    public void TamperedPlanHashIsRejectedBeforePrepare()
    {
        var plan = Plan([Workbook("book", "r1", SourceHash, Cell("row-1", 1, "old"))]);
        var tampered = plan with { TargetSchemaHash = 0x99 };
        var participant = new FakeParticipant(Assert.Single(plan.Workbooks).Expected);

        var report = MigrationCoordinator.Execute(tampered, [participant]);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Diagnostics, item => item.Code == "migration.apply.plan-hash");
        Assert.Equal(0, participant.PrepareCount);
    }

    private static MigrationPlan Plan(IEnumerable<MigrationWorkbookSnapshot> workbooks)
    {
        var key = new MigrationKey("prefix", 1);
        return MigrationPlanner.Create(
            SourceHash,
            TargetHash,
            [Step(key)],
            workbooks,
            new MigrationRegistry([new PrefixMigration()]));
    }

    private static MigrationStep Step(MigrationKey key) => new(1, key, 101, [1], [2]);

    private static MigrationWorkbookSnapshot Workbook(
        string id,
        string revision,
        ulong hash,
        params MigrationCell[] cells) =>
        new(id, revision, hash, [.. cells], ImmutableHashSet<MigrationKey>.Empty);

    private static MigrationCell Cell(string row, int fieldId, string value) =>
        new(new MigrationCellAddress(101, row, [fieldId]), CanonicalValue.FromValue(value));

    private sealed class PrefixMigration : ICanonicalValueMigration
    {
        public string Id => "prefix";

        public int Version => 1;

        public MigrationResult Transform(in MigrationContext context, CanonicalValue source) =>
            MigrationResult.Converted(CanonicalValue.FromValue($"new:{source.Text}"));
    }

    private sealed class SelectiveMigration : ICanonicalValueMigration
    {
        public string Id => "selective";

        public int Version => 1;

        public MigrationResult Transform(in MigrationContext context, CanonicalValue source) =>
            string.Equals(source.Text, "bad", StringComparison.Ordinal)
                ? MigrationResult.Error("The source value cannot be represented.")
                : MigrationResult.Converted(CanonicalValue.FromValue($"new:{source.Text}"));
    }

    private sealed class FakeParticipant : IMigrationWorkbookParticipant
    {
        public FakeParticipant(MigrationWorkbookSnapshot current) => Current = current;

        public string WorkbookId => Current.WorkbookId;

        public MigrationWorkbookSnapshot Current { get; private set; }

        public bool FailCommit { get; init; }

        public int PrepareCount { get; private set; }

        public int CommitCount { get; private set; }

        public int RollbackCount { get; private set; }

        public MigrationWorkbookSnapshot ReadCurrent() => Current;

        public IPreparedMigrationWorkbook Prepare(
            MigrationWorkbookSnapshot expected,
            MigrationWorkbookSnapshot candidate)
        {
            PrepareCount++;
            return new Prepared(this, expected, candidate);
        }

        private sealed class Prepared : IPreparedMigrationWorkbook
        {
            private readonly FakeParticipant _owner;
            private readonly MigrationWorkbookSnapshot _expected;
            private readonly MigrationWorkbookSnapshot _candidate;

            public Prepared(
                FakeParticipant owner,
                MigrationWorkbookSnapshot expected,
                MigrationWorkbookSnapshot candidate)
            {
                _owner = owner;
                _expected = expected;
                _candidate = candidate;
            }

            public string WorkbookId => _owner.WorkbookId;

            public void Commit()
            {
                _owner.CommitCount++;
                _owner.Current = _candidate;
                if (_owner.FailCommit)
                    throw new IOException("Injected commit failure.");
            }

            public void Rollback()
            {
                _owner.RollbackCount++;
                _owner.Current = _expected;
            }

            public void Dispose()
            {
            }
        }
    }
}
