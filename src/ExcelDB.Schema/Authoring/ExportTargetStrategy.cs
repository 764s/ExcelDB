using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace ExcelDb.Schema.Authoring;

public readonly record struct ExportTargetStrategyContext(string TableName);

public sealed record ExportTargetSelection
{
    private ExportTargetSelection(ImmutableArray<string> targets)
    {
        Targets = targets;
    }

    public ImmutableArray<string> Targets { get; }

    public static ExportTargetSelection Explicit(IEnumerable<string> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var input = targets.ToArray();
        var duplicate = input
            .GroupBy(static value => value, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Duplicate export target id '{duplicate.Key}'.", nameof(targets));

        foreach (var target in input)
        {
            if (!ExportTargetId.IsValid(target))
                throw new ArgumentException($"Invalid export target id '{target}'.", nameof(targets));
        }

        return new ExportTargetSelection(
            input.OrderBy(static value => value, StringComparer.Ordinal).ToImmutableArray());
    }
}

public interface IExportTargetStrategy
{
    string Id { get; }

    void Apply(in ExportTargetStrategyContext context, IExportTargetDraft draft);
}

public interface IExportTargetDraft
{
    string TableName { get; }

    ExportTargetSelection? TableExportTargets { get; }

    IReadOnlyList<IExportTargetFieldDraft> Fields { get; }

    bool TryFillTableExportTargets(IEnumerable<string> targets);

    bool TryFillFieldExportTargets(string fieldName, IEnumerable<string> targets);
}

public interface IExportTargetFieldDraft
{
    string Name { get; }

    ExportTargetSelection? ExportTargets { get; }
}

public sealed class StandardClientServerExportTargetStrategy : IExportTargetStrategy
{
    private static readonly string[] Defaults = ["client", "server"];

    public const string StableId = "exceldb.client-server";

    public string Id => StableId;

    public void Apply(in ExportTargetStrategyContext context, IExportTargetDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!string.Equals(context.TableName, draft.TableName, StringComparison.Ordinal))
            throw new InvalidOperationException("Export strategy context and table draft refer to different tables.");

        draft.TryFillTableExportTargets(Defaults);

        var inherited = draft.TableExportTargets!.Targets;
        foreach (var field in draft.Fields)
            draft.TryFillFieldExportTargets(field.Name, inherited);
    }
}

public static partial class ExportTargetId
{
    public static bool IsValid(string? value) =>
        value is not null && Pattern().IsMatch(value);

    [GeneratedRegex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
