using System.Reflection;
using System.Runtime.Versioning;
using ExcelDb.Authoring.Workbooks;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.OpenXml;
using ExcelDbEditor;

namespace ExcelDb.Authoring.Workbooks.Tests;

public sealed class NetStandardPortIdentityTests
{
    [Fact]
    public void Xlsx_authoring_chain_executes_from_netstandard21_assemblies()
    {
        AssertNetStandard21(typeof(CanonicalSchemaDescriptor).Assembly);
        AssertNetStandard21(typeof(XlsxWorkbookCodec).Assembly);
        AssertNetStandard21(typeof(AssetDatabase).Assembly);
        AssertNetStandard21(typeof(XlsxAuthoringWorkbookAdapter).Assembly);
    }

    private static void AssertNetStandard21(Assembly assembly)
    {
        var framework = assembly.GetCustomAttribute<TargetFrameworkAttribute>();
        Assert.NotNull(framework);
        Assert.Equal(".NETStandard,Version=v2.1", framework.FrameworkName);
    }
}
