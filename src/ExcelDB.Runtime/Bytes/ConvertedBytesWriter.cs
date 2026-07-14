using System.Security.Cryptography;
using System.Text;
using ExcelDb.Core.IO;
using ExcelDb.Core.Identity;

namespace ExcelDb.Runtime.Bytes;

public static class ConvertedBytesWriter
{
    public static ConvertedBytesPackage Build(SourceSnapshot snapshot, string toolVersion)
    {
        RuntimeCompatibility.NotNull(snapshot, nameof(snapshot));
        RuntimeCompatibility.NotNullOrWhiteSpace(toolVersion, nameof(toolVersion));
        ValidateSnapshot(snapshot);

        var assets = snapshot.Assets
            .OrderBy(static item => item.Identity, AssetIdentityComparer.Instance)
            .ToArray();
        var assetIndex = assets
            .Select(static (item, index) => (item.Identity, index))
            .ToDictionary(static item => item.Identity, static item => item.index);

        var strings = new SortedSet<string>(StringComparer.Ordinal) { snapshot.ExportTarget.Value };
        foreach (var asset in assets)
        {
            strings.Add(asset.Key);
            strings.Add(asset.Path);
        }

        var stringArray = strings.ToArray();
        var stringIndex = stringArray
            .Select(static (value, index) => (value, index))
            .ToDictionary(static item => item.value, static item => item.index, StringComparer.Ordinal);

        var tableSection = BuildTableSection(assets);
        var (stringIndexSection, stringDataSection) = BuildStringSections(stringArray);
        var (assetSection, fieldSection, fieldDataSection, referenceSection) =
            BuildAssetSections(assets, stringIndex, assetIndex);
        var keyIndexSection = BuildKeyIndexSection(assets, stringIndex);
        var guidIndexSection = BuildGuidIndexSection(assets);

        var sections = new Section[10];
        var offset = ConvertedBytesLayout.HeaderSize;
        sections[0] = Take(ref offset, tableSection.Length, assets.Select(static item => item.TableId).Distinct().Count());
        sections[1] = Take(ref offset, stringIndexSection.Length, stringArray.Length);
        sections[2] = Take(ref offset, stringDataSection.Length, stringDataSection.Length);
        sections[3] = Take(ref offset, assetSection.Length, assets.Length);
        sections[4] = Take(ref offset, fieldSection.Length, assets.Sum(static item => item.Fields.Length));
        sections[5] = Take(ref offset, fieldDataSection.Length, fieldDataSection.Length);
        sections[6] = Take(ref offset, keyIndexSection.Length, assets.Length);
        sections[7] = Take(ref offset, guidIndexSection.Length, assets.Length);
        sections[8] = Take(ref offset, referenceSection.Length, assets.Sum(static item => item.Dependencies.Length));
        sections[9] = new Section(offset, ConvertedBytesLayout.IntegrityLength);

        using var output = new MemoryStream(checked(offset + ConvertedBytesLayout.IntegrityLength));
        WriteHeader(output, snapshot, stringIndex[snapshot.ExportTarget.Value], sections);
        output.Write(tableSection);
        output.Write(stringIndexSection);
        output.Write(stringDataSection);
        output.Write(assetSection);
        output.Write(fieldSection);
        output.Write(fieldDataSection);
        output.Write(keyIndexSection);
        output.Write(guidIndexSection);
        output.Write(referenceSection);

        var withoutIntegrity = output.ToArray();
        output.Write(RuntimeCompatibility.Sha256(withoutIntegrity));
        var bytes = output.ToArray();
        var bytesHash = RuntimeCompatibility.HexLower(RuntimeCompatibility.Sha256(bytes));
        var manifest = new ConvertedBytesManifest(
            ConvertedBytesLayout.FormatVersion,
            snapshot.SchemaHash,
            snapshot.ExportTarget,
            snapshot.ContentHash,
            sections[0].CountOrLength,
            assets.Length,
            toolVersion,
            bytesHash);

        return new ConvertedBytesPackage(bytes, manifest, manifest.ToJson());
    }

