using System.IO.Compression;
using System.Text;

namespace SchemaPoc;

/// <summary>最小 xlsx 写出器:inline string、隐藏 sheet/列、数据有效性下拉。零依赖,仅 PoC。</summary>
public sealed class XlsxSheet
{
    public required string Name;
    public bool Hidden;
    public List<int> HiddenColumns = new();                       // 1-based
    public List<List<object?>> Rows = new();                      // string/double/int/bool/null
    public List<(int Col, int FirstRow, string ListSource)> Validations = new();  // ListSource: "A,B" 字面量 或 'sheet'!$A$2:$A$99
    public int FreezeTopRows;

    public void Cell(int row, int col, object? value)
    {
        while (Rows.Count < row) Rows.Add(new List<object?>());
        var r = Rows[row - 1];
        while (r.Count < col) r.Add(null);
        r[col - 1] = value;
    }
}

public static class XlsxWriter
{
    public static void Write(string path, IReadOnlyList<XlsxSheet> sheets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        AddEntry(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
        AddEntry(zip, "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        AddEntry(zip, "xl/workbook.xml", WorkbookXml(sheets));
        AddEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRels(sheets.Count));
        AddEntry(zip, "xl/styles.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
              <fills count="1"><fill><patternFill patternType="none"/></fill></fills>
              <borders count="1"><border/></borders>
              <cellStyleXfs count="1"><xf/></cellStyleXfs>
              <cellXfs count="1"><xf/></cellXfs>
            </styleSheet>
            """);
        for (var i = 0; i < sheets.Count; i++)
            AddEntry(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
    }

    static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    static string ContentTypes(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">""");
        sb.Append("""<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>""");
        sb.Append("""<Default Extension="xml" ContentType="application/xml"/>""");
        sb.Append("""<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>""");
        sb.Append("""<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
        for (var i = 1; i <= sheetCount; i++)
            sb.Append($"""<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
        sb.Append("</Types>");
        return sb.ToString();
    }

    static string WorkbookXml(IReadOnlyList<XlsxSheet> sheets)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
        for (var i = 0; i < sheets.Count; i++)
        {
            var state = sheets[i].Hidden ? " state=\"hidden\"" : "";
            sb.Append($"""<sheet name="{Esc(sheets[i].Name)}" sheetId="{i + 1}"{state} r:id="rId{i + 1}"/>""");
        }
        sb.Append("</sheets></workbook>");
        return sb.ToString();
    }

    static string WorkbookRels(int sheetCount)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (var i = 1; i <= sheetCount; i++)
            sb.Append($"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>""");
        sb.Append($"""<Relationship Id="rId{sheetCount + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>""");
        sb.Append("</Relationships>");
        return sb.ToString();
    }

    static string SheetXml(XlsxSheet sheet)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        if (sheet.FreezeTopRows > 0)
            sb.Append($"""<sheetViews><sheetView workbookViewId="0"><pane ySplit="{sheet.FreezeTopRows}" topLeftCell="A{sheet.FreezeTopRows + 1}" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        if (sheet.HiddenColumns.Count > 0)
        {
            sb.Append("<cols>");
            foreach (var c in sheet.HiddenColumns)
                sb.Append($"""<col min="{c}" max="{c}" hidden="1" width="12"/>""");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            sb.Append($"""<row r="{r + 1}">""");
            var row = sheet.Rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                var v = row[c];
                if (v == null) continue;
                var cellRef = $"{ColName(c + 1)}{r + 1}";
                switch (v)
                {
                    case bool b:
                        sb.Append($"""<c r="{cellRef}" t="b"><v>{(b ? 1 : 0)}</v></c>"""); break;
                    case int or long or float or double:
                        sb.Append($"""<c r="{cellRef}"><v>{Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)}</v></c>"""); break;
                    default:
                        sb.Append($"""<c r="{cellRef}" t="inlineStr"><is><t xml:space="preserve">{Esc(v.ToString()!)}</t></is></c>"""); break;
                }
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        if (sheet.Validations.Count > 0)
        {
            sb.Append($"""<dataValidations count="{sheet.Validations.Count}">""");
            foreach (var (col, firstRow, source) in sheet.Validations)
            {
                var colName = ColName(col);
                var formula = source.StartsWith('\'') || source.Contains('!')
                    ? source
                    : $"&quot;{Esc(source)}&quot;";
                sb.Append($"""<dataValidation type="list" allowBlank="1" showDropDown="0" sqref="{colName}{firstRow}:{colName}1048576"><formula1>{formula}</formula1></dataValidation>""");
            }
            sb.Append("</dataValidations>");
        }
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    public static string ColName(int index)
    {
        var name = "";
        while (index > 0)
        {
            index--;
            name = (char)('A' + index % 26) + name;
            index /= 26;
        }
        return name;
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
