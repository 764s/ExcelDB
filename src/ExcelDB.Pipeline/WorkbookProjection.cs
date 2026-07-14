using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;

namespace ExcelDb.Pipeline;

internal static class WorkbookProjection
{
    public static byte[] CreateOrAddTable(
        string workbookPath,
        CanonicalSchemaDescriptor schema,
        int tableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        ArgumentNullException.ThrowIfNull(schema);
        var tableSchema = schema.Tables.SingleOrDefault(table => table.Id == tableId && table.Kind == CanonicalTableKind.Asset)
            ?? throw new InvalidDataException($"Created table id {tableId} is absent from the candidate schema.");
        WorkbookDefinition projected;
        byte[]? sourceBytes = null;
        if (!File.Exists(workbookPath))
        {
            projected = new WorkbookDefinition(
                DeterministicWorkbookGuid(Path.GetFullPath(workbookPath)),
                schema.SchemaHash,
                DateTimeOffset.UnixEpoch,
                [WorkbookLayout.CreateTable(tableSchema)]);
        }
        else
        {
            sourceBytes = File.ReadAllBytes(workbookPath);
            var current = XlsxWorkbookCodec.Read(sourceBytes);
            if (current.Tables.Any(table => table.TableId == tableId))
                throw new InvalidDataException($"Workbook already contains table id {tableId}.");
            var updated = current.Tables
                .Select(table => ProjectTable(table, schema.Tables.SingleOrDefault(candidate => candidate.Id == table.TableId)))
                .Append(WorkbookLayout.CreateTable(tableSchema))
                .OrderBy(static table => table.TableId)
                .ToImmutableArray();
            projected = current with { SchemaHash = schema.SchemaHash, Tables = updated };
        }

        return sourceBytes is null
            ? XlsxWorkbookCodec.Write(projected)
            : XlsxWorkbookCodec.Project(sourceBytes, projected);
    }

    public static byte[]? UpdateExistingTables(string workbookPath, CanonicalSchemaDescriptor schema)
    {
        if (!File.Exists(workbookPath))
            return null;
        var bytes = File.ReadAllBytes(workbookPath);
        var current = XlsxWorkbookCodec.Read(bytes);
        var changed = current.SchemaHash != schema.SchemaHash;
        var projectedTables = ImmutableArray.CreateBuilder<WorkbookTable>();
        foreach (var table in current.Tables)
        {
            var tableSchema = schema.Tables.SingleOrDefault(candidate => candidate.Id == table.TableId);
            var projected = ProjectTable(table, tableSchema);
            projectedTables.Add(projected);
            changed |= !Equals(table, projected);
        }

        if (!changed)
            return null;
        var result = current with { SchemaHash = schema.SchemaHash, Tables = projectedTables.ToImmutable() };
        var projectedBytes = XlsxWorkbookCodec.Project(bytes, result);
        return bytes.AsSpan().SequenceEqual(projectedBytes) ? null : projectedBytes;
    }

    private static WorkbookTable ProjectTable(WorkbookTable current, CanonicalTableDescriptor? schema)
    {
        // Retired/removed tables remain physically intact so historical data is never silently purged.
        if (schema is null || schema.Kind != CanonicalTableKind.Asset)
            return current;
        var layout = WorkbookLayout.CreateTable(schema);
        var oldPropertyByFieldPath = current.Columns.ToDictionary(static column => column.FieldPath, static column => column.PropertyPath, StringComparer.Ordinal);
        var rows = current.Rows.Select(row =>
        {
            var cells = ImmutableDictionary.CreateBuilder<string, WorkbookCell>(StringComparer.Ordinal);
            foreach (var column in layout.Columns)
            {
                if (row.Cells.TryGetValue(column.PropertyPath, out var direct))
                    cells[column.PropertyPath] = direct;
                else if (oldPropertyByFieldPath.TryGetValue(column.FieldPath, out var oldProperty)
                         && row.Cells.TryGetValue(oldProperty, out var renamed))
                    cells[column.PropertyPath] = renamed;
            }
            return row with { Cells = cells.ToImmutable() };
        }).ToImmutableArray();
        return current with
        {
            ProtoName = schema.Name,
            SheetName = schema.SheetName,
            Columns = layout.Columns,
            Rows = rows,
        };
    }

    private static Guid DeterministicWorkbookGuid(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.Replace('\\', '/').ToLowerInvariant()));
        var bytes = hash[..16];
        if (bytes.All(static value => value == 0))
            bytes[0] = 1;
        return new Guid(bytes);
    }
}
