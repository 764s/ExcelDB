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
        var body = unsealed with { PlanHash = string.Empty };
        var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body, CompactOptions)));
        return body with { PlanHash = hash };
    }

    public static bool Validate(MutationPlan plan) =>
        string.Equals(Seal(plan).PlanHash, plan.PlanHash, StringComparison.Ordinal);

    public static byte[] Serialize(MutationPlan plan)
    {
        if (!Validate(plan))
            throw new InvalidDataException("MutationPlan planHash is invalid.");
        return [.. JsonSerializer.SerializeToUtf8Bytes(plan, IndentedOptions), (byte)'\n'];
    }

    public static MutationPlan Deserialize(ReadOnlySpan<byte> json)
    {
        var plan = JsonSerializer.Deserialize<MutationPlan>(json, CompactOptions)
            ?? throw new InvalidDataException("MutationPlan JSON is empty.");
        if (plan.FormatVersion != MutationPlan.CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported MutationPlan format {plan.FormatVersion}.");
        if (!Validate(plan))
            throw new InvalidDataException("MutationPlan planHash does not match its canonical body.");
        return plan;
    }

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = indented,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
}
