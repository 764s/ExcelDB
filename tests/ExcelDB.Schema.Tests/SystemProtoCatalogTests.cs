using System.Security.Cryptography;
using System.Text;
using ExcelDb.Schema.Compilation;

namespace ExcelDB.Schema.Tests;

public sealed class SystemProtoCatalogTests
{
    private static readonly string[] ExpectedGoogleFiles =
    [
        "google/protobuf/any.proto",
        "google/protobuf/api.proto",
        "google/protobuf/descriptor.proto",
        "google/protobuf/duration.proto",
        "google/protobuf/empty.proto",
        "google/protobuf/field_mask.proto",
        "google/protobuf/source_context.proto",
        "google/protobuf/struct.proto",
        "google/protobuf/timestamp.proto",
        "google/protobuf/type.proto",
        "google/protobuf/wrappers.proto",
    ];

    [Fact]
    public void Package_catalog_contains_options_and_the_complete_shipped_google_set()
    {
        var catalog = PackageSystemProtoCatalog.Default;

        Assert.Equal(
            ["exceldb/options.proto", .. ExpectedGoogleFiles],
            catalog.Files.Select(static file => file.LogicalPath));
        Assert.Equal(64, catalog.CatalogHash.Length);
        Assert.All(catalog.Files, file =>
        {
            Assert.Equal(64, file.Sha256.Length);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(file.CanonicalBytes.Span)).ToLowerInvariant(),
                file.Sha256);
            Assert.True(catalog.TryGetFile(file.LogicalPath, out var found));
            Assert.Same(file, found);
        });
    }

    [Fact]
    public void Catalog_hash_is_independent_of_physical_enumeration_order()
    {
        var source = PackageSystemProtoCatalog.Default;
        using var shuffledPackage = TemporarySchemaDirectory.Create();
        foreach (var file in source.Files.Reverse())
        {
            shuffledPackage.WriteBytes(
                $"schema-system/{file.LogicalPath}",
                file.CanonicalBytes.Span);
        }

        var reloaded = new PackageSystemProtoCatalog(shuffledPackage.Path);

        Assert.Equal(source.CatalogHash, reloaded.CatalogHash);
        Assert.Equal(
            source.Files.Select(static file => file.LogicalPath),
            reloaded.Files.Select(static file => file.LogicalPath));
    }

    [Fact]
    public void Catalog_hash_uses_the_canonical_path_zero_hash_newline_projection()
    {
        var catalog = PackageSystemProtoCatalog.Default;
        var projection = string.Concat(catalog.Files.Select(static file =>
            $"{file.LogicalPath}\0{file.Sha256}\n"));
        var expected = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(projection)))
            .ToLowerInvariant();

        Assert.Equal(expected, catalog.CatalogHash);
    }

    [Fact]
    public void Catalog_hash_changes_when_any_canonical_file_changes()
    {
        var source = PackageSystemProtoCatalog.Default;
        using var changedPackage = TemporarySchemaDirectory.Create();
        foreach (var file in source.Files)
        {
            var bytes = file.CanonicalBytes.ToArray();
            if (file.LogicalPath == "google/protobuf/empty.proto")
                bytes[^1] ^= 0x01;
            changedPackage.WriteBytes($"schema-system/{file.LogicalPath}", bytes);
        }

        var changed = new PackageSystemProtoCatalog(changedPackage.Path);

        Assert.NotEqual(source.CatalogHash, changed.CatalogHash);
        Assert.NotEqual(
            source.Files.Single(static file => file.LogicalPath == "google/protobuf/empty.proto").Sha256,
            changed.Files.Single(static file => file.LogicalPath == "google/protobuf/empty.proto").Sha256);
    }

    [Fact]
    public void Canonical_bytes_are_defensively_exposed()
    {
        var file = PackageSystemProtoCatalog.Default.Files[0];
        var originalHash = file.Sha256;
        var callerCopy = file.CanonicalBytes.ToArray();
        callerCopy[0] ^= 0xff;

        Assert.Equal(
            originalHash,
            Convert.ToHexString(SHA256.HashData(file.CanonicalBytes.Span)).ToLowerInvariant());
    }

    [Theory]
    [InlineData("exceldb/options.proto", true)]
    [InlineData("google/protobuf/descriptor.proto", true)]
    [InlineData("Google/Protobuf/descriptor.proto", true)]
    [InlineData("game/config.proto", false)]
    public void Reserved_namespace_detection_is_explicit(string path, bool expected)
    {
        Assert.Equal(expected, SystemProtoCatalogPaths.IsReservedNamespace(path));
    }
}
