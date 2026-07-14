using System.Text;
using ExcelDb.Schema.Hashing;

namespace ExcelDB.Schema.Tests;

public sealed class XxHash64Tests
{
    [Theory]
    [InlineData("", 0xef46db3751d8e999UL)]
    [InlineData("a", 0xd24ec4f1a98c6e5bUL)]
    [InlineData("abc", 0x44bc2cf5ad770999UL)]
    public void Matches_reference_vectors(string value, ulong expected)
    {
        Assert.Equal(expected, XxHash64.Hash(Encoding.UTF8.GetBytes(value)));
    }
}
