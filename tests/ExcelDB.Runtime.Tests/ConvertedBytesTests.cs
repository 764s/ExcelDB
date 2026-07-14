using ExcelDb.Runtime.Bytes;

namespace ExcelDb.Runtime.Tests;

public sealed class ConvertedBytesTests : IDisposable
{
    public ConvertedBytesTests()
    {
        if (RuntimeDatabase.IsOpen)
            RuntimeDatabase.Close();
    }

    public void Dispose()
    {
        if (RuntimeDatabase.IsOpen)
            RuntimeDatabase.Close();
    }

    [Fact]
    public void Writer_IsDeterministicAndReaderRoundTripsAllIndexes()
    {
        var one = RuntimeTestData.Asset(1, "one", "first", 1, path: "Data/one");
        var two = RuntimeTestData.Asset(
            2,
            "two",
            "second",
            2,
            path: "Data/two",
            dependencies: [RuntimeTestData.Identity(1)]);
        using var firstSnapshot = RuntimeTestData.Snapshot([two, one]);
        using var secondSnapshot = RuntimeTestData.Snapshot([one, two]);

        var first = ConvertedBytesWriter.Build(firstSnapshot, "test-tool");
        var second = ConvertedBytesWriter.Build(secondSnapshot, "test-tool");

        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(first.ManifestJson, second.ManifestJson);

        var read = ConvertedBytesReader.Read(first.Bytes, first.ManifestJson);
        using (read.Snapshot)
        {
            Assert.Equal(RuntimeTestData.SchemaHash, read.Snapshot.SchemaHash);
            Assert.Equal(RuntimeTestData.Client, read.Snapshot.ExportTarget);
            Assert.Equal([RuntimeTestData.Identity(1), RuntimeTestData.Identity(2)],
                read.Snapshot.Assets.Select(static item => item.Identity).ToArray());
            Assert.Equal(RuntimeTestData.Identity(1), read.Snapshot.Assets[1].Dependencies.Single());

            var rebuilt = ConvertedBytesWriter.Build(read.Snapshot, "test-tool");
            Assert.Equal(first.Bytes, rebuilt.Bytes);
            Assert.Equal(first.ManifestJson, rebuilt.ManifestJson);
        }
    }

    [Fact]
    public void Reader_RejectsTamperingAndManifestTargetMismatch()
    {
        using var snapshot = RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "one")]);
        var package = ConvertedBytesWriter.Build(snapshot, "test-tool");

        var tampered = package.Bytes.ToArray();
        tampered[40] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => ConvertedBytesReader.Read(tampered, package.ManifestJson));

        var wrongManifest = package.Manifest with { ExportTarget = RuntimeTestData.Server };
        Assert.Throws<InvalidDataException>(() =>
            ConvertedBytesReader.Read(package.Bytes, wrongManifest.ToJson()));
    }

    [Fact]
    public void FileDataSource_RefreshesSameIdentityAndParticipatesInRuntimeGates()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"exceldb-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var bytesPath = Path.Combine(directory, "config.bytes");
            var manifestPath = Path.Combine(directory, "config.manifest.json");
            using (var initial = RuntimeTestData.Snapshot([RuntimeTestData.Asset(1, "one", "initial", 1)]))
            {
                ConvertedBytesWriter.WriteFiles(bytesPath, manifestPath, initial, "test-tool");
            }

            var source = new ConvertedBytesDataSource(bytesPath, manifestPath);
            Assert.True(RuntimeDatabase.Open(
                source,
                new TestRegistry(RuntimeTestData.SchemaHash, RuntimeTestData.Client),
                new RuntimeBootstrapOptions(RuntimeMode.Release, false)));
            var instance = Assert.IsType<TestAsset>(RuntimeDatabase.LoadAsset<TestAsset>("one"));

            using (var updated = RuntimeTestData.Snapshot(
                       [RuntimeTestData.Asset(1, "one", "updated", 2)],
                       revision: "r2",
                       contentHash: "content-2"))
            {
                ConvertedBytesWriter.WriteFiles(bytesPath, manifestPath, updated, "test-tool");
            }

            Assert.True(RuntimeDatabase.Refresh());
            Assert.Same(instance, RuntimeDatabase.LoadAsset<TestAsset>("one"));
            Assert.Equal("updated", instance.Name);
            Assert.Equal(RuntimeSourceKind.ConvertedBytes, RuntimeDatabase.LastReport.Source!.Value.Kind);
        }
        finally
        {
            if (RuntimeDatabase.IsOpen)
                RuntimeDatabase.Close();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Writer_RejectsPendingIdentityBeforeProducingBytes()
    {
        using var snapshot = RuntimeTestData.Snapshot([new RuntimeAssetRecord(default, "pending")]);

        Assert.Throws<InvalidDataException>(() => ConvertedBytesWriter.Build(snapshot, "test-tool"));
    }
}
