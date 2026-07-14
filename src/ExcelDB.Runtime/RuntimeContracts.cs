using System.Collections.Immutable;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Runtime.References;

namespace ExcelDb.Runtime;

public enum RuntimeMode : byte
{
    EditorAuthoring = 0,
    EditorPlayDebug = 1,
    Development = 2,
    Release = 3,
}

public enum RuntimeAssetState : byte
{
    Resident = 0,
    Missing = 1,
}

public enum RuntimeQueryStatus : byte
{
    Success = 0,
    Truncated = 1,
    Closed = 2,
}

public enum RuntimeSourceKind : byte
{
    ConvertedBytes = 0,
    Excel = 1,
    Custom = 2,
}

[Flags]
public enum RuntimeSourceCapabilities : byte
{
    None = 0,
    Read = 1 << 0,
    Refresh = 1 << 1,
    Watch = 1 << 2,
}

public readonly record struct RuntimeBootstrapOptions
{
    public RuntimeBootstrapOptions(
        RuntimeMode mode,
        bool enableHotReload,
        IRuntimePublishPoint? publishPoint = null,
        IRuntimeProfiler? profiler = null,
        RuntimeReferenceServices? referenceServices = null)
    {
        Mode = mode;
        EnableHotReload = enableHotReload;
        PublishPoint = publishPoint;
        Profiler = profiler;
        ReferenceServices = referenceServices ?? RuntimeReferenceServices.Empty;
    }

    public RuntimeMode Mode { get; }

    public bool EnableHotReload { get; }

    /// <summary>
    /// Optional host notification seam. A watcher may request work through it, but the host remains
    /// responsible for calling <see cref="RuntimeDatabase.ProcessPendingHotReload"/> at its stable
    /// owner publish point.
    /// </summary>
    public IRuntimePublishPoint? PublishPoint { get; }

    /// <summary>Optional allocation-free marker sink for host profilers.</summary>
    public IRuntimeProfiler? Profiler { get; }

    /// <summary>Explicit provider set captured for the whole runtime session.</summary>
    public RuntimeReferenceServices ReferenceServices { get; }
}

public readonly record struct RuntimeSchemaIdentity(ulong SchemaHash, ExportTargetId ExportTarget)
{
    public bool IsValid => SchemaHash != 0 && !ExportTarget.IsEmpty;

    public override string ToString() => $"{SchemaHash:x16}/{ExportTarget}";
}

public sealed class RuntimeFieldValue
{
    private readonly byte[] _data;

    public RuntimeFieldValue(int fieldNumber, ReadOnlySpan<byte> data)
        : this([fieldNumber], data)
    {
    }

    public RuntimeFieldValue(IEnumerable<int> fieldIdPath, ReadOnlySpan<byte> data)
    {
        RuntimeCompatibility.NotNull(fieldIdPath, nameof(fieldIdPath));
        var path = fieldIdPath.ToImmutableArray();
        if (path.IsDefaultOrEmpty || path.Any(static fieldNumber => fieldNumber <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(fieldIdPath),
                "A field id path must contain only positive field numbers.");
        }

        FieldIdPath = path;
        _data = data.ToArray();
    }

    /// <summary>
    /// Stable protobuf field-number chain from the owning table message to the represented value.
    /// A scalar top-level field therefore has a one-segment path.
    /// </summary>
    public ImmutableArray<int> FieldIdPath { get; }

    /// <summary>Leaf field number retained for source compatibility with scalar consumers.</summary>
    public int FieldNumber => FieldIdPath[^1];

    public ReadOnlyMemory<byte> Data => _data;

    internal bool ContentEquals(RuntimeFieldValue other) =>
        FieldIdPath.AsSpan().SequenceEqual(other.FieldIdPath.AsSpan())
        && _data.AsSpan().SequenceEqual(other._data);

    internal static int ComparePath(RuntimeFieldValue left, RuntimeFieldValue right)
    {
        var length = Math.Min(left.FieldIdPath.Length, right.FieldIdPath.Length);
        for (var index = 0; index < length; index++)
        {
            var comparison = left.FieldIdPath[index].CompareTo(right.FieldIdPath[index]);
            if (comparison != 0)
                return comparison;
        }

        return left.FieldIdPath.Length.CompareTo(right.FieldIdPath.Length);
    }
}

