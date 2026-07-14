using ExcelDb.Core.Identity;

namespace ExcelDb.Core.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void RowGuid_UsesCanonicalLowercaseText()
    {
        var value = RowGuid.Parse("00112233445566778899aabbccddeeff");

        Assert.Equal("00112233445566778899aabbccddeeff", value.ToString());
        Assert.False(RowGuid.TryParse("00112233445566778899AABBCCDDEEFF", out _));
        Assert.False(RowGuid.TryParse(new string('0', 32), out _));
    }

    [Fact]
    public void AssetIdentity_RoundTripsTableAndRowGuid()
    {
        var identity = new AssetIdentity(101, RowGuid.Parse("00112233445566778899aabbccddeeff"));

        Assert.True(AssetIdentity.TryParse(identity.ToString(), out var parsed));
        Assert.Equal(identity, parsed);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("server")]
    [InlineData("lite-client")]
    public void ExportTargetId_AcceptsStableTokens(string token)
    {
        Assert.True(ExportTargetId.TryParse(token, out var target));
        Assert.Equal(token, target.Value);
    }
}
