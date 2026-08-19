using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Workbooks.Model;

namespace ExcelDb.Workbooks.Identity;

public enum IdentityClassification
{
    Retained,
    PendingNew,
    Renamed,
    Duplicated,
    Recovered,
    Invalid,
}

public sealed record WorkbookSource(
    string Path,
    WorkbookDefinition Workbook,
    ContentFingerprint Fingerprint);

public readonly record struct RowLocation(
    string WorkbookPath,
    string SheetName,
    int RowNumber,
    int TableId)
{
    public override string ToString() => $"{WorkbookPath}:{SheetName}!{RowNumber}";
}

public sealed record KnownIdentity(AssetIdentity Identity, string? Key);

public sealed record IdentityObservation(
    WorkbookSource Source,
    WorkbookTable Table,
    WorkbookRow Row,
    RowLocation Location,
    string? RawGuid,
    IdentityClassification Classification,
    RowGuid? CandidateGuid,
    AssetIdentity? PreviousIdentity)
{
    public string BusinessHash => WorkbookRowHash.Compute(Table.TableId, Row);
}

public sealed record IdentityScanResult(
    ImmutableArray<IdentityObservation> Observations,
    ImmutableArray<KnownIdentity> Removed,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool HasBlockers => Diagnostics.Any(static diagnostic => diagnostic.IsBlocker);
}

public interface IRowGuidGenerator
{
    RowGuid Next();
}

public sealed class RandomRowGuidGenerator : IRowGuidGenerator
{
    public RowGuid Next() => RowGuid.New();
}

public sealed class ProjectIdentityScanner(IRowGuidGenerator? generator = null)
{
    private readonly IRowGuidGenerator _generator = generator ?? new RandomRowGuidGenerator();

