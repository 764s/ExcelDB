using System.Collections.Immutable;

namespace ExcelDb.Schema.Descriptors;

public enum CanonicalTableKind
{
    Asset = 1,
    Embedded = 2,
}

public enum CanonicalFieldShape
{
    Scalar = 1,
    Enum = 2,
    Message = 3,
    RepeatedScalar = 4,
    RepeatedEnum = 5,
    RepeatedMessage = 6,
    Map = 7,
    OneOfVariant = 8,
}

public enum CanonicalDeletePolicy
{
    Block = 1,
    SetNull = 2,
    Cascade = 3,
}

public enum CanonicalExpandMode
{
    SingleCell = 1,
    ExpandedColumns = 2,
}

public sealed record CanonicalSchemaDescriptor(
    ImmutableArray<CanonicalTableDescriptor> Tables,
    ImmutableArray<CanonicalRetiredTableDescriptor> RetiredTables,
    ImmutableArray<CanonicalEnumDescriptor> Enums,
    ulong SchemaHash)
{
    public bool TryGetTable(string fullName, out CanonicalTableDescriptor? table)
    {
        table = Tables.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName, fullName, StringComparison.Ordinal)
            || string.Equals(candidate.Name, fullName, StringComparison.Ordinal));
        return table is not null;
    }
}

public sealed record CanonicalTableDescriptor(
    int Id,
    string Name,
    string FullName,
    CanonicalTableKind Kind,
    string SheetName,
    ImmutableArray<string> Implements,
    ImmutableArray<string> ValidatorIds,
    ImmutableArray<string> ExportTargets,
    ImmutableArray<CanonicalReservedNumberRange> ReservedFieldNumbers,
    ImmutableArray<CanonicalFieldDescriptor> Fields,
    ImmutableArray<int> KeyFieldIds)
{
    /// <summary>Human-facing table label; excluded from schema_hash.</summary>
    public string? DisplayName { get; init; }
}

public sealed record CanonicalRetiredTableDescriptor(int Id, string Name, string FullName);

public sealed record CanonicalFieldDescriptor(
    int Id,
    string Name,
    string PropertyPath,
    CanonicalFieldShape Shape,
    string TypeName,
    bool HasPresence,
    string? OneOfGroup,
    CanonicalMapDescriptor? Map,
    bool Required,
    string? DefaultValue,
    double? Minimum,
    double? Maximum,
    string? Regex,
    bool Unique,
    int KeyOrder,
    string? ReferenceTable,
    string? ReferenceGroup,
    CanonicalDeletePolicy DeletePolicy,
    CanonicalExpandMode ExpandMode,
    bool Labels,
    string? ChildTableSheetName,
    CanonicalCellFormat? CellFormat,
    CanonicalExpression? Expression,
    CanonicalWeighted? Weighted,
    ImmutableArray<string> ExportTargets,
    ImmutableArray<CanonicalReservedNumberRange> ReservedChildFieldNumbers,
    ImmutableArray<CanonicalFieldDescriptor> Children)
{
    /// <summary>
    /// Stable numeric identity from the owning table field to this node.  Unlike
    /// <see cref="PropertyPath"/>, this survives every legal rename and is the
    /// canonical key used by generated bindings, workbook ownership and patches.
    /// </summary>
    public ImmutableArray<int> FieldIdPath { get; init; } = [];

    /// <summary>Human-facing projection metadata; deliberately excluded from schema_hash.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Workbook header help text; deliberately excluded from schema_hash.</summary>
    public string? HeaderComment { get; init; }

    /// <summary>Historical workbook header spellings accepted by authoring import.</summary>
    public ImmutableArray<string> Aliases { get; init; } = [];

    public CanonicalChildTableDescriptor? ChildTable { get; init; }
}

public enum CanonicalChildTableKind
{
    RepeatedMessage = 1,
    MessageMap = 2,
}

/// <summary>
/// Physical ownership contract for values represented by a child sheet.  The
/// parent row guid plus the declared ordinal/map key identify a child row; it is
/// not an ASSET identity and therefore cannot be addressed by RowRef.
/// </summary>
public sealed record CanonicalChildTableDescriptor(
    CanonicalChildTableKind Kind,
    string SheetName,
    ImmutableArray<int> OwnerFieldIdPath,
    string ParentGuidColumn,
    string? OrdinalColumn,
    string? MapKeyColumn);

public sealed record CanonicalReservedNumberRange(long Start, long EndExclusive)
{
    public bool Contains(int number) => number >= Start && number < EndExclusive;
}

public sealed record CanonicalMapDescriptor(
    string KeyTypeName,
    string ValueTypeName,
    CanonicalFieldShape ValueShape,
    string? KeyEnumType);

public sealed record CanonicalCellFormat(
    string Kind,
    ImmutableArray<string> Parameters,
    ImmutableArray<CanonicalCellFormat> Legacy);

public sealed record CanonicalExpression(string SymbolsType, string ResultType);

public sealed record CanonicalWeighted(int WeightFieldId, int ConditionFieldId);

public sealed record CanonicalEnumDescriptor(
    string Name,
    string FullName,
    ImmutableArray<CanonicalReservedNumberRange> ReservedNumbers,
    ImmutableArray<CanonicalEnumValueDescriptor> Values);

public sealed record CanonicalEnumValueDescriptor(int Number, string Name);
