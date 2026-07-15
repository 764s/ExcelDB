using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExcelDb.Tooling.Plans;

public static class MutationPlanCodec
{
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(indented: false);
    private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(indented: true);

    public static MutationPlan Seal(MutationPlan unsealed)
    {
        ArgumentNullException.ThrowIfNull(unsealed);
        var body = Canonicalize(unsealed with { PlanHash = string.Empty });
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body, CompactOptions)));
        return body with { PlanHash = hash };
    }

    public static bool Validate(MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.FormatVersion != MutationPlan.CurrentFormatVersion)
            return false;

        try
        {
            var expected = Seal(plan);
            if (!string.Equals(expected.PlanHash, plan.PlanHash, StringComparison.Ordinal))
                return false;

            // A valid frozen plan is itself canonical; equivalent but differently ordered JSON is rejected.
            return JsonSerializer.SerializeToUtf8Bytes(plan, CompactOptions)
                .AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expected, CompactOptions));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static byte[] Serialize(MutationPlan plan)
    {
        if (plan.FormatVersion != MutationPlan.CurrentFormatVersion)
            throw UnsupportedVersion(plan.FormatVersion);
        if (!Validate(plan))
            throw new InvalidDataException("MutationPlan planHash is invalid or its body is not canonical.");
        return [.. JsonSerializer.SerializeToUtf8Bytes(plan, IndentedOptions), (byte)'\n'];
    }

    public static MutationPlan Deserialize(ReadOnlySpan<byte> json)
    {
        MutationPlan plan;
        try
        {
            plan = JsonSerializer.Deserialize<MutationPlan>(json, CompactOptions)
                ?? throw new InvalidDataException("MutationPlan JSON is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("MutationPlan JSON is invalid.", exception);
        }

        if (plan.FormatVersion != MutationPlan.CurrentFormatVersion)
            throw UnsupportedVersion(plan.FormatVersion);
        if (!Validate(plan))
            throw new InvalidDataException("MutationPlan planHash does not match its canonical body.");
        return plan;
    }

    private static MutationPlan Canonicalize(MutationPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.Operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ToolVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.ProjectConfigHash);
        ArgumentNullException.ThrowIfNull(plan.SystemCatalogHash);

        var projectRoot = PathFacts.CanonicalRoot(plan.ProjectRoot);
        var generatedCSharpRoot = PathFacts.CanonicalRoot(plan.GeneratedCSharpRoot ?? projectRoot);

        if ((!plan.Observations.IsDefault && plan.Observations.Any(static item => !Enum.IsDefined(item.Root) || !Enum.IsDefined(item.Kind)))
            || (!plan.InputSets.IsDefault && plan.InputSets.Any(static item =>
                !Enum.IsDefined(item.Root)
                || !Enum.IsDefined(item.Kind)
                || !Enum.IsDefined(item.RootKind)
                || (!item.Entries.IsDefault && item.Entries.Any(static entry => !Enum.IsDefined(entry.Kind)))))
            || (!plan.Mutations.IsDefault && plan.Mutations.Any(static item => !Enum.IsDefined(item.Root) || !Enum.IsDefined(item.Kind))))
        {
            throw new InvalidDataException("MutationPlan contains an unknown root, observation, or mutation kind.");
        }

        var observations = plan.Observations.IsDefault
            ? ImmutableArray<PathObservation>.Empty
            : plan.Observations
                .Select(item => item with
                {
                    RelativePath = CanonicalRelativePath(
                        item.Root == PlanRootKind.Project ? projectRoot : generatedCSharpRoot,
                        item.RelativePath),
                })
                .OrderBy(static item => item.Root)
                .ThenBy(static item => item.RelativePath, StringComparer.Ordinal)
                .ThenBy(static item => item.Kind)
                .ThenBy(static item => item.Length)
                .ThenBy(static item => item.Sha256, StringComparer.Ordinal)
                .ToImmutableArray();

        var inputSets = plan.InputSets.IsDefault
            ? ImmutableArray<InputSetObservation>.Empty
            : plan.InputSets
                .Select(item => InputSetSnapshot.Canonicalize(
                    item.Root == PlanRootKind.Project ? projectRoot : generatedCSharpRoot,
                    item))
                .OrderBy(static item => item.Root)
                .ThenBy(static item => item.Kind)
                .ThenBy(static item => item.RelativeRoot, StringComparer.Ordinal)
                .ThenBy(static item => item.Digest, StringComparer.Ordinal)
                .ToImmutableArray();

        var mutations = plan.Mutations.IsDefault
            ? ImmutableArray<FileMutation>.Empty
            : plan.Mutations
                .Select(item => item with
                {
                    RelativePath = CanonicalRelativePath(
                        item.Root == PlanRootKind.Project ? projectRoot : generatedCSharpRoot,
                        item.RelativePath),
                })
                .OrderBy(static item => item.Root)
                .ThenBy(static item => item.RelativePath, StringComparer.Ordinal)
                .ThenBy(static item => item.Kind)
                .ThenBy(static item => item.ContentSha256, StringComparer.Ordinal)
                .ThenBy(static item => item.ContentBase64, StringComparer.Ordinal)
                .ToImmutableArray();

        var diagnostics = plan.Diagnostics.IsDefault
            ? ImmutableArray<ExcelDb.Core.Diagnostics.Diagnostic>.Empty
            : plan.Diagnostics
                .OrderBy(static item => item.Code, StringComparer.Ordinal)
                .ThenBy(static item => item.Location, StringComparer.Ordinal)
                .ThenBy(static item => item.Severity)
                .ThenBy(static item => item.Message, StringComparer.Ordinal)
                .ToImmutableArray();

        var risks = plan.Risks.IsDefault
            ? ImmutableArray<string>.Empty
            : plan.Risks
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static item => item, StringComparer.Ordinal)
                .ToImmutableArray();

        return plan with
        {
            ProjectRoot = projectRoot,
            GeneratedCSharpRoot = generatedCSharpRoot,
            Observations = observations,
            InputSets = inputSets,
            Mutations = mutations,
            Diagnostics = diagnostics,
            Risks = risks,
            PlanHash = string.Empty,
        };
    }

    private static string CanonicalRelativePath(string root, string relativePath)
    {
        var fullPath = PathFacts.ResolveContained(root, PathFacts.Normalize(relativePath));
        return PathFacts.Normalize(Path.GetRelativePath(root, fullPath));
    }

    private static InvalidDataException UnsupportedVersion(int version) => version == 1
        ? new InvalidDataException(
            "MutationPlan format 1 is frozen and cannot be decoded or applied. Create a new format 2 plan.")
        : new InvalidDataException($"Unsupported MutationPlan format {version}; expected format 2.");

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = indented,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
}
