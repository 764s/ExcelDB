namespace ExcelDb.Runtime.Tests;

using System.Collections.Immutable;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Generation;

public sealed class GeneratedRegistryContractTests
{
    [Fact]
    public void GeneratedStyleRegistry_IsAClosedImmutableRuntimeContract()
    {
        var registry = GeneratedClientRegistry.Instance;

        Assert.IsAssignableFrom<RuntimeSchemaRegistry>(registry);
        Assert.Equal(RuntimeTestData.SchemaHash, registry.ExpectedSchemaHash);
        Assert.Equal(RuntimeTestData.Client, registry.ExpectedExportTarget);
        var binding = Assert.Single(registry.Bindings);
        Assert.Equal(RuntimeTestData.TableId, binding.TableId);
        Assert.Equal(typeof(TestAsset), binding.RuntimeType);
        Assert.Same(registry, GeneratedClientRegistry.Instance);
        Assert.Empty(typeof(GeneratedClientRegistry).GetConstructors());
    }

    [Fact]
    public void M1Generator_EmitsTheConcreteM7RegistryAndBindingContract()
    {
        var descriptor = new CanonicalSchemaDescriptor(
            [
                new CanonicalTableDescriptor(
                    RuntimeTestData.TableId,
                    "Config",
                    "Game.Config",
                    CanonicalTableKind.Asset,
                    "Configs",
                    [],
                    [],
                    ["client"],
                    [],
                    ImmutableArray<CanonicalFieldDescriptor>.Empty,
                    [])
            ],
            [],
            [],
            RuntimeTestData.SchemaHash);

        var result = new SchemaCodeGenerator().Generate(descriptor);
        var surface = Assert.Single(result.Artifacts, artifact =>
            artifact.Kind == "runtime-csharp" && artifact.ExportTarget == "client");
        var registry = Assert.Single(result.Artifacts, artifact =>
            artifact.Kind == "runtime-registry-csharp" && artifact.ExportTarget == "client");

        Assert.Contains(
            $": global::{typeof(RuntimeSchemaRegistry).FullName}",
            registry.Content,
            StringComparison.Ordinal);
        Assert.Contains("GeneratedRuntimeBindings.Tables", registry.Content, StringComparison.Ordinal);
        Assert.Contains("RuntimeTableBinding<Config>", surface.Content, StringComparison.Ordinal);
        Assert.Contains(
            $"base(0x{RuntimeTestData.SchemaHash:x16}UL, new ExportTargetId(\"client\")",
            registry.Content,
            StringComparison.Ordinal);
    }

    private sealed class GeneratedClientRegistry : RuntimeSchemaRegistry
    {
        public static GeneratedClientRegistry Instance { get; } = new();

        private GeneratedClientRegistry()
            : base(
                RuntimeTestData.SchemaHash,
                RuntimeTestData.Client,
                [
                    new RuntimeTableBinding<TestAsset>(
                        RuntimeTestData.TableId,
                        static () => new TestAsset(),
                        RuntimeTestData.Apply,
                        static asset =>
                        {
                            asset.Name = string.Empty;
                            asset.Value = 0;
                        },
                        static asset => new TestAssetState(asset.Name, asset.Value),
                        static (asset, state) =>
                        {
                            var typed = (TestAssetState)state;
                            asset.Name = typed.Name;
                            asset.Value = typed.Value;
                        })
                ])
        {
        }
    }
}
