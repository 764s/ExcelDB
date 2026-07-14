using System.Collections.Immutable;
using ExcelDb.Schema.Hashing;

namespace ExcelDb.Schema.Descriptors;

public static class CanonicalSchemaFactory
{
    public static CanonicalSchemaDescriptor Create(
        IEnumerable<CanonicalTableDescriptor> tables,
        IEnumerable<CanonicalRetiredTableDescriptor>? retiredTables = null,
        IEnumerable<CanonicalEnumDescriptor>? enums = null)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var normalized = new CanonicalSchemaDescriptor(
            tables.Select(NormalizeTable)
                .OrderBy(static table => table.Id)
                .ThenBy(static table => table.FullName, StringComparer.Ordinal)
                .ToImmutableArray(),
            (retiredTables ?? [])
                .OrderBy(static table => table.Id)
                .ThenBy(static table => table.FullName, StringComparer.Ordinal)
                .ToImmutableArray(),
            (enums ?? [])
                .Select(NormalizeEnum)
                .OrderBy(static item => item.FullName, StringComparer.Ordinal)
                .ToImmutableArray(),
            0);

        return normalized with { SchemaHash = CanonicalSchemaSerializer.ComputeHash(normalized) };
    }

    private static CanonicalTableDescriptor NormalizeTable(CanonicalTableDescriptor table) => table with
    {
        Implements = NormalizeStrings(table.Implements),
        ValidatorIds = NormalizeStrings(table.ValidatorIds),
        ExportTargets = NormalizeStrings(table.ExportTargets),
        ReservedFieldNumbers = NormalizeRanges(table.ReservedFieldNumbers),
        Fields = table.Fields
            .Select(field => NormalizeField(field, [], null))
            .OrderBy(static field => field.Id)
            .ToImmutableArray(),
        KeyFieldIds = table.KeyFieldIds.ToImmutableArray(),
    };

    private static CanonicalFieldDescriptor NormalizeField(
        CanonicalFieldDescriptor field,
        ImmutableArray<int> parentIdPath,
        CanonicalChildTableDescriptor? inheritedChildTable)
    {
        var idPath = parentIdPath.Add(field.Id);
        var childTable = field.ChildTable is null
            ? inheritedChildTable
            : field.ChildTable with { OwnerFieldIdPath = idPath };
        return field with
        {
            FieldIdPath = idPath,
            Aliases = NormalizeStrings(field.Aliases),
            ExportTargets = NormalizeStrings(field.ExportTargets),
            ReservedChildFieldNumbers = NormalizeRanges(field.ReservedChildFieldNumbers),
            Children = field.Children
                .Select(child => NormalizeField(child, idPath, childTable))
                .OrderBy(static child => child.Id)
                .ToImmutableArray(),
            CellFormat = NormalizeFormat(field.CellFormat),
            ChildTable = childTable,
        };
    }

    private static CanonicalCellFormat? NormalizeFormat(CanonicalCellFormat? format)
    {
        if (format is null)
            return null;

        return format with
        {
            Legacy = format.Legacy
                .Select(NormalizeFormat)
                .Cast<CanonicalCellFormat>()
                .OrderBy(static item => item.Kind, StringComparer.Ordinal)
                .ThenBy(static item => string.Join("\u001f", item.Parameters), StringComparer.Ordinal)
                .ToImmutableArray(),
        };
    }

    private static CanonicalEnumDescriptor NormalizeEnum(CanonicalEnumDescriptor value) => value with
    {
        ReservedNumbers = NormalizeRanges(value.ReservedNumbers),
        Values = value.Values
            .OrderBy(static item => item.Number)
            .ThenBy(static item => item.Name, StringComparer.Ordinal)
            .ToImmutableArray(),
    };

    private static ImmutableArray<string> NormalizeStrings(IEnumerable<string> values) =>
        values.OrderBy(static value => value, StringComparer.Ordinal).ToImmutableArray();

    private static ImmutableArray<CanonicalReservedNumberRange> NormalizeRanges(
        IEnumerable<CanonicalReservedNumberRange> values)
    {
        var ordered = values.OrderBy(static value => value.Start)
            .ThenBy(static value => value.EndExclusive)
            .ToArray();
        if (ordered.Length == 0)
            return [];

        var result = ImmutableArray.CreateBuilder<CanonicalReservedNumberRange>();
        var current = ordered[0];
        foreach (var next in ordered.AsSpan(1))
        {
            if (next.Start <= current.EndExclusive)
            {
                current = current with { EndExclusive = Math.Max(current.EndExclusive, next.EndExclusive) };
                continue;
            }

            result.Add(current);
            current = next;
        }

        result.Add(current);
        return result.ToImmutable();
    }
}
