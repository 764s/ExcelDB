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
        ArgumentException.ThrowIfNullOrWhiteSpace(formula);
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

public sealed record WorkbookDefinition(
    Guid WorkbookGuid,
    ulong SchemaHash,
    DateTimeOffset SavedUtc,
    ImmutableArray<WorkbookTable> Tables,
    ImmutableArray<string> MigrationMarkers = default)
{
    public ImmutableArray<string> EffectiveMigrationMarkers =>
        MigrationMarkers.IsDefault ? [] : MigrationMarkers;

    public static WorkbookDefinition Empty(CanonicalSchemaDescriptor schema, Guid? workbookGuid = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new WorkbookDefinition(
            workbookGuid ?? Guid.NewGuid(),
            schema.SchemaHash,
            DateTimeOffset.UtcNow,
            schema.Tables
                .Where(static table => table.Kind == CanonicalTableKind.Asset)
                .OrderBy(static table => table.Id)
                .Select(WorkbookLayout.CreateTable)
                .ToImmutableArray());
    }
}

public static class WorkbookLayout
{
    public static WorkbookTable CreateTable(CanonicalTableDescriptor table)
    {
        ArgumentNullException.ThrowIfNull(table);
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

    private static IEnumerable<WorkbookColumn> Flatten(CanonicalFieldDescriptor field, string? parentFieldPath)
    {
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
}
