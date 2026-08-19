using System.Collections.Immutable;
using ExcelDb.Core.Identity;
using ExcelDb.Schema.Descriptors;

namespace ExcelDb.Workbooks.Model;

public static class WorkbookProtocol
{
    public const ushort FormatVersion = 1;
    public const int HeaderRows = 3;
    public const int DefaultDataStartRow = HeaderRows + 1;
    public const string MetadataSheetName = "__exceldb";
    public const string KeySheetName = "__exceldb_keys";
    public const string GuidColumnName = "__guid";
    public const string RevisionColumnName = "__rev";
    public const string ParentGuidColumnName = "__parent_guid";
    public const string OrdinalColumnName = "__ordinal";
    public const string MapKeyColumnName = "__map_key";
    public const string ExplicitNullToken = "~";
}

/// <summary>A cell keeps formula text separate from its cached/displayed value.</summary>
public sealed record WorkbookCell(string? Text = null, string? Formula = null)
{
    public bool IsBlank => Text is null && Formula is null;

    /// <summary>Formula cells compare by formula text; cached values are deliberately ignored.</summary>
    public string ComparisonText => Formula is null ? Text ?? string.Empty : $"={Formula}";

    public static WorkbookCell FormulaCell(string formula, string? cachedValue = null)
    {
        Guard.NotNullOrWhiteSpace(formula);
        return new WorkbookCell(cachedValue, formula);
    }
}

public sealed record WorkbookColumn(
    string DisplayName,
    string PropertyPath,
    string TypeName,
    string FieldPath,
    string? HeaderComment = null,
    ImmutableArray<string> Aliases = default,
    string? ReferenceTable = null)
{
    public ImmutableArray<string> EffectiveAliases => Aliases.IsDefault ? [] : Aliases;
}

public sealed record WorkbookRow(
    RowGuid? RowGuid,
    uint Revision,
    ImmutableDictionary<string, WorkbookCell> Cells,
    string? Key = null,
    string? RawRowGuid = null,
    string? RawRevision = null,
    int? SourceRowNumber = null,
    bool KeyIsProjection = false)
{
    public static WorkbookRow Create(
        RowGuid? rowGuid,
        uint revision,
        IEnumerable<KeyValuePair<string, WorkbookCell>> cells,
        string? key = null) =>
        new(rowGuid, revision, cells.ToImmutableDictionary(StringComparer.Ordinal), key);
}

public sealed record WorkbookTable(
    int TableId,
    string ProtoName,
    string SheetName,
    int DataStartRow,
    ImmutableArray<WorkbookColumn> Columns,
    ImmutableArray<WorkbookRow> Rows,
    bool IsRetiredPreserved = false);

/// <summary>A repeated-message/map child row is owned by a parent ASSET row and has no AssetIdentity.</summary>
public sealed record WorkbookChildRow(
    RowGuid? ParentRowGuid,
    int? Ordinal,
    string? MapKey,
    ImmutableDictionary<string, WorkbookCell> Cells,
    string? RawParentRowGuid = null,
    int? SourceRowNumber = null)
{
    public static WorkbookChildRow Create(
        RowGuid? parentRowGuid,
        int? ordinal,
        string? mapKey,
        IEnumerable<KeyValuePair<string, WorkbookCell>> cells) =>
        new(parentRowGuid, ordinal, mapKey, cells.ToImmutableDictionary(StringComparer.Ordinal));
}

public sealed record WorkbookChildTable(
    int OwnerTableId,
    ImmutableArray<int> OwnerFieldIdPath,
    CanonicalChildTableKind Kind,
    string SheetName,
    int DataStartRow,
    ImmutableArray<WorkbookColumn> Columns,
    ImmutableArray<WorkbookChildRow> Rows);

