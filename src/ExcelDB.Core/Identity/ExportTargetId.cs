using System.Text.RegularExpressions;
using ExcelDb.Core.Compatibility;

namespace ExcelDb.Core.Identity;

public readonly partial record struct ExportTargetId : IComparable<ExportTargetId>
{
#if NETSTANDARD2_1
    private static readonly Regex TargetPattern = new(
        "^[a-z][a-z0-9-]*$",
        RegexOptions.CultureInvariant);
#endif

    public ExportTargetId(string value)
    {
        Guard.NotNullOrWhiteSpace(value, nameof(value));
        if (!Pattern().IsMatch(value))
            throw new ArgumentException($"Invalid export target id '{value}'.", nameof(value));
        Value = value;
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public int CompareTo(ExportTargetId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value ?? string.Empty;

    public static bool TryParse(string? value, out ExportTargetId result)
    {
        result = default;
        if (value is null || !Pattern().IsMatch(value))
            return false;
        result = new ExportTargetId(value);
        return true;
    }

#if NETSTANDARD2_1
    private static Regex Pattern() => TargetPattern;
#else
    [GeneratedRegex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
#endif
}
