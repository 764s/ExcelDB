#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}

[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
internal sealed class CallerArgumentExpressionAttribute : Attribute
{
    public CallerArgumentExpressionAttribute(string parameterName) => ParameterName = parameterName;

    public string ParameterName { get; }
}
#endif
