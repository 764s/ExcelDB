using System.Collections.Immutable;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Diagnostics;

namespace ExcelDb.Schema.Compilation;

public sealed record SchemaCompilationResult(
    CanonicalSchemaDescriptor? Descriptor,
    ImmutableArray<SchemaDiagnostic> Diagnostics)
{
    public bool Succeeded => Descriptor is not null && !Diagnostics.Any(static diagnostic => diagnostic.IsBlocking);
}
