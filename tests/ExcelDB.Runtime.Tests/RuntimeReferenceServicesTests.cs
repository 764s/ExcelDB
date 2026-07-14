using ExcelDb.Runtime.References;
using System.Text;
using ExcelDb.Core.Identity;

namespace ExcelDb.Runtime.Tests;

public sealed class RuntimeReferenceServicesTests
{
    [Fact]
    public void UnityAsset_IsBoundOnce_AndHotResolveUsesTheBoundProvider()
    {
        var asset = new object();
        var provider = new TestUnityProvider(asset);
        var reference = new UnityAssetReference(
            "0123456789abcdef0123456789abcdef",
            "Assets/DisplayOnly.asset",
            "TestAsset");

        Assert.True(BoundUnityAsset.TryBind(provider, reference, out var binding));
        Assert.True(binding.IsBound);

        for (var index = 0; index < 32; index++)
        {
            Assert.True(binding.TryResolve<object>(out var resolved));
            Assert.Same(asset, resolved);
        }

        Assert.Equal(1, provider.BindCount);
        Assert.Equal(32, provider.ResolveCount);
        Assert.Equal("0123456789abcdef0123456789abcdef", provider.LastGuid);
        Assert.NotEqual("Assets/DisplayOnly.asset", provider.LastGuid);
    }

    [Fact]
    public void Localization_MissingProviderOrKey_ReturnsKeyAndDeduplicatesWarning()
    {
        var warnings = new TestLocalizationWarnings();
        var missingProvider = new LocalizedTextResolver(warnings: warnings);

        Assert.Equal("ui.missing", missingProvider.Resolve("ui.missing"));
        Assert.Equal("ui.missing", missingProvider.Resolve("ui.missing"));
        Assert.Single(warnings.Events);
        Assert.False(warnings.Events[0].ProviderAvailable);

        var provider = new TestLocalizationProvider();
        var withProvider = new LocalizedTextResolver(provider, warnings);
        Assert.Equal("Start", withProvider.Resolve("ui.start"));
        Assert.Equal("ui.unknown", withProvider.Resolve("ui.unknown"));
        Assert.Equal("ui.unknown", withProvider.Resolve("ui.unknown"));
        Assert.Equal(2, warnings.Events.Count);
        Assert.True(warnings.Events[1].ProviderAvailable);
    }

    [Fact]
    public void CustomReferenceFamily_CanonicalizesAndPrebindsBeforeResolve()
    {
        var target = new object();
        var family = new TestReferenceFamily(target);
        var registry = new ReferenceFamilyRegistry([family]);

        Assert.True(registry.TryBind("game.item", "  Sword ", out var binding, out var error), error);
        Assert.Equal("sword", binding.CanonicalText);
        Assert.Equal("game.item", binding.FamilyId);
        Assert.Equal((ushort)3, binding.FamilyVersion);

        for (var index = 0; index < 32; index++)
        {
            Assert.True(binding.TryResolve<object>(out var resolved));
            Assert.Same(target, resolved);
        }

        Assert.Equal(1, family.BindCount);
        Assert.Equal(32, family.ResolveCount);
    }

    [Fact]
    public void BuiltInDictionaryProviders_AreUsableWithoutHostSpecificCode()
    {
        var localized = new DictionaryLocalizedTextProvider(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ui.ok"] = "OK",
            });
        Assert.Equal("OK", new LocalizedTextResolver(localized).Resolve("ui.ok"));