public sealed record WorkbookDefinition(
    Guid WorkbookGuid,
    ulong SchemaHash,
    DateTimeOffset SavedUtc,
    ImmutableArray<WorkbookTable> Tables,
    ImmutableArray<string> MigrationMarkers = default,
    ImmutableArray<WorkbookChildTable> ChildTables = default)
{
    public ImmutableArray<string> EffectiveMigrationMarkers =>
        MigrationMarkers.IsDefault ? [] : MigrationMarkers;

    public ImmutableArray<WorkbookChildTable> EffectiveChildTables =>
        ChildTables.IsDefault ? [] : ChildTables;

    public static WorkbookDefinition Empty(CanonicalSchemaDescriptor schema, Guid? workbookGuid = null)
    {
        Guard.NotNull(schema);
        return new WorkbookDefinition(
            workbookGuid ?? Guid.NewGuid(),
            schema.SchemaHash,
            DateTimeOffset.UtcNow,
            schema.Tables
                .Where(static table => table.Kind == CanonicalTableKind.Asset)
                .OrderBy(static table => table.Id)
                .Select(WorkbookLayout.CreateTable)
                .ToImmutableArray(),
            ChildTables: WorkbookLayout.CreateChildTables(schema));
    }
}

public static class WorkbookLayout
{
    public static WorkbookTable CreateTable(CanonicalTableDescriptor table)
    {
        Guard.NotNull(table);
        return new WorkbookTable(
            table.Id,
            table.Name,
            table.SheetName,
            WorkbookProtocol.DefaultDataStartRow,
            Flatten(table.Fields).ToImmutableArray(),
            []);
    }

    public static IEnumerable<WorkbookColumn> Flatten(ImmutableArray<CanonicalFieldDescriptor> fields)
    {
        foreach (var field in fields.OrderBy(static field => field.Id))
        {
            foreach (var column in Flatten(field, null))
                yield return column;
        }
    }

    public static ImmutableArray<WorkbookChildTable> CreateChildTables(CanonicalSchemaDescriptor schema) =>
        [.. schema.Tables
            .Where(static table => table.Kind == CanonicalTableKind.Asset)
            .OrderBy(static table => table.Id)
            .SelectMany(table => FlattenDescriptors(table.Fields)
                .Where(static field => IsChildTableOwner(field))
                .Select(field => CreateChildTable(table, field)))
            .OrderBy(static table => table.OwnerTableId)
            .ThenBy(static table => string.Join('.', table.OwnerFieldIdPath), StringComparer.Ordinal)];

    public static WorkbookChildTable CreateChildTable(
        CanonicalTableDescriptor owner,
        CanonicalFieldDescriptor field)
    {
        var child = field.ChildTable
            ?? throw new ArgumentException("The field has no child-table contract.", nameof(field));
        var columns = Flatten(field.Children).ToImmutableArray();
        return new WorkbookChildTable(
            owner.Id,
            child.OwnerFieldIdPath,
            child.Kind,
            child.SheetName,
            WorkbookProtocol.DefaultDataStartRow,
            columns,
            []);
    }

    private static IEnumerable<CanonicalFieldDescriptor> FlattenDescriptors(
        IEnumerable<CanonicalFieldDescriptor> fields)
    {
        foreach (var field in fields)
        {
            yield return field;
            foreach (var child in FlattenDescriptors(field.Children))
                yield return child;
        }
    }

    private static IEnumerable<WorkbookColumn> Flatten(CanonicalFieldDescriptor field, string? parentFieldPath)
    {
        // A child-table owner is represented only by its child worksheet.  Emitting an
        // additional JSON column in the parent table would create two writable sources
        // of truth and defeats the simple-field authoring model.
        if (IsChildTableOwner(field))
            yield break;

        var fieldPath = !field.FieldIdPath.IsDefaultOrEmpty
            ? string.Join('.', field.FieldIdPath)
            : parentFieldPath is null ? field.Id.ToString() : $"{parentFieldPath}.{field.Id}";
        if (!field.Children.IsDefaultOrEmpty && field.ExpandMode == CanonicalExpandMode.ExpandedColumns)
        {
            foreach (var child in field.Children.OrderBy(static child => child.Id))
            {
                foreach (var column in Flatten(child, fieldPath))
                    yield return column;
            }

            yield break;
        }

        yield return new WorkbookColumn(
            field.DisplayName ?? field.Name,
            field.PropertyPath,
            field.TypeName,
            fieldPath,
            field.HeaderComment,
            field.Aliases,
            field.ReferenceTable);
    }

    private static bool IsChildTableOwner(CanonicalFieldDescriptor field) =>
        field.ChildTable is { } child
        && field.FieldIdPath.AsSpan().SequenceEqual(child.OwnerFieldIdPath.AsSpan());
}
