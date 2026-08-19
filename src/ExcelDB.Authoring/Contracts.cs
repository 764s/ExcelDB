using System.Collections.Immutable;
using System.Reflection;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;

namespace ExcelDbEditor;

[Flags]
public enum ImportAssetOptions
{
    Default = 0,
    ForceUpdate = 1,
}

public readonly record struct GUID(RowGuid Value)
{
    public static GUID EmptyGuid => default;

    public bool Empty() => Value.IsEmpty;

    public static bool TryParse(string? text, out GUID guid)
    {
        if (RowGuid.TryParse(text, out var value))
        {
            guid = new GUID(value);
            return true;
        }

        guid = default;
        return false;
    }

    public static GUID Parse(string text) => TryParse(text, out var guid)
        ? guid
        : throw new FormatException("A GUID must be 32 lowercase hexadecimal characters and non-zero.");

    public override string ToString() => Value.IsEmpty ? new string('0', 32) : Value.ToString();
}

public sealed record ImportReport(
    string WorkbookPath,
    OperationReport OperationReport)
{
    public bool Ok => OperationReport.Succeeded;

    public ImmutableArray<Diagnostic> Diagnostics => OperationReport.Diagnostics;
}

public readonly record struct ConflictId(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;

    public static ConflictId New() => new(Guid.NewGuid());
}

public enum ConflictResolutionAction
{
    ReloadFromExcel = 0,
    TakeTheirs = ReloadFromExcel,
    KeepEditorValue = 1,
    KeepMine = KeepEditorValue,
    TakeBase = 2,
}

public sealed record ConflictRecord(
    ConflictId Id,
    GUID AssetGuid,
    string PropertyPath,
    string? BaseValue,
    string? MineValue,
    string? TheirValue);

public sealed record AssetDatabaseDiagnostic(
    DateTimeOffset Timestamp,
    Diagnostic Diagnostic);

public sealed class AuthoringTableRegistration
{
    public AuthoringTableRegistration(
        int tableId,
        string tableName,
        Type assetType,
        Func<object, string> getKey,
        Action<object, string> setKey,
        Func<object, object> clone,
        Func<object, IEnumerable<GUID>>? getDependencies = null,
        Func<object, IEnumerable<string>>? getLabels = null,
        Action<object, IReadOnlyList<string>>? setLabels = null,
        Func<object, IEnumerable<AuthoringReference>>? getReferences = null,
        IEnumerable<string>? typeAliases = null,
        Action<object, object>? applyImported = null)
    {
        if (tableId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tableId));
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(tableName));
        if (assetType is null)
            throw new ArgumentNullException(nameof(assetType));
        if (getKey is null)
            throw new ArgumentNullException(nameof(getKey));
        if (setKey is null)
            throw new ArgumentNullException(nameof(setKey));
        if (clone is null)
            throw new ArgumentNullException(nameof(clone));
        TableId = tableId;
        TableName = tableName;
        AssetType = assetType;
        GetKey = getKey;
        SetKey = setKey;
        Clone = clone;
        GetDependencies = getDependencies;
        GetLabels = getLabels;
        SetLabels = setLabels;
        GetReferences = getReferences;
        ApplyImported = applyImported ?? CopyPublicMembers;
        TypeAliases = (typeAliases ?? [])
            .Where(static alias => !string.IsNullOrWhiteSpace(alias))
            .Append(tableName)
            .Append(assetType.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static alias => alias, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public int TableId { get; }
    public string TableName { get; }
    public Type AssetType { get; }
    public Func<object, string> GetKey { get; }
    public Action<object, string> SetKey { get; }
    /// <summary>
    /// Canonical snapshot/copy primitive for this table. The delegate must return a new instance
    /// of <see cref="AssetType"/> whose mutable reference graph is independent from the source;
    /// immutable values may be shared. ExcelDB uses this same deep-clone contract for copied rows,
    /// persisted discard baselines, and post-save baselines.
    /// </summary>
    public Func<object, object> Clone { get; }
    public Func<object, IEnumerable<GUID>>? GetDependencies { get; }
    public Func<object, IEnumerable<string>>? GetLabels { get; }
    public Action<object, IReadOnlyList<string>>? SetLabels { get; }
    public Func<object, IEnumerable<AuthoringReference>>? GetReferences { get; }
    public ImmutableArray<string> TypeAliases { get; }
    public Action<object, object> ApplyImported { get; }

    internal object CloneAsset(object source)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));
        if (!AssetType.IsInstanceOfType(source))
        {
            throw new InvalidOperationException(
                $"The clone source for table '{TableName}' must be assignable to {AssetType.FullName}.");
        }

        var clone = Clone(source)
            ?? throw new InvalidOperationException($"The clone delegate for table '{TableName}' returned null.");
        if (!AssetType.IsInstanceOfType(clone))
        {
            throw new InvalidOperationException(
                $"The clone delegate for table '{TableName}' returned {clone.GetType().FullName}, expected {AssetType.FullName}.");
        }

        if (ReferenceEquals(source, clone))
        {
            throw new InvalidOperationException(
                $"The clone delegate for table '{TableName}' returned the source instance; persisted baselines require an independent deep clone.");
        }

        return clone;
    }

    private static void CopyPublicMembers(object destination, object source)
    {
        if (destination.GetType() != source.GetType())
            throw new ArgumentException("Imported source and resident destination types differ.", nameof(source));
        var type = destination.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                property.SetValue(destination, property.GetValue(source));
        }

        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!field.IsInitOnly)
                field.SetValue(destination, field.GetValue(source));
        }
    }
}

