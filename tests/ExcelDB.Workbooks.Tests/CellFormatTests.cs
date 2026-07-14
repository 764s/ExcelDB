using System.Collections.Immutable;
using ExcelDb.Core.Values;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;

namespace ExcelDb.Workbooks.Tests;

public sealed class CellFormatTests
{
    private static readonly CanonicalSchemaDescriptor Schema = new([], [], [], 42);

    [Fact]
    public void Repeated_scalar_join_round_trips_multi_character_separator_and_csv_quotes()
    {
        var field = Field(
            1,
            "tags",
            CanonicalFieldShape.RepeatedScalar,
            "string",
            Join("->"));

        var parsed = CanonicalCellParser.Parse(
            new WorkbookCell("\"a->b\"->\"say \"\"hi\"\"\"->\"\""),
            field,
            Schema);

        Assert.Null(parsed.Error);
        Assert.Equal("[\"a->b\",\"say \\\"hi\\\"\",\"\"]", parsed.Value.Text);
        Assert.Equal("\"a->b\"->\"say \"\"hi\"\"\"->\"\"", parsed.CanonicalPhysicalText);
        Assert.Equal(
            parsed.CanonicalPhysicalText,
            CanonicalCellWriter.Write(parsed.Value, field, Schema).PhysicalText);
    }

    [Fact]
    public void Flat_and_nested_message_join_round_trip_with_missing_tail()
    {
        var cost = Field(
            1,
            "cost",
            CanonicalFieldShape.Message,
            "game.Cost",
            Join("#"),
            children:
            [
                Field(1, "mp", CanonicalFieldShape.Scalar, "int32"),
                Field(2, "hp", CanonicalFieldShape.Scalar, "int32"),
            ]);
        var missingTail = CanonicalCellParser.Parse(new WorkbookCell("30#"), cost, Schema);

        Assert.Null(missingTail.Error);
        Assert.Equal("{\"mp\":30}", missingTail.Value.Text);
        Assert.Equal("30", missingTail.CanonicalPhysicalText);

        var range = Field(
            1,
            "window",
            CanonicalFieldShape.Message,
            "game.Window",
            Join("@", "-"),
            children:
            [
                Field(
                    1,
                    "range",
                    CanonicalFieldShape.Message,
                    "game.Range",
                    children:
                    [
                        Field(1, "start", CanonicalFieldShape.Scalar, "string"),
                        Field(2, "end", CanonicalFieldShape.Scalar, "string"),
                    ]),
                Field(2, "speed", CanonicalFieldShape.Scalar, "double"),
            ]);
        var nested = CanonicalCellParser.Parse(new WorkbookCell("8:00-12:00@2.5"), range, Schema);

        Assert.Null(nested.Error);
        Assert.Equal(
            "{\"range\":{\"end\":\"12:00\",\"start\":\"8:00\"},\"speed\":2.5}",
            nested.Value.Text);
        Assert.Equal("8:00-12:00@2.5", nested.CanonicalPhysicalText);
    }

    [Fact]
    public void Repeated_message_join_round_trips_two_layers()
    {
        var field = Field(
            1,
            "items",
            CanonicalFieldShape.RepeatedMessage,
            "game.ItemStack",
            Join("#", "&"),
            children:
            [
                Field(1, "item", CanonicalFieldShape.Scalar, "string"),
                Field(2, "count", CanonicalFieldShape.Scalar, "int32"),
            ]);

        var parsed = CanonicalCellParser.Parse(new WorkbookCell("sword&2#potion&5"), field, Schema);

        Assert.Null(parsed.Error);
        Assert.Equal(
            "[{\"count\":2,\"item\":\"sword\"},{\"count\":5,\"item\":\"potion\"}]",
            parsed.Value.Text);
        Assert.Equal("sword&2#potion&5", parsed.CanonicalPhysicalText);
    }

    [Fact]
    public void Positional_and_named_maps_have_deterministic_json_and_canonical_order()
    {
        var map = new CanonicalMapDescriptor("string", "int32", CanonicalFieldShape.Scalar, null);
        var positional = Field(
            1,
            "scores",
            CanonicalFieldShape.Map,
            "map<string,int32>",
            Join("||", "="),
            map: map);
        var joined = CanonicalCellParser.Parse(new WorkbookCell("b=2||a=1"), positional, Schema);

        Assert.Null(joined.Error);
        Assert.Equal("{\"a\":1,\"b\":2}", joined.Value.Text);
        Assert.Equal("a=1||b=2", joined.CanonicalPhysicalText);

        var named = positional with { CellFormat = Named("; ", "=") };
        var namedResult = CanonicalCellParser.Parse(new WorkbookCell("b=2; a=1"), named, Schema);

        Assert.Null(namedResult.Error);
        Assert.Equal(joined.Value.Text, namedResult.Value.Text);
        Assert.Equal("a=1; b=2", namedResult.CanonicalPhysicalText);
    }

    [Fact]
    public void Named_message_round_trips_custom_separators_and_quoted_values()
    {
        var field = Field(
            1,
            "stats",
            CanonicalFieldShape.Message,
            "game.Stats",
            Named("#", ":"),
            children:
            [
                Field(1, "label", CanonicalFieldShape.Scalar, "string"),
                Field(2, "hp", CanonicalFieldShape.Scalar, "int32"),
            ]);

        var parsed = CanonicalCellParser.Parse(new WorkbookCell("label:\"boss#phase:2\"#hp:30"), field, Schema);

        Assert.Null(parsed.Error);
        Assert.Equal("{\"hp\":30,\"label\":\"boss#phase:2\"}", parsed.Value.Text);
        Assert.Equal("label:\"boss#phase:2\"#hp:30", parsed.CanonicalPhysicalText);
    }

