using System.Collections.Immutable;
using ExcelDb.Schema.Authoring;

namespace ExcelDB.Schema.Tests;

public sealed class AuthoringStrategyTests
{
    [Fact]
    public void Default_initializer_and_standard_strategy_fill_only_unspecified_slots()
    {
        var requested = ImmutableArray.Create(
            new SimpleFieldDefinition("name", SimpleFieldType.String),
            new SimpleFieldDefinition("server_formula", SimpleFieldType.String),
            new SimpleFieldDefinition("authoring_note", SimpleFieldType.String));
        var context = new TableInitializationContext("SkillConfig", requested, AutoKey: true);
        var draft = new TableDraft(context.TableName);

        new DefaultTableInitializer().Initialize(in context, draft);
        draft.ApplyExplicitFieldExportTargets("server_formula", ExportTargetSelection.Explicit(["server"]));
        draft.ApplyExplicitFieldExportTargets("authoring_note", ExportTargetSelection.Explicit([]));
        var strategyContext = new ExportTargetStrategyContext(context.TableName);
        new StandardClientServerExportTargetStrategy().Apply(in strategyContext, draft);

        Assert.Equal(["id", "name", "server_formula", "authoring_note"], draft.Fields.Select(static field => field.Name));
        Assert.True(draft.Fields[0].IsKey);
        Assert.Equal(["client", "server"], draft.TableExportTargets!.Targets.ToArray());
        Assert.Equal(["client", "server"], draft.Fields.Single(field => field.Name == "id").ExportTargets!.Targets.ToArray());
        Assert.Equal(["client", "server"], draft.Fields.Single(field => field.Name == "name").ExportTargets!.Targets.ToArray());
        Assert.Equal(["server"], draft.Fields.Single(field => field.Name == "server_formula").ExportTargets!.Targets.ToArray());
        Assert.Empty(draft.Fields.Single(field => field.Name == "authoring_note").ExportTargets!.Targets);
    }

    [Fact]
    public void A_plain_csharp_strategy_can_add_a_future_target_without_a_declaration_language()
    {
        var context = new TableInitializationContext(
            "CompactConfig",
            [new SimpleFieldDefinition("id", SimpleFieldType.String, IsKey: true)],
            AutoKey: false);
        var draft = new TableDraft(context.TableName);
        new DefaultTableInitializer().Initialize(in context, draft);

        var strategyContext = new ExportTargetStrategyContext(context.TableName);
        IExportTargetStrategy strategy = new LiteClientStrategy();
        strategy.Apply(in strategyContext, draft);

        Assert.Equal("test.lite-client", strategy.Id);
        Assert.Equal(["lite-client"], draft.TableExportTargets!.Targets.ToArray());
        Assert.Equal(["lite-client"], draft.Fields[0].ExportTargets!.Targets.ToArray());
    }

    [Fact]
    public void Standard_strategy_copies_an_explicit_client_only_parent_to_unspecified_fields()
    {
        var context = new TableInitializationContext(
            "ClientConfig",
            [new SimpleFieldDefinition("id", SimpleFieldType.String, IsKey: true)],
            AutoKey: false);
        var draft = new TableDraft(context.TableName);
        new DefaultTableInitializer().Initialize(in context, draft);
        draft.ApplyExplicitTableExportTargets(ExportTargetSelection.Explicit(["client"]));

        var strategyContext = new ExportTargetStrategyContext(context.TableName);
        new StandardClientServerExportTargetStrategy().Apply(in strategyContext, draft);

        Assert.Equal(["client"], draft.TableExportTargets!.Targets.ToArray());
        Assert.Equal(["client"], draft.Fields[0].ExportTargets!.Targets.ToArray());
    }

    [Fact]
    public void Strategy_fill_api_cannot_replace_user_confirmed_targets()
    {
        var context = new TableInitializationContext(
            "ProtectedConfig",
            [
                new SimpleFieldDefinition("id", SimpleFieldType.String, IsKey: true),
                new SimpleFieldDefinition("note", SimpleFieldType.String),
            ],
            AutoKey: false);
        var draft = new TableDraft(context.TableName);
        new DefaultTableInitializer().Initialize(in context, draft);
        draft.ApplyExplicitTableExportTargets(ExportTargetSelection.Explicit(["client"]));
        draft.ApplyExplicitFieldExportTargets("id", ExportTargetSelection.Explicit(["client"]));
        draft.ApplyExplicitFieldExportTargets("note", ExportTargetSelection.Explicit([]));

        IExportTargetDraft strategyDraft = draft;
        Assert.False(strategyDraft.TryFillTableExportTargets(["server"]));
        Assert.False(strategyDraft.TryFillFieldExportTargets("id", ["server"]));
        Assert.False(strategyDraft.TryFillFieldExportTargets("note", ["server"]));

        Assert.Equal(["client"], draft.TableExportTargets!.Targets.ToArray());
        Assert.Equal(["client"], draft.Fields[0].ExportTargets!.Targets.ToArray());
        Assert.Empty(draft.Fields[1].ExportTargets!.Targets);
        Assert.False(strategyDraft.Fields is List<TableFieldDraft>);
    }

    [Theory]
    [InlineData("Client")]
    [InlineData("mobile_1")]
    [InlineData("")]
    public void Invalid_target_ids_are_rejected_at_the_draft_boundary(string value)
    {
        Assert.Throws<ArgumentException>(() => ExportTargetSelection.Explicit([value]));
    }

    private sealed class LiteClientStrategy : IExportTargetStrategy
    {
        public string Id => "test.lite-client";

        public void Apply(in ExportTargetStrategyContext context, IExportTargetDraft draft)
        {
            draft.TryFillTableExportTargets(["lite-client"]);
            foreach (var field in draft.Fields)
                draft.TryFillFieldExportTargets(field.Name, ["lite-client"]);
        }
    }
}
