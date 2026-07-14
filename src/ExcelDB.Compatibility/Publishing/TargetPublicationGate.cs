using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Schema.Hashing;

namespace ExcelDb.Compatibility.Publishing;

public sealed record TargetArtifactComponentEvidence(
    ulong SchemaHash,
    ExportTargetId ExportTarget,
    string ContentHash);

public sealed record TargetArtifactEvidence(
    ExportTargetId ExportTarget,
    TargetArtifactComponentEvidence? Registry,
    TargetArtifactComponentEvidence? Codegen,
    TargetArtifactComponentEvidence? Bytes,
    TargetArtifactComponentEvidence? Manifest);

public sealed record TargetPublicationRequest(
    CanonicalSchemaDescriptor CurrentDescriptor,
    CompatibilityReport Compatibility,
    ImmutableArray<TargetArtifactEvidence> Targets,
    ImmutableHashSet<string> CompletedHostMigrations,
    bool WorkbookMigrationsComplete)
{
    /// <summary>True only when the authoritative published-history store is provably empty.</summary>
    public bool InitialPublication { get; init; }
}

public sealed record TargetPublicationGateResult(
    bool Allowed,
    string EvidenceHash,
    ImmutableArray<Diagnostic> Diagnostics);

public static class TargetPublicationGate
{
    public static TargetPublicationGateResult Evaluate(TargetPublicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.CurrentDescriptor);
        ArgumentNullException.ThrowIfNull(request.Compatibility);

        var diagnostics = new List<Diagnostic>();
        var descriptor = request.CurrentDescriptor;
        if (request.Compatibility.FormatVersion != CompatibilityReport.CurrentFormatVersion)
        {
            diagnostics.Add(Blocker(
                "compat.publish.report-format",
                ".",
                $"Compatibility report format {request.Compatibility.FormatVersion} is unsupported."));
        }

        var canonicalSchemaHash = CanonicalSchemaSerializer.ComputeHash(descriptor);
        if (canonicalSchemaHash != descriptor.SchemaHash)
        {
            diagnostics.Add(Blocker(
                "compat.publish.descriptor-hash",
                ".",
                $"Descriptor declares schema {descriptor.SchemaHash:x16}, but canonical content hashes to {canonicalSchemaHash:x16}."));
        }

        if (request.Compatibility.CurrentSchemaHash != descriptor.SchemaHash)
        {
            diagnostics.Add(Blocker(
                "compat.publish.report-schema",
                ".",
                "Compatibility report and current descriptor use different schema hashes."));
        }

        if (!request.Compatibility.HistoryAvailable && !request.InitialPublication)
            diagnostics.Add(Blocker("compat.publish.history", ".", "A previous published descriptor is required."));
        if (request.Compatibility.MaximumSeverity >= CompatibilitySeverity.Error)
            diagnostics.Add(Blocker("compat.publish.compatibility", ".", "Compatibility contains unresolved error/blocker entries."));
        if (request.Compatibility.Entries.Any(static item =>
                item.Kind == CompatibilityChangeKind.WorkbookCoverageIncomplete))
        {
            diagnostics.Add(Blocker(
                "compat.publish.coverage",
                ".",
                "Compatibility evidence does not cover every controlled workbook."));
        }

        if (request.Compatibility.Entries.Any(static item =>
                item.Kind is CompatibilityChangeKind.WorkbookOutdated
                    or CompatibilityChangeKind.MigrationPending))
        {
            diagnostics.Add(Blocker(
                "compat.publish.workbook-current",
                ".",
                "Compatibility report still contains outdated or pending-migration workbooks."));
        }
        if (!request.WorkbookMigrationsComplete)
            diagnostics.Add(Blocker("compat.publish.migration", ".", "Controlled workbook migration evidence is incomplete."));

