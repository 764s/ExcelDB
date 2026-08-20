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

            message Effect {
              string kind = 1;
              int32 amount = 2;
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
              repeated Effect effects = 9 [(exceldb.field) = { child_table: { sheet_name: "ConfigEffects" } }];
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
        Assert.Equal(6, first.Artifacts.Length);
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
            artifact.RelativePath == "Authoring/ExcelDbSchema.AuthoringHost.g.cs");
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
        Assert.Contains(
            "instance.Effects = RuntimeGeneratedValueCodec.Parse<List<Effect>>(field.Data.Span);",
            client.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "instance.Effects.Kind =",
            client.Content,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "instance.Effects.Amount =",
            client.Content,
            StringComparison.Ordinal);

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

        var authoringHost = Assert.Single(first.Artifacts, static artifact =>
            artifact.Kind == "authoring-host-csharp");
        Assert.Contains("class GeneratedAuthoringHost", authoringHost.Content, StringComparison.Ordinal);
        Assert.Contains("class AuthoringRuntimeSchemaRegistry", authoringHost.Content, StringComparison.Ordinal);
        Assert.Contains("CreateSession(IWorkbookLocationOpener? opener = null)", authoringHost.Content, StringComparison.Ordinal);
        Assert.Contains("GeneratedAuthoringRuntimeSupport.GetDependencies", authoringHost.Content, StringComparison.Ordinal);
        Assert.Contains($"0x{compilation.Descriptor.SchemaHash:x16}UL", authoringHost.Content, StringComparison.Ordinal);

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
            var schemaModelProject = Path.Combine(repositoryRoot, "src", "ExcelDB.Schema.Model", "ExcelDB.Schema.Model.csproj");
            var authoringProject = Path.Combine(repositoryRoot, "src", "ExcelDB.Authoring", "ExcelDB.Authoring.csproj");
            var authoringWorkbooksProject = Path.Combine(repositoryRoot, "src", "ExcelDB.Authoring.Workbooks", "ExcelDB.Authoring.Workbooks.csproj");
            var workbooksProject = Path.Combine(repositoryRoot, "src", "ExcelDB.Workbooks", "ExcelDB.Workbooks.csproj");
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
                     <ProjectReference Include="{{SecurityElement.Escape(schemaModelProject)}}" />
                     <ProjectReference Include="{{SecurityElement.Escape(authoringProject)}}" />
                     <ProjectReference Include="{{SecurityElement.Escape(authoringWorkbooksProject)}}" />
                     <ProjectReference Include="{{SecurityElement.Escape(workbooksProject)}}" />
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

            var runnerDirectory = Path.Combine(temporaryDirectory, "Runner");
            Directory.CreateDirectory(runnerDirectory);
            var runnerProject = $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <LangVersion>9.0</LangVersion>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{SecurityElement.Escape(projectPath)}}" />
                  </ItemGroup>
                </Project>
                """;
            var runnerProjectPath = Path.Combine(runnerDirectory, "Runner.csproj");
            await File.WriteAllTextAsync(runnerProjectPath, runnerProject, new UTF8Encoding(false));
            await File.WriteAllTextAsync(
                Path.Combine(runnerDirectory, "Program.cs"),
                """
                using System;
                using System.Collections.Generic;
                using System.Collections.Immutable;
                using System.IO;
                using System.Linq;
                using ExcelDb.Generated.Authoring;
                using ExcelDb.Workbooks.Model;
                using ExcelDb.Workbooks.OpenXml;
                using ExcelDbEditor;

                internal static class Program
                {
                    private static int Main()
                    {
                        var schema = GeneratedAuthoringHost.Schema;
                        if (schema.SchemaHash == 0 || schema.Tables.Count(static table => table.Kind == ExcelDb.Schema.Descriptors.CanonicalTableKind.Asset) != 2)
                            throw new InvalidOperationException("Generated authoring descriptor did not round-trip.");

                        var configTable = schema.Tables.Single(static table => table.Name == "Config");
                        var itemTable = schema.Tables.Single(static table => table.Name == "Item");
                        var rowGuid = ExcelDb.Core.Identity.RowGuid.Parse("00000000000000000000000000000101");
                        var dependencyGuid = Guid.ParseExact("00000000000000000000000000000102", "N");
                        var dependencyToken = "78:sword";
                        var row = new WorkbookRow(
                            rowGuid,
                            1,
                            ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
                            {
                                new KeyValuePair<string, WorkbookCell>("id", new WorkbookCell("hero")),
                                new KeyValuePair<string, WorkbookCell>("authoring_note", new WorkbookCell("old")),
                                new KeyValuePair<string, WorkbookCell>("direct_item", new WorkbookCell(dependencyToken)),
                            }),
                            "4:hero");
                        var dependencyRow = new WorkbookRow(
                            ExcelDb.Core.Identity.RowGuid.Parse(dependencyGuid.ToString("N")),
                            1,
                            ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
                            {
                                new KeyValuePair<string, WorkbookCell>("id", new WorkbookCell("sword")),
                            }),
                            "5:sword");
                        var workbook = WorkbookDefinition.Empty(schema) with
                        {
                            Tables = WorkbookDefinition.Empty(schema).Tables
                                .Select(table => table.TableId == configTable.Id
                                    ? table with { Rows = ImmutableArray.Create(row) }
                                    : table.TableId == itemTable.Id
                                        ? table with { Rows = ImmutableArray.Create(dependencyRow) }
                                        : table)
                                .ToImmutableArray(),
                        };
                        var path = Path.Combine(Path.GetTempPath(), "exceldb-generated-host-" + Guid.NewGuid().ToString("N") + ".xlsx");
                        try
                        {
                            File.WriteAllBytes(path, XlsxWorkbookCodec.Write(workbook));
                            using var session = GeneratedAuthoringHost.CreateSession();
                            using var activation = session.Activate();
                            ImportReport? importReport = null;
                            AssetDatabase.workbookImported += report => importReport = report;
                            AssetDatabase.MountWorkbook(path);
                            var assetPath = path.Replace('\\', '/') + "/Config/hero";
                            var config = AssetDatabase.LoadAssetAtPath<Config>(assetPath)
                                ?? throw new InvalidOperationException(
                                    "Generated authoring session did not materialize Config. "
                                    + "Available=" + string.Join(",", AssetDatabase.FindAssets(string.Empty).Select(AssetDatabase.GUIDToAssetPath)) + ". "
                                    + "Import=" + (importReport is null ? "none" : string.Join(";", importReport.Diagnostics.Select(static item => item.Code + ":" + item.Message))) + ". "
                                    + string.Join(" | ", session.Diagnostics.Select(static item => item.Diagnostic.Code + ":" + item.Diagnostic.Message)));
                            if (config.AuthoringNote != "old")
                                throw new InvalidOperationException("Generated authoring runtime binding did not apply workbook data.");
                            if (config.DirectItem?.Table != 78 || config.DirectItem?.RowGuid != dependencyGuid)
                                throw new InvalidOperationException("Generated authoring runtime binding did not parse canonical RowRef data.");

                            var registration = GeneratedAuthoringHost.TableRegistrations.Single(static table => table.TableId == 77);
                            config.DirectItem = new RowRef(78, dependencyGuid);
                            config.Levels.Add(1);
                            var clone = (Config)registration.Clone(config);
                            clone.Levels.Add(2);
                            if (config.Levels.Count != 1)
                                throw new InvalidOperationException("Generated registration clone aliased a mutable list.");
                            if (clone.DirectItem?.Table != 78 || clone.DirectItem?.RowGuid != dependencyGuid)
                                throw new InvalidOperationException("Generated registration clone did not preserve RowRef values.");

                            var dependency = registration.GetDependencies!(config).Single();
                            if (dependency.ToString() != dependencyGuid.ToString("N"))
                                throw new InvalidOperationException("Generated RowRef dependency projection was incorrect.");

                            config.AuthoringNote = "saved";
                            EditorUtility.SetDirty(config);
                            AssetDatabase.SaveAssetIfDirty(config);
                            var saved = XlsxWorkbookCodec.Read(File.ReadAllBytes(path));
                            var savedRow = saved.Tables.Single(table => table.TableId == configTable.Id).Rows.Single();
                            if (savedRow.Cells["authoring_note"].Text != "saved")
                                throw new InvalidOperationException("Generated authoring session did not persist the edited value.");
                            if (savedRow.Cells["direct_item"].Text != dependencyToken)
                                throw new InvalidOperationException("Generated authoring session did not preserve canonical RowRef data.");
                        }
                        finally
                        {
                            if (File.Exists(path)) File.Delete(path);
                        }
                        return 0;
                    }
                }
                """,
                new UTF8Encoding(false));

            var runnerStartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = runnerDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            runnerStartInfo.ArgumentList.Add("run");
            runnerStartInfo.ArgumentList.Add("--project");
            runnerStartInfo.ArgumentList.Add(runnerProjectPath);
            runnerStartInfo.ArgumentList.Add("-c");
            runnerStartInfo.ArgumentList.Add("Release");
            runnerStartInfo.ArgumentList.Add("--nologo");
            runnerStartInfo.ArgumentList.Add("--disable-build-servers");
            runnerStartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            runnerStartInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            using var runner = Process.Start(runnerStartInfo)!;
            var runnerOutputTask = runner.StandardOutput.ReadToEndAsync();
            var runnerErrorTask = runner.StandardError.ReadToEndAsync();
            await runner.WaitForExitAsync();
            var runnerOutput = await runnerOutputTask;
            var runnerError = await runnerErrorTask;
            Assert.True(
                runner.ExitCode == 0,
                $"Generated authoring host execution failed.{Environment.NewLine}{runnerOutput}{Environment.NewLine}{runnerError}");
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
