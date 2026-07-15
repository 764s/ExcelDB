using ExcelDb.Tooling.Project;
using Xunit;

namespace ExcelDb.Tooling.Tests;

public sealed class ProjectOwnedAttributesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "exceldb-owned-attributes-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Repair_does_not_follow_a_system_mirror_reparse_point_when_supported()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var projectFile = Path.Combine(_root, ExcelDbProject.RelativeFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
        Directory.CreateDirectory(Path.Combine(_root, "Schema"));
        File.WriteAllBytes(projectFile, ExcelDbProject.Default.ToCanonicalJson());
        var context = ExcelDbProject.Load(projectFile).Resolve(projectFile);

        var outside = Path.Combine(_root, "outside");
        var outsideProtobuf = Path.Combine(outside, "protobuf");
        Directory.CreateDirectory(outsideProtobuf);
        var outsideProto = Path.Combine(outsideProtobuf, "descriptor.proto");
        File.WriteAllText(outsideProto, "syntax = \"proto3\";\n");
        var googleLink = Path.Combine(context.SchemaDirectory, "google");
        try
        {
            Directory.CreateSymbolicLink(googleLink, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                           or PlatformNotSupportedException
                                           or IOException)
        {
            return;
        }

        var diagnostics = ProjectOwnedAttributes.Repair(
            context,
            ["google/protobuf/descriptor.proto"]);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Code == "project.attribute-warning"
            && diagnostic.Message.Contains("reparse point", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.GetAttributes(outsideProto).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Repair_hides_the_google_mirror_parent_and_preserves_nested_attributes()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var projectFile = Path.Combine(_root, ExcelDbProject.RelativeFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(projectFile)!);
        var google = Path.Combine(_root, "Schema", "google");
        var protobuf = Path.Combine(google, "protobuf");
        Directory.CreateDirectory(protobuf);
        File.WriteAllText(Path.Combine(google, "business.proto"), "syntax = \"proto3\";\n");
        var descriptor = Path.Combine(protobuf, "descriptor.proto");
        File.WriteAllText(descriptor, "syntax = \"proto3\";\n");
        File.WriteAllBytes(projectFile, ExcelDbProject.Default.ToCanonicalJson());
        var context = ExcelDbProject.Load(projectFile).Resolve(projectFile);

        ProjectOwnedAttributes.Repair(context, ["google/protobuf/descriptor.proto"]);

        Assert.True(File.GetAttributes(google).HasFlag(FileAttributes.Hidden));
        Assert.True(File.GetAttributes(protobuf).HasFlag(FileAttributes.Hidden));
        Assert.True(File.GetAttributes(descriptor).HasFlag(FileAttributes.ReadOnly));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        var googleLink = Path.Combine(_root, "Schema", "google");
        if (Directory.Exists(googleLink)
            && File.GetAttributes(googleLink).HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(googleLink);
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                File.SetAttributes(path, FileAttributes.Normal);
        }
        Directory.Delete(_root, recursive: true);
    }
}
