using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Workbooks.Transactions;

namespace ExcelDb.Workbooks.Authoring;

/// <summary>
/// Mandatory startup gate: all pending journals are recovered before any workbook import may
/// publish a resident view.
/// </summary>
public static class AuthoringStartupRecovery
{
    public static OperationReport RecoverBeforeImport(IEnumerable<string> journalDirectories)
    {
        ArgumentNullException.ThrowIfNull(journalDirectories);
        var reports = journalDirectories
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .SelectMany(static directory => MultiWorkbookTransaction.RecoverPending(directory))
            .ToImmutableArray();
        if (reports.IsEmpty)
            return OperationReport.Success("authoring-startup-recovery", WorkbookWriteService.ToolVersion, applied: false);
        return new OperationReport(
            "authoring-startup-recovery",
            WorkbookWriteService.ToolVersion,
            reports.Any(static report => report.Applied),
            reports.SelectMany(static report => report.Diagnostics).ToImmutableArray(),
            reports.SelectMany(static report => report.Artifacts).ToImmutableArray());
    }

    public static OperationReport RecoverWorkbookDirectoryBeforeImport(string workbookPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(workbookPath))
            ?? throw new ArgumentException("Workbook path has no parent directory.", nameof(workbookPath));
        return RecoverBeforeImport([directory]);
    }
}
