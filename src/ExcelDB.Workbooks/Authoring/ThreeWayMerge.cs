using System.Collections.Immutable;
using ExcelDb.Core.Identity;
using ExcelDb.Core.Values;
using ExcelDb.Workbooks.Importing;

namespace ExcelDb.Workbooks.Authoring;

public enum MergeConflictKind
{
    Field,
    Key,
    DeleteModify,
    ConcurrentAdd,
}

public sealed record MergeConflict(
    AssetIdentity Identity,
    string Path,
    MergeConflictKind Kind,
    string? BaseValue,
    string? MineValue,
    string? TheirsValue);

public sealed record MergeResult(
    ImmutableDictionary<AssetIdentity, SnapshotRow> Rows,
    ImmutableArray<MergeConflict> Conflicts)
{
    public bool CanCommit => Conflicts.IsDefaultOrEmpty;
}

public static class ThreeWayMerge
{
    public static MergeResult Merge(
        ImportSnapshot @base,
        ImportSnapshot theirs,
        IReadOnlyDictionary<AssetIdentity, DraftChange> mine)
    {
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(theirs);
        ArgumentNullException.ThrowIfNull(mine);
        var merged = ImmutableDictionary.CreateBuilder<AssetIdentity, SnapshotRow>();
        var conflicts = ImmutableArray.CreateBuilder<MergeConflict>();
        var identities = @base.Rows.Keys
            .Concat(theirs.Rows.Keys)
            .Concat(mine.Keys)
            .Distinct()
            .OrderBy(static identity => identity.TableId)
            .ThenBy(static identity => identity.RowGuid.ToString(), StringComparer.Ordinal);
        foreach (var identity in identities)
        {
            @base.Rows.TryGetValue(identity, out var baseRow);
            theirs.Rows.TryGetValue(identity, out var theirRow);
            mine.TryGetValue(identity, out var draft);
            if (draft is null)
            {
                if (theirRow is not null)
                    merged[identity] = theirRow;
                continue;
            }

            if (draft.Kind == DraftChangeKind.Deleted)
            {
                if (theirRow is null || RowsEqual(baseRow, theirRow))
                    continue;
                conflicts.Add(new MergeConflict(
                    identity,
                    "$row",
                    MergeConflictKind.DeleteModify,
                    RowText(baseRow),
                    "<deleted>",
                    RowText(theirRow)));
                merged[identity] = theirRow;
                continue;
            }

            var myRow = draft.Row!;
            if (baseRow is null)
            {
                if (theirRow is null || RowsEqual(myRow, theirRow))
                {
                    merged[identity] = theirRow ?? myRow;
                    continue;
                }

                conflicts.Add(new MergeConflict(
                    identity,
                    "$row",
                    MergeConflictKind.ConcurrentAdd,
                    null,
                    RowText(myRow),
                    RowText(theirRow)));
                merged[identity] = theirRow;
                continue;
            }

            if (theirRow is null)
            {
                if (!RowsEqual(baseRow, myRow))
                {
                    conflicts.Add(new MergeConflict(
                        identity,
                        "$row",
                        MergeConflictKind.DeleteModify,
                        RowText(baseRow),
                        RowText(myRow),
                        "<deleted>"));
                    merged[identity] = myRow;
                }

                continue;
            }

            merged[identity] = MergeRow(identity, baseRow, myRow, theirRow, conflicts);
        }

        return new MergeResult(merged.ToImmutable(), conflicts.ToImmutable());
    }

