namespace ExcelDb.Schema.Diagnostics;

public enum SchemaDiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Blocker = 3,
}

public sealed record SchemaDiagnostic(
    string Code,
    SchemaDiagnosticSeverity Severity,
    string Location,
    string Message)
{
    public bool IsBlocking => Severity is SchemaDiagnosticSeverity.Error or SchemaDiagnosticSeverity.Blocker;
}
