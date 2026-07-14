namespace ExcelDb.Runtime.References;

/// <summary>
/// Immutable session composition for external reference providers. Bindings consume this only while
/// constructing or patching resident values and store pre-bound handles for their hot path.
/// </summary>
public sealed class RuntimeReferenceServices
{
    public static RuntimeReferenceServices Empty { get; } = new();

    public RuntimeReferenceServices(
        IUnityAssetProvider? unityAssets = null,
        LocalizedTextResolver? localizedText = null,
        ReferenceFamilyRegistry? referenceFamilies = null)
    {
        UnityAssets = unityAssets;
        LocalizedText = localizedText;
        ReferenceFamilies = referenceFamilies;
    }

    public IUnityAssetProvider? UnityAssets { get; }

    public LocalizedTextResolver? LocalizedText { get; }

    public ReferenceFamilyRegistry? ReferenceFamilies { get; }
}
