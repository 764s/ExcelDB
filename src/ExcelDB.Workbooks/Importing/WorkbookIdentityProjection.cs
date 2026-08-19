using System.Collections.Immutable;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Model;

namespace ExcelDb.Workbooks.Importing;

/// <summary>
/// Projects schema-derived import keys back onto the physical workbook model for identity
/// analysis.  Values copied from <c>__exceldb_keys</c> remain explicitly untrusted and must never
/// be used as recovery evidence.
/// </summary>
public static class WorkbookIdentityProjection
{
    public static WorkbookDefinition BindCanonicalKeys(
        WorkbookDefinition workbook,
        WorkbookImportResult import,
        CanonicalSchemaDescriptor? schema = null)
    {
        Guard.NotNull(workbook);
        Guard.NotNull(import);
        var keys = import.Rows.ToDictionary(
            static row => (row.TableId, row.RowNumber),
            static row => row.Key);
        var retiredIds = schema?.RetiredTables.Select(static table => table.Id).ToHashSet() ?? [];
        var tables = workbook.Tables.Select(table => table with
        {
            IsRetiredPreserved = table.IsRetiredPreserved || retiredIds.Contains(table.TableId),
            Rows = table.Rows.Select((row, index) =>
            {
                var rowNumber = row.SourceRowNumber ?? (table.DataStartRow + index);
                return keys.TryGetValue((table.TableId, rowNumber), out var key)
                    ? row with { Key = key, KeyIsProjection = false }
                    : row;
            }).ToImmutableArray(),
        }).ToImmutableArray();
        return workbook with { Tables = tables };
    }
}
