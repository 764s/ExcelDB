using System.Collections.Immutable;
using System.Text.RegularExpressions;
using ExcelDb.Schema.Descriptors;

namespace ExcelDb.Schema.Authoring;

public enum SimpleFieldType
{
    String,
    Int32,
    Int64,
    UInt32,
    UInt64,
    Float,
    Double,
    Boolean,
    Enum,
}

public sealed record SimpleFieldDefinition(
    string Name,
    SimpleFieldType Type,
    string? EnumType = null,
    bool IsKey = false);

public readonly record struct TableInitializationContext(
    string TableName,
    ImmutableArray<SimpleFieldDefinition> RequestedFields,
    bool AutoKey)
{
    public CanonicalSchemaDescriptor? CurrentSchema { get; init; }

    public TableInitializationOptions Options { get; init; } = TableInitializationOptions.Default;

    public ImmutableDictionary<string, TableFieldOptions> FieldOptions { get; init; } =
        ImmutableDictionary<string, TableFieldOptions>.Empty.WithComparers(StringComparer.Ordinal);
}

public sealed record TableInitializationOptions(
    string? DisplayName,
    string? SheetName,
    ImmutableArray<string> ValidatorIds)
{
    public static TableInitializationOptions Default { get; } = new(null, null, []);
}

public sealed record TableFieldOptions(
    string? DisplayName,
    string? HeaderComment,
    ImmutableArray<string> Aliases,
    bool Required,
    string? DefaultValue,
    double? Minimum,
    double? Maximum,
    string? Regex,
    bool Unique)
{
    public static TableFieldOptions Default { get; } = new(null, null, [], false, null, null, null, null, false);
}

public interface ITableInitializer
{
    string Id { get; }

    void Initialize(in TableInitializationContext context, ITableDraft draft);
}

public interface ITableDraft
{
    string TableName { get; }

    IReadOnlyList<TableFieldDraft> Fields { get; }

    TableInitializationOptions Options { get; }

    void AddField(SimpleFieldDefinition field);

    void ConfigureTable(TableInitializationOptions options);

    void ConfigureField(string fieldName, TableFieldOptions options);
}

public sealed class DefaultTableInitializer : ITableInitializer
{
    public const string StableId = "exceldb.default-table";

    public string Id => StableId;

    public void Initialize(in TableInitializationContext context, ITableDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!string.Equals(context.TableName, draft.TableName, StringComparison.Ordinal))
            throw new InvalidOperationException("Initialization context and table draft refer to different tables.");

        var explicitKeys = context.RequestedFields.Count(static field => field.IsKey);
        if (context.AutoKey && explicitKeys != 0)
            throw new InvalidOperationException("AutoKey and an explicit key cannot be selected together.");
        if (!context.AutoKey && explicitKeys == 0)
            throw new InvalidOperationException("A new ASSET table requires an explicit key or AutoKey.");

        if (context.AutoKey)
        {
            draft.AddField(new SimpleFieldDefinition(
                "id",
                SimpleFieldType.String,
                IsKey: true));
        }

        foreach (var field in context.RequestedFields)
            draft.AddField(field);

        draft.ConfigureTable(context.Options);
        foreach (var pair in context.FieldOptions.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            draft.ConfigureField(pair.Key, pair.Value);
    }
}

public sealed partial class TableDraft : ITableDraft, IExportTargetDraft
{
    private readonly List<TableFieldDraft> _fields = [];
    private readonly IReadOnlyList<TableFieldDraft> _fieldsView;

    public TableDraft(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        if (!ProtoIdentifierPattern().IsMatch(tableName))
            throw new ArgumentException($"Invalid proto message name '{tableName}'.", nameof(tableName));
        TableName = tableName;
        _fieldsView = _fields.AsReadOnly();
    }

    public string TableName { get; }

    public IReadOnlyList<TableFieldDraft> Fields => _fieldsView;

    public TableInitializationOptions Options { get; private set; } = TableInitializationOptions.Default;

    IReadOnlyList<IExportTargetFieldDraft> IExportTargetDraft.Fields => _fieldsView;

    public ExportTargetSelection? TableExportTargets { get; private set; }