    public static ConvertedBytesManifest WriteFiles(
        string bytesPath,
        string manifestPath,
        SourceSnapshot snapshot,
        string toolVersion)
    {
        RuntimeCompatibility.NotNullOrWhiteSpace(bytesPath, nameof(bytesPath));
        RuntimeCompatibility.NotNullOrWhiteSpace(manifestPath, nameof(manifestPath));
        var package = Build(snapshot, toolVersion);
        AtomicFile.WriteAllBytes(bytesPath, package.Bytes);
        AtomicFile.WriteAllBytes(manifestPath, Encoding.UTF8.GetBytes(package.ManifestJson));
        return package.Manifest;
    }

    private static void ValidateSnapshot(SourceSnapshot snapshot)
    {
        if (snapshot.SchemaHash == 0 || snapshot.ExportTarget.IsEmpty)
            throw new InvalidDataException("A converted snapshot requires a non-empty schema identity.");
        if (snapshot.Diagnostics.Any(static item => item.IsFailure))
            throw new InvalidDataException("A snapshot with error/blocker diagnostics cannot be converted.");

        var identities = new HashSet<AssetIdentity>();
        var keys = new HashSet<(int TableId, string Key)>();
        foreach (var asset in snapshot.Assets)
        {
            if (asset.Identity.TableId <= 0 || asset.Identity.RowGuid.IsEmpty)
                throw new InvalidDataException("Pending/empty identities cannot be converted.");
            if (!identities.Add(asset.Identity))
                throw new InvalidDataException($"Duplicate identity {asset.Identity}.");
            if (string.IsNullOrEmpty(asset.Key) || !keys.Add((asset.TableId, asset.Key)))
                throw new InvalidDataException($"Missing or duplicate key '{asset.Key}' in table {asset.TableId}.");

            var fieldPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in asset.Fields)
            {
                var path = string.Join('.', field.FieldIdPath);
                if (!fieldPaths.Add(path))
                    throw new InvalidDataException($"Duplicate field path {path} in {asset.Identity}.");
            }
        }