        var target = new object();
        var family = new OrdinalReferenceFamily<object>(
            "game.simple",
            1,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["item/sword"] = 5,
            },
            new Dictionary<int, object>
            {
                [5] = target,
            });
        var registry = new ReferenceFamilyRegistry([family]);

        Assert.True(registry.TryBind("game.simple", " item/sword ", out var binding, out var error), error);
        Assert.True(binding.TryResolve<object>(out var resolved));
        Assert.Same(target, resolved);
    }

    [Fact]
    public void RuntimeOpen_PassesProvidersToBindingsAndStoresOnlyPreboundHandles()
    {
        RuntimeDatabase.Close();
        var unityTarget = new object();
        var customTarget = new object();
        var unity = new TestUnityProvider(unityTarget);
        var family = new TestReferenceFamily(customTarget);
        var references = new RuntimeReferenceServices(
            unity,
            new LocalizedTextResolver(new TestLocalizationProvider()),
            new ReferenceFamilyRegistry([family]));
        var identity = new AssetIdentity(
            ProviderRegistry.TableId,
            RowGuid.Parse("00000000000000000000000000000001"));
        var record = new RuntimeAssetRecord(
            identity,
            "provider",
            [
                new RuntimeFieldValue(1, Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")),
                new RuntimeFieldValue(2, Encoding.UTF8.GetBytes("ui.start")),
                new RuntimeFieldValue(3, Encoding.UTF8.GetBytes("Sword")),
            ]);
        var source = new TestSource(
            RuntimeTestData.SchemaHash,
            RuntimeTestData.Client,
            RuntimeTestData.Snapshot([record]));

        try
        {
            Assert.True(RuntimeDatabase.Open(
                source,
                new ProviderRegistry(),
                new RuntimeBootstrapOptions(
                    RuntimeMode.Development,
                    false,
                    referenceServices: references)));
            var asset = RuntimeDatabase.LoadAsset<ProviderBackedAsset>("provider");
            Assert.NotNull(asset);
            Assert.Equal("Start", asset.LocalizedText);

            for (var index = 0; index < 32; index++)
            {
                Assert.True(asset.Unity.TryResolve<object>(out var resolvedUnity));
                Assert.Same(unityTarget, resolvedUnity);
                Assert.True(asset.Custom.TryResolve<object>(out var resolvedCustom));
                Assert.Same(customTarget, resolvedCustom);
            }

            Assert.Equal(1, unity.BindCount);
            Assert.Equal(1, family.BindCount);
        }
        finally
        {
            RuntimeDatabase.Close();
        }
    }

    private sealed class TestUnityProvider(object asset) : IUnityAssetProvider
    {
        public string Id => "test.unity";

        public int BindCount { get; private set; }

        public int ResolveCount { get; private set; }

        public string? LastGuid { get; private set; }

        public bool TryBind(in UnityAssetReference reference, out UnityAssetProviderKey key)
        {
            BindCount++;
            LastGuid = reference.Guid;
            key = new UnityAssetProviderKey(7);
            return true;
        }

        public bool TryResolve<T>(in UnityAssetProviderKey key, out T? resolved)
            where T : class
        {
            ResolveCount++;
            resolved = key.Value == 7 ? asset as T : null;
            return resolved is not null;
        }
    }

    private sealed class TestLocalizationProvider : ILocalizedTextProvider
    {
        public bool TryResolve(string key, out string? text)
        {
            text = key == "ui.start" ? "Start" : null;
            return text is not null;
        }
    }

    private sealed class TestLocalizationWarnings : ILocalizedTextWarningSink
    {
        public List<(string Key, bool ProviderAvailable)> Events { get; } = [];

        public void MissingLocalizedText(string key, bool providerAvailable) =>
            Events.Add((key, providerAvailable));
    }

    private sealed class TestReferenceFamily(object target) : IReferenceFamily
    {
        public string Id => "game.item";

        public ushort Version => 3;

        public int BindCount { get; private set; }

        public int ResolveCount { get; private set; }

        public bool TryParse(string text, out CanonicalReferenceValue value, out string? error)
        {
            value = new CanonicalReferenceValue(text.Trim().ToLowerInvariant());
            error = null;
            return true;
        }

        public string WriteCanonical(in CanonicalReferenceValue value) => value.Text;

        public ReferenceFamilyValidation Validate(in CanonicalReferenceValue value) =>
            value.Text.Length > 0
                ? ReferenceFamilyValidation.Success
                : ReferenceFamilyValidation.Failure("Reference is empty.");

        public bool TryBind(in CanonicalReferenceValue value, out ReferenceProviderKey key)
        {
            BindCount++;
            key = value.Text == "sword" ? new ReferenceProviderKey(11) : default;
            return key.IsValid;
        }

        public bool TryResolve<T>(in ReferenceProviderKey key, out T? value)
            where T : class
        {
            ResolveCount++;
            value = key.Value == 11 ? target as T : null;
            return value is not null;
        }
    }

    private sealed class ProviderBackedAsset
    {
        public BoundUnityAsset Unity { get; set; }

        public string LocalizedText { get; set; } = string.Empty;

        public BoundReference Custom { get; set; }
    }

    private sealed record ProviderState(
        BoundUnityAsset Unity,
        string LocalizedText,
        BoundReference Custom);

    private sealed class ProviderRegistry : RuntimeSchemaRegistry
    {
        public const int TableId = 303;

        public ProviderRegistry()
            : base(
                RuntimeTestData.SchemaHash,
                RuntimeTestData.Client,
                [
                    new RuntimeTableBinding<ProviderBackedAsset>(
                        TableId,
                        static () => new ProviderBackedAsset(),
                        static (asset, record, services) =>
                        {
                            var guid = Encoding.UTF8.GetString(record.Fields[0].Data.Span);
                            var localizationKey = Encoding.UTF8.GetString(record.Fields[1].Data.Span);
                            var customToken = Encoding.UTF8.GetString(record.Fields[2].Data.Span);
                            if (services.UnityAssets is null
                                || !BoundUnityAsset.TryBind(
                                    services.UnityAssets,
                                    new UnityAssetReference(guid),
                                    out var unity))
                            {
                                throw new InvalidOperationException("Unity provider binding failed.");
                            }
                            if (services.LocalizedText is null)
                                throw new InvalidOperationException("Localization provider is absent.");
                            string? bindError = null;
                            if (services.ReferenceFamilies is null
                                || !services.ReferenceFamilies.TryBind(
                                    "game.item",
                                    customToken,
                                    out var custom,
                                    out bindError))
                            {
                                throw new InvalidOperationException(bindError ?? "Custom provider binding failed.");
                            }

                            asset.Unity = unity;
                            asset.LocalizedText = services.LocalizedText.Resolve(localizationKey);
                            asset.Custom = custom;
                        },
                        static asset =>
                        {
                            asset.Unity = default;
                            asset.LocalizedText = string.Empty;
                            asset.Custom = default;
                        },
                        static asset => new ProviderState(
                            asset.Unity,
                            asset.LocalizedText,
                            asset.Custom),
                        static (asset, state) =>
                        {
                            var typed = (ProviderState)state;
                            asset.Unity = typed.Unity;
                            asset.LocalizedText = typed.LocalizedText;
                            asset.Custom = typed.Custom;
                        })
                ])
        {
        }
    }
}
