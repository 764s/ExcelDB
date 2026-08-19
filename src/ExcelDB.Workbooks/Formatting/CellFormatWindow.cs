using ExcelDb.Core.Values;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Model;

namespace ExcelDb.Workbooks.Formatting;

internal readonly record struct CellFormatWindowResult(
    string CanonicalValue,
    string CanonicalPhysicalText,
    bool UsedLegacyFormat);

internal static class CellFormatWindow
{
    public static bool TryParse(
        string physicalText,
        CanonicalFieldDescriptor field,
        CanonicalSchemaDescriptor schema,
        CellFormatRegistry registry,
        out CellFormatWindowResult result,
        out string? error)
    {
        result = default;
        error = null;
        var declaration = field.CellFormat;
        if (declaration is null)
        {
            error = $"Field '{field.PropertyPath}' has no CellFormat declaration.";
            return false;
        }

        var context = new CellFormatContext(field, schema);
        if (!registry.TryResolve(declaration, out var canonicalFormatter, out var resolutionError))
        {
            error = resolutionError;
            return false;
        }

        var candidates = new List<(bool IsCanonical, string Identity, string Value)>();
        var failures = new List<string>();
        TryCandidate(declaration, canonicalFormatter!, isCanonical: true);
        foreach (var legacy in declaration.Legacy)
        {
            if (!registry.TryResolve(legacy, out var formatter, out resolutionError))
            {
                failures.Add(resolutionError!);
                continue;
            }
            TryCandidate(legacy, formatter!, isCanonical: false);
        }

        if (candidates.Count == 0)
        {
            error = failures.Count == 0
                ? $"No CellFormat accepted '{physicalText}'."
                : $"No CellFormat accepted '{physicalText}': {string.Join(" ", failures)}";
            return false;
        }

        var distinctValues = candidates
            .Select(static candidate => candidate.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctValues.Length != 1)
        {
            error = "format.ambiguous: canonical and legacy CellFormats parse the cell to different canonical values: " +
                    string.Join(
                        "; ",
                        candidates.Select(static candidate => $"{candidate.Identity} => {candidate.Value}"));
            return false;
        }

        var canonicalValue = distinctValues[0];
        if (!canonicalFormatter!.TryWrite(canonicalValue, context, out var canonicalPhysicalText, out var writeError))
        {
            error = $"Canonical CellFormat '{canonicalFormatter.Identity}' cannot write the parsed value: {writeError}";
            return false;
        }

        result = new CellFormatWindowResult(
            canonicalValue,
            canonicalPhysicalText,
            UsedLegacyFormat: !candidates.Any(static candidate => candidate.IsCanonical));
        return true;

        void TryCandidate(CanonicalCellFormat candidate, ICellFormat formatter, bool isCanonical)
        {
            if (!formatter.TryParse(physicalText, context, out var value, out var parseError))
            {
                failures.Add($"{formatter.Identity}: {parseError}");
                return;
            }

            if (IsJsonShape(field.Shape) && !CanonicalJson.TryNormalize(value, out value, out parseError))
            {
                failures.Add($"{formatter.Identity}: codec returned {parseError}");
                return;
            }

            candidates.Add((isCanonical, formatter.Identity, value));
        }
    }

    private static bool IsJsonShape(CanonicalFieldShape shape) => shape is
        CanonicalFieldShape.Message or
        CanonicalFieldShape.RepeatedScalar or
        CanonicalFieldShape.RepeatedEnum or
        CanonicalFieldShape.RepeatedMessage or
        CanonicalFieldShape.Map;
}

public readonly record struct CanonicalCellWriteResult(string? PhysicalText, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Writes an already-canonical value using only the canonical (never legacy) format.</summary>
public static class CanonicalCellWriter
{
    private static readonly CellFormatRegistry BuiltInOnly = new();

    public static CanonicalCellWriteResult Write(
        CanonicalValue value,
        CanonicalFieldDescriptor field,
        CanonicalSchemaDescriptor schema,
        CellFormatRegistry? formats = null)
    {
        Guard.NotNull(field);
        Guard.NotNull(schema);
        switch (value.State)
        {
            case CanonicalValueState.Missing:
                return new CanonicalCellWriteResult(string.Empty, null);
            case CanonicalValueState.Null:
                return new CanonicalCellWriteResult(WorkbookProtocol.ExplicitNullToken, null);
            case CanonicalValueState.Invalid:
                return new CanonicalCellWriteResult(null, "An invalid canonical value cannot be written.");
        }

        var canonical = value.Text ?? string.Empty;
        if (field.CellFormat is null)
        {
            var physical = string.Equals(field.TypeName, "string", StringComparison.Ordinal)
                           && string.Equals(canonical, WorkbookProtocol.ExplicitNullToken, StringComparison.Ordinal)
                ? "%7E"
                : canonical;
            return new CanonicalCellWriteResult(physical, null);
        }

        var registry = formats ?? BuiltInOnly;
        if (!registry.TryResolve(field.CellFormat, out var formatter, out var error))
            return new CanonicalCellWriteResult(null, error);
        if (!formatter!.TryWrite(canonical, new CellFormatContext(field, schema), out var result, out error))
            return new CanonicalCellWriteResult(null, error);
        return new CanonicalCellWriteResult(result, null);
    }
}