        foreach (var asset in snapshot.Assets)
        {
            foreach (var dependency in asset.Dependencies)
            {
                if (!identities.Contains(dependency))
                    throw new InvalidDataException($"Dangling reference {asset.Identity} -> {dependency}.");
            }
        }
    }

    private static byte[] BuildTableSection(IReadOnlyList<RuntimeAssetRecord> assets)
    {
        using var stream = new MemoryStream();
        var index = 0;
        while (index < assets.Count)
        {
            var tableId = assets[index].TableId;
            var first = index;
            while (index < assets.Count && assets[index].TableId == tableId)
                index++;

            ConvertedBytesLayout.WriteInt32(stream, tableId);
            ConvertedBytesLayout.WriteInt32(stream, first);
            ConvertedBytesLayout.WriteInt32(stream, index - first);
        }
        return stream.ToArray();
    }

    private static (byte[] Index, byte[] Data) BuildStringSections(IReadOnlyList<string> strings)
    {
        using var indexStream = new MemoryStream();
        using var dataStream = new MemoryStream();
        var utf8 = new UTF8Encoding(false, true);
        foreach (var value in strings)
        {
            var bytes = utf8.GetBytes(value);
            ConvertedBytesLayout.WriteInt32(indexStream, checked((int)dataStream.Length));
            ConvertedBytesLayout.WriteInt32(indexStream, bytes.Length);
            dataStream.Write(bytes);
        }
        return (indexStream.ToArray(), dataStream.ToArray());
    }

    private static (byte[] Assets, byte[] Fields, byte[] FieldData, byte[] References) BuildAssetSections(
        IReadOnlyList<RuntimeAssetRecord> assets,
        IReadOnlyDictionary<string, int> stringIndex,
        IReadOnlyDictionary<AssetIdentity, int> assetIndex)
    {
        using var assetStream = new MemoryStream();
        using var fieldStream = new MemoryStream();
        using var fieldDataStream = new MemoryStream();
        using var referenceStream = new MemoryStream();
        var firstField = 0;
        var firstReference = 0;

        for (var assetPosition = 0; assetPosition < assets.Count; assetPosition++)
        {
            var asset = assets[assetPosition];
            ConvertedBytesLayout.WriteInt32(assetStream, asset.TableId);
            WriteGuid(assetStream, asset.Identity.RowGuid.Value);
            ConvertedBytesLayout.WriteInt32(assetStream, stringIndex[asset.Key]);
            ConvertedBytesLayout.WriteInt32(assetStream, stringIndex[asset.Path]);
            ConvertedBytesLayout.WriteInt32(assetStream, firstField);
            ConvertedBytesLayout.WriteInt32(assetStream, asset.Fields.Length);
            ConvertedBytesLayout.WriteInt32(assetStream, firstReference);
            ConvertedBytesLayout.WriteInt32(assetStream, asset.Dependencies.Length);

            foreach (var field in asset.Fields)
            {
                ConvertedBytesLayout.WriteInt32(fieldStream, field.FieldNumber);
                ConvertedBytesLayout.WriteInt32(fieldStream, field.FieldIdPath.Length);
                ConvertedBytesLayout.WriteInt32(fieldStream, checked((int)fieldDataStream.Length));
                ConvertedBytesLayout.WriteInt32(fieldStream, field.Data.Length);
                foreach (var fieldNumber in field.FieldIdPath)
                    ConvertedBytesLayout.WriteInt32(fieldDataStream, fieldNumber);
                fieldDataStream.Write(field.Data.Span);
                firstField++;
            }

            foreach (var dependency in asset.Dependencies)
            {
                ConvertedBytesLayout.WriteInt32(referenceStream, assetPosition);
                ConvertedBytesLayout.WriteInt32(referenceStream, assetIndex[dependency]);
                firstReference++;
            }
        }

        return (assetStream.ToArray(), fieldStream.ToArray(), fieldDataStream.ToArray(), referenceStream.ToArray());
    }

    private static byte[] BuildKeyIndexSection(
        IReadOnlyList<RuntimeAssetRecord> assets,
        IReadOnlyDictionary<string, int> stringIndex)
    {
        using var stream = new MemoryStream();
        foreach (var item in assets
                     .Select(static (asset, index) => (asset, index))
                     .OrderBy(static item => item.asset.TableId)
                     .ThenBy(static item => item.asset.Key, StringComparer.Ordinal))
        {
            ConvertedBytesLayout.WriteInt32(stream, item.asset.TableId);
            ConvertedBytesLayout.WriteInt32(stream, stringIndex[item.asset.Key]);
            ConvertedBytesLayout.WriteInt32(stream, item.index);
        }
        return stream.ToArray();
    }

    private static byte[] BuildGuidIndexSection(IReadOnlyList<RuntimeAssetRecord> assets)
    {
        using var stream = new MemoryStream();
        for (var index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];
            ConvertedBytesLayout.WriteInt32(stream, asset.TableId);
            WriteGuid(stream, asset.Identity.RowGuid.Value);
            ConvertedBytesLayout.WriteInt32(stream, index);
        }
        return stream.ToArray();
    }

    private static void WriteHeader(
        Stream output,
        SourceSnapshot snapshot,
        int targetStringIndex,
        IReadOnlyList<Section> sections)
    {
        output.Write(ConvertedBytesLayout.Magic);
        ConvertedBytesLayout.WriteInt32(output, ConvertedBytesLayout.FormatVersion);
        ConvertedBytesLayout.WriteInt32(output, ConvertedBytesLayout.HeaderSize);
        ConvertedBytesLayout.WriteUInt64(output, snapshot.SchemaHash);
        ConvertedBytesLayout.WriteInt32(output, targetStringIndex);
        foreach (var section in sections)
        {
            ConvertedBytesLayout.WriteInt32(output, section.Offset);
            ConvertedBytesLayout.WriteInt32(output, section.CountOrLength);
        }
        if (output.Position != ConvertedBytesLayout.HeaderSize)
            throw new InvalidOperationException("Converted-bytes header size drifted from its versioned layout.");
    }

    private static Section Take(ref int offset, int byteLength, int countOrLength)
    {
        var result = new Section(offset, countOrLength);
        offset = checked(offset + byteLength);
        return result;
    }

    private static void WriteGuid(Stream stream, Guid guid)
    {
        Span<byte> bytes = stackalloc byte[16];
        RuntimeCompatibility.WriteBigEndianGuid(guid, bytes);
        stream.Write(bytes);
    }
}
