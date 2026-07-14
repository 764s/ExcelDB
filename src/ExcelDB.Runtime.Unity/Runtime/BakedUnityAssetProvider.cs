using ExcelDb.Runtime.References;

namespace ExcelDb.Runtime.Unity;

public readonly struct UnityAssetBakeEntry
{
    public UnityAssetBakeEntry(string guid, int providerKey)
    {
        Guid = guid;
        ProviderKey = providerKey;
    }

    public string Guid { get; }

    public int ProviderKey { get; }
}

/// <summary>
/// Release-safe Unity provider: canonical guids are bound to baked integer keys up front, and a
/// display path is never consulted as an identity fallback.
/// </summary>
public sealed class BakedUnityAssetProvider : IUnityAssetProvider
{
    private readonly IUnityAssetHost _host;
    private readonly IReadOnlyDictionary<string, int> _keys;

    public BakedUnityAssetProvider(
        string id,
        IUnityAssetHost host,
        IEnumerable<UnityAssetBakeEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A provider id is required.", nameof(id));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        if (entries is null)
            throw new ArgumentNullException(nameof(entries));
        Id = id;

        var builder = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            _ = new UnityAssetReference(entry.Guid);
            if (entry.ProviderKey <= 0)
                throw new ArgumentOutOfRangeException(nameof(entries), "Baked provider keys must be positive.");
            if (!builder.TryAdd(entry.Guid, entry.ProviderKey))
                throw new ArgumentException($"Unity guid '{entry.Guid}' is baked more than once.", nameof(entries));
        }
        _keys = builder;
    }

    public string Id { get; }

    public bool TryBind(in UnityAssetReference reference, out UnityAssetProviderKey key)
    {
        if (_keys.TryGetValue(reference.Guid, out var rawKey))
        {
            key = new UnityAssetProviderKey(rawKey);
            return true;
        }

        key = default;
        return false;
    }

    public bool TryResolve<T>(in UnityAssetProviderKey key, out T? asset)
        where T : class => _host.TryResolve(key.Value, out asset);
}
