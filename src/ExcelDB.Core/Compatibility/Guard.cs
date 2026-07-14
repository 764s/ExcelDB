namespace ExcelDb.Core.Compatibility;

internal static class Guard
{
    public static void NotNullOrWhiteSpace(string? value, string parameterName)
    {
#if NETSTANDARD2_1
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value cannot be null, empty, or whitespace.", parameterName);
#else
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
#endif
    }
}
