using System.Globalization;

namespace ExcelDb.Core.Values;

public enum CanonicalValueState : byte
{
    Missing = 0,
    Defaulted = 1,
    Null = 2,
    Value = 3,
    Invalid = 4,
}

/// <summary>
/// Canonical authoring value. Missing, materialized schema default, explicit null and an explicit
/// value remain distinct so import, merge and writeback never invent an equivalence.
/// </summary>
public readonly record struct CanonicalValue
{
    private CanonicalValue(CanonicalValueState state, string? text, string? rawText)
    {
        State = state;
        Text = text;
        RawText = rawText;
    }

    public CanonicalValueState State { get; }

    /// <summary>Invariant canonical text for Value/Defaulted, otherwise null.</summary>
    public string? Text { get; }

    /// <summary>Unparsed source text for Invalid, otherwise null.</summary>
    public string? RawText { get; }

    public static CanonicalValue Missing => new(CanonicalValueState.Missing, null, null);

    public static CanonicalValue Null => new(CanonicalValueState.Null, null, null);

    public static CanonicalValue FromValue(string value) =>
        new(CanonicalValueState.Value, value ?? throw new ArgumentNullException(nameof(value)), null);

    public static CanonicalValue FromDefault(string value) =>
        new(CanonicalValueState.Defaulted, value ?? throw new ArgumentNullException(nameof(value)), null);

    public static CanonicalValue Invalid(string rawText) =>
        new(CanonicalValueState.Invalid, null, rawText ?? throw new ArgumentNullException(nameof(rawText)));

    public CanonicalValue MaterializeDefault(string? schemaDefault) =>
        State == CanonicalValueState.Missing && schemaDefault is not null
            ? FromDefault(schemaDefault)
            : this;

    public static string CanonicalizeBoolean(bool value) => value ? "true" : "false";

    public static string CanonicalizeInt64(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string CanonicalizeUInt64(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    public static string CanonicalizeDouble(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public override string ToString() => State switch
    {
        CanonicalValueState.Missing => "<missing>",
        CanonicalValueState.Defaulted => $"<default:{Text}>",
        CanonicalValueState.Null => "<null>",
        CanonicalValueState.Value => Text ?? string.Empty,
        CanonicalValueState.Invalid => $"<invalid:{RawText}>",
        _ => throw new InvalidOperationException($"Unknown canonical value state {State}."),
    };
}
