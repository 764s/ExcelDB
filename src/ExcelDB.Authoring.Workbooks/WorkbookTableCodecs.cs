using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ExcelDb.Core.Identity;
using ExcelDb.Core.Values;
using ExcelDb.Runtime;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDbEditor;

namespace ExcelDb.Authoring.Workbooks;

/// <summary>
/// Generated code may implement this small C# seam when reflection is insufficient (for example,
/// formula-backed fields or custom single-cell formats).
/// </summary>
public interface IAuthoringWorkbookTableCodec
{
    int TableId { get; }

    string TableName { get; }

    Type AssetType { get; }

    RuntimeAssetRecord CreateRuntimeRecord(ImportedRow row);

    SnapshotRow Capture(AuthoringSaveItem item, SnapshotRow? baseline);
}

public interface IWorkbookLocationOpener
{
    bool Open(string workbookPath, string sheetName, int rowNumber);
}

public sealed class DelegateAuthoringWorkbookTableCodec<T> : IAuthoringWorkbookTableCodec
    where T : class
{
    private readonly Func<ImportedRow, RuntimeAssetRecord> _createRuntimeRecord;
    private readonly Func<AuthoringSaveItem, SnapshotRow?, T, SnapshotRow> _capture;

    public DelegateAuthoringWorkbookTableCodec(
        int tableId,
        string tableName,
        Func<ImportedRow, RuntimeAssetRecord> createRuntimeRecord,
        Func<AuthoringSaveItem, SnapshotRow?, T, SnapshotRow> capture)
    {
        if (tableId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableId));
        Guard.NotNullOrWhiteSpace(tableName);
        TableId = tableId;
        TableName = tableName;
        _createRuntimeRecord = createRuntimeRecord ?? throw new ArgumentNullException(nameof(createRuntimeRecord));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    public int TableId { get; }

    public string TableName { get; }

    public Type AssetType => typeof(T);

    public RuntimeAssetRecord CreateRuntimeRecord(ImportedRow row) => _createRuntimeRecord(row);

    public SnapshotRow Capture(AuthoringSaveItem item, SnapshotRow? baseline)
    {
        if (item.Asset is not T asset)
            throw new ArgumentException($"Expected asset type {typeof(T).FullName}.", nameof(item));
        return _capture(item, baseline, asset);
    }
}

/// <summary>
/// Stable field payload used between the default workbook codec and generated RuntimeTableBinding
/// delegates. The first byte is CanonicalValueState and the remainder is strict UTF-8 text.
/// </summary>
public static class CanonicalRuntimeFieldEncoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(CanonicalValue value)
    {
        var text = value.State switch
        {
            CanonicalValueState.Defaulted or CanonicalValueState.Value => value.Text,
            CanonicalValueState.Invalid => value.RawText,
            _ => null,
        };
        var textBytes = text is null ? [] : StrictUtf8.GetBytes(text);
        var result = new byte[textBytes.Length + 1];
        result[0] = (byte)value.State;
        textBytes.CopyTo(result, 1);
        return result;
    }

    public static CanonicalValue Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || !Enum.IsDefined(typeof(CanonicalValueState), (CanonicalValueState)payload[0]))
            throw new InvalidDataException("Runtime canonical field payload has an invalid state.");
        var state = (CanonicalValueState)payload[0];
        var text = payload.Length == 1 ? string.Empty : StrictUtf8.GetString(payload[1..]);
        return state switch
        {
            CanonicalValueState.Missing when text.Length == 0 => CanonicalValue.Missing,
            CanonicalValueState.Defaulted => CanonicalValue.FromDefault(text),
            CanonicalValueState.Null when text.Length == 0 => CanonicalValue.Null,
            CanonicalValueState.Value => CanonicalValue.FromValue(text),
            CanonicalValueState.Invalid => CanonicalValue.Invalid(text),
            _ => throw new InvalidDataException("Runtime canonical field payload has text for a text-free state."),
        };
    }
}

