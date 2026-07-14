using System.Collections.Immutable;
using ExcelDb.Compatibility.History;
using ExcelDb.Compatibility.Publishing;
using ExcelDb.Core.Identity;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Hashing;

namespace ExcelDb.Compatibility.Tests;

public sealed class CompatibilityAdvancedTests
{
    [Fact]
    public void LiveTableCanBecomeRenamedRetiredTombstoneWithoutIdentityReuse()
    {
        var previous = Schema([Table("game.Hero")], [], 11);
        var current = Schema([], [new CanonicalRetiredTableDescriptor(101, "Character", "game.Character")], 12);

        var report = new CompatibilityAnalyzer().Analyze(current, previous);

        Assert.Contains(report.Entries, item =>
            item.Kind == CompatibilityChangeKind.TableRenamed
            && item.Severity == CompatibilitySeverity.Safe);
        Assert.Contains(report.Entries, item =>
            item.Kind == CompatibilityChangeKind.TableRetired
            && item.Severity == CompatibilitySeverity.Warning);
        Assert.DoesNotContain(report.Entries, item => item.Kind == CompatibilityChangeKind.TableIdentityReused);
        Assert.False(report.HasBlockers);
    }

    [Fact]
    public void SheetRenameUsesStableTableIdAndIsSafe()
    {
        var previousTable = Table("game.Hero");
        var currentTable = previousTable with { SheetName = "Characters" };

        var report = new CompatibilityAnalyzer().Analyze(
            Schema([currentTable], [], 12),
            Schema([previousTable], [], 11));

        Assert.Contains(report.Entries, item =>
            item.Kind == CompatibilityChangeKind.TableRenamed
            && item.Severity == CompatibilitySeverity.Safe
            && item.Location.TableId == 101);
    }

    [Fact]
    public void CellFormatWindowRequiresOldCanonicalInLegacy()
    {
        var csv = new CanonicalCellFormat("csv", [","], []);
        var json = new CanonicalCellFormat("json", [], [csv]);
        var previous = Schema([Table("game.Hero", Field(1, "tags", csv))], [], 11);
        var current = Schema([Table("game.Hero", Field(1, "tags", json))], [], 12);

        var report = new CompatibilityAnalyzer().Analyze(current, previous);

        Assert.Contains(report.Entries, item =>
            item.Kind == CompatibilityChangeKind.CellFormatMigrationStarted
            && item.Severity == CompatibilitySeverity.Warning);
        Assert.DoesNotContain(report.Entries, item => item.Kind == CompatibilityChangeKind.FieldSemanticChanged);
    }

    [Fact]
    public void CellFormatLegacyRemovalNeedsCompleteZeroHitEvidence()
    {
        var csv = new CanonicalCellFormat("csv", [","], []);
        var withLegacy = new CanonicalCellFormat("json", [], [csv]);
        var canonicalOnly = new CanonicalCellFormat("json", [], []);
        var previous = Schema([Table("game.Hero", Field(1, "tags", withLegacy))], [], 11);
        var current = Schema([Table("game.Hero", Field(1, "tags", canonicalOnly))], [], 12);

        var blocked = new CompatibilityAnalyzer().Analyze(current, previous);
        var safe = new CompatibilityAnalyzer().Analyze(
            current,
            previous,
            CompatibilityDataFacts.Empty with
            {
                HasCompleteWorkbookCoverage = true,
                LegacyFormatEvidence =
                [
                    new LegacyFormatEvidence(101, [1], true, 0, 0)
                    {
                        SchemaHash = 11,
                    },
                ],
            });

        Assert.Contains(blocked.Entries, item =>
            item.Kind == CompatibilityChangeKind.CellFormatLegacyRemoved
            && item.Severity == CompatibilitySeverity.Blocker);
        Assert.Contains(safe.Entries, item =>
            item.Kind == CompatibilityChangeKind.CellFormatLegacyRemoved
            && item.Severity == CompatibilitySeverity.Safe);
    }

