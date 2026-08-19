using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace ExcelDb.Authoring.Workbooks.Compatibility;

internal static class Guard
{
    public static void NotNull(
        [NotNull] object? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
#if NETSTANDARD2_1
        if (value is null)
            throw new ArgumentNullException(parameterName);
#else
        ArgumentNullException.ThrowIfNull(value, parameterName);
#endif
    }

    public static void NotNullOrWhiteSpace(
        [NotNull] string? value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
#if NETSTANDARD2_1
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value cannot be null, empty, or whitespace.", parameterName);
#else
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
#endif
    }
}
