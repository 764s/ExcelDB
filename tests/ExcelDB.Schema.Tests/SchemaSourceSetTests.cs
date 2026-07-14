using ExcelDb.Schema.Compilation;

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

    [Theory]
    [InlineData("exceldb/options.proto")]
    [InlineData("google/protobuf/descriptor.proto")]
    public void Discovery_rejects_paths_reserved_for_package_local_system_imports(string logicalPath)
    {
        using var schema = TemporarySchemaDirectory.Create();
        schema.Write(logicalPath, "syntax = \"proto3\";");

        var error = Assert.Throws<InvalidDataException>(() => SchemaSourceSet.Discover(schema.Path));

        Assert.Contains("reserved", error.Message, StringComparison.OrdinalIgnoreCase);
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
}