/// <summary>Authoring-only referential-integrity policy projected from a RowRef field.</summary>
public enum AuthoringReferenceDeletePolicy
{
    Block = 0,
    SetNull = 1,
    Cascade = 2,
}

/// <summary>
/// A current reference occurrence returned by a generated or custom table registration.
/// Clear and RewriteTarget mutate the already-resident owner instance; the facade then marks the
/// owner dirty so the same workbook transaction persists the complete impact closure.
/// </summary>
public sealed record AuthoringReference(
    GUID Target,
    string PropertyPath,
    AuthoringReferenceDeletePolicy DeletePolicy = AuthoringReferenceDeletePolicy.Block,
    Action? Clear = null,
    Action<AuthoringReferenceTarget>? RewriteTarget = null);

public readonly record struct AuthoringReferenceTarget(
    GUID Guid,
    int TableId,
    string Key,
    string AssetPath);

public sealed record AuthoringImportedAsset(
    GUID Guid,
    string TableName,
    string Key,
    object Asset,
    uint Revision = 0);

public sealed record AuthoringWorkbookImport(
    string WorkbookPath,
    ImmutableArray<AuthoringImportedAsset> Assets,
    OperationReport Report,
    string? Fingerprint = null,
    ImmutableArray<string> AvailableTables = default);

public sealed record AuthoringSaveItem(
    GUID Guid,
    int TableId,
    string TableName,
    string Key,
    object? Asset,
    bool Deleted,
    uint Revision);

public sealed record AuthoringSaveRequest(
    string WorkbookPath,
    ImmutableArray<AuthoringSaveItem> Items,
    string? ExpectedFingerprint);

public interface IAuthoringWorkbookAdapter
{
    AuthoringWorkbookImport Import(string workbookPath, bool forceUpdate);

    OperationReport Save(AuthoringSaveRequest request);

    bool Open(string workbookPath, string tableName, GUID guid);
}

/// <summary>
/// Optional adapter capability used whenever one facade operation spans more than one workbook.
/// Implementations must stage and recover the entire request set as one transaction.
/// </summary>
public interface ITransactionalAuthoringWorkbookAdapter : IAuthoringWorkbookAdapter
{
    OperationReport Save(ImmutableArray<AuthoringSaveRequest> requests);
}

/// <summary>Optional adapter projection for merge conflicts produced during save preflight.</summary>
public interface IAuthoringConflictSource
{
    ImmutableArray<ConflictRecord> Conflicts { get; }

    bool ResolveConflict(ConflictId id, ConflictResolutionAction action);
}
