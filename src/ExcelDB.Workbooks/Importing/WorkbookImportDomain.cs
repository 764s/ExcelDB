using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;

namespace ExcelDb.Workbooks.Importing;

public sealed record WorkbookImportDomainEntry(string WorkbookPath, WorkbookImportResult Import);

/// <summary>Project-wide constraints that cannot be decided by a single workbook import.</summary>
public static class WorkbookImportDomain
{
    public static ImmutableArray<Diagnostic> Validate(
        IEnumerable<WorkbookImportDomainEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var rows = entries
            .SelectMany(entry => entry.Import.Rows.Select(row => (entry.WorkbookPath, Row: row)))
            .ToArray();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach (var group in rows
                     .Where(static item => item.Row.Key is not null)
                     .GroupBy(
                         static item => new TableKey(item.Row.TableId, item.Row.Key!),
                         TableKeyEqualityComparer.Instance)
                     .Where(static group => group.Count() > 1))
        {
            var locations = group
                .Select(static item => $"{item.WorkbookPath}:{item.Row.Location}")
                .Order(StringComparer.Ordinal)
                .ToArray();
            diagnostics.Add(new Diagnostic(
                "EXWB2022",
                DiagnosticSeverity.Blocker,
                string.Join(", ", locations),
                $"Key '{group.Key.Key}' is duplicated across workbooks in table {group.Key.TableId}; no project index is valid."));
        }

        return diagnostics.ToImmutable();
    }

    private sealed class TableKeyEqualityComparer : IEqualityComparer<TableKey>
    {
        public static TableKeyEqualityComparer Instance { get; } = new();

        public bool Equals(TableKey x, TableKey y) =>
            x.TableId == y.TableId && string.Equals(x.Key, y.Key, StringComparison.Ordinal);

        public int GetHashCode(TableKey obj) =>
            HashCode.Combine(obj.TableId, StringComparer.Ordinal.GetHashCode(obj.Key));
    }
}
