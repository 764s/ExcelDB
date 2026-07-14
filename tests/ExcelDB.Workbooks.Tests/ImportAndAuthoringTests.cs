using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.Values;
using ExcelDb.Workbooks.Authoring;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Identity;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;
using ExcelDb.Schema.Descriptors;

namespace ExcelDb.Workbooks.Tests;

public sealed class ImportAndAuthoringTests
{
    [Fact]
    public void Import_distinguishes_missing_default_null_value_and_invalid_and_excludes_invalid_rows_from_indexes()
    {
        var workbook = TestData.Workbook(TestData.Row(
            TestData.GuidText(40),
            2,
            "alpha",
            note: new WorkbookCell(WorkbookProtocol.ExplicitNullToken),
            ratio: new WorkbookCell("not-a-double")));
        var bytes = XlsxWorkbookCodec.Write(workbook);
        var read = XlsxWorkbookCodec.Read(bytes);

        var result = WorkbookImporter.Import("items.xlsx", bytes, read, TestData.Schema());

        var row = Assert.Single(result.Rows);
        Assert.Equal(CanonicalValueState.Value, row.Values["id"].State);
        Assert.Equal(CanonicalValueState.Defaulted, row.Values["count"].State);
        Assert.Equal("7", row.Values["count"].Text);
        Assert.Equal(CanonicalValueState.Null, row.Values["note"].State);
        Assert.Equal(CanonicalValueState.Invalid, row.Values["ratio"].State);
        Assert.Equal("not-a-double", row.Values["ratio"].RawText);
        Assert.False(row.IsIndexable);
        Assert.Empty(result.IdentityIndex);
        Assert.Empty(result.KeyIndex);
        Assert.Single(result.Snapshot.Rows);
        Assert.Equal("not-a-double", result.Snapshot.Rows.Values.Single().RawCells["ratio"].Text);
    }

    [Fact]
    public void Formula_values_validate_cached_canonical_value_but_compare_by_formula_text()
    {
        var schema = TestData.Schema();
        var field = schema.Tables[0].Fields.Single(item => item.PropertyPath == "ratio");

        var first = CanonicalCellParser.Parse(WorkbookCell.FormulaCell("A1*2", "4"), field, schema);
        var second = CanonicalCellParser.Parse(WorkbookCell.FormulaCell("A1*2", "999"), field, schema);

        Assert.Equal(CanonicalValue.FromValue("4"), first.Value);
        Assert.Equal(CanonicalValue.FromValue("999"), second.Value);
        Assert.Equal(CanonicalValue.FromValue("=A1*2"), first.PhysicalValue);
        Assert.Equal(first.PhysicalValue, second.PhysicalValue);
    }