    public void AddField(SimpleFieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!ProtoIdentifierPattern().IsMatch(field.Name))
            throw new ArgumentException($"Invalid proto field name '{field.Name}'.", nameof(field));
        if (_fields.Any(candidate => string.Equals(candidate.Name, field.Name, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Field '{field.Name}' already exists in table '{TableName}'.");
        if (field.Type == SimpleFieldType.Enum && string.IsNullOrWhiteSpace(field.EnumType))
            throw new InvalidOperationException($"Enum field '{field.Name}' requires an enum type name.");
        if (field.Type != SimpleFieldType.Enum && field.EnumType is not null)
            throw new InvalidOperationException($"Non-enum field '{field.Name}' cannot declare an enum type name.");

        _fields.Add(new TableFieldDraft(field));
    }

    public void ConfigureTable(TableInitializationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateTableOptions(options);
        Options = options with
        {
            ValidatorIds = NormalizeStrings(options.ValidatorIds),
        };
    }

    public void ConfigureField(string fieldName, TableFieldOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(options);
        var field = _fields.SingleOrDefault(candidate => string.Equals(candidate.Name, fieldName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Field '{fieldName}' does not exist in table '{TableName}'.");
        field.Configure(options);
    }

    internal bool SemanticallyEquals(TableDraft other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(TableName, other.TableName, StringComparison.Ordinal)
            && string.Equals(Options.DisplayName, other.Options.DisplayName, StringComparison.Ordinal)
            && string.Equals(Options.SheetName, other.Options.SheetName, StringComparison.Ordinal)
            && Options.ValidatorIds.SequenceEqual(other.Options.ValidatorIds, StringComparer.Ordinal)
            && _fields.Count == other._fields.Count
            && _fields.Zip(other._fields).All(static pair => pair.First.SemanticallyEquals(pair.Second));
    }

    private static void ValidateTableOptions(TableInitializationOptions options)
    {
        if (options.SheetName is { Length: 0 })
            throw new InvalidOperationException("A configured sheet name cannot be empty.");
        if (options.ValidatorIds.Any(static value => string.IsNullOrWhiteSpace(value)))
            throw new InvalidOperationException("Validator ids cannot be empty.");
    }

    private static ImmutableArray<string> NormalizeStrings(IEnumerable<string> values)
    {
        if (values is null)
            return [];
        return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>Applies a user-confirmed target choice before the automatic strategy runs.</summary>
    public void ApplyExplicitTableExportTargets(ExportTargetSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (TableExportTargets is not null)
            throw new InvalidOperationException($"Table '{TableName}' already has an export target selection.");
        TableExportTargets = selection;
    }

    bool IExportTargetDraft.TryFillTableExportTargets(IEnumerable<string> targets)
    {
        if (TableExportTargets is not null)
            return false;
        TableExportTargets = ExportTargetSelection.Explicit(targets);
        return true;
    }

    bool IExportTargetDraft.TryFillFieldExportTargets(string fieldName, IEnumerable<string> targets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        var field = _fields.SingleOrDefault(candidate => string.Equals(candidate.Name, fieldName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Field '{fieldName}' does not exist in table '{TableName}'.");
        if (field.ExportTargets is not null)
            return false;
        field.FillExportTargets(targets);
        return true;
    }

    /// <summary>Applies a user-confirmed field target choice before the automatic strategy runs.</summary>
    public void ApplyExplicitFieldExportTargets(string fieldName, ExportTargetSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        var field = _fields.SingleOrDefault(candidate => string.Equals(candidate.Name, fieldName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Field '{fieldName}' does not exist in table '{TableName}'.");
        if (field.ExportTargets is not null)
            throw new InvalidOperationException($"Field '{fieldName}' already has an export target selection.");
        field.ApplyExplicitExportTargets(selection);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProtoIdentifierPattern();
}

public sealed class TableFieldDraft : IExportTargetFieldDraft
{
    internal TableFieldDraft(SimpleFieldDefinition definition)
    {
        Name = definition.Name;
        Type = definition.Type;
        EnumType = definition.EnumType;
        IsKey = definition.IsKey;
    }

    public string Name { get; }

    public SimpleFieldType Type { get; }

    public string? EnumType { get; }

    public bool IsKey { get; }

    public TableFieldOptions Options { get; private set; } = TableFieldOptions.Default;

    public ExportTargetSelection? ExportTargets { get; private set; }

    internal void FillExportTargets(IEnumerable<string> targets) =>
        ExportTargets = ExportTargetSelection.Explicit(targets);

    internal void ApplyExplicitExportTargets(ExportTargetSelection selection) => ExportTargets = selection;

    internal void Configure(TableFieldOptions options)
    {
        if (options.Minimum is { } minimum && options.Maximum is { } maximum && minimum > maximum)
            throw new InvalidOperationException($"Field '{Name}' has min greater than max.");
        if (options.Aliases.Any(static value => string.IsNullOrWhiteSpace(value)))
            throw new InvalidOperationException($"Field '{Name}' has an empty alias.");
        if (options.Required && string.Equals(options.DefaultValue, "~", StringComparison.Ordinal))
            throw new InvalidOperationException($"Required field '{Name}' cannot default to explicit null.");
        Options = options with
        {
            Aliases = options.Aliases.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(),
        };
    }

    internal bool SemanticallyEquals(TableFieldDraft other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Type == other.Type
        && string.Equals(EnumType, other.EnumType, StringComparison.Ordinal)
        && IsKey == other.IsKey
        && string.Equals(Options.DisplayName, other.Options.DisplayName, StringComparison.Ordinal)
        && string.Equals(Options.HeaderComment, other.Options.HeaderComment, StringComparison.Ordinal)
        && Options.Required == other.Options.Required
        && string.Equals(Options.DefaultValue, other.Options.DefaultValue, StringComparison.Ordinal)
        && Options.Minimum == other.Options.Minimum
        && Options.Maximum == other.Options.Maximum
        && string.Equals(Options.Regex, other.Options.Regex, StringComparison.Ordinal)
        && Options.Unique == other.Options.Unique
        && Options.Aliases.SequenceEqual(other.Options.Aliases, StringComparer.Ordinal);
}
