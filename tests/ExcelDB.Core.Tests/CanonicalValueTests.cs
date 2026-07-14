using ExcelDb.Core.Values;

namespace ExcelDb.Core.Tests;

public sealed class CanonicalValueTests
{
    [Fact]
    public void Missing_Default_Null_AndValue_AreDistinct()
    {
        var missing = CanonicalValue.Missing;
        var defaulted = missing.MaterializeDefault("0");
        var explicitNull = CanonicalValue.Null;
        var explicitValue = CanonicalValue.FromValue("0");

        Assert.NotEqual(missing, defaulted);
        Assert.NotEqual(defaulted, explicitNull);
        Assert.NotEqual(defaulted, explicitValue);
        Assert.Equal(CanonicalValueState.Defaulted, defaulted.State);
    }
}
