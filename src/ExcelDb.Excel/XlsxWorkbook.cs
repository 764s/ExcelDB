using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace ExcelDb.Excel
{
    sealed class XlsxWorkbook
    {
        static readonly XNamespace MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        static readonly XNamespace OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        static readonly XNamespace PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        readonly Dictionary<string, SheetData> _sheets = new Dictionary<string, SheetData>(StringComparer.Ordinal);
        readonly List<string> _sheetOrder = new List<string>();

        public static XlsxWorkbook Empty() => new XlsxWorkbook();

        public static XlsxWorkbook Load(string path)
        {
            var workbook = new XlsxWorkbook();
            using var archive = ZipFile.OpenRead(path);

            var workbookEntry = archive.GetEntry("xl/workbook.xml")
                ?? throw new InvalidDataException("Workbook is missing xl/workbook.xml.");
            var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
                ?? throw new InvalidDataException("Workbook is missing xl/_rels/workbook.xml.rels.");

            var sharedStrings = ReadSharedStrings(archive);
            var relTargets = ReadWorkbookRelationships(relsEntry);

            XDocument workbookDoc;
            using (var stream = workbookEntry.Open())
                workbookDoc = XDocument.Load(stream);

            foreach (var sheet in workbookDoc.Descendants(MainNs + "sheet"))
            {
                var name = (string?)sheet.Attribute("name");
                var relId = (string?)sheet.Attribute(OfficeRelNs + "id");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(relId))
                    continue;
                if (!relTargets.TryGetValue(relId!, out var target))
                    continue;

                var sheetEntry = archive.GetEntry(NormalizeWorkbookTarget(target));
                if (sheetEntry == null)
                    continue;

                workbook.SetSheet(name!, ReadSheet(sheetEntry, sharedStrings));
            }

            return workbook;
        }

        public bool TryGetSheet(string name, out SheetData sheet) => _sheets.TryGetValue(name, out sheet!);

        public void SetSheet(string name, SheetData sheet)
        {
            if (!_sheets.ContainsKey(name))
                _sheetOrder.Add(name);
            _sheets[name] = sheet;
        }

        public void Save(string path, bool createBackup = false)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tempPath = fullPath + ".tmp";
            var backupPath = fullPath + ".bak";
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            try
            {
                using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
                {
                    WriteText(archive, "[Content_Types].xml", BuildContentTypes());
                    WriteText(archive, "_rels/.rels", BuildRootRelationships());
                    WriteText(archive, "xl/workbook.xml", BuildWorkbook());
                    WriteText(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationships());

                    for (int i = 0; i < _sheetOrder.Count; i++)
                    {
                        var sheetName = _sheetOrder[i];
                        WriteText(archive, $"xl/worksheets/sheet{i + 1}.xml", BuildWorksheet(_sheets[sheetName]));
                    }
                }

                if (File.Exists(fullPath))
                {
                    if (createBackup)
                        File.Copy(fullPath, backupPath, overwrite: true);
                    File.Delete(fullPath);
                }
                File.Move(tempPath, fullPath);
            }
            catch
            {
                if (createBackup && !File.Exists(fullPath) && File.Exists(backupPath))
                    File.Copy(backupPath, fullPath, overwrite: true);
                throw;
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        static List<string> ReadSharedStrings(ZipArchive archive)
        {
            var entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
                return new List<string>();

            XDocument doc;
            using (var stream = entry.Open())
                doc = XDocument.Load(stream);

            return doc.Descendants(MainNs + "si")
                .Select(si => string.Concat(si.Descendants(MainNs + "t").Select(t => t.Value)))
                .ToList();
        }

        static Dictionary<string, string> ReadWorkbookRelationships(ZipArchiveEntry entry)
        {
            XDocument doc;
            using (var stream = entry.Open())
                doc = XDocument.Load(stream);

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rel in doc.Descendants(PackageRelNs + "Relationship"))
            {
                var id = (string?)rel.Attribute("Id");
                var target = (string?)rel.Attribute("Target");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(target))
                    result[id!] = target!;
            }
            return result;
        }

        static SheetData ReadSheet(ZipArchiveEntry entry, IReadOnlyList<string> sharedStrings)
        {
            XDocument doc;
            using (var stream = entry.Open())
                doc = XDocument.Load(stream);

            var rows = new List<List<string?>>();
            foreach (var row in doc.Descendants(MainNs + "row"))
            {
                var values = new List<string?>();
                foreach (var cell in row.Elements(MainNs + "c"))
                {
                    var reference = (string?)cell.Attribute("r");
                    var column = reference == null ? values.Count : CellReferenceToColumn(reference);
                    while (values.Count <= column)
                        values.Add(null);
                    values[column] = ReadCell(cell, sharedStrings);
                }
                rows.Add(values);
            }

            var sheet = new SheetData();
            var headerIndex = rows.FindIndex(r => r.Any(v => !string.IsNullOrWhiteSpace(v)));
            if (headerIndex < 0)
                return sheet;

            sheet.Headers.AddRange(rows[headerIndex]);
            for (int i = headerIndex + 1; i < rows.Count; i++)
                sheet.Rows.Add(rows[i]);
            return sheet;
        }

        static string? ReadCell(XElement cell, IReadOnlyList<string> sharedStrings)
        {
            var type = (string?)cell.Attribute("t");
            if (type == "inlineStr")
                return string.Concat(cell.Descendants(MainNs + "t").Select(t => t.Value));

            var raw = cell.Element(MainNs + "v")?.Value;
            if (raw == null)
                return null;

            if (type == "s" && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedIndex))
                return sharedIndex >= 0 && sharedIndex < sharedStrings.Count ? sharedStrings[sharedIndex] : string.Empty;
            if (type == "b")
                return raw == "1" ? "true" : "false";
            return raw;
        }

        static string NormalizeWorkbookTarget(string target)
        {
            var normalized = target.Replace('\\', '/').TrimStart('/');
            return normalized.StartsWith("xl/", StringComparison.Ordinal)
                ? normalized
                : "xl/" + normalized;
        }

        static int CellReferenceToColumn(string reference)
        {
            var column = 0;
            foreach (var ch in reference)
            {
                if (ch < 'A' || ch > 'Z')
                    break;
                column = column * 26 + (ch - 'A' + 1);
            }
            return Math.Max(0, column - 1);
        }

        static string ColumnName(int zeroBasedColumn)
        {
            var n = zeroBasedColumn + 1;
            var chars = new Stack<char>();
            while (n > 0)
            {
                n--;
                chars.Push((char)('A' + n % 26));
                n /= 26;
            }
            return new string(chars.ToArray());
        }

        XDocument BuildContentTypes()
        {
            XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
            var types = new XElement(ns + "Types",
                new XElement(ns + "Default",
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ns + "Default",
                    new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")),
                new XElement(ns + "Override",
                    new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")));

            for (int i = 0; i < _sheetOrder.Count; i++)
            {
                types.Add(new XElement(ns + "Override",
                    new XAttribute("PartName", $"/xl/worksheets/sheet{i + 1}.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
            }
            return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), types);
        }

        static XDocument BuildRootRelationships() =>
            new XDocument(new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(PackageRelNs + "Relationships",
                    new XElement(PackageRelNs + "Relationship",
                        new XAttribute("Id", "rId1"),
                        new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                        new XAttribute("Target", "xl/workbook.xml"))));

        XDocument BuildWorkbook()
        {
            var sheets = new XElement(MainNs + "sheets");
            for (int i = 0; i < _sheetOrder.Count; i++)
            {
                sheets.Add(new XElement(MainNs + "sheet",
                    new XAttribute("name", SafeSheetName(_sheetOrder[i])),
                    new XAttribute("sheetId", i + 1),
                    new XAttribute(OfficeRelNs + "id", $"rId{i + 1}")));
            }

            return new XDocument(new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(MainNs + "workbook",
                    new XAttribute(XNamespace.Xmlns + "r", OfficeRelNs),
                    sheets));
        }

        XDocument BuildWorkbookRelationships()
        {
            var relationships = new XElement(PackageRelNs + "Relationships");
            for (int i = 0; i < _sheetOrder.Count; i++)
            {
                relationships.Add(new XElement(PackageRelNs + "Relationship",
                    new XAttribute("Id", $"rId{i + 1}"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", $"worksheets/sheet{i + 1}.xml")));
            }
            return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), relationships);
        }

        XDocument BuildWorksheet(SheetData sheet)
        {
            var sheetData = new XElement(MainNs + "sheetData");
            WriteRow(sheetData, 1, sheet.Headers);
            for (int i = 0; i < sheet.Rows.Count; i++)
                WriteRow(sheetData, i + 2, sheet.Rows[i]);

            return new XDocument(new XDeclaration("1.0", "utf-8", "yes"),
                new XElement(MainNs + "worksheet", sheetData));
        }

        static void WriteRow(XElement sheetData, int rowNumber, IReadOnlyList<string?> values)
        {
            var row = new XElement(MainNs + "row", new XAttribute("r", rowNumber));
            for (int i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (string.IsNullOrEmpty(value))
                    continue;

                row.Add(new XElement(MainNs + "c",
                    new XAttribute("r", ColumnName(i) + rowNumber.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("t", "inlineStr"),
                    new XElement(MainNs + "is",
                        new XElement(MainNs + "t",
                            new XAttribute(XNamespace.Xml + "space", "preserve"),
                            value))));
            }
            sheetData.Add(row);
        }

        static string SafeSheetName(string name)
        {
            var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
            var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            var safe = new string(chars);
            return safe.Length <= 31 ? safe : safe.Substring(0, 31);
        }

        static void WriteText(ZipArchive archive, string path, XDocument document)
        {
            var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using var stream = entry.Open();
            document.Save(stream);
        }
    }
}
