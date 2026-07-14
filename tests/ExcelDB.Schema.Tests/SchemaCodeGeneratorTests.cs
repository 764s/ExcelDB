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
            message Config {
              option (exceldb.table) = { kind: ASSET, id: 77 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              int32 client_value = 2 [(exceldb.field) = { export_targets: { ids: "client" } }];
              int32 server_value = 3 [(exceldb.field) = { export_targets: { ids: "server" } }];
              string authoring_note = 4 [(exceldb.field) = { export_targets: {} }];
            }
            """);
        var compilation = await new SchemaCompiler().CompileAsync(schema.Path);
        Assert.True(compilation.Succeeded, Describe(compilation));

        var generator = new SchemaCodeGenerator();
        var first = generator.Generate(compilation.Descriptor!);
        var second = generator.Generate(compilation.Descriptor!);

        Assert.Equal(first.Manifest, second.Manifest);
        Assert.Equal(first.Artifacts.ToArray(), second.Artifacts.ToArray());
        Assert.Equal(5, first.Artifacts.Length);
        Assert.Contains("exceldb.codegen-manifest.v1", first.Manifest.Json, StringComparison.Ordinal);
        Assert.Equal(64, first.Manifest.CodegenHash.Length);
        Assert.Equal(64, first.Manifest.ManifestHash.Length);

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

        await AssertRuntimeArtifactsCompile(first.Artifacts);
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

    private static string Describe(SchemaCompilationResult result) =>
        string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}"));
}