        var expectedTargets = descriptor.Tables
            .SelectMany(static table => table.ExportTargets)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var duplicate in request.Targets
                     .GroupBy(static item => item.ExportTarget.Value, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
        {
            diagnostics.Add(Blocker(
                "compat.publish.target-duplicate",
                duplicate.Key,
                $"Export target '{duplicate.Key}' has more than one staged artifact set."));
        }

        var byTarget = request.Targets
            .GroupBy(static item => item.ExportTarget.Value, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        foreach (var target in expectedTargets)
        {
            if (!byTarget.TryGetValue(target, out var evidence))
            {
                diagnostics.Add(Blocker(
                    "compat.publish.target-missing",
                    target,
                    $"Export target '{target}' has no complete staged registry/codegen/bytes/manifest set."));
                continue;
            }

            ValidateComponent("registry", evidence.Registry, descriptor.SchemaHash, target, diagnostics);
            ValidateComponent("codegen", evidence.Codegen, descriptor.SchemaHash, target, diagnostics);
            ValidateComponent("bytes", evidence.Bytes, descriptor.SchemaHash, target, diagnostics);
            ValidateComponent("manifest", evidence.Manifest, descriptor.SchemaHash, target, diagnostics);
        }

        foreach (var stale in byTarget.Keys.Except(expectedTargets, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            diagnostics.Add(Blocker(
                "compat.publish.target-stale",
                stale,
                $"Staged target '{stale}' is not a runtime projection of the current descriptor."));
        }

        var removedTargets = request.Compatibility.Entries
            .Where(static entry => entry.Kind == CompatibilityChangeKind.ExportTargetRemoved)
            .Select(static entry => entry.Location.ExportTarget)
            .Where(static target => target is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var target in removedTargets)
        {
            if (!request.CompletedHostMigrations.Contains(target))
            {
                diagnostics.Add(Blocker(
                    "compat.publish.host-migration",
                    target,
                    $"Host/runtime API migration for removed target '{target}' is incomplete."));
            }
        }

        var orderedDiagnostics = diagnostics
            .OrderBy(static item => item.Code, StringComparer.Ordinal)
            .ThenBy(static item => item.Location, StringComparer.Ordinal)
            .ThenBy(static item => item.Message, StringComparer.Ordinal)
            .ToImmutableArray();
        return new TargetPublicationGateResult(
            !orderedDiagnostics.Any(static item => item.IsFailure),
            ComputeEvidenceHash(request, expectedTargets),
            orderedDiagnostics);
    }

    private static void ValidateComponent(
        string kind,
        TargetArtifactComponentEvidence? component,
        ulong schemaHash,
        string target,
        List<Diagnostic> diagnostics)
    {
        if (component is null)
        {
            diagnostics.Add(Blocker(
                $"compat.publish.{kind}-missing",
                target,
                $"Target '{target}' is missing staged {kind}."));
            return;
        }

        if (component.SchemaHash != schemaHash)
        {
            diagnostics.Add(Blocker(
                $"compat.publish.{kind}-schema",
                target,
                $"Target '{target}' {kind} uses schema {component.SchemaHash:x16}, expected {schemaHash:x16}."));
        }

        if (!string.Equals(component.ExportTarget.Value, target, StringComparison.Ordinal))
        {
            diagnostics.Add(Blocker(
                $"compat.publish.{kind}-target",
                target,
                $"Target '{target}' {kind} declares export target '{component.ExportTarget}'."));
        }

        if (string.IsNullOrWhiteSpace(component.ContentHash))
            diagnostics.Add(Blocker($"compat.publish.{kind}-hash", target, $"Target '{target}' {kind} has no content hash."));
    }

    private static string ComputeEvidenceHash(TargetPublicationRequest request, IEnumerable<string> expectedTargets)
    {
        var builder = new StringBuilder();
        builder.Append("ExcelDB.TargetPublication.v1\n")
            .Append(request.CurrentDescriptor.SchemaHash.ToString("x16")).Append('\n')
            .Append(request.Compatibility.PreviousSchemaHash?.ToString("x16") ?? "none").Append('\n')
            .Append(CompatibilityReportSerializer.ComputeHash(request.Compatibility)).Append('\n')
            .Append(request.WorkbookMigrationsComplete ? "1\n" : "0\n")
            .Append(request.InitialPublication ? "initial:1\n" : "initial:0\n");
        foreach (var target in expectedTargets)
            builder.Append("required:").Append(target).Append('\n');
        foreach (var evidence in request.Targets.OrderBy(static item => item.ExportTarget.Value, StringComparer.Ordinal))
        {
            builder.Append("target:").Append(evidence.ExportTarget.Value).Append('\n');
            Append(builder, "registry", evidence.Registry);
            Append(builder, "codegen", evidence.Codegen);
            Append(builder, "bytes", evidence.Bytes);
            Append(builder, "manifest", evidence.Manifest);
        }

        foreach (var target in request.CompletedHostMigrations.Order(StringComparer.Ordinal))
            builder.Append("host:").Append(target).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();

        static void Append(StringBuilder builder, string kind, TargetArtifactComponentEvidence? component)
        {
            builder.Append(kind).Append(':');
            if (component is null)
                builder.Append("missing\n");
            else
                builder.Append(component.SchemaHash.ToString("x16")).Append(':')
                    .Append(component.ExportTarget.Value).Append(':').Append(component.ContentHash).Append('\n');
        }
    }

    private static Diagnostic Blocker(string code, string location, string message) =>
        new(code, DiagnosticSeverity.Blocker, location, message);
}
