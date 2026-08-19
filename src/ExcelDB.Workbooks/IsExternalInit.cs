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

[AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
internal sealed class CompilerFeatureRequiredAttribute : Attribute
{
    public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

    public string FeatureName { get; }

    public bool IsOptional { get; init; }
}

[AttributeUsage(
    AttributeTargets.Class
    | AttributeTargets.Struct
    | AttributeTargets.Field
    | AttributeTargets.Property,
    Inherited = false)]
internal sealed class RequiredMemberAttribute : Attribute
{
}
#endif