    [Fact]
    public void Required_missing_with_default_reports_error_while_preserving_raw_missing()
    {
        var schema = TestData.Schema();
        var field = schema.Tables[0].Fields.Single(item => item.PropertyPath == "count") with
        {
            Required = true,
        };

        var parsed = CanonicalCellParser.Parse(null, field, schema);

        Assert.Equal(CanonicalValueState.Defaulted, parsed.Value.State);
        Assert.Equal("7", parsed.Value.Text);
        Assert.Equal(CanonicalValueState.Missing, parsed.PhysicalValue.State);
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void Formula_cached_value_must_pass_declared_type_validation()
    {
        var schema = TestData.Schema();
        var field = schema.Tables[0].Fields.Single(item => item.PropertyPath == "ratio");

        var parsed = CanonicalCellParser.Parse(WorkbookCell.FormulaCell("A1/0", "not-a-number"), field, schema);

        Assert.Equal(CanonicalValueState.Invalid, parsed.Value.State);
        Assert.Equal(CanonicalValue.FromValue("=A1/0"), parsed.PhysicalValue);
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void Numeric_field_identity_and_alias_bind_renamed_workbook_headers()
    {
        var schema = TestData.Schema();
        var tableSchema = schema.Tables[0];
        var noteIndex = tableSchema.Fields
            .Select(static (field, index) => (field, index))
            .Single(static item => item.field.PropertyPath == "note")
            .index;
        var renamedField = tableSchema.Fields[noteIndex] with
        {
            FieldIdPath = [3],
            Aliases = ["legacy_note"],
        };
        schema = schema with
        {
            Tables = [tableSchema with { Fields = tableSchema.Fields.SetItem(noteIndex, renamedField) }],
        };
        var workbook = TestData.Workbook(TestData.Row(TestData.GuidText(46), 1, "alias"));
        var table = workbook.Tables[0];
        var noteColumnIndex = table.Columns
            .Select(static (column, index) => (column, index))
            .Single(static item => item.column.PropertyPath == "note")
            .index;
        var legacyColumn = table.Columns[noteColumnIndex] with
        {
            PropertyPath = "legacy_note",
            FieldPath = "999",
        };
        var row = table.Rows[0] with
        {
            Cells = table.Rows[0].Cells
                .Remove("note")
                .Add("legacy_note", new WorkbookCell("kept")),
        };
        workbook = workbook with
        {
            Tables = [table with { Columns = table.Columns.SetItem(noteColumnIndex, legacyColumn), Rows = [row] }],
        };
        var bytes = XlsxWorkbookCodec.Write(workbook);

        var imported = WorkbookImporter.Import("alias.xlsx", bytes, XlsxWorkbookCodec.Read(bytes), schema);

        var importedRow = Assert.Single(imported.Rows);
        Assert.Equal("kept", importedRow.Values["note"].Text);
        Assert.DoesNotContain(imported.Diagnostics, static diagnostic => diagnostic.Code == "EXWB2009");
    }

    [Fact]
    public void Retired_preserved_sheets_are_reported_and_excluded_from_live_rows_and_identity_scan()
    {
        var schema = TestData.Schema() with
        {
            RetiredTables = [new CanonicalRetiredTableDescriptor(9, "Legacy", "game.Legacy")],
        };
        var workbook = TestData.Workbook(TestData.Row(TestData.GuidText(47), 1, "live"));
        var legacy = workbook.Tables[0] with
        {
            TableId = 9,
            ProtoName = "Legacy",
            SheetName = "Legacy",
            Rows = [TestData.Row(TestData.GuidText(48), 1, "legacy")],
        };
        workbook = workbook with { Tables = workbook.Tables.Add(legacy) };
        var bytes = XlsxWorkbookCodec.Write(workbook);
        var read = XlsxWorkbookCodec.Read(bytes);

        var imported = WorkbookImporter.Import("retired.xlsx", bytes, read, schema);
        var identityWorkbook = WorkbookIdentityProjection.BindCanonicalKeys(read, imported, schema);
        var scan = new ProjectIdentityScanner().Scan(
            [new WorkbookSource("retired.xlsx", identityWorkbook, Core.IO.ContentFingerprint.FromBytes(bytes))]);

        Assert.Single(imported.Rows);
        Assert.Contains(imported.Diagnostics, static diagnostic => diagnostic.Code == "table.retired-present");
        Assert.Single(scan.Observations);
        Assert.True(identityWorkbook.Tables.Single(static table => table.TableId == 9).IsRetiredPreserved);
    }

    [Fact]
    public void Multiple_oneof_variant_cells_are_rejected()
    {
        var schema = TestData.Schema();
        var tableSchema = schema.Tables[0];
        var left = TestData.Field(20, "left", "left", "string") with
        {
            Shape = CanonicalFieldShape.OneOfVariant,
            OneOfGroup = "choice",
        };
        var right = TestData.Field(21, "right", "right", "string") with
        {
            Shape = CanonicalFieldShape.OneOfVariant,
            OneOfGroup = "choice",
        };
        tableSchema = tableSchema with { Fields = tableSchema.Fields.Add(left).Add(right) };
        schema = schema with { Tables = [tableSchema] };
        var row = TestData.Row(TestData.GuidText(49), 1, "oneof") with
        {
            Cells = TestData.Row(TestData.GuidText(49), 1, "oneof").Cells
                .Add("left", new WorkbookCell("a"))
                .Add("right", new WorkbookCell("b")),
        };
        var workbook = new WorkbookDefinition(
            Guid.NewGuid(),
            schema.SchemaHash,
            DateTimeOffset.UtcNow,
            [WorkbookLayout.CreateTable(tableSchema) with { Rows = [row] }]);
        var bytes = XlsxWorkbookCodec.Write(workbook);

        var imported = WorkbookImporter.Import("oneof.xlsx", bytes, XlsxWorkbookCodec.Read(bytes), schema);

        Assert.False(Assert.Single(imported.Rows).IsIndexable);
        Assert.Contains(imported.Diagnostics, static diagnostic => diagnostic.Code == "EXWB2013");
    }

    [Fact]
    public void Registered_row_validators_gate_indexes_and_project_domain_rejects_cross_workbook_keys()
    {
        var schema = TestData.Schema();
        schema = schema with
        {
            Tables = [schema.Tables[0] with { ValidatorIds = ["reject"] }],
        };
        var firstWorkbook = TestData.Workbook(TestData.Row(TestData.GuidText(50), 1, "duplicate"));
        var firstBytes = XlsxWorkbookCodec.Write(firstWorkbook);
        var registry = new WorkbookValidatorRegistry([new RejectingValidator()]);
        var first = WorkbookImporter.Import(
            "first.xlsx",
            firstBytes,
            XlsxWorkbookCodec.Read(firstBytes),
            schema,
            validators: registry);
        Assert.False(Assert.Single(first.Rows).IsIndexable);
        Assert.Contains(first.Diagnostics, static diagnostic => diagnostic.Code == "test.reject");

        var plainSchema = TestData.Schema();
        var secondWorkbook = TestData.Workbook(TestData.Row(TestData.GuidText(51), 1, "duplicate"));
        var secondBytes = XlsxWorkbookCodec.Write(secondWorkbook);
        var firstPlain = WorkbookImporter.Import(
            "first.xlsx",
            firstBytes,
            XlsxWorkbookCodec.Read(firstBytes),
            plainSchema);
        var second = WorkbookImporter.Import(
            "second.xlsx",
            secondBytes,
            XlsxWorkbookCodec.Read(secondBytes),
            plainSchema);
        var domain = WorkbookImportDomain.Validate(
            [new("first.xlsx", firstPlain), new("second.xlsx", second)]);
        Assert.Contains(domain, static diagnostic => diagnostic.Code == "EXWB2022" && diagnostic.IsBlocker);
    }

    [Fact]
    public void ExplicitNullAndEscapedLiteralTildeRemainDistinct()
    {
        var schema = TestData.Schema();
        var field = schema.Tables[0].Fields.Single(item => item.PropertyPath == "note");

        var explicitNull = CanonicalCellParser.Parse(new WorkbookCell("~"), field, schema);
        var literal = CanonicalCellParser.Parse(new WorkbookCell("%7e"), field, schema);

        Assert.Equal(CanonicalValueState.Null, explicitNull.Value.State);
        Assert.Equal(CanonicalValueState.Value, literal.Value.State);
        Assert.Equal("~", literal.Value.Text);
        Assert.Equal("%7E", literal.CanonicalPhysicalText);
    }

    [Fact]
    public void UnknownHelperColumnsAreIgnoredWhileMissingOwnedColumnsBlockConversion()
    {
        var original = TestData.Workbook(TestData.Row(TestData.GuidText(45), 1, "alpha"));
        var table = original.Tables[0];
        var helper = new WorkbookColumn("Helper", "helper", "string", "999");
        var rowWithHelper = table.Rows[0] with
        {
            Cells = table.Rows[0].Cells.Add("helper", new WorkbookCell("keep me")),
        };
        var withHelper = original with
        {
            Tables = [table with { Columns = table.Columns.Add(helper), Rows = [rowWithHelper] }],
        };
        var helperBytes = XlsxWorkbookCodec.Write(withHelper);

        var helperImport = WorkbookImporter.Import(
            "helper.xlsx",
            helperBytes,
            XlsxWorkbookCodec.Read(helperBytes),
            TestData.Schema());

        Assert.True(Assert.Single(helperImport.Rows).IsIndexable);
        Assert.DoesNotContain("helper", helperImport.Rows[0].Values.Keys);
        Assert.DoesNotContain("helper", helperImport.Rows[0].RawCells.Keys);
        Assert.Contains(helperImport.Diagnostics, item => item.Code == "EXWB2002" && item.Severity == DiagnosticSeverity.Info);

        var withoutNote = original with
        {
            Tables = [table with { Columns = table.Columns.RemoveAt(2) }],
        };
        var missingBytes = XlsxWorkbookCodec.Write(withoutNote);
        var missingImport = WorkbookImporter.Import(
            "missing.xlsx",
            missingBytes,
            XlsxWorkbookCodec.Read(missingBytes),
            TestData.Schema());

        Assert.False(Assert.Single(missingImport.Rows).IsIndexable);
        Assert.Equal(CanonicalValueState.Missing, missingImport.Rows[0].Values["note"].State);
        Assert.Contains(missingImport.Diagnostics, item => item.Code == "EXWB2009" && item.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Direct_key_cell_change_is_imported_as_rename()
    {
        var guid = TestData.GuidText(41);
        var beforeWorkbook = TestData.Workbook(TestData.Row(guid, 1, "old"));
        var beforeBytes = XlsxWorkbookCodec.Write(beforeWorkbook);
        var before = WorkbookImporter.Import(
            "items.xlsx",
            beforeBytes,
            XlsxWorkbookCodec.Read(beforeBytes),
            TestData.Schema());
        var afterWorkbook = TestData.Workbook(TestData.Row(guid, 2, "new", key: "3:old"));
        var afterBytes = XlsxWorkbookCodec.Write(afterWorkbook);

        var after = WorkbookImporter.Import(
            "items.xlsx",
            afterBytes,
            XlsxWorkbookCodec.Read(afterBytes),
            TestData.Schema(),
            before.Snapshot);

        var row = Assert.Single(after.Rows);
        Assert.Equal(ImportChangeKind.Renamed, row.ChangeKind);
        Assert.Equal("3:new", row.Key);
    }

    [Fact]
    public void Drafts_are_memory_only_and_discard_returns_to_latest_snapshot()
    {
        var snapshot = Snapshot(TestData.GuidText(42), "old", "1");
        var identity = snapshot.Rows.Keys.Single();
        var store = new AuthoringDraftStore(snapshot);

        store.Edit(identity, "count", CanonicalValue.FromValue("2"));
        store.Rename(identity, "renamed");
        Assert.Equal(DraftChangeKind.Renamed, store.Changes[identity].Kind);
        store.Discard();

        Assert.Empty(store.Changes);
        Assert.Equal("old", store.LatestSnapshot.Rows[identity].Key);
        Assert.Equal("1", store.LatestSnapshot.Rows[identity].Values["count"].Text);
    }

    [Fact]
    public void Three_way_merge_combines_independent_edits_and_reports_divergent_fields()
    {
        var baseSnapshot = Snapshot(TestData.GuidText(43), "key", "1", "base");
        var identity = baseSnapshot.Rows.Keys.Single();
        var baseRow = baseSnapshot.Rows[identity];
        var theirsRow = baseRow with { Values = baseRow.Values.SetItem("note", CanonicalValue.FromValue("theirs")) };
        var theirs = baseSnapshot with { Rows = baseSnapshot.Rows.SetItem(identity, theirsRow) };
        var myRow = baseRow with { Values = baseRow.Values.SetItem("count", CanonicalValue.FromValue("2")) };

        var independent = ThreeWayMerge.Merge(
            baseSnapshot,
            theirs,
            new Dictionary<AssetIdentity, DraftChange>
            {
                [identity] = new(DraftChangeKind.Modified, myRow),
            });

        Assert.True(independent.CanCommit);
        Assert.Equal("2", independent.Rows[identity].Values["count"].Text);
        Assert.Equal("theirs", independent.Rows[identity].Values["note"].Text);

        var conflictingMine = baseRow with
        {
            Values = baseRow.Values.SetItem("note", CanonicalValue.FromValue("mine")),
        };
        var conflict = ThreeWayMerge.Merge(
            baseSnapshot,
            theirs,
            new Dictionary<AssetIdentity, DraftChange>
            {
                [identity] = new(DraftChangeKind.Modified, conflictingMine),
            });
        Assert.False(conflict.CanCommit);
        var item = Assert.Single(conflict.Conflicts);
        Assert.Equal(MergeConflictKind.Field, item.Kind);
        Assert.Equal("note", item.Path);
    }

    [Fact]
    public void Three_way_merge_compares_formula_text_while_publishing_the_latest_validated_cached_value()
    {
        var baseline = Snapshot(TestData.GuidText(52), "formula", "1");
        var identity = baseline.Rows.Keys.Single();
        var baseRow = baseline.Rows[identity] with
        {
            Values = baseline.Rows[identity].Values.SetItem("ratio", CanonicalValue.FromValue("4")),
            RawValues = baseline.Rows[identity].RawValues.SetItem("ratio", CanonicalValue.FromValue("=A1*2")),
        };
        baseline = baseline with { Rows = baseline.Rows.SetItem(identity, baseRow) };
        var theirRow = baseRow with
        {
            Values = baseRow.Values.SetItem("ratio", CanonicalValue.FromValue("999")),
        };
        var theirs = baseline with { Rows = baseline.Rows.SetItem(identity, theirRow) };
        var myRow = baseRow with
        {
            Values = baseRow.Values.SetItem("count", CanonicalValue.FromValue("2")),
            RawValues = baseRow.RawValues.SetItem("count", CanonicalValue.FromValue("2")),
        };

        var merged = ThreeWayMerge.Merge(
            baseline,
            theirs,
            new Dictionary<AssetIdentity, DraftChange>
            {
                [identity] = new(DraftChangeKind.Modified, myRow),
            });

        Assert.True(merged.CanCommit);
        Assert.Equal("999", merged.Rows[identity].Values["ratio"].Text);
        Assert.Equal("=A1*2", merged.Rows[identity].RawValues["ratio"].Text);
        Assert.Equal("2", merged.Rows[identity].Values["count"].Text);
    }

    [Fact]
    public void Publication_order_is_fixed_and_reentrancy_is_rejected()
    {
        var workbook = TestData.Workbook(TestData.Row(TestData.GuidText(44), 0, "publish"));
        var bytes = XlsxWorkbookCodec.Write(workbook);
        var import = WorkbookImporter.Import(
            "items.xlsx",
            bytes,
            XlsxWorkbookCodec.Read(bytes),
            TestData.Schema());
        var report = OperationReport.Success("import", "test");
        var publisher = new AuthoringPublisher();
        var phases = new List<PublishPhase>();
        var reentrancyBlocked = false;
        publisher.PhaseCompleted += phases.Add;
        publisher.ReportPublished += _ =>
        {
            Assert.NotNull(publisher.Snapshot);
            Assert.NotEmpty(publisher.IdentityIndex);
            Assert.Throws<InvalidOperationException>(() => publisher.Publish(import, report));
            reentrancyBlocked = true;
        };

        publisher.Publish(import, report);

        Assert.True(reentrancyBlocked);
        Assert.Equal(
            [
                PublishPhase.ResidentAndIndex,
                PublishPhase.Snapshot,
                PublishPhase.Report,
                PublishPhase.ImportEvent,
                PublishPhase.RuntimeNotification,
            ],
            phases);
    }

    [Fact]
    public void WatcherAndExplicitRefreshDrainTheSameSerializedPath()
    {
        var explicitCount = 0;
        var explicitScheduler = new AuthoringRefreshScheduler(() => explicitCount++);
        var watcherCount = 0;
        var watcherScheduler = new AuthoringRefreshScheduler(() => watcherCount++);

        Assert.True(explicitScheduler.RefreshExplicit());
        watcherScheduler.SignalWatcher();
        watcherScheduler.SignalWatcher();
        Assert.Equal(0, watcherCount);
        Assert.True(watcherScheduler.Drain());

        Assert.Equal(1, explicitCount);
        Assert.Equal(1, watcherCount);
        Assert.Equal(AuthoringRefreshTrigger.Explicit, explicitScheduler.LastDrained);
        Assert.Equal(AuthoringRefreshTrigger.Watcher, watcherScheduler.LastDrained);
    }

    [Fact]
    public void WatcherRaisedDuringPublicationQueuesNextPointAndFailureRetainsHint()
    {
        AuthoringRefreshScheduler? scheduler = null;
        var calls = 0;
        scheduler = new AuthoringRefreshScheduler(() =>
        {
            calls++;
            if (calls == 1)
            {
                scheduler!.SignalWatcher();
                Assert.False(scheduler.Drain());
                throw new IOException("locked");
            }
        });

        scheduler.SignalWatcher();
        Assert.Throws<IOException>(() => scheduler.Drain());
        Assert.Equal(AuthoringRefreshTrigger.Watcher, scheduler.Pending);
        Assert.True(scheduler.Drain());
        Assert.Equal(2, calls);
        Assert.Equal(AuthoringRefreshTrigger.None, scheduler.Pending);
    }

    private static ImportSnapshot Snapshot(
        string guid,
        string key,
        string count,
        string note = "note")
    {
        var identity = new AssetIdentity(1, RowGuid.Parse(guid));
        var values = ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
        {
            new KeyValuePair<string, CanonicalValue>("id", CanonicalValue.FromValue(key)),
            new KeyValuePair<string, CanonicalValue>("count", CanonicalValue.FromValue(count)),
            new KeyValuePair<string, CanonicalValue>("note", CanonicalValue.FromValue(note)),
        });
        var row = new SnapshotRow(
            identity,
            key,
            1,
            values,
            ImmutableDictionary<string, WorkbookCell>.Empty);
        return new ImportSnapshot(
            "items.xlsx",
            new Core.IO.ContentFingerprint(0, new string('0', 64)),
            TestData.SchemaHash,
            ImmutableDictionary<AssetIdentity, SnapshotRow>.Empty.Add(identity, row));
    }

    private sealed class RejectingValidator : IWorkbookValidator
    {
        public string Id => "reject";

        public IEnumerable<Diagnostic> Validate(WorkbookValidationContext context)
        {
            yield return new Diagnostic("test.reject", DiagnosticSeverity.Error, context.Row.Location, "Rejected by test rule.");
        }
    }
}
