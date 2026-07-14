namespace ExcelDb.Runtime.References;

public interface ILocalizedTextProvider
{
    bool TryResolve(string key, out string? text);
}

public interface ILocalizedTextWarningSink
{
    void MissingLocalizedText(string key, bool providerAvailable);
}

/// <summary>A small host-independent provider suitable for baked locale dictionaries.</summary>
public sealed class DictionaryLocalizedTextProvider : ILocalizedTextProvider
{
    private readonly IReadOnlyDictionary<string, string> _texts;

    public DictionaryLocalizedTextProvider(IReadOnlyDictionary<string, string> texts)
    {
        _texts = texts ?? throw new ArgumentNullException(nameof(texts));
    }

    public bool TryResolve(string key, out string? text)
    {
        RuntimeCompatibility.NotNull(key, nameof(key));
        if (_texts.TryGetValue(key, out var resolved))
        {
            text = resolved;
            return true;
        }

        text = null;
        return false;
    }
}

/// <summary>
/// Runtime localization facade. Missing providers/keys return the key and emit at most one warning
/// per key for the lifetime of this facade.
/// </summary>
public sealed class LocalizedTextResolver
{
    private readonly ILocalizedTextProvider? _provider;
    private readonly ILocalizedTextWarningSink? _warnings;
    private readonly HashSet<string> _warnedKeys = new(StringComparer.Ordinal);

    public LocalizedTextResolver(
        ILocalizedTextProvider? provider = null,
        ILocalizedTextWarningSink? warnings = null)
    {
        _provider = provider;
        _warnings = warnings;
    }

    public string Resolve(string key)
    {
        RuntimeCompatibility.NotNullOrWhiteSpace(key, nameof(key));
        if (_provider is not null
            && _provider.TryResolve(key, out var text)
            && text is not null)
        {
            return text;
        }

        if (_warnedKeys.Add(key))
            _warnings?.MissingLocalizedText(key, _provider is not null);
        return key;
    }
}
