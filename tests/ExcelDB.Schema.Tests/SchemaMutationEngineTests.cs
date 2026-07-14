using System.Collections.Immutable;
using ExcelDb.Schema.Authoring;
using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Mutation;
using Google.Protobuf.Reflection;

namespace ExcelDB.Schema.Tests;

public sealed class SchemaMutationEngineTests
{
    [Fact]
    public async Task Create_invokes_plain_csharp_initializer_and_strategy_and_emits_recompilable_proto()
    {
        var engine = new SchemaMutationEngine();
        var request = new CreateTableMutationRequest
        {
            FileName = "game/config.proto",
            Package = "game",
            TableName = "Config",
            Fields = [new SimpleFieldDefinition("id", SimpleFieldType.String, IsKey: true)],
            AutoKey = false,
        };

        var result = engine.CreateTable(
            new FileDescriptorSet(),
            request,
            new EnabledInitializer(),
            new LiteClientStrategy());

        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal(1, result.AssignedTableId);
        Assert.Equal(1, result.AssignedFieldNumbers["id"]);
        Assert.Equal(2, result.AssignedFieldNumbers["enabled"]);
        var proto = result.CandidateProtoFiles["game/config.proto"];
        Assert.Contains("export_targets: { ids: \"lite-client\" }", proto, StringComparison.Ordinal);
        Assert.Contains("bool enabled = 2", proto, StringComparison.Ordinal);

        using var candidateDirectory = TemporarySchemaDirectory.Create();
        candidateDirectory.Write("game/config.proto", proto);
        var fromText = await new SchemaCompiler().CompileAsync(candidateDirectory.Path);
        var fromDescriptor = new SchemaCompiler().CompileDescriptorSet(result.CandidateDescriptorSet!);
        Assert.True(fromText.Succeeded, Describe(fromText));
        Assert.True(fromDescriptor.Succeeded, Describe(fromDescriptor));
        Assert.Equal(fromText.Descriptor!.SchemaHash, fromDescriptor.Descriptor!.SchemaHash);
    }

    [Fact]
    public async Task Edit_preserves_numbers_reserves_deletion_and_retire_preserves_table_identity()
    {
        var engine = new SchemaMutationEngine();
        var created = engine.CreateTable(
            new FileDescriptorSet(),
            new CreateTableMutationRequest
            {
                FileName = "config.proto",
                Package = "game",
                TableName = "Config",
                Fields =
                [
                    new SimpleFieldDefinition("id", SimpleFieldType.String, IsKey: true),
                    new SimpleFieldDefinition("name", SimpleFieldType.String),
                    new SimpleFieldDefinition("enabled", SimpleFieldType.Boolean),
                ],
                AutoKey = false,
            });
        Assert.True(created.Succeeded, Describe(created));

        var edited = engine.EditTable(
            created.CandidateDescriptorSet!,
            new EditTableMutationRequest
            {
                TableFullName = "game.Config",
                RenameFields = [new RenameFieldMutation(1, "key")],
                RemoveFieldNumbers = [2],
                AddFields = [new AddSimpleFieldMutation(new SimpleFieldDefinition("power", SimpleFieldType.Int32))],
            });

        Assert.True(edited.Succeeded, Describe(edited));
        Assert.Equal(4, edited.AssignedFieldNumbers["power"]);
        var proto = edited.CandidateProtoFiles["config.proto"];
        Assert.Contains("string key = 1", proto, StringComparison.Ordinal);
        Assert.Contains("reserved 2;", proto, StringComparison.Ordinal);
        Assert.Contains("reserved \"name\";", proto, StringComparison.Ordinal);
        Assert.Contains("int32 power = 4", proto, StringComparison.Ordinal);

        using var candidateDirectory = TemporarySchemaDirectory.Create();
        candidateDirectory.Write("config.proto", proto);
        var recompiled = await new SchemaCompiler().CompileAsync(candidateDirectory.Path);
        Assert.True(recompiled.Succeeded, Describe(recompiled));
        var table = Assert.Single(recompiled.Descriptor!.Tables);
        Assert.Equal([1, 3, 4], table.Fields.Select(static field => field.Id).ToArray());
        Assert.True(Assert.Single(table.ReservedFieldNumbers).Contains(2));

        var retired = engine.RetireTable(
            edited.CandidateDescriptorSet!,
            new RetireTableMutationRequest("game.Config"));
        Assert.True(retired.Succeeded, Describe(retired));
        var retiredDescriptor = new SchemaCompiler().CompileDescriptorSet(retired.CandidateDescriptorSet!);
        Assert.True(retiredDescriptor.Succeeded, Describe(retiredDescriptor));
        Assert.Empty(retiredDescriptor.Descriptor!.Tables);
        var tombstone = Assert.Single(retiredDescriptor.Descriptor.RetiredTables);
        Assert.Equal(1, tombstone.Id);

        var next = engine.CreateTable(
            retired.CandidateDescriptorSet!,
            new CreateTableMutationRequest
            {
                FileName = "other.proto",
                Package = "game",
                TableName = "Other",
                Fields = [new SimpleFieldDefinition("id", SimpleFieldType.String, IsKey: true)],
                AutoKey = false,
            });
        Assert.True(next.Succeeded, Describe(next));
        Assert.Equal(2, next.AssignedTableId);
    }