/// <summary>A source-neutral canonical runtime row.</summary>
public sealed class RuntimeAssetRecord
{
    public RuntimeAssetRecord(
        AssetIdentity identity,
        string key,
        IEnumerable<RuntimeFieldValue>? fields = null,
        IEnumerable<AssetIdentity>? dependencies = null,
        string? path = null)
    {
        RuntimeCompatibility.NotNull(key, nameof(key));

        Identity = identity;
        Key = key;
        Path = path ?? key;
        Fields = fields is null
            ? []
            : fields.OrderBy(static item => item, RuntimeFieldValuePathComparer.Instance).ToImmutableArray();
        Dependencies = dependencies is null
            ? []
            : dependencies.OrderBy(static item => item, AssetIdentityComparer.Instance).ToImmutableArray();
    }

    public AssetIdentity Identity { get; }

    public int TableId => Identity.TableId;

    public string Key { get; }

    public string Path { get; }

    public ImmutableArray<RuntimeFieldValue> Fields { get; }

    /// <summary>Resolved hard references represented by stable identities.</summary>
    public ImmutableArray<AssetIdentity> Dependencies { get; }

    internal bool FieldsEqual(RuntimeAssetRecord other)
    {
        if (Fields.Length != other.Fields.Length)
            return false;

        for (var index = 0; index < Fields.Length; index++)
        {
            if (!Fields[index].ContentEquals(other.Fields[index]))
                return false;
        }

        return true;
    }

    internal bool DependenciesEqual(RuntimeAssetRecord other) =>
        Dependencies.AsSpan().SequenceEqual(other.Dependencies.AsSpan());

    internal bool CanonicallyEquals(RuntimeAssetRecord other) =>
        Identity == other.Identity
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && string.Equals(Path, other.Path, StringComparison.Ordinal)
        && FieldsEqual(other)
        && DependenciesEqual(other);
}

public sealed class SourceSnapshot : IDisposable
{
    private IDisposable? _owner;

    public SourceSnapshot(
        ulong schemaHash,
        ExportTargetId exportTarget,
        string revision,
        string contentHash,
        IEnumerable<RuntimeAssetRecord> assets,
        IEnumerable<Diagnostic>? diagnostics = null,
        int formatVersion = 1,
        IDisposable? owner = null)
    {
        RuntimeCompatibility.NotNull(revision, nameof(revision));
        RuntimeCompatibility.NotNull(contentHash, nameof(contentHash));
        RuntimeCompatibility.NotNull(assets, nameof(assets));

        SchemaHash = schemaHash;
        ExportTarget = exportTarget;
        Revision = revision;
        ContentHash = contentHash;
        FormatVersion = formatVersion;
        Assets = assets.ToImmutableArray();
        Diagnostics = diagnostics?.ToImmutableArray() ?? [];
        _owner = owner;
    }

    public ulong SchemaHash { get; }

    public ExportTargetId ExportTarget { get; }

    public string Revision { get; }

    public string ContentHash { get; }

    public int FormatVersion { get; }

    public ImmutableArray<RuntimeAssetRecord> Assets { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
}

internal sealed class RuntimeFieldValuePathComparer : IComparer<RuntimeFieldValue>
{
    public static RuntimeFieldValuePathComparer Instance { get; } = new();

    public int Compare(RuntimeFieldValue? left, RuntimeFieldValue? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;
        return RuntimeFieldValue.ComparePath(left, right);
    }
}

internal sealed class AssetIdentityComparer : IComparer<AssetIdentity>
{
    public static AssetIdentityComparer Instance { get; } = new();

    public int Compare(AssetIdentity left, AssetIdentity right)
    {
        var tableComparison = left.TableId.CompareTo(right.TableId);
        if (tableComparison != 0)
            return tableComparison;

        return left.RowGuid.Value.CompareTo(right.RowGuid.Value);
    }
}
