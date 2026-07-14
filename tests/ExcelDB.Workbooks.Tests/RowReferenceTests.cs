using ExcelDb.Core.Values;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;
using ExcelDb.Workbooks.References;

namespace ExcelDb.Workbooks.Tests;

public sealed class RowReferenceTests
{
    [Fact]
    public void HumanTokenRoundTripsUtf8CompositeKeysAndCanonicalizesPercentHex()
    {
        var text = RowReferenceToken.Format(101, ["sword|rare", "中文", "100%"]);

        Assert.Equal("101:sword%7Crare|%E4%B8%AD%E6%96%87|100%25", text);
        Assert.True(RowReferenceToken.TryParse(
            "101:sword%7crare|%e4%b8%ad%e6%96%87|100%25",
            out var parsed,
            out var error),
            error);
        Assert.Equal(["sword|rare", "中文", "100%"], parsed.KeyComponents.ToArray());
        Assert.Equal(text, parsed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0:key")]
    [InlineData("01:key")]
    [InlineData("1:raw slash")]
    [InlineData("1:中文")]
    [InlineData("1:bad%")]
    [InlineData("1:%ff")]
    public void HumanTokenRejectsNonCanonicalOrInvalidGrammar(string text)
    {
        Assert.False(RowReferenceToken.TryParse(text, out _, out _));
    }

    [Fact]
    public void HumanTokenWriterRejectsAnEmptyKeyComponent()
    {
        Assert.Throws<ArgumentException>(() => RowReferenceToken.Format(1, [string.Empty]));
    }

    [Fact]
    public void InternalCompositeKeyRoundTripsWithoutConfusingEmbeddedSeparators()
    {
        var canonical = CanonicalKeyCodec.Format(["a|b", "涓枃", "42"]);

        Assert.True(CanonicalKeyCodec.TryParse(canonical, out var components));
        Assert.Equal(["a|b", "涓枃", "42"], components.ToArray());
        Assert.False(CanonicalKeyCodec.TryParse("03:bad", out _));
        Assert.False(CanonicalKeyCodec.TryParse("3:ab", out _));
    }

    [Fact]
    public void KeyProjectionCarriesCanonicalHumanReferenceToken()
    {
        var workbook = TestData.Workbook(TestData.Row(TestData.GuidText(78), 1, "sword"));

        var bytes = XlsxWorkbookCodec.Write(workbook);

        Assert.Equal("reference_token", XlsxWorkbookCodec.ReadCell(bytes, WorkbookProtocol.KeySheetName, 1, 5)!.Text);
        Assert.Equal("1:sword", XlsxWorkbookCodec.ReadCell(bytes, WorkbookProtocol.KeySheetName, 2, 5)!.Text);
        Assert.Equal("1:Item", XlsxWorkbookCodec.ReadCell(bytes, WorkbookProtocol.KeySheetName, 1, 6)!.Text);
        Assert.Equal("1:sword", XlsxWorkbookCodec.ReadCell(bytes, WorkbookProtocol.KeySheetName, 2, 6)!.Text);
    }

    [Fact]
    public void ImportParsesHumanTokenAndResolverProducesStableAssetIdentity()
    {
        var schema = TestData.Schema();
        var targetWorkbook = TestData.Workbook(TestData.Row(TestData.GuidText(77), 1, "sword"));
        var targetBytes = XlsxWorkbookCodec.Write(targetWorkbook);
        var imported = WorkbookImporter.Import(
            "items.xlsx",
            targetBytes,
            XlsxWorkbookCodec.Read(targetBytes),
            schema);
        var referenceField = TestData.Field(9, "item", "item", "exceldb.RowRef") with
        {
            Shape = CanonicalFieldShape.Message,
            ReferenceTable = "Item",
        };

        var parsed = CanonicalCellParser.Parse(new WorkbookCell("1:sword"), referenceField, schema);
        var resolution = RowReferenceResolver.Resolve(referenceField, parsed.Value.Text!, schema, imported.Rows);

        Assert.Null(parsed.Error);
        Assert.Equal(CanonicalValueState.Value, parsed.Value.State);
        Assert.True(resolution.Succeeded, resolution.Error);
        Assert.Equal(imported.Rows[0].Identity, resolution.Identity);
        Assert.Equal("1:sword", resolution.CanonicalToken);
    }
}
