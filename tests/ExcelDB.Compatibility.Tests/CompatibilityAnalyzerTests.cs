using System.Collections.Immutable;
using ExcelDb.Schema.Descriptors;

namespace ExcelDb.Compatibility.Tests;

public sealed class CompatibilityAnalyzerTests
{
    [Fact]
    public void SameNumericIdentityRename_IsSafe()
    {
        var previous = Schema(Table("game.Hero", Field(1, "name")));
        var current = Schema(Table("game.Character", Field(1, "display_name")));

        var report = new CompatibilityAnalyzer().Analyze(current, previous);

        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.TableRenamed && item.Severity == CompatibilitySeverity.Safe);
        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.FieldRenamed && item.Severity == CompatibilitySeverity.Safe);
        Assert.False(report.HasBlockers);
    }

    [Fact]
    public void RemovingPublishedTableWithoutTombstone_IsBlocker()
    {
        var previous = Schema(Table("game.Hero", Field(1, "name")));
        var current = Schema();

        var report = new CompatibilityAnalyzer().Analyze(current, previous);

        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.TableTombstoneMissing && item.Severity == CompatibilitySeverity.Blocker);
    }

    [Theory]
    [InlineData(true, CompatibilitySeverity.Warning)]
    [InlineData(false, CompatibilitySeverity.Blocker)]
    public void RemovedField_RequiresReservedNumber(bool reserved, CompatibilitySeverity severity)
    {
        var previous = Schema(Table("game.Hero", Field(1, "name")));
        var currentTable = Table("game.Hero") with
        {
            ReservedFieldNumbers = reserved ? [new CanonicalReservedNumberRange(1, 2)] : [],
        };

        var report = new CompatibilityAnalyzer().Analyze(Schema(currentTable), previous);

        Assert.Contains(report.Entries, item => item.Severity == severity && item.Location.FieldIdPath.SequenceEqual([1]));
    }

    [Fact]
    public void ExportTargetDelta_ExpandsPerField()
    {
        var previous = Schema(TableWithTargets("game.Hero", ["client", "server"], Field(1, "name", ["client", "server"])));
        var current = Schema(TableWithTargets("game.Hero", ["client", "lite-client"], Field(1, "name", ["client", "lite-client"])));

        var report = new CompatibilityAnalyzer().Analyze(current, previous);

        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.ExportTargetAdded && item.Location.ExportTarget == "lite-client" && item.Location.FieldIdPath.SequenceEqual([1]));
        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.ExportTargetRemoved && item.Location.ExportTarget == "server" && item.Location.FieldIdPath.SequenceEqual([1]));
    }

    [Fact]
    public void KeyChangeOnNonEmptyTable_IsBlocker()
    {
        var previous = Schema(Table("game.Hero", Field(1, "id") with { KeyOrder = 1 }) with { KeyFieldIds = [1] });
        var current = Schema(Table("game.Hero", Field(1, "id"), Field(2, "code") with { KeyOrder = 1 }) with { KeyFieldIds = [2] });
        var facts = new CompatibilityDataFacts(ImmutableHashSet.Create(101));

        var report = new CompatibilityAnalyzer().Analyze(current, previous, facts);

        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.KeyChanged && item.Severity == CompatibilitySeverity.Blocker);
    }

    [Fact]
    public void MissingHistory_AllowsReadOnlyButBlocksDestructiveApply()
    {
        var report = new CompatibilityAnalyzer().Analyze(Schema(Table("game.Hero")), null);

        Assert.True(CompatibilityGate.Evaluate(report, CompatibilityOperation.ImportCheck).Allowed);
        Assert.False(CompatibilityGate.Evaluate(
            report,
            CompatibilityOperation.DestructiveApply,
            hasFreshPlan: true,
            hasMigrationEvidence: true,
            hasExplicitDataLossConfirmation: true).Allowed);
    }

    private static CanonicalSchemaDescriptor Schema(params CanonicalTableDescriptor[] tables) =>
        new([.. tables], [], [], (ulong)tables.Length + 1);

    private static CanonicalTableDescriptor Table(
        string fullName,
        params CanonicalFieldDescriptor[] fields) => TableWithTargets(fullName, ["client", "server"], fields);

    private static CanonicalTableDescriptor TableWithTargets(
        string fullName,
        ImmutableArray<string> targets,
        params CanonicalFieldDescriptor[] fields) =>
        new(
            101,
            fullName[(fullName.LastIndexOf('.') + 1)..],
            fullName,
            CanonicalTableKind.Asset,
            "Hero",
            [],
            [],
            targets,
            [],
            [.. fields],
            fields.Where(static item => item.KeyOrder > 0).Select(static item => item.Id).ToImmutableArray());

    private static CanonicalFieldDescriptor Field(
        int id,
        string name,
        ImmutableArray<string> targets = default) =>
        new(
            id,
            name,
            name,
            CanonicalFieldShape.Scalar,
            "string",
            true,
            null,
            null,
            false,
            null,
            null,
            null,
            null,
            false,
            0,
            null,
            null,
            CanonicalDeletePolicy.Block,
            CanonicalExpandMode.SingleCell,
            false,
            null,
            null,
            null,
            null,
            targets.IsDefault ? ["client", "server"] : targets,
            [],
            []);
}
