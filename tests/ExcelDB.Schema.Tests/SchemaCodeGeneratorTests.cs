using System.Diagnostics;
using System.Security;
using System.Text;
using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Generation;

namespace ExcelDB.Schema.Tests;

public sealed class SchemaCodeGeneratorTests
{
    [Fact]
    public async Task Per_target_surfaces_registry_and_manifest_are_deterministic_and_compile()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("codegen.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";

            message Details {
              string label = 1;
            }

            message Item {
              option (exceldb.table) = { kind: ASSET, id: 78, implements: "loot" };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }

            message Config {
              option (exceldb.table) = { kind: ASSET, id: 77 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              int32 client_value = 2 [(exceldb.field) = { export_targets: { ids: "client" } }];
              int32 server_value = 3 [(exceldb.field) = { export_targets: { ids: "server" } }];
              string authoring_note = 4 [(exceldb.field) = { export_targets: {} }];
              exceldb.RowRef direct_item = 5 [(exceldb.field) = { ref_table: "Item" }];
              exceldb.RowRef grouped_item = 6 [(exceldb.field) = { ref_group: "loot" }];
              repeated int32 levels = 7;
              Details details = 8 [(exceldb.field) = { expand: EXPANDED_COLUMNS }];
            }
            """);
        var compilation = await new SchemaCompiler().CompileAsync(schema.Path);
        Assert.True(compilation.Succeeded, Describe(compilation));

        var generator = new SchemaCodeGenerator();
        var catalogHash = PackageSystemProtoCatalog.Default.CatalogHash;
        var first = generator.Generate(compilation.Descriptor!, catalogHash);
        var second = generator.Generate(compilation.Descriptor!, catalogHash);

        Assert.Equal(first.Manifest, second.Manifest);
        Assert.Equal(first.Artifacts.ToArray(), second.Artifacts.ToArray());
        Assert.Equal(5, first.Artifacts.Length);
        Assert.Contains("exceldb.codegen-manifest.v1", first.Manifest.Json, StringComparison.Ordinal);
        Assert.Equal(64, first.Manifest.CodegenHash.Length);
        Assert.Equal(64, first.Manifest.ManifestHash.Length);
        Assert.Equal(catalogHash, first.Manifest.CatalogHash);
        Assert.Contains($"\"catalogHash\":\"{catalogHash}\"", first.Manifest.Json, StringComparison.Ordinal);
        Assert.All(first.Artifacts, static artifact =>
            Assert.True(
                artifact.RelativePath.StartsWith("Authoring/", StringComparison.Ordinal)
                || artifact.RelativePath.StartsWith("Runtime/", StringComparison.Ordinal),
                $"Unexpected generated path: {artifact.RelativePath}"));
        Assert.Contains(first.Artifacts, static artifact =>
            artifact.RelativePath == "Authoring/ExcelDbSchema.Authoring.g.cs");
        Assert.Contains(first.Artifacts, static artifact =>
            artifact.RelativePath.StartsWith("Runtime/client/", StringComparison.Ordinal));
        Assert.Contains("\"path\":\"Authoring/", first.Manifest.Json, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"Runtime/client/", first.Manifest.Json, StringComparison.Ordinal);

        var outputRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"exceldb-path-test-{Guid.NewGuid():N}"));
        var rootPrefix = Path.TrimEndingDirectorySeparator(outputRoot) + Path.DirectorySeparatorChar;
        Assert.All(first.Artifacts, artifact =>
        {
            var physical = Path.GetFullPath(
                Path.Combine(outputRoot, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.StartsWith(rootPrefix, physical, StringComparison.OrdinalIgnoreCase);
        });

        var invalidTable = compilation.Descriptor!.Tables[0] with { ExportTargets = ["../server"] };
        var invalidDescriptor = compilation.Descriptor with { Tables = [invalidTable] };
        Assert.Throws<InvalidDataException>(() => generator.Generate(invalidDescriptor, catalogHash));

        var client = Assert.Single(first.Artifacts, artifact =>
            artifact.Kind == "runtime-csharp" && artifact.ExportTarget == "client");
        Assert.Contains("ClientValue", client.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerValue", client.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthoringNote", client.Content, StringComparison.Ordinal);
        Assert.Contains("RuntimeTableBinding<Config>", client.Content, StringComparison.Ordinal);
        Assert.Contains("RuntimeAssetRecord record", client.Content, StringComparison.Ordinal);
        Assert.Contains("Encoding.UTF8.GetString", client.Content, StringComparison.Ordinal);
        Assert.Contains("CreateRollback_77", client.Content, StringComparison.Ordinal);
        Assert.Contains("CaptureRollback_77", client.Content, StringComparison.Ordinal);
        Assert.Contains("RollbackState_77", client.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("new object?[]", client.Content, StringComparison.Ordinal);

        var clientRegistry = Assert.Single(first.Artifacts, artifact =>
            artifact.Kind == "runtime-registry-csharp" && artifact.ExportTarget == "client");
        Assert.Contains(": global::ExcelDb.Runtime.RuntimeSchemaRegistry", clientRegistry.Content, StringComparison.Ordinal);
        Assert.Contains("new ExportTargetId(\"client\")", clientRegistry.Content, StringComparison.Ordinal);
        Assert.Contains($"0x{compilation.Descriptor!.SchemaHash:x16}UL", clientRegistry.Content, StringComparison.Ordinal);

        var authoring = Assert.Single(first.Artifacts, static artifact => artifact.Kind == "authoring-csharp");
        Assert.Contains("ClientValue", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("ServerValue", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("AuthoringNote", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("public enum GeneratedFieldShape", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("public int KeyOrder { get; }", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("public string? ReferenceTable { get; }", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("public string? ReferenceGroup { get; }", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("public IReadOnlyList<string> Implements { get; }", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("new string[] { \"loot\" }", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("GeneratedFieldShape.RepeatedScalar", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("GeneratedFieldShape.Message", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("GeneratedFieldShape.Scalar, false, false, 1, null, null", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("GeneratedFieldShape.Message, true, false, 0, \"Item\", null", authoring.Content, StringComparison.Ordinal);
        Assert.Contains("GeneratedFieldShape.Message, true, false, 0, null, \"loot\"", authoring.Content, StringComparison.Ordinal);

        await AssertRuntimeArtifactsCompile(first.Artifacts);
    }

    [Fact]
    public async Task Nested_member_paths_use_the_generated_member_names_at_every_level()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("nested-member-paths.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";

            message Nested {
              // protoc rejects lower-case foo_bar/foo__bar as JSON-name duplicates, so the
              // leading-case variant creates the same generated C# collision in a valid schema.
              string foo__bar = 1;
              string Foo__bar = 2;
            }

            message Details {
              Nested foo_bar = 3 [(exceldb.field) = { expand: EXPANDED_COLUMNS }];
              Nested Foo_bar = 4 [(exceldb.field) = { expand: EXPANDED_COLUMNS }];
            }

            message Config {
              option (exceldb.table) = { kind: ASSET, id: 79 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              Details details = 2 [(exceldb.field) = { expand: EXPANDED_COLUMNS }];
            }
            """);

        var compilation = await new SchemaCompiler().CompileAsync(schema.Path);
        Assert.True(compilation.Succeeded, Describe(compilation));

        var artifacts = new SchemaCodeGenerator().Generate(
            compilation.Descriptor!,
            PackageSystemProtoCatalog.Default.CatalogHash).Artifacts;
        var authoring = Assert.Single(artifacts, static artifact => artifact.Kind == "authoring-csharp");

        Assert.Contains(
            "\"details.foo_bar.foo__bar\", \"Details.FooBar_F3.FooBar_F1\"",
            authoring.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"details.foo_bar.Foo__bar\", \"Details.FooBar_F3.FooBar_F2\"",
            authoring.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"details.Foo_bar.foo__bar\", \"Details.FooBar_F4.FooBar_F1\"",
            authoring.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"details.Foo_bar.Foo__bar\", \"Details.FooBar_F4.FooBar_F2\"",
            authoring.Content,
            StringComparison.Ordinal);

        await AssertAuthoringMemberPathsResolve(authoring);
    }

