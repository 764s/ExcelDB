using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Schema.Descriptors;

namespace ExcelDb.Workbooks.Importing;

public sealed record WorkbookValidationContext(
    string WorkbookPath,
    CanonicalSchemaDescriptor Schema,
    CanonicalTableDescriptor Table,
    ImportedRow Row);

/// <summary>Host-registered row validator referenced by a table's stable validator id.</summary>
public interface IWorkbookValidator
{
    string Id { get; }

    IEnumerable<Diagnostic> Validate(WorkbookValidationContext context);
}

public sealed class WorkbookValidatorRegistry
{
    private readonly ImmutableDictionary<string, IWorkbookValidator> _validators;

    public WorkbookValidatorRegistry(IEnumerable<IWorkbookValidator>? validators = null)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, IWorkbookValidator>(StringComparer.Ordinal);
        foreach (var validator in validators ?? [])
        {
            Guard.NotNull(validator);
            Guard.NotNullOrWhiteSpace(validator.Id);
            if (!builder.TryAdd(validator.Id, validator))
                throw new ArgumentException($"Duplicate workbook validator id '{validator.Id}'.", nameof(validators));
        }

        _validators = builder.ToImmutable();
    }

    public bool TryGet(string id, out IWorkbookValidator validator) =>
        _validators.TryGetValue(id, out validator!);
}
