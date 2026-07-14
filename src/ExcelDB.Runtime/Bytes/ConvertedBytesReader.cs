using System.Security.Cryptography;
using System.Text;
using ExcelDb.Core.Identity;

namespace ExcelDb.Runtime.Bytes;

public static class ConvertedBytesReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ConvertedBytesReadResult Read(ReadOnlySpan<byte> content, string? manifestJson = null)
    {
        var header = ReadHeader(content);
        VerifyIntegrity(content, header.Sections[9]);
        var strings = ReadStrings(content, header.Sections[1], header.Sections[2]);
        if ((uint)header.TargetStringIndex >= (uint)strings.Length
            || !ExportTargetId.TryParse(strings[header.TargetStringIndex], out var exportTarget))
        {
            throw new InvalidDataException("Converted-bytes target string index is invalid.");
        }

        var rawAssets = ReadRawAssets(content, header.Sections[3], strings.Length);
        var fields = ReadFields(content, header.Sections[4], header.Sections[5], header.FormatVersion);
        var references = ReadReferences(content, header.Sections[8], rawAssets.Length);
        var identities = rawAssets
            .Select(static item => new AssetIdentity(item.TableId, new RowGuid(item.RowGuid)))
            .ToArray();

        ValidateCanonicalAssetOrder(identities);
        var assets = new RuntimeAssetRecord[rawAssets.Length];
        var expectedFirstField = 0;
        var expectedFirstReference = 0;
        for (var assetIndex = 0; assetIndex < rawAssets.Length; assetIndex++)
        {
            var raw = rawAssets[assetIndex];
            EnsureSlice(raw.FirstField, raw.FieldCount, fields.Length, "field");
            EnsureSlice(raw.FirstReference, raw.ReferenceCount, references.Length, "reference");
            if (raw.FirstField != expectedFirstField || raw.FirstReference != expectedFirstReference)
                throw new InvalidDataException("Asset field/reference ranges must be contiguous and canonical.");
            expectedFirstField = checked(expectedFirstField + raw.FieldCount);
            expectedFirstReference = checked(expectedFirstReference + raw.ReferenceCount);

            var assetFields = new RuntimeFieldValue[raw.FieldCount];
            RuntimeFieldValue? previousField = null;
            for (var offset = 0; offset < raw.FieldCount; offset++)
            {
                var field = fields[raw.FirstField + offset];
                var currentField = new RuntimeFieldValue(field.FieldIdPath, field.Data);
                if (previousField is not null && RuntimeFieldValue.ComparePath(previousField, currentField) >= 0)
                    throw new InvalidDataException("Runtime fields must be strictly ordered by field id path.");
                previousField = currentField;
                assetFields[offset] = currentField;
            }

            var dependencies = new AssetIdentity[raw.ReferenceCount];
            AssetIdentity? previousDependency = null;
            for (var offset = 0; offset < raw.ReferenceCount; offset++)
            {
                var reference = references[raw.FirstReference + offset];
                if (reference.SourceAssetIndex != assetIndex)
                    throw new InvalidDataException("Reference section source index does not match its asset range.");
                var dependency = identities[reference.TargetAssetIndex];
                if (previousDependency is not null
                    && AssetIdentityComparer.Instance.Compare(previousDependency.Value, dependency) >= 0)
                {
                    throw new InvalidDataException("References must be strictly ordered and unique.");
                }
                dependencies[offset] = dependency;
                previousDependency = dependency;
            }

            assets[assetIndex] = new RuntimeAssetRecord(
                identities[assetIndex],
                strings[raw.KeyStringIndex],
                assetFields,
                dependencies,
                strings[raw.PathStringIndex]);
            if (assets[assetIndex].Key.Length == 0)
                throw new InvalidDataException("A converted runtime asset key must not be empty.");
        }

        if (expectedFirstField != fields.Length || expectedFirstReference != references.Length)
            throw new InvalidDataException("Asset directory does not cover every field/reference entry exactly once.");

        ValidateTableDirectory(content, header.Sections[0], assets);
        ValidateKeyIndex(content, header.Sections[6], assets, strings);
        ValidateGuidIndex(content, header.Sections[7], assets);

        var wholeHash = RuntimeCompatibility.HexLower(RuntimeCompatibility.Sha256(content));
        ConvertedBytesManifest? manifest = null;
        if (manifestJson is not null)
        {
            manifest = ConvertedBytesManifest.Parse(manifestJson);
            ValidateManifest(manifest, header, exportTarget, wholeHash);
        }

        var contentHash = manifest?.SourceContentHash ?? wholeHash;
        var snapshot = new SourceSnapshot(
            header.SchemaHash,
            exportTarget,
            wholeHash,
            contentHash,
            assets,
            formatVersion: header.FormatVersion);
        return new ConvertedBytesReadResult(snapshot, manifest);
    }

    internal static ConvertedBytesHeader ReadHeader(ReadOnlySpan<byte> content)
    {
        if (content.Length < ConvertedBytesLayout.HeaderSize + ConvertedBytesLayout.IntegrityLength)
            throw new InvalidDataException("Converted-bytes content is shorter than the v1 header and integrity hash.");
        if (!content[..ConvertedBytesLayout.Magic.Length].SequenceEqual(ConvertedBytesLayout.Magic))
            throw new InvalidDataException("Converted-bytes magic is invalid.");

        var formatVersion = ConvertedBytesLayout.ReadInt32(content, 8);
        if (formatVersion is not (ConvertedBytesLayout.LegacyFormatVersion or ConvertedBytesLayout.FormatVersion))
            throw new InvalidDataException($"Unsupported converted-bytes format version {formatVersion}.");
        if (ConvertedBytesLayout.ReadInt32(content, 12) != ConvertedBytesLayout.HeaderSize)
            throw new InvalidDataException("Converted-bytes header size does not match format v1.");

        var schemaHash = ConvertedBytesLayout.ReadUInt64(content, 16);
        if (schemaHash == 0)
            throw new InvalidDataException("Converted-bytes schema hash must be non-zero.");
        var targetStringIndex = ConvertedBytesLayout.ReadInt32(content, 24);

        var sections = new Section[10];
        for (var index = 0; index < sections.Length; index++)
        {
            sections[index] = new Section(
                ConvertedBytesLayout.ReadInt32(content, 28 + (index * 8)),
                ConvertedBytesLayout.ReadInt32(content, 32 + (index * 8)));
        }

        var expectedOffset = ConvertedBytesLayout.HeaderSize;
        ValidateSection(sections[0], ConvertedBytesLayout.TableEntrySize, ref expectedOffset);
        ValidateSection(sections[1], ConvertedBytesLayout.StringIndexEntrySize, ref expectedOffset);
        ValidateByteSection(sections[2], ref expectedOffset);
        ValidateSection(sections[3], ConvertedBytesLayout.AssetEntrySize, ref expectedOffset);
        var fieldEntrySize = formatVersion == ConvertedBytesLayout.LegacyFormatVersion
            ? ConvertedBytesLayout.LegacyFieldEntrySize
            : ConvertedBytesLayout.FieldEntrySize;
        ValidateSection(sections[4], fieldEntrySize, ref expectedOffset);
        ValidateByteSection(sections[5], ref expectedOffset);
        ValidateSection(sections[6], ConvertedBytesLayout.KeyIndexEntrySize, ref expectedOffset);
        ValidateSection(sections[7], ConvertedBytesLayout.GuidIndexEntrySize, ref expectedOffset);
        ValidateSection(sections[8], ConvertedBytesLayout.ReferenceEntrySize, ref expectedOffset);
        if (sections[9].Offset != expectedOffset
            || sections[9].CountOrLength != ConvertedBytesLayout.IntegrityLength
            || expectedOffset + ConvertedBytesLayout.IntegrityLength != content.Length)
        {
            throw new InvalidDataException("Converted-bytes integrity section is not the final v1 section.");
        }

        return new ConvertedBytesHeader(formatVersion, schemaHash, targetStringIndex, sections);
    }

    private static string[] ReadStrings(
        ReadOnlySpan<byte> content,
        Section indexSection,
        Section dataSection)
    {
        var strings = new string[indexSection.CountOrLength];
        var expectedDataOffset = 0;
        string? previous = null;
        for (var index = 0; index < strings.Length; index++)
        {
            var entryOffset = indexSection.Offset + (index * ConvertedBytesLayout.StringIndexEntrySize);
            var dataOffset = ConvertedBytesLayout.ReadInt32(content, entryOffset);
            var length = ConvertedBytesLayout.ReadInt32(content, entryOffset + 4);
            if (dataOffset != expectedDataOffset)
                throw new InvalidDataException("String data must be contiguous and canonical.");
            ConvertedBytesLayout.EnsureRange(content, dataSection.Offset + dataOffset, length);
            if (dataOffset + length > dataSection.CountOrLength)
                throw new InvalidDataException("String data lies outside the declared string section.");

            var value = StrictUtf8.GetString(content.Slice(dataSection.Offset + dataOffset, length));
            if (previous is not null && StringComparer.Ordinal.Compare(previous, value) >= 0)
                throw new InvalidDataException("String table must be strictly ordinal-sorted and deduplicated.");
            strings[index] = value;
            previous = value;
            expectedDataOffset = checked(dataOffset + length);
        }

        if (expectedDataOffset != dataSection.CountOrLength)
            throw new InvalidDataException("String data contains unreachable trailing bytes.");
        return strings;
    }

    private static RawAsset[] ReadRawAssets(ReadOnlySpan<byte> content, Section section, int stringCount)
    {
        var assets = new RawAsset[section.CountOrLength];
        for (var index = 0; index < assets.Length; index++)
        {
            var offset = section.Offset + (index * ConvertedBytesLayout.AssetEntrySize);
            var tableId = ConvertedBytesLayout.ReadInt32(content, offset);
            ConvertedBytesLayout.EnsureRange(content, offset + 4, 16);
            var rowGuid = RuntimeCompatibility.ReadBigEndianGuid(content.Slice(offset + 4, 16));
            var keyIndex = ConvertedBytesLayout.ReadInt32(content, offset + 20);
            var pathIndex = ConvertedBytesLayout.ReadInt32(content, offset + 24);
            var firstField = ConvertedBytesLayout.ReadInt32(content, offset + 28);
            var fieldCount = ConvertedBytesLayout.ReadInt32(content, offset + 32);
            var firstReference = ConvertedBytesLayout.ReadInt32(content, offset + 36);
            var referenceCount = ConvertedBytesLayout.ReadInt32(content, offset + 40);
            if (tableId <= 0 || rowGuid == Guid.Empty
                || (uint)keyIndex >= (uint)stringCount
                || (uint)pathIndex >= (uint)stringCount)
            {
                throw new InvalidDataException("Asset directory contains an invalid identity or string index.");
            }
            assets[index] = new RawAsset(
                tableId,
                rowGuid,
                keyIndex,
                pathIndex,
                firstField,
                fieldCount,
                firstReference,
                referenceCount);
        }
        return assets;
    }

    private static RawField[] ReadFields(
        ReadOnlySpan<byte> content,
        Section section,
        Section dataSection,
        int formatVersion)
    {
        var fields = new RawField[section.CountOrLength];
        var expectedDataOffset = 0;
        var entrySize = formatVersion == ConvertedBytesLayout.LegacyFormatVersion
            ? ConvertedBytesLayout.LegacyFieldEntrySize
            : ConvertedBytesLayout.FieldEntrySize;
        for (var index = 0; index < fields.Length; index++)
        {
            var offset = section.Offset + (index * entrySize);
            var fieldNumber = ConvertedBytesLayout.ReadInt32(content, offset);
            var pathLength = formatVersion == ConvertedBytesLayout.LegacyFormatVersion
                ? 1
                : ConvertedBytesLayout.ReadInt32(content, offset + 4);
            var dataOffset = ConvertedBytesLayout.ReadInt32(
                content,
                offset + (formatVersion == ConvertedBytesLayout.LegacyFormatVersion ? 4 : 8));
            var dataLength = ConvertedBytesLayout.ReadInt32(
                content,
                offset + (formatVersion == ConvertedBytesLayout.LegacyFormatVersion ? 8 : 12));
            var pathByteLength = formatVersion == ConvertedBytesLayout.LegacyFormatVersion
                ? 0L
                : (long)pathLength * sizeof(int);
            var totalLength = pathByteLength + dataLength;
            if (fieldNumber <= 0 || dataOffset != expectedDataOffset || dataLength < 0
                || pathLength <= 0 || totalLength < 0
                || dataOffset < 0 || totalLength > dataSection.CountOrLength - (long)dataOffset)
            {
                throw new InvalidDataException("Field directory contains an invalid number or payload range.");
            }

            var fieldIdPath = new int[pathLength];
            if (formatVersion == ConvertedBytesLayout.LegacyFormatVersion)
            {
                fieldIdPath[0] = fieldNumber;
            }
            else
            {
                for (var pathIndex = 0; pathIndex < fieldIdPath.Length; pathIndex++)
                {
                    fieldIdPath[pathIndex] = ConvertedBytesLayout.ReadInt32(
                        content,
                        checked(dataSection.Offset + dataOffset + (pathIndex * sizeof(int))));
                    if (fieldIdPath[pathIndex] <= 0)
                        throw new InvalidDataException("Field id paths must contain only positive field numbers.");
                }

                if (fieldIdPath[^1] != fieldNumber)
                    throw new InvalidDataException("Field directory leaf number does not match its field id path.");
            }

            var payloadOffset = checked(dataSection.Offset + dataOffset + (int)pathByteLength);
            fields[index] = new RawField(
                fieldIdPath,
                content.Slice(payloadOffset, dataLength).ToArray());
            expectedDataOffset = checked(dataOffset + (int)totalLength);
        }
        if (expectedDataOffset != dataSection.CountOrLength)
            throw new InvalidDataException("Field payload contains unreachable trailing bytes.");
        return fields;
    }

    private static RawReference[] ReadReferences(ReadOnlySpan<byte> content, Section section, int assetCount)
    {
        var references = new RawReference[section.CountOrLength];
        for (var index = 0; index < references.Length; index++)
        {
            var offset = section.Offset + (index * ConvertedBytesLayout.ReferenceEntrySize);
            var source = ConvertedBytesLayout.ReadInt32(content, offset);
            var target = ConvertedBytesLayout.ReadInt32(content, offset + 4);
            if ((uint)source >= (uint)assetCount || (uint)target >= (uint)assetCount)
                throw new InvalidDataException("Reference index lies outside the asset directory.");
            references[index] = new RawReference(source, target);
        }
        return references;
    }

    private static void ValidateTableDirectory(
        ReadOnlySpan<byte> content,
        Section section,
        IReadOnlyList<RuntimeAssetRecord> assets)
    {
        var expected = assets
            .Select(static (asset, index) => (asset, index))
            .GroupBy(static item => item.asset.TableId)
            .Select(static group => (TableId: group.Key, First: group.First().index, Count: group.Count()))
            .ToArray();
        if (expected.Length != section.CountOrLength)
            throw new InvalidDataException("Table directory count does not match the asset directory.");

        for (var index = 0; index < expected.Length; index++)
        {
            var offset = section.Offset + (index * ConvertedBytesLayout.TableEntrySize);
            var actual = (
                TableId: ConvertedBytesLayout.ReadInt32(content, offset),
                First: ConvertedBytesLayout.ReadInt32(content, offset + 4),
                Count: ConvertedBytesLayout.ReadInt32(content, offset + 8));
            if (actual != expected[index])
                throw new InvalidDataException("Table directory is not the canonical projection of the asset directory.");
        }
    }

    private static void ValidateKeyIndex(
        ReadOnlySpan<byte> content,
        Section section,
        IReadOnlyList<RuntimeAssetRecord> assets,
        IReadOnlyList<string> strings)
    {
        if (section.CountOrLength != assets.Count)
            throw new InvalidDataException("Key index count does not match the asset directory.");

        var stringMap = strings
            .Select(static (value, index) => (value, index))
            .ToDictionary(static item => item.value, static item => item.index, StringComparer.Ordinal);
        var expected = assets
            .Select(static (asset, index) => (asset, index))
            .OrderBy(static item => item.asset.TableId)
            .ThenBy(static item => item.asset.Key, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < expected.Length; index++)
        {
            var offset = section.Offset + (index * ConvertedBytesLayout.KeyIndexEntrySize);
            var tableId = ConvertedBytesLayout.ReadInt32(content, offset);
            var keyStringIndex = ConvertedBytesLayout.ReadInt32(content, offset + 4);
            var assetIndex = ConvertedBytesLayout.ReadInt32(content, offset + 8);
            var item = expected[index];
            if (tableId != item.asset.TableId
                || keyStringIndex != stringMap[item.asset.Key]
                || assetIndex != item.index)
            {
                throw new InvalidDataException("Key index is not canonical or does not match its asset.");
            }
        }
    }

    private static void ValidateGuidIndex(
        ReadOnlySpan<byte> content,
        Section section,
        IReadOnlyList<RuntimeAssetRecord> assets)
    {
        if (section.CountOrLength != assets.Count)
            throw new InvalidDataException("Guid index count does not match the asset directory.");

        for (var index = 0; index < assets.Count; index++)
        {
            var offset = section.Offset + (index * ConvertedBytesLayout.GuidIndexEntrySize);
            var tableId = ConvertedBytesLayout.ReadInt32(content, offset);
            var rowGuid = RuntimeCompatibility.ReadBigEndianGuid(content.Slice(offset + 4, 16));
            var assetIndex = ConvertedBytesLayout.ReadInt32(content, offset + 20);
            if (tableId != assets[index].TableId
                || rowGuid != assets[index].Identity.RowGuid.Value
                || assetIndex != index)
            {
                throw new InvalidDataException("Guid index is not canonical or does not match its asset.");
            }
        }
    }

    private static void ValidateManifest(
        ConvertedBytesManifest manifest,
        ConvertedBytesHeader header,
        ExportTargetId exportTarget,
        string wholeHash)
    {
        if (manifest.FormatVersion != header.FormatVersion
            || manifest.SchemaHash != header.SchemaHash
            || manifest.ExportTarget != exportTarget
            || manifest.TableCount != header.Sections[0].CountOrLength
            || manifest.RowCount != header.Sections[3].CountOrLength
            || !string.Equals(manifest.BytesSha256, wholeHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Manifest identity/count/hash does not match the converted bytes.");
        }
    }

    private static void VerifyIntegrity(ReadOnlySpan<byte> content, Section integrity)
    {
        var expected = RuntimeCompatibility.Sha256(content[..integrity.Offset]);
        if (!CryptographicOperations.FixedTimeEquals(expected, content.Slice(integrity.Offset, integrity.CountOrLength)))
            throw new InvalidDataException("Converted-bytes integrity hash is invalid.");
    }

    private static void ValidateCanonicalAssetOrder(IReadOnlyList<AssetIdentity> identities)
    {
        for (var index = 1; index < identities.Count; index++)
        {
            if (AssetIdentityComparer.Instance.Compare(identities[index - 1], identities[index]) >= 0)
                throw new InvalidDataException("Asset directory must be strictly identity-sorted and deduplicated.");
        }
    }

    private static void ValidateSection(Section section, int entrySize, ref int expectedOffset)
    {
        if (section.CountOrLength < 0 || section.Offset != expectedOffset)
            throw new InvalidDataException("Converted-bytes sections are not canonical and contiguous.");
        try
        {
            expectedOffset = checked(expectedOffset + (section.CountOrLength * entrySize));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Converted-bytes section size overflowed.", exception);
        }
    }

    private static void ValidateByteSection(Section section, ref int expectedOffset)
    {
        if (section.CountOrLength < 0 || section.Offset != expectedOffset)
            throw new InvalidDataException("Converted-bytes sections are not canonical and contiguous.");
        try
        {
            expectedOffset = checked(expectedOffset + section.CountOrLength);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Converted-bytes section size overflowed.", exception);
        }
    }

    private static void EnsureSlice(int first, int count, int total, string name)
    {
        if (first < 0 || count < 0 || first > total - count)
            throw new InvalidDataException($"Asset {name} range lies outside its section.");
    }

    private readonly record struct RawAsset(
        int TableId,
        Guid RowGuid,
        int KeyStringIndex,
        int PathStringIndex,
        int FirstField,
        int FieldCount,
        int FirstReference,
        int ReferenceCount);

    private sealed record RawField(int[] FieldIdPath, byte[] Data);

    private readonly record struct RawReference(int SourceAssetIndex, int TargetAssetIndex);
}

internal sealed record ConvertedBytesHeader(
    int FormatVersion,
    ulong SchemaHash,
    int TargetStringIndex,
    Section[] Sections);