    private static async Task AssertRuntimeArtifactsCompile(IEnumerable<GeneratedSchemaArtifact> artifacts)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"exceldb-generated-compile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            foreach (var artifact in artifacts.Where(static artifact =>
                         artifact.RelativePath.EndsWith(".cs", StringComparison.Ordinal)))
            {
                var path = Path.Combine(temporaryDirectory, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, artifact.Content, new UTF8Encoding(false));
            }

            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var coreProject = Path.Combine(repositoryRoot, "src", "ExcelDB.Core", "ExcelDB.Core.csproj");
            var runtimeProject = Path.Combine(repositoryRoot, "src", "ExcelDB.Runtime", "ExcelDB.Runtime.csproj");
            var project = $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.1</TargetFramework>
                    <LangVersion>9.0</LangVersion>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{SecurityElement.Escape(coreProject)}}" />
                    <ProjectReference Include="{{SecurityElement.Escape(runtimeProject)}}" />
                  </ItemGroup>
                </Project>
                """;
            var projectPath = Path.Combine(temporaryDirectory, "Generated.csproj");
            await File.WriteAllTextAsync(projectPath, project, new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = temporaryDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("build");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("--nologo");
            startInfo.ArgumentList.Add("--disable-build-servers");
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            using var process = Process.Start(startInfo)!;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            Assert.True(process.ExitCode == 0, $"Generated runtime compilation failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task AssertAuthoringMemberPathsResolve(GeneratedSchemaArtifact authoring)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"exceldb-generated-reflection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory, "ExcelDbSchema.Authoring.g.cs"),
                authoring.Content,
                new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory, "Program.cs"),
                """
                using System;
                using System.Reflection;
                using ExcelDb.Generated.Authoring;

                internal static class Program
                {
                    private static int Main()
                    {
                        foreach (var table in GeneratedSchemaBindings.Tables)
                        {
                            foreach (var field in table.Fields)
                            {
                                var currentType = table.ClrType;
                                foreach (var segment in field.MemberPath.Split('.'))
                                {
                                    var property = currentType.GetProperty(
                                        segment,
                                        BindingFlags.Instance | BindingFlags.Public);
                                    if (property is null)
                                    {
                                        throw new InvalidOperationException(
                                            $"Generated metadata path '{field.MemberPath}' cannot resolve '{segment}' on '{currentType}'.");
                                    }

                                    currentType = property.PropertyType;
                                }
                            }
                        }

                        return 0;
                    }
                }
                """,
                new UTF8Encoding(false));

            var project = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <LangVersion>9.0</LangVersion>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                </Project>
                """;
            var projectPath = Path.Combine(temporaryDirectory, "GeneratedReflection.csproj");
            await File.WriteAllTextAsync(projectPath, project, new UTF8Encoding(false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = temporaryDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(projectPath);
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("--no-launch-profile");
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            using var process = Process.Start(startInfo)!;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            Assert.True(
                process.ExitCode == 0,
                $"Generated authoring reflection verification failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static string Describe(SchemaCompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}"));
}
