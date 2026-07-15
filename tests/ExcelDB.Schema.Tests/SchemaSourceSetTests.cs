using System.Diagnostics.CodeAnalysis;
using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Generation;

namespace ExcelDB.Schema.Tests;

public sealed class SchemaSourceSetTests
{
    [Fact]
    public void Discovery_returns_schema_relative_forward_slash_paths_in_ordinal_order()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("z.proto", "syntax = \"proto3\";");
        schema.Write("nested/a.proto", "syntax = \"proto3\";");

        var sourceSet = SchemaSourceSet.Discover(schema.Path);

        Assert.Equal(["nested/a.proto", "z.proto"], sourceSet.Files.Select(static file => file.LogicalPath));
        Assert.All(sourceSet.Files, file => Assert.True(System.IO.Path.IsPathFullyQualified(file.FullPath)));
    }

    [Fact]
    public void Discovery_rejects_a_schema_root_that_is_a_reparse_point_when_supported()
    {
        using var container = TemporarySchemaDirectory.Create();
        using var outside = TemporarySchemaDirectory.Create();
        outside.Write("outside.proto", "syntax = \"proto3\"; message Outside {}");
        var schemaLink = System.IO.Path.Combine(container.Path, "Schema");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(schemaLink, outside.Path);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                               or PlatformNotSupportedException
                                               or IOException)
            {
                return;
            }

            var error = Assert.Throws<InvalidDataException>(() => SchemaSourceSet.Discover(schemaLink));

            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(schemaLink))
                Directory.Delete(schemaLink);
        }
    }

    [Theory]
    [InlineData("exceldb/options.proto")]
    [InlineData("google/protobuf/descriptor.proto")]
    public void Discovery_rejects_modified_paths_owned_by_the_system_catalog(string logicalPath)
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write(logicalPath, "syntax = \"proto3\";");

        var error = Assert.Throws<SystemProtoMirrorException>(() => SchemaSourceSet.Discover(schema.Path));

        Assert.Contains("reserved", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SystemProtoMirrorProblem.Modified, error.Problem);
        Assert.NotNull(error.ExpectedSha256);
        Assert.Equal(64, error.ActualSha256.Length);
    }

    [Fact]
    public void Discovery_excludes_canonical_mirrors_from_business_inputs()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("game.proto", "syntax = \"proto3\"; message Game {}");
        var catalog = PackageSystemProtoCatalog.Default;
        foreach (var file in catalog.Files)
            schema.WriteBytes(file.LogicalPath, file.CanonicalBytes.Span);

        var sourceSet = SchemaSourceSet.Discover(schema.Path, catalog);

        Assert.Equal(["game.proto"], sourceSet.Files.Select(static file => file.LogicalPath));
        Assert.Equal(
            catalog.Files.Select(static file => file.LogicalPath),
            sourceSet.ExcludedSystemProtoMirrors.Select(static file => file.LogicalPath));
        Assert.All(sourceSet.ExcludedSystemProtoMirrors, mirror =>
            Assert.Equal(
                catalog.Files.Single(file => file.LogicalPath == mirror.LogicalPath).Sha256,
                mirror.Sha256));
    }

    [Theory]
    [InlineData("exceldb/not-owned.proto")]
    [InlineData("google/protobuf/not-owned.proto")]
    [InlineData("ExcelDb/options.proto")]
    public void Discovery_rejects_unknown_files_in_reserved_namespaces(string logicalPath)
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write(logicalPath, "syntax = \"proto3\";");

        var error = Assert.Throws<SystemProtoMirrorException>(() =>
            SchemaSourceSet.Discover(schema.Path));

        Assert.Equal(SystemProtoMirrorProblem.UnknownReservedPath, error.Problem);
        Assert.Equal(logicalPath, error.LogicalPath);
        Assert.Null(error.ExpectedSha256);
        Assert.Contains("not present", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_and_canonical_mirrors_produce_identical_descriptor_bytes()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("game.proto", """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";
            message Game {
              option (exceldb.table) = { kind: ASSET, id: 1 };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }
            """);
        var catalog = PackageSystemProtoCatalog.Default;
        var compiler = new EmbeddedProtocCompiler(catalog);

        var withoutMirrors = SchemaSourceSet.Discover(schema.Path, catalog);
        var first = await compiler.CompileAsync(withoutMirrors);

        foreach (var file in catalog.Files)
            schema.WriteBytes(file.LogicalPath, file.CanonicalBytes.Span);

        var withMirrors = SchemaSourceSet.Discover(schema.Path, catalog);
        var second = await compiler.CompileAsync(withMirrors);
        var firstSchema = new SchemaCompiler().CompileDescriptorSet(first.DescriptorSet);
        var secondSchema = new SchemaCompiler().CompileDescriptorSet(second.DescriptorSet);
        Assert.True(firstSchema.Succeeded);
        Assert.True(secondSchema.Succeeded);
        var firstCode = new SchemaCodeGenerator().Generate(firstSchema.Descriptor!, catalog.CatalogHash);
        var secondCode = new SchemaCodeGenerator().Generate(secondSchema.Descriptor!, catalog.CatalogHash);

        Assert.Equal(
            withoutMirrors.Files.Select(static file => file.LogicalPath),
            withMirrors.Files.Select(static file => file.LogicalPath));
        Assert.Equal(first.DescriptorBytes.ToArray(), second.DescriptorBytes.ToArray());
        Assert.Equal(firstSchema.Descriptor!.SchemaHash, secondSchema.Descriptor!.SchemaHash);
        Assert.Equal(
            firstCode.Artifacts.Select(static item => (item.RelativePath, item.Content, item.ContentHash)),
            secondCode.Artifacts.Select(static item => (item.RelativePath, item.Content, item.ContentHash)));
        Assert.Equal(firstCode.Manifest.Json, secondCode.Manifest.Json);
    }

    [Fact]
    public async Task Complete_catalog_resolves_transitive_google_imports_without_disk_mirrors()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("api-user.proto", """
            syntax = "proto3";
            package game;
            import "google/protobuf/api.proto";
            message ApiUser { google.protobuf.Api api = 1; }
            """);

        var result = await new EmbeddedProtocCompiler().CompileAsync(
            SchemaSourceSet.Discover(schema.Path));

        Assert.Contains(result.DescriptorSet.File, static file =>
            file.Name == "google/protobuf/api.proto");
        Assert.Contains(result.DescriptorSet.File, static file =>
            file.Name == "google/protobuf/type.proto");
        Assert.Contains(result.DescriptorSet.File, static file =>
            file.Name == "google/protobuf/source_context.proto");
    }

    [Theory]
    [InlineData("missing/business.proto")]
    [InlineData("exceldb/not-owned.proto")]
    public async Task Missing_import_reports_the_import_and_its_business_importer(string importPath)
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("root.proto", $$"""
            syntax = "proto3";
            package game;
            import "{{importPath}}";
            message Root {}
            """);

        var error = await Assert.ThrowsAsync<ProtocCompilationException>(() =>
            new EmbeddedProtocCompiler().CompileAsync(SchemaSourceSet.Discover(schema.Path)));

        Assert.Equal(1, error.ExitCode);
        Assert.Contains(importPath, error.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("root.proto", error.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("not found", error.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transitive_missing_import_reports_each_business_link()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("root.proto", """
            syntax = "proto3";
            import "middle.proto";
            message Root { Middle middle = 1; }
            """);
        schema.Write("middle.proto", """
            syntax = "proto3";
            import "missing.proto";
            message Middle {}
            """);

        var error = await Assert.ThrowsAsync<ProtocCompilationException>(() =>
            new EmbeddedProtocCompiler().CompileAsync(SchemaSourceSet.Discover(schema.Path)));

        Assert.Contains("missing.proto", error.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("middle.proto", error.Diagnostic, StringComparison.Ordinal);
        Assert.Contains("root.proto", error.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_catalog_dependency_reports_business_and_catalog_links()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("root.proto", """
            syntax = "proto3";
            import "exceldb/options.proto";
            message Root {}
            """);
        var catalog = new IncompleteCatalog();

        var error = await Assert.ThrowsAsync<ProtocCompilationException>(() =>
            new EmbeddedProtocCompiler(catalog).CompileAsync(
                SchemaSourceSet.Discover(schema.Path, catalog)));

        Assert.Contains(
            "Import chain: root.proto -> exceldb/options.proto -> google/protobuf/descriptor.proto -> google/protobuf/missing.proto",
            error.Diagnostic,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Protoc_compiles_the_captured_snapshot_even_if_the_source_changes_after_discovery()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("game.proto", """
            syntax = "proto3";
            package game;
            message Captured {}
            """);
        var sourceSet = SchemaSourceSet.Discover(schema.Path);
        schema.Write("game.proto", "this is no longer valid proto");

        var result = await new EmbeddedProtocCompiler().CompileAsync(sourceSet);

        Assert.Contains(result.DescriptorSet.File, file =>
            file.Package == "game" && file.MessageType.Any(message => message.Name == "Captured"));
    }

    [Fact]
    public async Task Missing_package_payload_never_falls_back_to_path()
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write("game.proto", "syntax = \"proto3\"; message Config {}");
        var sourceSet = SchemaSourceSet.Discover(schema.Path);
        using var emptyPackage = TemporarySchemaDirectory.Create();
        var previousPath = Environment.GetEnvironmentVariable("PATH");

        try
        {
            Environment.SetEnvironmentVariable("PATH", AppContext.BaseDirectory);
            var compiler = new EmbeddedProtocCompiler(emptyPackage.Path);
            var error = await Assert.ThrowsAsync<ProtocCompilationException>(() => compiler.CompileAsync(sourceSet));
            Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    private sealed class IncompleteCatalog : ISystemProtoCatalog
    {
        private readonly SystemProtoFile _options = new(
            "exceldb/options.proto",
            System.Text.Encoding.UTF8.GetBytes("""
                syntax = "proto3";
                import "google/protobuf/descriptor.proto";
                """));
        private readonly SystemProtoFile _descriptor = new(
            "google/protobuf/descriptor.proto",
            System.Text.Encoding.UTF8.GetBytes("""
                syntax = "proto3";
                import "google/protobuf/missing.proto";
                """));

        public IReadOnlyList<SystemProtoFile> Files => [_options, _descriptor];

        public string CatalogHash => new('a', 64);

        public bool TryGetFile(string logicalPath, [NotNullWhen(true)] out SystemProtoFile? file)
        {
            file = string.Equals(logicalPath, _options.LogicalPath, StringComparison.Ordinal)
                ? _options
                : string.Equals(logicalPath, _descriptor.LogicalPath, StringComparison.Ordinal)
                    ? _descriptor
                    : null;
            return file is not null;
        }
    }
}