/// <summary>Default scalar POCO codec; custom formats should use the delegate/interface seam.</summary>
public sealed class ReflectionAuthoringWorkbookTableCodec<T> : IAuthoringWorkbookTableCodec
    where T : class
{
    private readonly CanonicalTableDescriptor _table;
    private readonly ImmutableArray<CanonicalFieldDescriptor> _fields;
    private readonly IReadOnlyDictionary<string, string> _memberPaths;
    private readonly Func<ImportedRow, IEnumerable<AssetIdentity>>? _decodeDependencies;

    public ReflectionAuthoringWorkbookTableCodec(
        CanonicalTableDescriptor table,
        Func<ImportedRow, IEnumerable<AssetIdentity>>? decodeDependencies = null)
        : this(table, decodeDependencies, memberPaths: null)
    {
    }

    public ReflectionAuthoringWorkbookTableCodec(
        CanonicalTableDescriptor table,
        Func<ImportedRow, IEnumerable<AssetIdentity>>? decodeDependencies,
        IReadOnlyDictionary<string, string>? memberPaths)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _fields = Flatten(table.Fields).ToImmutableArray();
        var duplicatePath = _fields
            .GroupBy(static field => FieldPath(field), StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicatePath is not null)
        {
            throw new ArgumentException(
                $"Expanded table '{table.Name}' repeats runtime field path {duplicatePath.Key}; provide a custom codec.",
                nameof(table));
        }

        _memberPaths = memberPaths is null
            ? ImmutableDictionary<string, string>.Empty
            : memberPaths.ToImmutableDictionary(StringComparer.Ordinal);
        _decodeDependencies = decodeDependencies;
    }

    public int TableId => _table.Id;

    public string TableName => _table.Name;

    public Type AssetType => typeof(T);

    public RuntimeAssetRecord CreateRuntimeRecord(ImportedRow row)
    {
        Guard.NotNull(row);
        var identity = row.Identity
            ?? throw new ArgumentException("An indexable runtime row requires an identity.", nameof(row));
        var key = row.Key
            ?? throw new ArgumentException("An indexable runtime row requires a key.", nameof(row));
        var fields = _fields.Select(field => new RuntimeFieldValue(
            field.FieldIdPath.IsDefaultOrEmpty ? [field.Id] : field.FieldIdPath,
            CanonicalRuntimeFieldEncoding.Encode(
                row.Values.GetValueOrDefault(field.PropertyPath, CanonicalValue.Missing))));
        return new RuntimeAssetRecord(
            identity,
            DisplayKey(row, key),
            fields,
            _decodeDependencies?.Invoke(row),
            $"{TableName}/{key}");
    }

    public SnapshotRow Capture(AuthoringSaveItem item, SnapshotRow? baseline)
    {
        Guard.NotNull(item);
        if (item.TableId != TableId || !string.Equals(item.TableName, TableName, StringComparison.Ordinal))
            throw new ArgumentException("Save item does not belong to this table codec.", nameof(item));
        if (item.Asset is not T asset)
            throw new ArgumentException($"Expected asset type {typeof(T).FullName}.", nameof(item));
        var identity = new AssetIdentity(TableId, item.Guid.Value);
        var values = ImmutableDictionary.CreateBuilder<string, CanonicalValue>(StringComparer.Ordinal);
        foreach (var field in _fields)
        {
            var rawValue = ReadMemberPath(
                asset,
                _memberPaths.TryGetValue(field.PropertyPath, out var memberPath)
                    ? memberPath
                    : field.PropertyPath);
            var value = ToCanonical(rawValue);
            if (baseline is not null
                && baseline.Values.TryGetValue(field.PropertyPath, out var previous)
                && PreservesSourceState(previous, value, rawValue))
            {
                value = previous;
            }

            values[field.PropertyPath] = value;
        }

        var workbookKey = BuildWorkbookKey(values);
        return new SnapshotRow(
            identity,
            workbookKey,
            item.Revision,
            values.ToImmutable(),
            baseline?.RawCells ?? ImmutableDictionary<string, WorkbookCell>.Empty);
    }

    private string DisplayKey(ImportedRow row, string fallback)
    {
        if (_table.KeyFieldIds.Length != 1)
            return fallback;
        var keyField = _table.Fields.SingleOrDefault(field => field.Id == _table.KeyFieldIds[0]);
        if (keyField is null || !row.Values.TryGetValue(keyField.PropertyPath, out var value))
            return fallback;
        return value.State is CanonicalValueState.Value or CanonicalValueState.Defaulted
            ? value.Text ?? fallback
            : fallback;
    }

    private string? BuildWorkbookKey(ImmutableDictionary<string, CanonicalValue>.Builder values)
    {
        if (_table.KeyFieldIds.IsDefaultOrEmpty)
            return null;
        var fields = _table.Fields.ToDictionary(static field => field.Id);
        var parts = new List<string>();
        foreach (var fieldId in _table.KeyFieldIds)
        {
            if (!fields.TryGetValue(fieldId, out var field)
                || !values.TryGetValue(field.PropertyPath, out var value)
                || value.State is not (CanonicalValueState.Value or CanonicalValueState.Defaulted))
            {
                throw new InvalidOperationException($"Key field {fieldId} cannot be captured from {typeof(T).FullName}.");
            }

            var text = value.Text!;
            parts.Add($"{text.Length.ToString(CultureInfo.InvariantCulture)}:{text}");
        }

        return string.Join('|', parts);
    }

    private static bool PreservesSourceState(
        CanonicalValue previous,
        CanonicalValue candidate,
        object? rawValue)
    {
        if (rawValue is null)
            return previous.State is CanonicalValueState.Missing or CanonicalValueState.Null;
        return previous.State == CanonicalValueState.Defaulted
            && candidate.State == CanonicalValueState.Value
            && string.Equals(previous.Text, candidate.Text, StringComparison.Ordinal);
    }

    private static CanonicalValue ToCanonical(object? value) => value switch
    {
        null => CanonicalValue.Null,
        string text => CanonicalValue.FromValue(text),
        bool boolean => CanonicalValue.FromValue(CanonicalValue.CanonicalizeBoolean(boolean)),
        byte number => CanonicalValue.FromValue(number.ToString(CultureInfo.InvariantCulture)),
        sbyte number => CanonicalValue.FromValue(number.ToString(CultureInfo.InvariantCulture)),
        short number => CanonicalValue.FromValue(number.ToString(CultureInfo.InvariantCulture)),
        ushort number => CanonicalValue.FromValue(number.ToString(CultureInfo.InvariantCulture)),
        int number => CanonicalValue.FromValue(number.ToString(CultureInfo.InvariantCulture)),
        uint number => CanonicalValue.FromValue(number.ToString(CultureInfo.InvariantCulture)),
        long number => CanonicalValue.FromValue(number.ToString(CultureInfo.InvariantCulture)),
        ulong number => CanonicalValue.FromValue(number.ToString(CultureInfo.InvariantCulture)),
        float number when float.IsFinite(number) => CanonicalValue.FromValue(number.ToString("R", CultureInfo.InvariantCulture)),
        double number when double.IsFinite(number) => CanonicalValue.FromValue(number.ToString("R", CultureInfo.InvariantCulture)),
        decimal number => CanonicalValue.FromValue(number.ToString(CultureInfo.InvariantCulture)),
        Enum enumValue => CanonicalValue.FromValue(enumValue.ToString()),
        Guid guid => CanonicalValue.FromValue(guid.ToString("N")),
        _ => CanonicalValue.FromValue(JsonSerializer.Serialize(value, value.GetType())),
    };

    private static object? ReadMemberPath(object instance, string propertyPath)
    {
        object? current = instance;
        var currentType = instance.GetType();
        foreach (var segment in propertyPath.Split('.'))
        {
            if (current is null)
                return null;
            var member = FindMember(currentType, segment)
                ?? throw new InvalidOperationException(
                    $"POCO type '{currentType.FullName}' has no readable member for '{segment}' in '{propertyPath}'.");
            current = member switch
            {
                PropertyInfo property when property.CanRead => property.GetValue(current),
                FieldInfo field => field.GetValue(current),
                _ => throw new InvalidOperationException($"Member '{member.Name}' is not readable."),
            };
            currentType = member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => currentType,
            };
        }

        return current;
    }

    private static MemberInfo? FindMember(Type type, string name)
    {
        var normalized = NormalizeMemberName(name);
        return type
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(static member => member is PropertyInfo or FieldInfo)
            .FirstOrDefault(member => string.Equals(NormalizeMemberName(member.Name), normalized, StringComparison.Ordinal));
    }

    private static string NormalizeMemberName(string value) =>
        string.Concat(value.Where(static character => character != '_')).ToUpperInvariant();

    private static string FieldPath(CanonicalFieldDescriptor field) =>
        string.Join('.', field.FieldIdPath.IsDefaultOrEmpty ? [field.Id] : field.FieldIdPath);

    private static IEnumerable<CanonicalFieldDescriptor> Flatten(
        ImmutableArray<CanonicalFieldDescriptor> fields)
    {
        foreach (var field in fields)
        {
            if (!field.Children.IsDefaultOrEmpty && field.ExpandMode == CanonicalExpandMode.ExpandedColumns)
            {
                foreach (var child in Flatten(field.Children))
                    yield return child;
            }
            else
            {
                yield return field;
            }
        }
    }
}
