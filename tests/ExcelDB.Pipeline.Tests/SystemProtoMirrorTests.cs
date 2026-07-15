using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExcelDb.Pipeline;
using ExcelDb.Schema.Compilation;
using ExcelDb.Tooling.Plans;
using ExcelDb.Tooling.Project;

namespace ExcelDb.Pipeline.Tests;

public sealed class SystemProtoMirrorTests
{
    [Fact]
    public void Missing_mirror_is_planned_from_catalog_without_becoming_schema_authority()
    {
        using var project = TestProject.Create();
        var catalog = TestCatalog.Create();

        var analysis = new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false);

        Assert.Equal(SystemProtoMirrorStatus.Missing, analysis.Status);
        Assert.False(analysis.HasBlockers);
        Assert.Equal(catalog.Files.Count + 1, analysis.Mutations.Length);
        Assert.Contains(analysis.Mutations, mutation =>
            mutation.AbsolutePath == project.Context.SystemImportsManifestPath);
    }

    [Fact]
    public void Current_owned_mirror_is_a_no_op()
    {
        using var project = TestProject.Create();
        var catalog = TestCatalog.Create();
        Apply(new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false));

        var analysis = new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false);

        Assert.Equal(SystemProtoMirrorStatus.Current, analysis.Status);
        Assert.Empty(analysis.Mutations);
        Assert.False(analysis.HasBlockers);
    }

    [Fact]
    public void Modified_owned_mirror_requires_explicit_repair()
    {
        using var project = TestProject.Create();
        var catalog = TestCatalog.Create();
        Apply(new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false));
        File.WriteAllText(
            Path.Combine(project.Context.SchemaDirectory, "root.proto"),
            "syntax = \"proto3\";\nimport \"exceldb/options.proto\";\nmessage Root {}\n");
        var options = Path.Combine(project.Context.SchemaDirectory, "exceldb", "options.proto");
        File.SetAttributes(options, FileAttributes.Normal);
        File.WriteAllText(options, "syntax = \"proto3\"; // modified\n");

        var ordinary = new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false);
        var explicitPlan = new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: true);

        Assert.True(ordinary.HasBlockers);
        Assert.Contains(ordinary.Diagnostics, diagnostic =>
            diagnostic.Code == "system-import.modified"
            && diagnostic.Message.Contains(
                "Import chain: root.proto -> exceldb/options.proto",
                StringComparison.Ordinal));
        Assert.False(explicitPlan.HasBlockers);
        Assert.Contains(explicitPlan.Mutations, mutation => mutation.AbsolutePath == options);
    }

    [Fact]
    public void Modified_transitive_catalog_import_reports_complete_business_chain()
    {
        using var project = TestProject.Create();
        var catalog = TestCatalog.Create();
        Apply(new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false));
        File.WriteAllText(
            Path.Combine(project.Context.SchemaDirectory, "root.proto"),
            "syntax = \"proto3\";\nimport \"exceldb/options.proto\";\nmessage Root {}\n");
        var descriptor = Path.Combine(project.Context.SchemaDirectory, "google", "protobuf", "descriptor.proto");
        File.SetAttributes(descriptor, FileAttributes.Normal);
        File.WriteAllText(descriptor, "syntax = \"proto3\"; // modified\n");

        var analysis = new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false);

        Assert.True(analysis.HasBlockers);
        Assert.Contains(analysis.Diagnostics, diagnostic =>
            diagnostic.Code == "system-import.modified"
            && diagnostic.Message.Contains(
                "Import chain: root.proto -> exceldb/options.proto -> google/protobuf/descriptor.proto",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Reserved_or_manifest_change_after_snapshot_blocks_all_repair_mutations()
    {
        using var project = TestProject.Create();
        var catalog = TestCatalog.Create();
        var unknown = Path.Combine(project.Context.SchemaDirectory, "google", "protobuf", "late.proto");
        var mirror = new SystemProtoMirror(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(unknown)!);
            File.WriteAllText(unknown, "syntax = \"proto3\";\n");
        });

        var analysis = mirror.Analyze(project.Context, catalog, explicitRepair: false);

        Assert.True(analysis.HasBlockers);
        Assert.Empty(analysis.Mutations);
        Assert.Contains(analysis.Diagnostics, diagnostic =>
            diagnostic.Code == "system-import.changed-during-analysis");
        Assert.NotNull(analysis.ReservedInputSet);
        Assert.NotNull(analysis.ManifestObservation);
    }

    [Fact]
    public void ExplicitRepair_AnalyzeToObserveGapBecomesBlockerPlanInsteadOfEscaping()
    {
        using var project = TestProject.Create();
        var catalog = TestCatalog.Create();
        Apply(new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false));
        var analysis = new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: true);
        var late = Path.Combine(project.Context.SchemaDirectory, "google", "protobuf", "late.proto");
        File.WriteAllText(late, "syntax = \"proto3\";\n");
        var builder = new MutationPlanBuilder(
            "project-repair-imports",
            "test",
            project.Context.RootDirectory,
            project.Context.RootDirectory,
            "config",
            catalog.CatalogHash,
            schemaHash: 0);

        var exception = Record.Exception(() =>
        {
            builder.ObserveSystemProtoMirrorSet(analysis.ReservedInputSet!);
            builder.Observe(analysis.ManifestObservation!);
        });
        var plan = builder.Build();

        Assert.Null(exception);
        Assert.True(plan.HasBlockers);
        Assert.Contains(plan.Diagnostics, diagnostic => diagnostic.Code == "plan.stale-input-set");
    }

    [Fact]
    public void Ownership_hash_mismatch_is_blocked_until_explicit_manifest_repair()
    {
        using var project = TestProject.Create();
        var catalog = TestCatalog.Create();
        Apply(new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false));
        var manifestPath = project.Context.SystemImportsManifestPath;
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var target = manifest["files"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(static entry => entry["path"]!.GetValue<string>() == "exceldb/options.proto");
        target["sha256"] = new string('0', 64);
        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));

        var ordinary = new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false);
        var repair = new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: true);

        Assert.True(ordinary.HasBlockers);
        Assert.Contains(ordinary.Diagnostics, diagnostic => diagnostic.Code == "system-import.ownership-mismatch");
        Assert.False(repair.HasBlockers);
        Assert.Contains(repair.Diagnostics, diagnostic => diagnostic.Code == "system-import.ownership-repair");
        Assert.Contains(repair.Mutations, mutation => mutation.AbsolutePath == manifestPath);

        Apply(repair);
        var current = new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false);
        Assert.Equal(SystemProtoMirrorStatus.Current, current.Status);
        Assert.False(current.HasBlockers);
        Assert.Empty(current.Mutations);
    }

    [Fact]
    public void Unknown_reserved_file_is_always_a_blocker()
    {
        using var project = TestProject.Create();
        var catalog = TestCatalog.Create();
        Apply(new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false));
        var unknown = Path.Combine(project.Context.SchemaDirectory, "google", "protobuf", "unknown.proto");
        File.WriteAllText(unknown, "syntax = \"proto3\";\n");

        var analysis = new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: true);

        Assert.True(analysis.HasBlockers);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "system-import.unknown-reserved");
    }

    [Fact]
    public void Nested_reparse_point_in_reserved_mirror_is_a_blocker_when_supported()
    {
        using var project = TestProject.Create();
        var catalog = TestCatalog.Create();
        Apply(new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: false));
        var outside = Path.Combine(Path.GetTempPath(), $"exceldb-mirror-outside-{Guid.NewGuid():N}");
        var link = Path.Combine(project.Context.SchemaDirectory, "google", "protobuf", "nested");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside.proto"), "syntax = \"proto3\";\n");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                               or PlatformNotSupportedException
                                               or IOException)
            {
                return;
            }

            var analysis = new SystemProtoMirror().Analyze(project.Context, catalog, explicitRepair: true);

            Assert.True(analysis.HasBlockers);
            Assert.Contains(analysis.Diagnostics, diagnostic =>
                diagnostic.Code == "system-import.enumerate"
                && diagnostic.Message.Contains("reparse point", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            if (Directory.Exists(outside))
                Directory.Delete(outside, recursive: true);
        }
    }

    private static void Apply(SystemProtoMirrorAnalysis analysis)
    {
        Assert.False(analysis.HasBlockers);
        foreach (var mutation in analysis.Mutations)
        {
            if (mutation.Kind == SystemProtoMirrorMutationKind.DeleteFile)
            {
                File.Delete(mutation.AbsolutePath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(mutation.AbsolutePath)!);
            File.WriteAllBytes(mutation.AbsolutePath, mutation.Content!);
        }
    }

    private sealed class TestCatalog : ISystemProtoCatalog
    {
        private readonly IReadOnlyDictionary<string, SystemProtoFile> _files;

        private TestCatalog(IEnumerable<SystemProtoFile> files)
        {
            Files = files.OrderBy(static file => file.LogicalPath, StringComparer.Ordinal).ToArray();
            _files = Files.ToDictionary(static file => file.LogicalPath, StringComparer.Ordinal);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in Files)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(file.LogicalPath));
                hash.AppendData([0]);
                hash.AppendData(Encoding.ASCII.GetBytes(file.Sha256));
                hash.AppendData([(byte)'\n']);
            }
            CatalogHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        public IReadOnlyList<SystemProtoFile> Files { get; }

        public string CatalogHash { get; }

        public bool TryGetFile(string logicalPath, [NotNullWhen(true)] out SystemProtoFile? file) =>
            _files.TryGetValue(logicalPath, out file);

        public static TestCatalog Create() => new(
        [
            new SystemProtoFile(
                "exceldb/options.proto",
                Encoding.UTF8.GetBytes("syntax = \"proto3\";\nimport \"google/protobuf/descriptor.proto\";\n")),
            new SystemProtoFile("google/protobuf/descriptor.proto", Encoding.UTF8.GetBytes("syntax = \"proto3\";\n")),
        ]);
    }

    private sealed class TestProject : IDisposable
    {
        private TestProject(string root, ProjectContext context)
        {
            Root = root;
            Context = context;
        }

        public string Root { get; }

        public ProjectContext Context { get; }

        public static TestProject Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"exceldb-mirror-{Guid.NewGuid():N}");
            var projectPath = Path.Combine(root, ExcelDbProject.RelativeFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
            File.WriteAllBytes(projectPath, ExcelDbProject.Default.ToCanonicalJson());
            Directory.CreateDirectory(Path.Combine(root, "Schema"));
            return new TestProject(root, ExcelDbProject.Load(projectPath).Resolve(projectPath));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
