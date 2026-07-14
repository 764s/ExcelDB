using System.Collections.Immutable;
using ExcelDb.Core.Identity;
using ExcelDb.Core.Values;
using ExcelDb.Workbooks.Importing;

namespace ExcelDb.Workbooks.Authoring;

public enum DraftChangeKind
{
    Added,
    Modified,
    Renamed,
    Deleted,
}

public sealed record DraftChange(DraftChangeKind Kind, SnapshotRow? Row);

/// <summary>Drafts are memory-only. Discard always returns to the latest published import snapshot.</summary>
public sealed class AuthoringDraftStore
{
    private ImportSnapshot _latestSnapshot;
    private readonly Dictionary<AssetIdentity, DraftChange> _changes = [];

    public AuthoringDraftStore(ImportSnapshot latestSnapshot)
    {
        _latestSnapshot = latestSnapshot ?? throw new ArgumentNullException(nameof(latestSnapshot));
    }

    public ImportSnapshot LatestSnapshot => _latestSnapshot;

    public ImmutableDictionary<AssetIdentity, DraftChange> Changes => _changes.ToImmutableDictionary();

    public void Edit(AssetIdentity identity, string propertyPath, CanonicalValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        var current = GetEditable(identity);
        var values = current.Values.SetItem(propertyPath, value);
        var rawValue = value.State == CanonicalValueState.Defaulted
            ? CanonicalValue.FromValue(value.Text!)
            : value;
        _changes[identity] = new DraftChange(
            CurrentKind(identity, DraftChangeKind.Modified),
            current with
            {
                Values = values,
                RawValues = current.RawValues.SetItem(propertyPath, rawValue),
            });
    }

    /// <summary>A direct edit of key cells is represented as a rename, never as delete-plus-add.</summary>
    public void Rename(AssetIdentity identity, string newKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newKey);
        var current = GetEditable(identity);
        _changes[identity] = new DraftChange(
            CurrentKind(identity, DraftChangeKind.Renamed),
            current with { Key = newKey });
    }

    public void Add(SnapshotRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Revision != 0)
            throw new ArgumentException("A new row must start at revision 0.", nameof(row));
        if (_latestSnapshot.Rows.ContainsKey(row.Identity) || _changes.ContainsKey(row.Identity))
            throw new InvalidOperationException($"Identity {row.Identity} already exists.");
        _changes.Add(row.Identity, new DraftChange(DraftChangeKind.Added, row));
    }

    public void Delete(AssetIdentity identity)
    {
        if (_changes.TryGetValue(identity, out var existing) && existing.Kind == DraftChangeKind.Added)
        {
            _changes.Remove(identity);
            return;
        }

        if (!_latestSnapshot.Rows.ContainsKey(identity))
            throw new KeyNotFoundException($"Identity {identity} is not in the latest snapshot.");
        _changes[identity] = new DraftChange(DraftChangeKind.Deleted, null);
    }

    public void Discard() => _changes.Clear();

    public void AcceptPublishedSnapshot(ImportSnapshot snapshot)
    {
        _latestSnapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _changes.Clear();
    }

    private SnapshotRow GetEditable(AssetIdentity identity)
    {
        if (_changes.TryGetValue(identity, out var draft))
            return draft.Row ?? throw new InvalidOperationException($"Identity {identity} is deleted in the draft.");
        return _latestSnapshot.Rows.TryGetValue(identity, out var snapshotRow)
            ? snapshotRow
            : throw new KeyNotFoundException($"Identity {identity} is not in the latest snapshot.");
    }

    private DraftChangeKind CurrentKind(AssetIdentity identity, DraftChangeKind requested)
    {
        if (_changes.TryGetValue(identity, out var current) && current.Kind == DraftChangeKind.Added)
            return DraftChangeKind.Added;
        return requested;
    }
}