    [Fact]
    public void Legacy_window_distinguishes_canonical_same_value_legacy_only_and_ambiguity()
    {
        var same = Field(
            1,
            "levels",
            CanonicalFieldShape.RepeatedScalar,
            "int32",
            JoinWithLegacy([";"], Join(",")));

        var bothSame = CanonicalCellParser.Parse(new WorkbookCell("1"), same, Schema);
        Assert.Null(bothSame.Error);
        Assert.False(bothSame.UsedLegacyFormat);
        Assert.Equal("1", bothSame.CanonicalPhysicalText);

        var legacyOnly = CanonicalCellParser.Parse(new WorkbookCell("1,2"), same, Schema);
        Assert.Null(legacyOnly.Error);
        Assert.True(legacyOnly.UsedLegacyFormat);
        Assert.Equal("[1,2]", legacyOnly.Value.Text);
        Assert.Equal("1;2", legacyOnly.CanonicalPhysicalText);

        var children = ImmutableArray.Create(
            Field(1, "left", CanonicalFieldShape.Scalar, "int32"),
            Field(2, "right", CanonicalFieldShape.Scalar, "int32"));
        var ambiguous = Field(
            2,
            "pairs",
            CanonicalFieldShape.RepeatedMessage,
            "game.Pair",
            JoinWithLegacy(["#", "&"], Join("&", "#")),
            children: children);

        var result = CanonicalCellParser.Parse(new WorkbookCell("1&2#3&4"), ambiguous, Schema);
        Assert.Equal(CanonicalValueState.Invalid, result.Value.State);
        Assert.Contains("format.ambiguous", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1#2#3", "only 2 fields")]
    [InlineData("\"unterminated", "unterminated")]
    [InlineData("one#2", "not a valid int32")]
    public void Invalid_join_values_fail_loudly(string text, string expected)
    {
        var field = Field(
            1,
            "pair",
            CanonicalFieldShape.Message,
            "game.Pair",
            Join("#"),
            children:
            [
                Field(1, "left", CanonicalFieldShape.Scalar, "int32"),
                Field(2, "right", CanonicalFieldShape.Scalar, "int32"),
            ]);

        var parsed = CanonicalCellParser.Parse(new WorkbookCell(text), field, Schema);

        Assert.Equal(CanonicalValueState.Invalid, parsed.Value.State);
        Assert.Contains(expected, parsed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registered_codec_uses_same_parse_write_seam_and_missing_codec_is_an_error()
    {
        var field = Field(
            1,
            "duration",
            CanonicalFieldShape.Scalar,
            "string",
            Codec("brackets:v1"));
        var registry = new CellFormatRegistry([new BracketCodec()]);

        var parsed = CanonicalCellParser.Parse(new WorkbookCell("[thirty]"), field, Schema, registry);

        Assert.Null(parsed.Error);
        Assert.Equal("thirty", parsed.Value.Text);
        Assert.Equal("[thirty]", parsed.CanonicalPhysicalText);
        Assert.Equal("[thirty]", CanonicalCellWriter.Write(parsed.Value, field, Schema, registry).PhysicalText);

        var missing = CanonicalCellParser.Parse(new WorkbookCell("[thirty]"), field, Schema);
        Assert.Equal(CanonicalValueState.Invalid, missing.Value.State);
        Assert.Contains("not registered", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_fallback_is_canonicalized_deterministically()
    {
        var field = Field(1, "payload", CanonicalFieldShape.Message, "game.Payload");

        var parsed = CanonicalCellParser.Parse(new WorkbookCell("{ \"z\": 1, \"a\": { \"y\":2, \"x\":1 } }"), field, Schema);

        Assert.Null(parsed.Error);
        Assert.Equal("{\"a\":{\"x\":1,\"y\":2},\"z\":1}", parsed.Value.Text);
        Assert.Equal(parsed.Value.Text, parsed.CanonicalPhysicalText);
    }

    private static CanonicalCellFormat Join(params string[] separators) =>
        new("join", separators.ToImmutableArray(), []);

    private static CanonicalCellFormat JoinWithLegacy(
        IEnumerable<string> separators,
        params CanonicalCellFormat[] legacy) =>
        new("join", separators.ToImmutableArray(), legacy.ToImmutableArray());

    private static CanonicalCellFormat Named(string pairSeparator, string keyValueSeparator) =>
        new("named", [pairSeparator, keyValueSeparator], []);

    private static CanonicalCellFormat Codec(string identity) => new("codec", [identity], []);

    private static CanonicalFieldDescriptor Field(
        int id,
        string name,
        CanonicalFieldShape shape,
        string typeName,
        CanonicalCellFormat? format = null,
        ImmutableArray<CanonicalFieldDescriptor> children = default,
        CanonicalMapDescriptor? map = null) =>
        new(
            id,
            name,
            name,
            shape,
            typeName,
            false,
            null,
            map,
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
            children.IsDefault ? [] : children);

    private sealed class BracketCodec : ICellFormat
    {
        public string Identity => "brackets:v1";

        public string Describe(CellFormatContext context) => "a value enclosed in square brackets";

        public bool TryParse(
            string physicalText,
            CellFormatContext context,
            out string canonicalValue,
            out string? error)
        {
            if (physicalText.Length >= 2 && physicalText[0] == '[' && physicalText[^1] == ']')
            {
                canonicalValue = physicalText[1..^1];
                error = null;
                return true;
            }

            canonicalValue = string.Empty;
            error = "Expected [value].";
            return false;
        }

        public bool TryWrite(
            string canonicalValue,
            CellFormatContext context,
            out string physicalText,
            out string? error)
        {
            physicalText = $"[{canonicalValue}]";
            error = null;
            return true;
        }
    }
}