    public IdentityScanResult Scan(
        IEnumerable<WorkbookSource> sources,
        IEnumerable<KnownIdentity>? knownIdentities = null)
    {
        Guard.NotNull(sources);
        var sourceArray = sources.ToArray();
        var known = (knownIdentities ?? []).ToArray();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var rows = FlattenRows(sourceArray).ToArray();

        var duplicateKnown = known
            .GroupBy(static item => item.Identity.RowGuid)
            .FirstOrDefault(static group => group.Select(static item => item.Identity).Distinct().Count() > 1);
        if (duplicateKnown is not null)
        {
            diagnostics.Add(new Diagnostic(
                "EXWB1000",
                DiagnosticSeverity.Blocker,
                "identity-snapshot",
                $"Snapshot row guid {duplicateKnown.Key} belongs to more than one table."));
        }

        var observedGuidGroups = rows
            .Where(static row => row.Row.RowGuid is not null)
            .GroupBy(static row => row.Row.RowGuid!.Value)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var duplicateObserved = observedGuidGroups
            .Where(static pair => pair.Value.Length > 1)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        foreach (var pair in duplicateObserved.OrderBy(static pair => pair.Key.ToString(), StringComparer.Ordinal))
        {
            diagnostics.Add(new Diagnostic(
                "EXWB1001",
                DiagnosticSeverity.Blocker,
                string.Join(", ", pair.Value.Select(static row => row.Location.ToString())),
                $"Row guid {pair.Key} occurs {pair.Value.Length} times in the project domain; no survivor was selected."));
        }

        var knownByIdentity = known
            .GroupBy(static item => item.Identity)
            .ToDictionary(static group => group.Key, static group => group.First());
        var knownByGuid = known
            .GroupBy(static item => item.Identity.RowGuid)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var knownByKey = known
            .Where(static item => item.Key is not null)
            .GroupBy(static item => (item.Identity.TableId, item.Key!), TableKeyComparer.Instance)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), TableKeyComparer.Instance);

        var reserved = known.Select(static item => item.Identity.RowGuid)
            .Concat(rows.Where(static row => row.Row.RowGuid is not null).Select(static row => row.Row.RowGuid!.Value))
            .ToHashSet();
        var observations = new List<IdentityObservation>(rows.Length);
        var seen = new HashSet<AssetIdentity>();
        foreach (var input in rows)
        {
            var rawGuid = input.Row.RawRowGuid ?? input.Row.RowGuid?.ToString();
            if (input.Row.RowGuid is { } currentGuid)
            {
                var identity = new AssetIdentity(input.Table.TableId, currentGuid);
                if (duplicateObserved.ContainsKey(currentGuid)
                    || (knownByGuid.TryGetValue(currentGuid, out var knownOwners)
                        && knownOwners.Any(owner => owner.Identity != identity)))
                {
                    if (!duplicateObserved.ContainsKey(currentGuid))
                    {
                        diagnostics.Add(new Diagnostic(
                            "EXWB1002",
                            DiagnosticSeverity.Blocker,
                            input.Location.ToString(),
                            $"Row guid {currentGuid} is already owned by another table in the project domain."));
                    }

                    observations.Add(CreateObservation(input, rawGuid, IdentityClassification.Duplicated, null, null));
                    continue;
                }

                knownByIdentity.TryGetValue(identity, out var previous);
                var trustedKey = input.Row.KeyIsProjection ? null : input.Row.Key;
                var classification = previous is not null
                    && trustedKey is not null
                    && !string.Equals(previous.Key, trustedKey, StringComparison.Ordinal)
                    ? IdentityClassification.Renamed
                    : IdentityClassification.Retained;
                observations.Add(CreateObservation(input, rawGuid, classification, currentGuid, previous?.Identity));
                seen.Add(identity);
                continue;
            }

            KnownIdentity[] recoveryMatches = [];
            var recoveryKey = input.Row.KeyIsProjection ? null : input.Row.Key;
            if (recoveryKey is not null
                && knownByKey.TryGetValue((input.Table.TableId, recoveryKey), out var matches))
            {
                recoveryMatches = matches;
            }
            if (recoveryMatches.Length == 1)
            {
                var recovered = recoveryMatches[0];
                observations.Add(CreateObservation(
                    input,
                    rawGuid,
                    IdentityClassification.Recovered,
                    recovered.Identity.RowGuid,
                    recovered.Identity));
                seen.Add(recovered.Identity);
                diagnostics.Add(new Diagnostic(
                    "EXWB1003",
                    DiagnosticSeverity.Warning,
                    input.Location.ToString(),
                    $"Identity {recovered.Identity} can be recovered, but requires an explicit identity-repair operation."));
                continue;
            }

            if (recoveryMatches.Length > 1)
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB1004",
                    DiagnosticSeverity.Blocker,
                    input.Location.ToString(),
                    $"Key '{recoveryKey}' matches more than one snapshot identity."));
                observations.Add(CreateObservation(input, rawGuid, IdentityClassification.Invalid, null, null));
                continue;
            }

            if (!string.IsNullOrEmpty(rawGuid))
            {
                diagnostics.Add(new Diagnostic(
                    "EXWB1005",
                    DiagnosticSeverity.Blocker,
                    input.Location.ToString(),
                    $"Malformed row guid '{rawGuid}' cannot be replaced implicitly."));
                observations.Add(CreateObservation(input, rawGuid, IdentityClassification.Invalid, null, null));
                continue;
            }

            var candidate = NextUnique(reserved);
            observations.Add(CreateObservation(input, null, IdentityClassification.PendingNew, candidate, null));
        }

        var duplicateRecoveries = observations
            .Where(static observation => observation.Classification == IdentityClassification.Recovered)
            .GroupBy(static observation => observation.CandidateGuid)
            .Where(static group => group.Count() > 1)
            .ToArray();
        foreach (var group in duplicateRecoveries)
        {
            diagnostics.Add(new Diagnostic(
                "EXWB1006",
                DiagnosticSeverity.Blocker,
                string.Join(", ", group.Select(static observation => observation.Location.ToString())),
                $"Snapshot identity {group.Key} matches multiple rows; repair is ambiguous."));
            var locations = group.Select(static observation => observation.Location).ToHashSet();
            for (var index = 0; index < observations.Count; index++)
            {
                if (locations.Contains(observations[index].Location))
                    observations[index] = observations[index] with { Classification = IdentityClassification.Invalid, CandidateGuid = null };
            }
        }

        var removed = known.Where(item => !seen.Contains(item.Identity)).ToImmutableArray();
        return new IdentityScanResult(observations.ToImmutableArray(), removed, diagnostics.ToImmutable());
    }

    private static IEnumerable<InputRow> FlattenRows(IEnumerable<WorkbookSource> sources)
    {
        foreach (var source in sources.OrderBy(static source => source.Path, StringComparer.Ordinal))
        {
            foreach (var table in source.Workbook.Tables.OrderBy(static table => table.TableId))
            {
                if (table.IsRetiredPreserved)
                    continue;
                for (var index = 0; index < table.Rows.Length; index++)
                {
                    var row = table.Rows[index];
                    var rowNumber = row.SourceRowNumber ?? (table.DataStartRow + index);
                    yield return new InputRow(
                        source,
                        table,
                        row,
                        new RowLocation(source.Path, table.SheetName, rowNumber, table.TableId));
                }
            }
        }
    }

    private static IdentityObservation CreateObservation(
        InputRow input,
        string? rawGuid,
        IdentityClassification classification,
        RowGuid? candidate,
        AssetIdentity? previous) =>
        new(input.Source, input.Table, input.Row, input.Location, rawGuid, classification, candidate, previous);

    private RowGuid NextUnique(HashSet<RowGuid> reserved)
    {
        for (var attempt = 0; attempt < 1_024; attempt++)
        {
            var candidate = _generator.Next();
            if (candidate.IsEmpty)
                throw new InvalidOperationException("A row guid generator returned the zero guid.");
            if (reserved.Add(candidate))
                return candidate;
        }

        throw new InvalidOperationException("A row guid generator failed to produce a unique value.");
    }

    private sealed record InputRow(
        WorkbookSource Source,
        WorkbookTable Table,
        WorkbookRow Row,
        RowLocation Location);

    private sealed class TableKeyComparer : IEqualityComparer<(int TableId, string Key)>
    {
        public static TableKeyComparer Instance { get; } = new();

        public bool Equals((int TableId, string Key) x, (int TableId, string Key) y) =>
            x.TableId == y.TableId && string.Equals(x.Key, y.Key, StringComparison.Ordinal);

        public int GetHashCode((int TableId, string Key) obj) =>
            HashCode.Combine(obj.TableId, StringComparer.Ordinal.GetHashCode(obj.Key));
    }
}

public static class WorkbookRowHash
{
    public static string Compute(int tableId, WorkbookRow row)
    {
        Guard.NotNull(row);
        var builder = new StringBuilder();
        Append(builder, tableId.ToString());
        Append(builder, row.Revision.ToString());
        foreach (var pair in row.Cells.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Append(builder, pair.Key);
            Append(builder, pair.Value.Formula is null ? "v" : "f");
            Append(builder, pair.Value.ComparisonText);
        }

        return HashUtility.Sha256Upper(Encoding.UTF8.GetBytes(builder.ToString()))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder builder, string text) =>
        builder.Append(text.Length).Append(':').Append(text).Append(';');
}
