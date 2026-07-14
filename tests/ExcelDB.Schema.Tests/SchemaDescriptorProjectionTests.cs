using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Generation;

namespace ExcelDB.Schema.Tests;

public sealed class SchemaDescriptorProjectionTests
{
    [Fact]
    public async Task Complex_fields_keep_full_numeric_identity_and_child_table_contracts()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("projection.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";

            message Cost {
              int32 mp = 7 [(exceldb.field) = {
                display_name: "Mana"
                header_comment: "Mana cost"
                aliases: "Old Mana"
              }];
            }
            message Reward { string item = 4; int32 count = 9; }
            message Hero {
              option (exceldb.table) = { kind: ASSET, id: 88 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              Cost cost = 2 [(exceldb.field) = { expand: EXPANDED_COLUMNS }];
              repeated Reward rewards = 3 [(exceldb.field) = { child_table: { sheet_name: "HeroRewards" } }];
              map<string, Reward> reward_by_slot = 5;
            }
            """);

        var result = await new SchemaCompiler().CompileAsync(schema.Path);
        Assert.True(result.Succeeded, Describe(result));
        var table = Assert.Single(result.Descriptor!.Tables, static table => table.Id == 88);
        var cost = Assert.Single(table.Fields, static field => field.Id == 2);
        var mana = Assert.Single(cost.Children);
        Assert.Equal(new[] { 2, 7 }, mana.FieldIdPath.ToArray());
        Assert.Equal("cost.mp", mana.PropertyPath);
        Assert.Equal("Mana", mana.DisplayName);
        Assert.Equal("Mana cost", mana.HeaderComment);
        Assert.Equal(new[] { "Old Mana" }, mana.Aliases.ToArray());

        var rewards = Assert.Single(table.Fields, static field => field.Id == 3);
        Assert.Equal(CanonicalChildTableKind.RepeatedMessage, rewards.ChildTable!.Kind);
        Assert.Equal("HeroRewards", rewards.ChildTable.SheetName);
        Assert.Equal(new[] { 3 }, rewards.ChildTable.OwnerFieldIdPath.ToArray());
        Assert.Equal("__parent_guid", rewards.ChildTable.ParentGuidColumn);
        Assert.Equal("__ordinal", rewards.ChildTable.OrdinalColumn);
        Assert.All(rewards.Children, child =>
        {
            Assert.Equal(new[] { 3, child.Id }, child.FieldIdPath.ToArray());
            Assert.Equal(new[] { 3 }, child.ChildTable!.OwnerFieldIdPath.ToArray());
        });

        var map = Assert.Single(table.Fields, static field => field.Id == 5);
        Assert.Equal(CanonicalChildTableKind.MessageMap, map.ChildTable!.Kind);
        Assert.Equal("__map_key", map.ChildTable.MapKeyColumn);
        Assert.All(map.Children, child => Assert.Equal(new[] { 5, child.Id }, child.FieldIdPath.ToArray()));

        var authoring = Assert.Single(
            new SchemaCodeGenerator().Generate(result.Descriptor).Artifacts,
            static artifact => artifact.Kind == "authoring-csharp");
        Assert.Contains("new int[] { 2, 7 }", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("T88_P2_7_", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("GeneratedPhysicalFieldKind.RepeatedMessageChildTable", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("\"Mana cost\"", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("public static int T88_P2_7_", authoring.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Display_comment_and_alias_projection_does_not_change_schema_hash()
    {
        using var left = TemporarySchemaDirectory.Create();
        using var right = TemporarySchemaDirectory.Create();
        left.Write("schema.proto", Proto("First", "Comment A", "Old A"));
        right.Write("schema.proto", Proto("Second", "Comment B", "Old B"));

        var first = await new SchemaCompiler().CompileAsync(left.Path);
        var second = await new SchemaCompiler().CompileAsync(right.Path);
        Assert.True(first.Succeeded, Describe(first));
        Assert.True(second.Succeeded, Describe(second));
        Assert.Equal(first.Descriptor!.SchemaHash, second.Descriptor!.SchemaHash);
        Assert.NotEqual(first.Descriptor.Tables[0].Fields[1].DisplayName, second.Descriptor.Tables[0].Fields[1].DisplayName);
    }

    private static string Proto(string display, string comment, string alias) => $$"""
        syntax = "proto3";
        package game;
        import "exceldb/options.proto";
        message Hero {
          option (exceldb.table) = { kind: ASSET, id: 1 };
          string id = 1 [(exceldb.field) = { key: 1 }];
          int32 hp = 2 [(exceldb.field) = {
            display_name: "{{display}}"
            header_comment: "{{comment}}"
            aliases: "{{alias}}"
          }];
        }
        """;

    private static string Describe(SchemaCompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}"));
}