    [Fact]
    public void Strategy_failure_returns_no_candidate_and_does_not_mutate_input()
    {
        var input = new FileDescriptorSet();
        var result = new SchemaMutationEngine().CreateTable(
            input,
            new CreateTableMutationRequest
            {
                FileName = "config.proto",
                Package = "game",
                TableName = "Config",
                Fields = [new SimpleFieldDefinition("id", SimpleFieldType.String, IsKey: true)],
                AutoKey = false,
            },
            exportTargetStrategy: new InvalidStrategy());

        Assert.False(result.Succeeded);
        Assert.Null(result.CandidateDescriptorSet);
        Assert.Empty(result.CandidateProtoFiles);
        Assert.Empty(input.File);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "XDB000");
    }

    [Fact]
    public void Initializer_options_are_materialized_and_initializer_id_is_not_canonical()
    {
        var request = new CreateTableMutationRequest
        {
            FileName = "config.proto",
            Package = "game",
            TableName = "Config",
            Fields = [new SimpleFieldDefinition("id", SimpleFieldType.String, IsKey: true)],
            AutoKey = false,
            InitializationOptions = new TableInitializationOptions("配置", "ConfigSheet", ["config_rules"]),
            FieldOptions = ImmutableDictionary<string, TableFieldOptions>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("id", new TableFieldOptions("标识", "Stable key", ["旧标识"], true, null, null, null, "^[a-z]+$", true)),
        };
        var engine = new SchemaMutationEngine();
        var firstInitializer = new NamedInitializer("first-id");
        var secondInitializer = new NamedInitializer("second-id");
        var first = engine.CreateTable(new FileDescriptorSet(), request, firstInitializer);
        var second = engine.CreateTable(new FileDescriptorSet(), request, secondInitializer);

        Assert.True(first.Succeeded, Describe(first));
        Assert.True(second.Succeeded, Describe(second));
        Assert.True(firstInitializer.ObservedSchema);
        Assert.True(secondInitializer.ObservedSchema);
        Assert.Equal(first.CandidateProtoFiles["config.proto"], second.CandidateProtoFiles["config.proto"]);
        var firstDescriptor = new SchemaCompiler().CompileDescriptorSet(first.CandidateDescriptorSet!).Descriptor!;
        Assert.Equal(
            firstDescriptor.SchemaHash,
            new SchemaCompiler().CompileDescriptorSet(second.CandidateDescriptorSet!).Descriptor!.SchemaHash);
        var proto = first.CandidateProtoFiles["config.proto"];
        Assert.Contains("sheet_name: \"ConfigSheet\"", proto, StringComparison.Ordinal);
        Assert.Contains("validators: \"config_rules\"", proto, StringComparison.Ordinal);
        Assert.Contains("header_comment: \"Stable key\"", proto, StringComparison.Ordinal);
        var table = Assert.Single(firstDescriptor.Tables);
        Assert.Equal("配置", table.DisplayName);
        var field = Assert.Single(table.Fields);
        Assert.Equal("标识", field.DisplayName);
        Assert.Equal("Stable key", field.HeaderComment);
        Assert.Equal(new[] { "旧标识" }, field.Aliases.ToArray());
    }

    [Fact]
    public void Non_deterministic_or_throwing_initializer_is_a_zero_candidate_blocker()
    {
        var request = new CreateTableMutationRequest
        {
            FileName = "config.proto",
            Package = "game",
            TableName = "Config",
            Fields = [new SimpleFieldDefinition("id", SimpleFieldType.String, IsKey: true)],
            AutoKey = false,
        };
        var engine = new SchemaMutationEngine();
        var nondeterministic = engine.CreateTable(new FileDescriptorSet(), request, new AlternatingInitializer());
        var throwing = engine.CreateTable(new FileDescriptorSet(), request, new ThrowingInitializer());

        Assert.False(nondeterministic.Succeeded);
        Assert.Null(nondeterministic.CandidateDescriptorSet);
        Assert.Contains(nondeterministic.Diagnostics, static diagnostic => diagnostic.Message.Contains("non-deterministic", StringComparison.Ordinal));
        Assert.False(throwing.Succeeded);
        Assert.Null(throwing.CandidateDescriptorSet);
        Assert.Contains(throwing.Diagnostics, static diagnostic => diagnostic.Message.Contains("initializer failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Structured_edit_adds_complex_repeated_map_and_oneof_fields_and_replaces_options()
    {
        using var source = TemporarySchemaDirectory.Create();
        source.Write("base.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            message Cost { int32 mp = 1; }
            message Hero {
              option (exceldb.table) = { kind: ASSET, id: 7 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              int32 hp = 2;
            }
            """);
        var compiled = await new EmbeddedProtocCompiler().CompileAsync(SchemaSourceSet.Discover(source.Path));
        var hpOptions = new ExcelDb.Protocol.FieldOpts { Required = true, DefaultValue = "100" };
        var result = new SchemaMutationEngine().EditTable(
            compiled.DescriptorSet,
            new EditTableMutationRequest
            {
                TableFullName = "game.Hero",
                AddStructuredFields =
                [
                    new AddStructuredFieldMutation("cost", "game.Cost", FieldNumber: 3,
                        Options: new ExcelDb.Protocol.FieldOpts { Expand = ExcelDb.Protocol.ExpandMode.ExpandedColumns }),
                    new AddStructuredFieldMutation("tags", "string", StructuredFieldCardinality.Repeated, FieldNumber: 4),
                    new AddStructuredFieldMutation("stats", "int32", StructuredFieldCardinality.Map, "string", FieldNumber: 5),
                    new AddStructuredFieldMutation("effect_cost", "game.Cost", OneOfGroup: "effect", FieldNumber: 6),
                ],
                FieldOptionsReplacements = ImmutableDictionary<int, ExcelDb.Protocol.FieldOpts>.Empty.Add(2, hpOptions),
            },
            new StandardClientServerExportTargetStrategy());

        Assert.True(result.Succeeded, Describe(result));
        var proto = result.CandidateProtoFiles["base.proto"];
        Assert.Contains("repeated string tags = 4", proto, StringComparison.Ordinal);
        Assert.Contains("map<string, int32> stats = 5", proto, StringComparison.Ordinal);
        Assert.Contains("oneof effect", proto, StringComparison.Ordinal);
        Assert.Contains("game.Cost effect_cost = 6", proto, StringComparison.Ordinal);
        var descriptor = new SchemaCompiler().CompileDescriptorSet(result.CandidateDescriptorSet!).Descriptor!;
        var table = Assert.Single(descriptor.Tables);
        Assert.True(Assert.Single(table.Fields, static field => field.Id == 2).Required);
        Assert.Equal(CanonicalFieldShape.RepeatedScalar, Assert.Single(table.Fields, static field => field.Id == 4).Shape);
        Assert.Equal(CanonicalFieldShape.Map, Assert.Single(table.Fields, static field => field.Id == 5).Shape);
        Assert.Equal("effect", Assert.Single(table.Fields, static field => field.Id == 6).OneOfGroup);
    }

    [Fact]
    public void Enum_mutations_preserve_numbers_and_reserve_removed_identity()
    {
        var engine = new SchemaMutationEngine();
        var created = engine.CreateEnum(
            new FileDescriptorSet(),
            new CreateEnumMutationRequest
            {
                FileName = "types.proto",
                Package = "game",
                EnumName = "State",
                Values =
                [
                    new EnumValueMutation("STATE_UNSPECIFIED", 0),
                    new EnumValueMutation("ACTIVE", 1),
                    new EnumValueMutation("REMOVED", 2),
                ],
            });
        Assert.True(created.Succeeded, Describe(created));

        var edited = engine.EditEnum(
            created.CandidateDescriptorSet!,
            new EditEnumMutationRequest
            {
                EnumFullName = "game.State",
                NewName = "Status",
                RenameValues = [new RenameEnumValueMutation(1, "ENABLED")],
                RemoveValueNumbers = [2],
                AddValues = [new EnumValueMutation("DISABLED", 3, "Disabled")],
            });
        Assert.True(edited.Succeeded, Describe(edited));
        var proto = edited.CandidateProtoFiles["types.proto"];
        Assert.Contains("enum Status", proto, StringComparison.Ordinal);
        Assert.Contains("ENABLED = 1", proto, StringComparison.Ordinal);
        Assert.Contains("reserved 2;", proto, StringComparison.Ordinal);
        Assert.Contains("reserved \"REMOVED\";", proto, StringComparison.Ordinal);
        Assert.Contains("DISABLED = 3", proto, StringComparison.Ordinal);
        var descriptor = new SchemaCompiler().CompileDescriptorSet(edited.CandidateDescriptorSet!);
        Assert.True(descriptor.Succeeded, Describe(descriptor));
        Assert.Equal("game.Status", Assert.Single(descriptor.Descriptor!.Enums).FullName);
    }

    private static string Describe(SchemaMutationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}"));

    private static string Describe(SchemaCompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}"));

    private sealed class EnabledInitializer : ITableInitializer
    {
        public string Id => "test.enabled";

        public void Initialize(in TableInitializationContext context, ITableDraft draft)
        {
            new DefaultTableInitializer().Initialize(in context, draft);
            draft.AddField(new SimpleFieldDefinition("enabled", SimpleFieldType.Boolean));
        }
    }

    private sealed class LiteClientStrategy : IExportTargetStrategy
    {
        public string Id => "test.lite";

        public void Apply(in ExportTargetStrategyContext context, IExportTargetDraft draft)
        {
            draft.TryFillTableExportTargets(["lite-client"]);
            foreach (var field in draft.Fields)
                draft.TryFillFieldExportTargets(field.Name, ["lite-client"]);
        }
    }

    private sealed class InvalidStrategy : IExportTargetStrategy
    {
        public string Id => "test.invalid";

        public void Apply(in ExportTargetStrategyContext context, IExportTargetDraft draft) =>
            draft.TryFillTableExportTargets(["INVALID"]);
    }

    private sealed class NamedInitializer(string id) : ITableInitializer
    {
        public string Id { get; } = id;

        public bool ObservedSchema { get; private set; }

        public void Initialize(in TableInitializationContext context, ITableDraft draft)
        {
            ObservedSchema |= context.CurrentSchema is not null;
            new DefaultTableInitializer().Initialize(in context, draft);
        }
    }

    private sealed class AlternatingInitializer : ITableInitializer
    {
        private bool _alternate;

        public string Id => "test.alternating";

        public void Initialize(in TableInitializationContext context, ITableDraft draft)
        {
            new DefaultTableInitializer().Initialize(in context, draft);
            draft.AddField(new SimpleFieldDefinition(_alternate ? "second" : "first", SimpleFieldType.String));
            _alternate = !_alternate;
        }
    }

    private sealed class ThrowingInitializer : ITableInitializer
    {
        public string Id => "test.throwing";

        public void Initialize(in TableInitializationContext context, ITableDraft draft) =>
            throw new InvalidOperationException("initializer failed");
    }
}
