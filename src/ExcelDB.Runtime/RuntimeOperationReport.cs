using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;

namespace ExcelDb.Runtime;

public readonly record struct RuntimeOperationReport(
    string Operation,
    bool Succeeded,
    bool Changed,
    RuntimeSchemaIdentity? Expected,
    RuntimeSchemaIdentity? Declared,
    RuntimeSchemaIdentity? Actual,
    SourceInfo? Source,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public static RuntimeOperationReport Empty { get; } =
        new("none", false, false, null, null, null, null, []);
}

public static class RuntimeDiagnosticCodes
{
    public const string AlreadyOpen = "runtime.open.already-open";
    public const string Closed = "runtime.closed";
    public const string InvalidRegistry = "runtime.registry.invalid";
    public const string SchemaHashMismatch = "runtime.schema-hash.mismatch";
    public const string ExportTargetMismatch = "runtime.export-target.mismatch";
    public const string SourceRejected = "runtime.mode.source-rejected";
    public const string SourceFailure = "runtime.source.failed";
    public const string CommitFailure = "runtime.commit.failed";
    public const string PendingIdentity = "identity.pending";
    public const string DuplicateIdentity = "runtime.identity.duplicate";
    public const string DuplicateKey = "runtime.key.duplicate";
    public const string DanglingReference = "runtime.reference.dangling";
    public const string InvalidRow = "runtime.row.invalid";
    public const string HotReloadRejected = "runtime.hot-reload.rejected";
}
