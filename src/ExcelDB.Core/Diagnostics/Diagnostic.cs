namespace ExcelDb.Core.Diagnostics;

public enum DiagnosticSeverity : byte
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Blocker = 3,
}

public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Location,
    string Message)
{
    public bool IsFailure => Severity >= DiagnosticSeverity.Error;

    public bool IsBlocker => Severity == DiagnosticSeverity.Blocker;
}
