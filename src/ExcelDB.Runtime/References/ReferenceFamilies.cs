using System.Collections.Immutable;

namespace ExcelDb.Runtime.References;

public readonly record struct CanonicalReferenceValue
{
    public CanonicalReferenceValue(string text)
    {
        RuntimeCompatibility.NotNull(text, nameof(text));
        Text = text;
    }

    public string Text { get; }
}

public readonly record struct ReferenceFamilyValidation(bool IsValid, string? Error)
{
    public static ReferenceFamilyValidation Success => new(true, null);

    public static ReferenceFamilyValidation Failure(string error)
    {
        RuntimeCompatibility.NotNullOrWhiteSpace(error, nameof(error));
        return new ReferenceFamilyValidation(false, error);
    }
}

public readonly record struct ReferenceProviderKey(int Value)
{
    public bool IsValid => Value > 0;
}

/// <summary>
/// Complete deterministic custom-reference contract. Parse/write/validate/convert run outside the
/// hot path; TryResolve consumes the pre-bound provider key directly.
/// </summary>
public interface IReferenceFamily
{
    string Id { get; }

    ushort Version { get; }

    bool TryParse(string text, out CanonicalReferenceValue value, out string? error);

    string WriteCanonical(in CanonicalReferenceValue value);

    ReferenceFamilyValidation Validate(in CanonicalReferenceValue value);

    bool TryBind(in CanonicalReferenceValue value, out ReferenceProviderKey key);

    bool TryResolve<T>(in ReferenceProviderKey key, out T? value)
        where T : class;
}

public sealed class ReferenceFamilyRegistry
{
    private readonly ImmutableDictionary<string, IReferenceFamily> _families;
    private readonly ImmutableArray<IReferenceFamily> _orderedFamilies;

    public ReferenceFamilyRegistry(IEnumerable<IReferenceFamily> families)
    {
        RuntimeCompatibility.NotNull(families, nameof(families));
        var builder = ImmutableDictionary.CreateBuilder<string, IReferenceFamily>(StringComparer.Ordinal);
        foreach (var family in families)
        {
            RuntimeCompatibility.NotNull(family, nameof(family));
            ValidateIdentity(family);
            if (!builder.TryAdd(family.Id, family))
                throw new ArgumentException($"Duplicate reference family id '{family.Id}'.", nameof(families));
        }

        _families = builder.ToImmutable();
        _orderedFamilies = _families.Values.OrderBy(static family => family.Id, StringComparer.Ordinal).ToImmutableArray();
    }

    public IReadOnlyList<IReferenceFamily> Families => _orderedFamilies;

    public bool TryBind(string familyId, string sourceText, out BoundReference binding, out string? error)
    {
        RuntimeCompatibility.NotNullOrWhiteSpace(familyId, nameof(familyId));
        RuntimeCompatibility.NotNull(sourceText, nameof(sourceText));
        binding = default;
        if (!_families.TryGetValue(familyId, out var family))
        {
            error = $"Reference family '{familyId}' is not registered.";
            return false;
        }

        if (!family.TryParse(sourceText, out var canonical, out error))
            return false;

        var validation = family.Validate(canonical);
        if (!validation.IsValid)
        {
            error = validation.Error ?? "Reference validation failed.";
            return false;
        }

        var stableText = family.WriteCanonical(canonical);
        if (!family.TryParse(stableText, out var roundTripped, out var roundTripError)
            || !string.Equals(family.WriteCanonical(roundTripped), stableText, StringComparison.Ordinal))
        {
            error = roundTripError ?? "Reference family canonical write is not deterministic.";
            return false;
        }

        if (!family.TryBind(roundTripped, out var key) || !key.IsValid)
        {
            error = "Reference provider could not bind the canonical value.";
            return false;
        }

        binding = new BoundReference(family, key, stableText);
        error = null;
        return true;
    }

    private static void ValidateIdentity(IReferenceFamily family)
    {
        if (string.IsNullOrWhiteSpace(family.Id)
            || !IsAsciiLower(family.Id[0])
            || family.Id.Any(static character =>
                !IsAsciiLower(character)
                && character is not (>= '0' and <= '9')
                && character is not '.' and not '-'))
        {
            throw new ArgumentException($"Invalid reference family id '{family.Id}'.", nameof(family));
        }

        if (family.Version == 0)
            throw new ArgumentException($"Reference family '{family.Id}' has version zero.", nameof(family));
    }

    private static bool IsAsciiLower(char value) => value is >= 'a' and <= 'z';
}

public readonly struct BoundReference
{
    private readonly IReferenceFamily? _family;
    private readonly ReferenceProviderKey _key;

    internal BoundReference(IReferenceFamily family, ReferenceProviderKey key, string canonicalText)
    {
        _family = family;
        _key = key;
        CanonicalText = canonicalText;
    }

    public bool IsBound => _family is not null && _key.IsValid;

    public string? FamilyId => _family?.Id;

    public ushort FamilyVersion => _family?.Version ?? 0;

    public string? CanonicalText { get; }

    public bool TryResolve<T>(out T? value)
        where T : class
    {
        if (_family is not null && _key.IsValid)
            return _family.TryResolve(_key, out value);

        value = null;
        return false;
    }
}

/// <summary>
/// Deterministic reference family for the common case where canonical tokens and baked integer
/// provider keys are sufficient. More specialized families can implement <see cref="IReferenceFamily"/>.
/// </summary>
public sealed class OrdinalReferenceFamily<T> : IReferenceFamily
    where T : class
{
    private readonly IReadOnlyDictionary<string, int> _keys;
    private readonly IReadOnlyDictionary<int, T> _values;

    public OrdinalReferenceFamily(
        string id,
        ushort version,
        IReadOnlyDictionary<string, int> keys,
        IReadOnlyDictionary<int, T> values)
    {
        RuntimeCompatibility.NotNullOrWhiteSpace(id, nameof(id));
        if (version == 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        Id = id;
        Version = version;
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _values = values ?? throw new ArgumentNullException(nameof(values));
    }

    public string Id { get; }

    public ushort Version { get; }

    public bool TryParse(string text, out CanonicalReferenceValue value, out string? error)
    {
        RuntimeCompatibility.NotNull(text, nameof(text));
        var canonical = text.Trim();
        value = new CanonicalReferenceValue(canonical);
        error = canonical.Length == 0 ? "Reference token is empty." : null;
        return error is null;
    }

    public string WriteCanonical(in CanonicalReferenceValue value) => value.Text;

    public ReferenceFamilyValidation Validate(in CanonicalReferenceValue value) =>
        _keys.ContainsKey(value.Text)
            ? ReferenceFamilyValidation.Success
            : ReferenceFamilyValidation.Failure($"Reference token '{value.Text}' is not registered.");

    public bool TryBind(in CanonicalReferenceValue value, out ReferenceProviderKey key)
    {
        if (_keys.TryGetValue(value.Text, out var rawKey) && rawKey > 0)
        {
            key = new ReferenceProviderKey(rawKey);
            return true;
        }

        key = default;
        return false;
    }

    public bool TryResolve<TValue>(in ReferenceProviderKey key, out TValue? value)
        where TValue : class
    {
        if (_values.TryGetValue(key.Value, out var resolved) && resolved is TValue typed)
        {
            value = typed;
            return true;
        }

        value = null;
        return false;
    }
}
