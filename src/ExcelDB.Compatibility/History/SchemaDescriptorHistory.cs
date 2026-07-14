using System.Collections.Immutable;
using System.Security.Cryptography;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Hashing;

namespace ExcelDb.Compatibility.History;

public sealed record SchemaDescriptorSnapshot(
    int Sequence,
    string Revision,
    ulong SchemaHash,
    string ContentHash,
    CanonicalSchemaDescriptor Descriptor);

public interface ISchemaDescriptorHistory
{
    IReadOnlyList<SchemaDescriptorSnapshot> Snapshots { get; }

    bool TryGet(ulong schemaHash, out SchemaDescriptorSnapshot? snapshot);

    bool TryGetRevision(string revision, out SchemaDescriptorSnapshot? snapshot);

    SchemaDescriptorSnapshot? PreviousOf(ulong schemaHash);
}

/// <summary>
/// Versioned canonical descriptor history. Persistence is supplied by the host;
/// this implementation validates and owns immutable machine snapshots.
/// </summary>
public sealed class SchemaDescriptorHistory : ISchemaDescriptorHistory
{
    private readonly object _gate = new();
    private ImmutableArray<SchemaDescriptorSnapshot> _snapshots = [];

    public IReadOnlyList<SchemaDescriptorSnapshot> Snapshots
    {
        get
        {
            lock (_gate)
                return _snapshots;
        }
    }

    public SchemaDescriptorSnapshot RecordPublished(
        string revision,
        CanonicalSchemaDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentNullException.ThrowIfNull(descriptor);
        var bytes = CanonicalSchemaSerializer.Serialize(descriptor);
        var canonicalHash = CanonicalSchemaSerializer.ComputeHash(descriptor);
        if (canonicalHash != descriptor.SchemaHash)
        {
            throw new InvalidDataException(
                $"Descriptor declares schema hash {descriptor.SchemaHash:x16}, but canonical bytes hash to {canonicalHash:x16}.");
        }

        var contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        lock (_gate)
        {
            var sameRevision = _snapshots.FirstOrDefault(item =>
                string.Equals(item.Revision, revision, StringComparison.Ordinal));
            if (sameRevision is not null)
            {
                if (sameRevision.SchemaHash == descriptor.SchemaHash
                    && string.Equals(sameRevision.ContentHash, contentHash, StringComparison.Ordinal))
                {
                    return sameRevision;
                }

                throw new InvalidOperationException($"Published revision '{revision}' already identifies another descriptor.");
            }

            var sameHash = _snapshots.FirstOrDefault(item => item.SchemaHash == descriptor.SchemaHash);
            if (sameHash is not null)
            {
                if (!string.Equals(sameHash.ContentHash, contentHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"Schema hash collision detected for {descriptor.SchemaHash:x16}.");
                return sameHash;
            }

            var snapshot = new SchemaDescriptorSnapshot(
                _snapshots.Length + 1,
                revision,
                descriptor.SchemaHash,
                contentHash,
                descriptor);
            _snapshots = _snapshots.Add(snapshot);
            return snapshot;
        }
    }

    public bool TryGet(ulong schemaHash, out SchemaDescriptorSnapshot? snapshot)
    {
        lock (_gate)
            snapshot = _snapshots.FirstOrDefault(item => item.SchemaHash == schemaHash);
        return snapshot is not null;
    }

    public bool TryGetRevision(string revision, out SchemaDescriptorSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(revision);
        lock (_gate)
            snapshot = _snapshots.FirstOrDefault(item =>
                string.Equals(item.Revision, revision, StringComparison.Ordinal));
        return snapshot is not null;
    }

    public SchemaDescriptorSnapshot? PreviousOf(ulong schemaHash)
    {
        lock (_gate)
        {
            for (var index = 1; index < _snapshots.Length; index++)
            {
                if (_snapshots[index].SchemaHash == schemaHash)
                    return _snapshots[index - 1];
            }

            return null;
        }
    }
}
