namespace ExcelDb.Runtime.References;

/// <summary>A canonical Unity resource identity. The path is display-only and never a fallback key.</summary>
public readonly record struct UnityAssetReference
{
    public UnityAssetReference(string guid, string? mainAssetPath = null, string? assetType = null)
    {
        RuntimeCompatibility.NotNullOrWhiteSpace(guid, nameof(guid));
        if (!IsCanonicalGuid(guid))
            throw new ArgumentException("A Unity guid must be 32 lowercase hexadecimal characters.", nameof(guid));

        Guid = guid;
        MainAssetPath = mainAssetPath ?? string.Empty;
        AssetType = assetType ?? string.Empty;
    }

    public string Guid { get; }

    public string MainAssetPath { get; }

    public string AssetType { get; }

    public bool IsValid => IsCanonicalGuid(Guid);

    private static bool IsCanonicalGuid(string? value)
    {
        if (value is null || value.Length != 32)
            return false;

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var isDigit = character is >= '0' and <= '9';
            var isLowerHex = character is >= 'a' and <= 'f';
            if (!isDigit && !isLowerHex)
                return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '0')
                return true;
        }
        return false;
    }
}

/// <summary>Opaque, provider-owned key produced during initialization or bake.</summary>
public readonly record struct UnityAssetProviderKey(int Value)
{
    public bool IsValid => Value > 0;
}

public interface IUnityAssetProvider
{
    string Id { get; }

    bool TryBind(in UnityAssetReference reference, out UnityAssetProviderKey key);

    bool TryResolve<T>(in UnityAssetProviderKey key, out T? asset)
        where T : class;
}

/// <summary>
/// A pre-bound provider/key pair. Resolve performs no registry lookup or display-path fallback.
/// </summary>
public readonly struct BoundUnityAsset
{
    private readonly IUnityAssetProvider? _provider;
    private readonly UnityAssetProviderKey _key;

    private BoundUnityAsset(IUnityAssetProvider provider, UnityAssetProviderKey key)
    {
        _provider = provider;
        _key = key;
    }

    public bool IsBound => _provider is not null && _key.IsValid;

    public UnityAssetProviderKey ProviderKey => _key;

    public static bool TryBind(
        IUnityAssetProvider provider,
        in UnityAssetReference reference,
        out BoundUnityAsset binding)
    {
        RuntimeCompatibility.NotNull(provider, nameof(provider));
        if (provider.TryBind(reference, out var key) && key.IsValid)
        {
            binding = new BoundUnityAsset(provider, key);
            return true;
        }

        binding = default;
        return false;
    }

    public bool TryResolve<T>(out T? asset)
        where T : class
    {
        if (_provider is not null && _key.IsValid)
            return _provider.TryResolve(_key, out asset);

        asset = null;
        return false;
    }
}