    [Fact]
    public void NewCanonicalFormatCannotSilentlyDropAnOlderLegacyWindow()
    {
        var pipe = new CanonicalCellFormat("csv", ["|"], []);
        var comma = new CanonicalCellFormat("csv", [","], []);
        var previous = new CanonicalCellFormat("json", [], [pipe]);
        var current = new CanonicalCellFormat("yaml", [], [previous with { Legacy = [] }, comma]);
        var previousSchema = Schema([Table("game.Hero", Field(1, "tags", previous))], [], 11);
        var currentSchema = Schema([Table("game.Hero", Field(1, "tags", current))], [], 12);

        var report = new CompatibilityAnalyzer().Analyze(currentSchema, previousSchema);

        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.CellFormatMigrationStarted);
        Assert.Contains(report.Entries, item =>
            item.Kind == CompatibilityChangeKind.CellFormatLegacyRemoved
            && item.Severity == CompatibilitySeverity.Blocker);
    }

    [Fact]
    public void StaleLegacyZeroHitEvidenceCannotAuthorizeRemoval()
    {
        var csv = new CanonicalCellFormat("csv", [","], []);
        var previous = Schema([Table("game.Hero", Field(1, "tags", new CanonicalCellFormat("json", [], [csv])))], [], 11);
        var current = Schema([Table("game.Hero", Field(1, "tags", new CanonicalCellFormat("json", [], [])))], [], 12);
        var facts = new CompatibilityDataFacts([], HasCompleteWorkbookCoverage: true)
        {
            Workbooks = [new WorkbookCompatibilityFacts("book", "r2", 11)],
            LegacyFormatEvidence =
            [
                new LegacyFormatEvidence(101, [1], true, 0, 0)
                {
                    SchemaHash = 11,
                    WorkbookRevisions = ImmutableDictionary<string, string>.Empty
                        .WithComparers(StringComparer.Ordinal)
                        .Add("book", "r1"),
                },
            ],
        };

        var report = new CompatibilityAnalyzer().Analyze(current, previous, facts);

        Assert.Contains(report.Entries, item =>
            item.Kind == CompatibilityChangeKind.CellFormatLegacyRemoved
            && item.Severity == CompatibilitySeverity.Blocker);
    }

    [Fact]
    public void KeyChangeNeedsCompleteEvidenceBeforeItCanBeClassifiedEmptyAndSafe()
    {
        var previous = Schema([Table("game.Hero", Field(1, "id") with { KeyOrder = 1 })], [], 11);
        var current = Schema([Table("game.Hero", Field(2, "code") with { KeyOrder = 1 })], [], 12);

        var unknown = new CompatibilityAnalyzer().Analyze(current, previous);
        var provenEmpty = new CompatibilityAnalyzer().Analyze(
            current,
            previous,
            new CompatibilityDataFacts([], HasCompleteWorkbookCoverage: true));

        Assert.Contains(unknown.Entries, item =>
            item.Kind == CompatibilityChangeKind.KeyChanged
            && item.Severity == CompatibilitySeverity.Blocker);
        Assert.Contains(provenEmpty.Entries, item =>
            item.Kind == CompatibilityChangeKind.KeyChanged
            && item.Severity == CompatibilitySeverity.Safe);
    }

    [Fact]
    public void WorkbookFactsClassifyDriftMappingOwnershipAndParseFailures()
    {
        var previous = Schema([Table("game.Hero")], [], 11);
        var current = Schema([Table("game.Hero")], [], 12);
        var facts = new CompatibilityDataFacts([], false)
        {
            Workbooks =
            [
                new WorkbookCompatibilityFacts("old", "r1", 11),
                new WorkbookCompatibilityFacts(
                    "broken",
                    "r2",
                    99,
                    MappingComplete: false,
                    OwnershipSafe: false,
                    UnparseableValueCount: 2,
                    PendingMigrationCount: 1,
                    LegacyFormatHitCount: 3,
                    LegacyFormatErrorCount: 1),
            ],
        };

        var report = new CompatibilityAnalyzer().Analyze(current, previous, facts);

        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.WorkbookOutdated);
        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.WorkbookSchemaDrift);
        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.WorkbookMappingInvalid);
        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.WorkbookOwnershipConflict);
        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.UnparseableData);
        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.MigrationPending);
        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.LegacyFormatHits);
        Assert.Contains(report.Entries, item => item.Kind == CompatibilityChangeKind.WorkbookCoverageIncomplete);
    }

    [Fact]
    public void CompatibilityReportSerializationIsDeterministicAcrossFactOrder()
    {
        var previous = Schema([Table("game.Hero")], [], 11);
        var current = Schema([Table("game.Hero")], [], 12);
        var first = new WorkbookCompatibilityFacts("a", "r1", 11);
        var second = new WorkbookCompatibilityFacts("b", "r2", 99, UnparseableValueCount: 1);
        var analyzer = new CompatibilityAnalyzer();

        var left = analyzer.Analyze(current, previous, new CompatibilityDataFacts([], true) { Workbooks = [second, first] });
        var right = analyzer.Analyze(current, previous, new CompatibilityDataFacts([], true) { Workbooks = [first, second] });

        Assert.Equal(CompatibilityReportSerializer.Serialize(left), CompatibilityReportSerializer.Serialize(right));
        Assert.Equal(CompatibilityReportSerializer.ComputeHash(left), CompatibilityReportSerializer.ComputeHash(right));
    }

    [Fact]
    public void DuplicateOrMalformedWorkbookEvidenceIsABlocker()
    {
        var schema = Schema([Table("game.Hero")], [], 12);
        var facts = new CompatibilityDataFacts([], HasCompleteWorkbookCoverage: true)
        {
            Workbooks =
            [
                new WorkbookCompatibilityFacts("book", "r1", 12),
                new WorkbookCompatibilityFacts("book", "", 12, UnparseableValueCount: -1),
            ],
        };

        var report = new CompatibilityAnalyzer().Analyze(schema, schema with { SchemaHash = 11 }, facts);

        Assert.Contains(report.Entries, item =>
            item.Kind == CompatibilityChangeKind.WorkbookEvidenceInvalid
            && item.Severity == CompatibilitySeverity.Blocker);
    }

    [Fact]
    public void DescriptorHistoryIsCanonicalVersionedAndIdempotent()
    {
        var first = WithCanonicalHash(Schema([Table("game.Hero")], [], 0));
        var second = WithCanonicalHash(Schema([Table("game.Hero", Field(1, "name"))], [], 0));
        var history = new SchemaDescriptorHistory();

        var one = history.RecordPublished("r1", first);
        var again = history.RecordPublished("r1", first);
        var two = history.RecordPublished("r2", second);

        Assert.Same(one, again);
        Assert.Equal(one, history.PreviousOf(two.SchemaHash));
        Assert.True(history.TryGetRevision("r2", out var byRevision));
        Assert.Equal(two, byRevision);
        Assert.Throws<InvalidOperationException>(() => history.RecordPublished("r1", second));
        Assert.Throws<InvalidDataException>(() => history.RecordPublished("bad", second with { SchemaHash = 123 }));
    }

    [Fact]
    public void PublicationRequiresEveryTargetComponentAtOneFullSchemaHash()
    {
        var previous = WithCanonicalHash(Schema([TableWithTargets("game.Hero", ["client", "server"])], [], 0));
        var current = WithCanonicalHash(Schema([TableWithTargets("game.Hero", ["client", "server", "lite-client"])], [], 0));
        var report = new CompatibilityAnalyzer().Analyze(current, previous);
        var hash = current.SchemaHash;
        var complete = new TargetPublicationRequest(
            current,
            report,
            [Artifacts("client", hash), Artifacts("server", hash), Artifacts("lite-client", hash)],
            [],
            true);

        var allowed = TargetPublicationGate.Evaluate(complete);
        var staleUnaffectedTarget = TargetPublicationGate.Evaluate(complete with
        {
            Targets = [Artifacts("client", previous.SchemaHash), Artifacts("server", hash), Artifacts("lite-client", hash)],
        });
        var wrongTarget = TargetPublicationGate.Evaluate(complete with
        {
            Targets =
            [
                Artifacts("client", hash) with
                {
                    Bytes = new TargetArtifactComponentEvidence(hash, new ExportTargetId("server"), "bytes"),
                },
                Artifacts("server", hash),
                Artifacts("lite-client", hash),
            ],
        });

        Assert.True(allowed.Allowed);
        Assert.False(staleUnaffectedTarget.Allowed);
        Assert.Contains(staleUnaffectedTarget.Diagnostics, item => item.Code == "compat.publish.registry-schema");
        Assert.False(wrongTarget.Allowed);
        Assert.Contains(wrongTarget.Diagnostics, item => item.Code == "compat.publish.bytes-target");
    }

    [Fact]
    public void RemovedTargetNeedsHostMigrationBeforePublication()
    {
        var previous = WithCanonicalHash(Schema([TableWithTargets("game.Hero", ["client", "server"])], [], 0));
        var current = WithCanonicalHash(Schema([TableWithTargets("game.Hero", ["client"])], [], 0));
        var report = new CompatibilityAnalyzer().Analyze(current, previous);
        var request = new TargetPublicationRequest(current, report, [Artifacts("client", current.SchemaHash)], [], true);

        var blocked = TargetPublicationGate.Evaluate(request);
        var allowed = TargetPublicationGate.Evaluate(request with
        {
            CompletedHostMigrations = ImmutableHashSet.Create(StringComparer.Ordinal, "server"),
        });

        Assert.False(blocked.Allowed);
        Assert.Contains(blocked.Diagnostics, item => item.Code == "compat.publish.host-migration");
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public void PublicationCannotOverrideOutdatedWorkbookEvidenceWithABooleanAttestation()
    {
        var previous = WithCanonicalHash(Schema([TableWithTargets("game.Hero", ["client"])], [], 0));
        var current = WithCanonicalHash(Schema([TableWithTargets("game.Character", ["client"])], [], 0));
        var facts = new CompatibilityDataFacts([], HasCompleteWorkbookCoverage: true)
        {
            Workbooks = [new WorkbookCompatibilityFacts("book", "r1", previous.SchemaHash)],
        };
        var report = new CompatibilityAnalyzer().Analyze(current, previous, facts);
        var request = new TargetPublicationRequest(
            current,
            report,
            [Artifacts("client", current.SchemaHash)],
            [],
            WorkbookMigrationsComplete: true);

        var result = TargetPublicationGate.Evaluate(request);

        Assert.False(result.Allowed);
        Assert.Contains(result.Diagnostics, item => item.Code == "compat.publish.workbook-current");
    }

    [Fact]
    public void OperationGatesRequireStableOwnershipAndRuntimeIdentityEvidence()
    {
        var schema = Schema([Table("game.Hero")], [], 12);
        var report = new CompatibilityAnalyzer().Analyze(schema, schema with { SchemaHash = 11 });
        var full = new CompatibilityGateEvidence
        {
            HasFreshPlan = true,
            HasMigrationEvidence = true,
            HasExplicitDataLossConfirmation = true,
            HasOwnershipProof = true,
            HasCompleteWorkbookCoverage = true,
            HasStableSourceRevision = true,
            HasMappingIntegrity = true,
            LegacyParsingIsUnambiguous = true,
            HasNoUnparseableData = true,
            TargetPublicationReady = true,
            RuntimeIdentityMatches = true,
        };

        Assert.True(CompatibilityGate.Evaluate(report, CompatibilityOperation.Publish, full).Allowed);
        Assert.False(CompatibilityGate.Evaluate(
            report,
            CompatibilityOperation.Normalize,
            full with { HasStableSourceRevision = false }).Allowed);
        Assert.False(CompatibilityGate.Evaluate(
            report,
            CompatibilityOperation.GenerateApply,
            full with { HasOwnershipProof = false }).Allowed);
        Assert.False(CompatibilityGate.Evaluate(
            report,
            CompatibilityOperation.RuntimeOpen,
            full with { RuntimeIdentityMatches = false }).Allowed);
    }

    [Fact]
    public void DiagnosticSeverityParticipatesInEveryCompatibilityGate()
    {
        var report = new CompatibilityReport(
            CompatibilityReport.CurrentFormatVersion,
            11,
            12,
            true,
            [],
            [new("compat.test", ExcelDb.Core.Diagnostics.DiagnosticSeverity.Blocker, ".", "blocked")]);

        Assert.Equal(CompatibilitySeverity.Blocker, report.MaximumSeverity);
        Assert.False(CompatibilityGate.Evaluate(report, CompatibilityOperation.SchemaBuild).Allowed);
    }

    private static CanonicalSchemaDescriptor WithCanonicalHash(CanonicalSchemaDescriptor descriptor) =>
        descriptor with { SchemaHash = CanonicalSchemaSerializer.ComputeHash(descriptor) };

    private static TargetArtifactEvidence Artifacts(string target, ulong hash)
    {
        var id = new ExportTargetId(target);
        return new TargetArtifactEvidence(
            id,
            new TargetArtifactComponentEvidence(hash, id, "registry"),
            new TargetArtifactComponentEvidence(hash, id, "codegen"),
            new TargetArtifactComponentEvidence(hash, id, "bytes"),
            new TargetArtifactComponentEvidence(hash, id, "manifest"));
    }

    private static CanonicalSchemaDescriptor Schema(
        ImmutableArray<CanonicalTableDescriptor> tables,
        ImmutableArray<CanonicalRetiredTableDescriptor> retired,
        ulong hash) => new(tables, retired, [], hash);

    private static CanonicalTableDescriptor Table(
        string fullName,
        params CanonicalFieldDescriptor[] fields) =>
        TableWithTargets(fullName, ["client", "server"], fields);

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
        CanonicalCellFormat? format = null) =>
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
            format,
            null,
            null,
            ["client", "server"],
            [],
            []);
}