    private static SnapshotRow MergeRow(
        AssetIdentity identity,
        SnapshotRow baseRow,
        SnapshotRow mine,
        SnapshotRow theirs,
        ImmutableArray<MergeConflict>.Builder conflicts)
    {
        var key = MergeScalar(
            identity,
            "$key",
            MergeConflictKind.Key,
            baseRow.Key,
            mine.Key,
            theirs.Key,
            conflicts);
        var values = ImmutableDictionary.CreateBuilder<string, CanonicalValue>(StringComparer.Ordinal);
        var rawValues = ImmutableDictionary.CreateBuilder<string, CanonicalValue>(StringComparer.Ordinal);
        foreach (var path in baseRow.RawValues.Keys
                     .Concat(mine.RawValues.Keys)
                     .Concat(theirs.RawValues.Keys)
                     .Concat(baseRow.Values.Keys)
                     .Concat(mine.Values.Keys)
                     .Concat(theirs.Values.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            var baseRaw = baseRow.RawValues.GetValueOrDefault(path, CanonicalValue.Missing);
            var myRaw = ComparableRaw(mine, baseRow, path);
            var theirRaw = ComparableRaw(theirs, baseRow, path);
            var baseValue = baseRow.Values.GetValueOrDefault(path, CanonicalValue.Missing);
            var myValue = mine.Values.GetValueOrDefault(path, CanonicalValue.Missing);
            var theirValue = theirs.Values.GetValueOrDefault(path, CanonicalValue.Missing);
            var merged = MergeField(
                identity,
                path,
                baseRaw,
                myRaw,
                theirRaw,
                baseValue,
                myValue,
                theirValue,
                conflicts);
            rawValues[path] = merged.Raw;
            values[path] = merged.Effective;
        }

        return theirs with
        {
            Key = key,
            Values = values.ToImmutable(),
            RawCells = MergeRawCells(baseRow, mine, theirs),
            RawValues = rawValues.ToImmutable(),
        };
    }

    private static MergedField MergeField(
        AssetIdentity identity,
        string path,
        CanonicalValue baseRaw,
        CanonicalValue mineRaw,
        CanonicalValue theirsRaw,
        CanonicalValue baseEffective,
        CanonicalValue mineEffective,
        CanonicalValue theirsEffective,
        ImmutableArray<MergeConflict>.Builder conflicts)
    {
        _ = baseEffective;
        if (mineRaw == baseRaw)
            return new MergedField(theirsRaw, theirsEffective);
        if (theirsRaw == baseRaw || mineRaw == theirsRaw)
            return new MergedField(mineRaw, mineEffective);
        conflicts.Add(new MergeConflict(
            identity,
            path,
            MergeConflictKind.Field,
            baseRaw.ToString(),
            mineRaw.ToString(),
            theirsRaw.ToString()));
        return new MergedField(theirsRaw, theirsEffective);
    }

    private static string? MergeScalar(
        AssetIdentity identity,
        string path,
        MergeConflictKind kind,
        string? @base,
        string? mine,
        string? theirs,
        ImmutableArray<MergeConflict>.Builder conflicts)
    {
        if (string.Equals(mine, @base, StringComparison.Ordinal))
            return theirs;
        if (string.Equals(theirs, @base, StringComparison.Ordinal)
            || string.Equals(mine, theirs, StringComparison.Ordinal))
        {
            return mine;
        }

        conflicts.Add(new MergeConflict(identity, path, kind, @base, mine, theirs));
        return theirs;
    }

    private static ImmutableDictionary<string, Model.WorkbookCell> MergeRawCells(
        SnapshotRow @base,
        SnapshotRow mine,
        SnapshotRow theirs)
    {
        var result = theirs.RawCells.ToBuilder();
        foreach (var pair in mine.RawCells)
        {
            var baseValue = @base.RawCells.GetValueOrDefault(pair.Key);
            var theirValue = theirs.RawCells.GetValueOrDefault(pair.Key);
            if (Equals(theirValue, baseValue) || Equals(pair.Value, theirValue))
                result[pair.Key] = pair.Value;
        }

        return result.ToImmutable();
    }

    private static bool RowsEqual(SnapshotRow? left, SnapshotRow? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;
        if (!string.Equals(left.Key, right.Key, StringComparison.Ordinal))
            return false;
        return left.RawValues.Keys
            .Concat(right.RawValues.Keys)
            .Concat(left.Values.Keys)
            .Concat(right.Values.Keys)
            .Distinct(StringComparer.Ordinal)
            .All(path => left.RawValues.GetValueOrDefault(path, CanonicalValue.Missing)
                == ComparableRaw(right, left, path));
    }

    private static CanonicalValue ComparableRaw(
        SnapshotRow candidate,
        SnapshotRow baseline,
        string path)
    {
        var raw = candidate.RawValues.GetValueOrDefault(path, CanonicalValue.Missing);
        var baseRaw = baseline.RawValues.GetValueOrDefault(path, CanonicalValue.Missing);
        var effective = candidate.Values.GetValueOrDefault(path, CanonicalValue.Missing);
        var baseEffective = baseline.Values.GetValueOrDefault(path, CanonicalValue.Missing);
        if (raw != baseRaw || effective == baseEffective)
            return raw;
        if (raw.State == CanonicalValueState.Value
            && raw.Text?.StartsWith("=", StringComparison.Ordinal) == true)
        {
            return raw;
        }

        return effective.State == CanonicalValueState.Defaulted
            ? CanonicalValue.FromValue(effective.Text!)
            : effective;
    }

    private static string? RowText(SnapshotRow? row) => row is null
        ? null
        : $"rev={row.Revision};key={row.Key};fields={row.Values.Count}";

    private readonly record struct MergedField(CanonicalValue Raw, CanonicalValue Effective);
}
