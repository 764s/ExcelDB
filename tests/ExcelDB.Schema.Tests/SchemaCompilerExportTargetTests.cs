using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Descriptors;

namespace ExcelDB.Schema.Tests;

public sealed class SchemaCompilerExportTargetTests
{
    [Fact]
    public async Task Defaults_inheritance_explicit_empty_and_future_target_are_canonical()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("game.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";

            message DefaultConfig {
              option (exceldb.table) = { kind: ASSET, id: 10 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              string client_name = 2 [(exceldb.field) = { export_targets: { ids: "client" } }];
              string authoring_note = 3 [(exceldb.field) = { export_targets: {} }];
            }

            message FutureConfig {
              option (exceldb.table) = {
                kind: ASSET,
                id: 11,
                export_targets: { ids: ["server", "lite-client", "client"] }
              };
              string id = 1 [(exceldb.field) = { key: 1 }];
              string compact_name = 2 [(exceldb.field) = { export_targets: { ids: "lite-client" } }];
            }
            """);

        var result = await CompileAsync(schema);

        Assert.True(result.Succeeded, Describe(result));
        var defaults = Assert.Single(result.Descriptor!.Tables, table => table.Name == "DefaultConfig");
        Assert.Equal(["client", "server"], defaults.ExportTargets.ToArray());
        Assert.Equal(["client", "server"], Field(defaults, "id").ExportTargets.ToArray());
        Assert.Equal(["client"], Field(defaults, "client_name").ExportTargets.ToArray());
        Assert.Empty(Field(defaults, "authoring_note").ExportTargets);

        var future = Assert.Single(result.Descriptor.Tables, table => table.Name == "FutureConfig");
        Assert.Equal(["client", "lite-client", "server"], future.ExportTargets.ToArray());
        Assert.Equal(["lite-client"], Field(future, "compact_name").ExportTargets.ToArray());
    }

    [Fact]
    public async Task Target_order_and_legacy_equivalent_spelling_do_not_change_hash()
    {
        using var legacy = TemporarySchemaDirectory.Create();
        legacy.Write("game.proto", SchemaWithExports(
            "export: EXPORT_ALL",
            "export: EDITOR_ONLY"));

        using var explicitTargets = TemporarySchemaDirectory.Create();
        explicitTargets.Write("game.proto", SchemaWithExports(
            "export_targets: { ids: [\"server\", \"client\"] }",
            "export_targets: {}"));

        using var reorderedTargets = TemporarySchemaDirectory.Create();
        reorderedTargets.Write("game.proto", SchemaWithExports(
            "export_targets: { ids: [\"client\", \"server\"] }",
            "export_targets: {}"));

        var legacyResult = await CompileAsync(legacy);
        var explicitResult = await CompileAsync(explicitTargets);
        var reorderedResult = await CompileAsync(reorderedTargets);

        Assert.True(legacyResult.Succeeded, Describe(legacyResult));
        Assert.True(explicitResult.Succeeded, Describe(explicitResult));
        Assert.True(reorderedResult.Succeeded, Describe(reorderedResult));
        Assert.Equal(legacyResult.Descriptor!.SchemaHash, explicitResult.Descriptor!.SchemaHash);
        Assert.Equal(explicitResult.Descriptor.SchemaHash, reorderedResult.Descriptor!.SchemaHash);
    }

    [Fact]
    public async Task File_name_comments_and_declaration_order_do_not_change_hash()
    {
        using var first = TemporarySchemaDirectory.Create();
        first.Write("z/config.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            // First spelling deliberately declares fields in numeric order.
            message Config {
              option (exceldb.table) = { kind: ASSET, id: 5 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              int32 value = 2;
            }
            """);

        using var second = TemporarySchemaDirectory.Create();
        second.Write("a.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            /* Different file path, comment and declaration order. */
            message Config {
              int32 value = 2;
              option (exceldb.table) = { id: 5, kind: ASSET };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }
            """);

        var firstResult = await CompileAsync(first);
        var secondResult = await CompileAsync(second);

        Assert.True(firstResult.Succeeded, Describe(firstResult));
        Assert.True(secondResult.Succeeded, Describe(secondResult));
        Assert.Equal(firstResult.Descriptor!.SchemaHash, secondResult.Descriptor!.SchemaHash);
    }

    [Fact]
    public async Task Target_or_scalar_type_changes_change_hash()
    {
        using var client = TemporarySchemaDirectory.Create();
        client.Write("game.proto", ScalarSchema("client", "int32"));
        using var server = TemporarySchemaDirectory.Create();
        server.Write("game.proto", ScalarSchema("server", "int32"));
        using var stringValue = TemporarySchemaDirectory.Create();
        stringValue.Write("game.proto", ScalarSchema("client", "string"));

        var clientResult = await CompileAsync(client);
        var serverResult = await CompileAsync(server);
        var stringResult = await CompileAsync(stringValue);

        Assert.True(clientResult.Succeeded, Describe(clientResult));
        Assert.True(serverResult.Succeeded, Describe(serverResult));
        Assert.True(stringResult.Succeeded, Describe(stringResult));
        Assert.NotEqual(clientResult.Descriptor!.SchemaHash, serverResult.Descriptor!.SchemaHash);
        Assert.NotEqual(clientResult.Descriptor.SchemaHash, stringResult.Descriptor!.SchemaHash);
    }

    [Fact]
    public async Task Map_and_file_format_defaults_are_fully_materialized()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("unsupported.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            option (exceldb.defaults) = {
              struct_format: { named: { pair_separator: ";", kv_separator: "=" } }
            };

            message Cost { int32 amount = 1; string currency = 2; }
            message Config {
              option (exceldb.table) = { kind: ASSET, id: 6 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              map<string, int32> values = 2;
              Cost cost = 3;
            }
            """);

        var result = await CompileAsync(schema);

        Assert.True(result.Succeeded, Describe(result));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "XDB900");
        var table = Assert.Single(result.Descriptor!.Tables);
        var map = Field(table, "values");
        Assert.Equal("string", map.Map!.KeyTypeName);
        Assert.Equal("int32", map.Map.ValueTypeName);
        Assert.Equal("named", map.CellFormat!.Kind);
        Assert.Equal(["; ", "="], map.CellFormat.Parameters.ToArray());
        var cost = Field(table, "cost");
        Assert.Equal("named", cost.CellFormat!.Kind);
        Assert.Equal([";", "="], cost.CellFormat.Parameters.ToArray());
    }

    [Fact]
    public async Task RowRef_shape_and_lexical_target_resolution_are_enforced()
    {
        using var valid = TemporarySchemaDirectory.Create();
        valid.Write("a.proto", """
            syntax = "proto3";
            package a;
            import "exceldb/options.proto";
            message Target {
              option (exceldb.table) = { kind: ASSET, id: 40 };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }
            message Owner {
              option (exceldb.table) = { kind: ASSET, id: 41 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              exceldb.RowRef target = 2 [(exceldb.field) = { ref_table: "Target" }];
            }
            """);
        valid.Write("b.proto", """
            syntax = "proto3";
            package b;
            import "exceldb/options.proto";
            message Target {
              option (exceldb.table) = { kind: ASSET, id: 42 };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }
            """);

        var validResult = await CompileAsync(valid);
        Assert.True(validResult.Succeeded, Describe(validResult));

        using var invalid = TemporarySchemaDirectory.Create();
        invalid.Write("invalid-ref.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            message Shape {
              option (exceldb.table) = { kind: EMBEDDED };
              string value = 1;
            }
            message Owner {
              option (exceldb.table) = { kind: ASSET, id: 43 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              exceldb.RowRef missing_policy = 2;
              exceldb.RowRef both = 3 [(exceldb.field) = { ref_table: "Shape", ref_group: "shape" }];
              string wrong_type = 4 [(exceldb.field) = { ref_table: "Shape" }];
              exceldb.RowRef embedded = 5 [(exceldb.field) = { ref_table: "Shape" }];
            }
            """);

        var invalidResult = await CompileAsync(invalid);
        Assert.False(invalidResult.Succeeded);
        Assert.True(invalidResult.Diagnostics.Count(diagnostic => diagnostic.Code == "XDB007") >= 4, Describe(invalidResult));
        Assert.Contains(invalidResult.Diagnostics, diagnostic => diagnostic.Message.Contains("not a live ASSET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_duplicate_conflicting_and_widened_targets_are_blockers()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("invalid.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";

            message InvalidTargets {
              option (exceldb.table) = {
                kind: ASSET,
                id: 20,
                export_targets: { ids: "client" }
              };
              string id = 1 [(exceldb.field) = { key: 1 }];
              string widened = 2 [(exceldb.field) = { export_targets: { ids: "server" } }];
              string duplicate = 3 [(exceldb.field) = { export_targets: { ids: ["client", "client"] } }];
              string invalid = 4 [(exceldb.field) = { export_targets: { ids: "Mobile_1" } }];
              string conflict = 5 [(exceldb.field) = {
                export: EDITOR_ONLY,
                export_targets: { ids: "client" }
              }];
            }
            """);

        var result = await CompileAsync(schema);

        Assert.False(result.Succeeded);
        Assert.Null(result.Descriptor);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "XDB016" && diagnostic.Location.EndsWith(".widened", StringComparison.Ordinal));
        Assert.True(result.Diagnostics.Count(diagnostic => diagnostic.Code == "XDB020") >= 3, Describe(result));
    }

    [Fact]
    public async Task Key_and_hard_reference_must_be_visible_in_every_target()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("closure.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";

            message ClientTarget {
              option (exceldb.table) = {
                kind: ASSET,
                id: 30,
                export_targets: { ids: "client" }
              };
              string id = 1 [(exceldb.field) = { key: 1, export_targets: {} }];
            }

            message ServerOwner {
              option (exceldb.table) = {
                kind: ASSET,
                id: 31,
                export_targets: { ids: "server" }
              };
              string id = 1 [(exceldb.field) = { key: 1 }];
              exceldb.RowRef target = 2 [(exceldb.field) = { ref_table: "ClientTarget" }];
            }
            """);

        var result = await CompileAsync(schema);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Code == "XDB016"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Key field", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Reference target", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Package_local_protoc_works_with_empty_path()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("game.proto", SchemaWithExports(string.Empty, string.Empty));

        var previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", string.Empty);
            var result = await CompileAsync(schema);
            Assert.True(result.Succeeded, Describe(result));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    private static async Task<SchemaCompilationResult> CompileAsync(TemporarySchemaDirectory schema)
    {
        var compiler = new SchemaCompiler();
        return await compiler.CompileAsync(schema.Path);
    }

    private static CanonicalFieldDescriptor Field(CanonicalTableDescriptor table, string name) =>
        Assert.Single(table.Fields, field => field.Name == name);

    private static string Describe(SchemaCompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}"));

    private static string SchemaWithExports(string tableExport, string editorFieldExport)
    {
        var tableClause = string.IsNullOrWhiteSpace(tableExport) ? string.Empty : $", {tableExport}";
        return $$"""
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";

            message Config {
              option (exceldb.table) = { kind: ASSET, id: 1{{tableClause}} };
              string id = 1 [(exceldb.field) = { key: 1 }];
              string note = 2 [(exceldb.field) = { {{editorFieldExport}} }];
            }
            """;
    }

    private static string ScalarSchema(string target, string valueType) => $$"""
        syntax = "proto3";
        package game;
        import "exceldb/options.proto";
        message Config {
          option (exceldb.table) = {
            kind: ASSET,
            id: 7,
            export_targets: { ids: "{{target}}" }
          };
          string id = 1 [(exceldb.field) = { key: 1 }];
          {{valueType}} value = 2;
        }
        """;
}
