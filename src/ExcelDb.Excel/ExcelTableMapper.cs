using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using ExcelDb.Loading;
using ExcelDb.Schema;

namespace ExcelDb.Excel
{
    static class ExcelTableMapper
    {
        public static TableContent Read(
            TableSchema schema,
            SheetData sheet,
            SchemaRegistry registry,
            ExcelTableLoaderOptions options)
        {
            var idColumn = FindHeader(sheet.Headers, options.IdColumnName);
            if (idColumn < 0)
                throw new FormatException($"Worksheet '{schema.Name}' is missing the '{options.IdColumnName}' id column.");

            var fieldsByColumn = new Dictionary<int, FieldDescriptor>();
            for (int i = 0; i < sheet.Headers.Count; i++)
            {
                if (i == idColumn)
                    continue;

                var header = sheet.Headers[i];
                if (string.IsNullOrWhiteSpace(header))
                    continue;

                var field = ExcelFieldCodec.FindField(schema.Descriptor, header.Trim());
                if (field != null)
                    fieldsByColumn[i] = field;
            }

            var records = new List<TableRecord>();
            foreach (var row in sheet.Rows)
            {
                var idText = GetCell(row, idColumn);
                if (string.IsNullOrWhiteSpace(idText))
                    continue;

                var id = int.Parse(idText.Trim(), CultureInfo.InvariantCulture);
                var message = schema.Descriptor.Parser.ParseFrom(ByteString.Empty);
                foreach (var pair in fieldsByColumn)
                {
                    var cell = GetCell(row, pair.Key);
                    if (string.IsNullOrWhiteSpace(cell))
                        continue;
                    ExcelFieldCodec.SetField(message, pair.Value, cell, registry);
                }

                records.Add(new TableRecord(id, message));
            }

            return new TableContent(records);
        }

        public static SheetData Write(
            TableSchema schema,
            TableContent content,
            ExcelTableLoaderOptions options)
        {
            var sheet = new SheetData();
            sheet.Headers.Add(options.IdColumnName);
            foreach (var field in schema.Descriptor.Fields.InDeclarationOrder())
                sheet.Headers.Add(field.Name);

            foreach (var record in content.Rows.OrderBy(row => row.Id))
            {
                var row = new List<string?>
                {
                    record.Id.ToString(CultureInfo.InvariantCulture)
                };
                foreach (var field in schema.Descriptor.Fields.InDeclarationOrder())
                    row.Add(ExcelFieldCodec.FormatField(record.Row, field));
                sheet.Rows.Add(row);
            }

            return sheet;
        }

        static int FindHeader(IReadOnlyList<string?> headers, string name)
        {
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i]?.Trim(), name, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        static string? GetCell(IReadOnlyList<string?> row, int index) =>
            index >= 0 && index < row.Count ? row[index] : null;
    }
}
