using ExcelDb.Protocol;
using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Descriptors;

namespace ExcelDB.Schema.Tests;

public sealed class SchemaCompilerCanonicalTests
{
    [Fact]
    public async Task Full_m1_shapes_options_reserved_and_defaults_are_canonical()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("full.proto", FullSchema);

        var result = await new SchemaCompiler().CompileAsync(schema.Path);

        Assert.True(result.Succeeded, Describe(result));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "XDB900");
        var descriptor = result.Descriptor!;
        var table = Assert.Single(descriptor.Tables, static table => table.Name == "Config");
        Assert.Equal([30L, 40L], table.ReservedFieldNumbers.Select(static range => range.Start).ToArray());
        Assert.Equal([31L, 42L], table.ReservedFieldNumbers.Select(static range => range.EndExclusive).ToArray());

        var map = Field(table, "damage_scale");
        Assert.Equal(CanonicalFieldShape.Map, map.Shape);
        Assert.Equal("game.DamageType", map.Map!.KeyEnumType);
        Assert.Equal(CanonicalFieldShape.Scalar, map.Map.ValueShape);
        Assert.Equal(["| ", "="], map.CellFormat!.Parameters.ToArray());

        var weighted = Field(table, "drops");
        Assert.Equal(new CanonicalWeighted(4, 5), weighted.Weighted);
        Assert.Equal("Drops", weighted.ChildTableSheetName);
        Assert.Equal([1, 2], weighted.ReservedChildFieldNumbers.SelectMany(Expand).ToArray());

        var expression = Field(table, "formula").Expression;
        Assert.Equal(new CanonicalExpression("game.Symbols", "float"), expression);

        var variants = table.Fields.Where(static field => field.OneOfGroup == "effect").OrderBy(static field => field.Id).ToArray();
        Assert.Equal([20, 21], variants.Select(static field => field.Id).ToArray());
        Assert.All(variants, static field => Assert.Equal(CanonicalFieldShape.OneOfVariant, field.Shape));

        var list = Field(table, "levels");
        Assert.Equal("join", list.CellFormat!.Kind);
        Assert.Equal([";"], list.CellFormat.Parameters.ToArray());
        var legacy = Assert.Single(list.CellFormat.Legacy);
        Assert.Equal([","] , legacy.Parameters.ToArray());

        var enumDescriptor = Assert.Single(descriptor.Enums, static item => item.FullName == "game.DamageType");
        var enumReserved = Assert.Single(enumDescriptor.ReservedNumbers);
        Assert.True(enumReserved.Contains(9));
        Assert.False(enumReserved.Contains(10));
    }

    [Fact]
    public async Task Invalid_complex_semantics_report_their_owned_lints_without_xdb900()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("invalid.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";

            enum E { E_UNSPECIFIED = 0; E_A = 1; }
            message BadSymbols { repeated int32 values = 1; }
            message Entry { string weight = 1; int32 condition = 2; }
            message Config {
              option (exceldb.table) = { kind: ASSET, id: 91 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              map<string, int32> bad_map = 2 [(exceldb.field) = { map_key_enum: "E" }];
              repeated Entry bad_weighted = 3 [(exceldb.field) = { weighted: { weight_field: 1, condition_field: 2 } }];
              int32 bad_expression = 4 [(exceldb.field) = { expression: { symbols_type: "BadSymbols", result: EXPR_BOOL } }];
              repeated int32 bad_format = 5 [(exceldb.field) = {
                format: { join: { separators: ["#", "##"] }, legacy: { join: { separators: ["#", "##"] } } }
              }];
            }
            """);

        var result = await new SchemaCompiler().CompileAsync(schema.Path);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "XDB008");
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "XDB009");
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "XDB010");
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "XDB017");
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "XDB018");
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "XDB900");
    }

    [Fact]
    public async Task Canonical_bytes_and_hash_have_a_fixed_golden()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("golden.proto", """
            syntax = "proto3";
            package golden;
            import "exceldb/options.proto";
            enum Kind { KIND_UNSPECIFIED = 0; KIND_ONE = 1; reserved 7; }
            message Config {
              option (exceldb.table) = { kind: ASSET, id: 17, export_targets: { ids: ["server", "client"] } };
              reserved 8, 10 to 12;
              string id = 1 [(exceldb.field) = { key: 1 }];
              repeated Kind kinds = 2;
              map<string, int32> values = 3;
            }
            """);

        var result = await new SchemaCompiler().CompileAsync(schema.Path);
        Assert.True(result.Succeeded, Describe(result));

        Assert.Equal(0x5d2b8084c78113b9UL, result.Descriptor!.SchemaHash);
    }

    [Fact]
    public void RowRef_wire_contract_is_table_plus_exact_16_byte_row_guid_field()
    {
        var fields = RowRef.Descriptor.Fields.InFieldNumberOrder();
        Assert.Collection(
            fields,
            table =>
            {
                Assert.Equal(1, table.FieldNumber);
                Assert.Equal(Google.Protobuf.Reflection.FieldType.Int32, table.FieldType);
            },
            rowGuid =>
            {
                Assert.Equal(2, rowGuid.FieldNumber);
                Assert.Equal("row_guid", rowGuid.Name);
                Assert.Equal(Google.Protobuf.Reflection.FieldType.Bytes, rowGuid.FieldType);
            });

        var value = new RowRef { Table = 7, RowGuid = Google.Protobuf.ByteString.CopyFrom(new byte[16]) };
        Assert.Equal(16, value.RowGuid.Length);
    }

    private static IEnumerable<int> Expand(CanonicalReservedNumberRange range)
    {
        for (var number = range.Start; number < range.EndExclusive; number++)
            yield return checked((int)number);
    }

    private static CanonicalFieldDescriptor Field(CanonicalTableDescriptor table, string name) =>
        Assert.Single(table.Fields, field => field.Name == name);

    private static string Describe(SchemaCompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}"));

    private const string FullSchema = """
        syntax = "proto3";
        package game;
        import "exceldb/options.proto";
        option (exceldb.defaults) = {
          struct_format: { named: { pair_separator: "; ", kv_separator: "=" } },
          scalar_list_format: { join: { separators: ";" } },
          map_format: { named: { pair_separator: "| ", kv_separator: "=" } }
        };

        enum DamageType {
          DAMAGE_TYPE_UNSPECIFIED = 0;
          FIRE = 1;
          reserved 9;
        }
        message Symbols { float level = 1; bool elite = 2; }
        message DropEntry {
          reserved 1 to 2;
          string item = 3;
          float weight = 4;
          string condition = 5 [(exceldb.field) = { expression: { symbols_type: "Symbols", result: EXPR_BOOL } }];
        }
        message DamageEffect { int32 amount = 1; }
        message DotEffect { int32 amount = 1; float duration = 2; }

        message Config {
          option (exceldb.table) = { kind: ASSET, id: 90 };
          reserved 30, 40 to 41;
          string id = 1 [(exceldb.field) = { key: 1 }];
          repeated string tags = 2 [(exceldb.field) = { labels: true }];
          map<int32, float> damage_scale = 3 [(exceldb.field) = { map_key_enum: "DamageType" }];
          repeated DropEntry drops = 4 [(exceldb.field) = {
            child_table: { sheet_name: "Drops" },
            weighted: { weight_field: 4, condition_field: 5 }
          }];
          string formula = 5 [(exceldb.field) = { expression: { symbols_type: "Symbols", result: EXPR_FLOAT } }];
          repeated int32 levels = 6 [(exceldb.field) = {
            format: { join: { separators: ";" }, legacy: { join: { separators: "," } } }
          }];
          oneof effect {
            DamageEffect damage = 20;
            DotEffect dot = 21;
          }
        }
        """;
}
