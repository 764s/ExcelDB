using System.Text;
using ExcelDb.Core.IO;
using ExcelDb.Core.Identity;

namespace ExcelDb.Runtime.Bytes;

public sealed class ConvertedBytesDataSource : IRefreshableDataSource
{
    private readonly string? _bytesPath;
    private readonly string? _manifestPath;
    private readonly byte[]? _content;
    private readonly string? _manifestJson;
    private readonly int _formatVersion;

    public ConvertedBytesDataSource(ReadOnlySpan<byte> content, string? manifestJson = null)
    {
        _content = content.ToArray();
        _manifestJson = manifestJson;
        var result = ConvertedBytesReader.Read(_content, manifestJson);
        using (result.Snapshot)
        {
            SchemaHash = result.Snapshot.SchemaHash;
            ExportTarget = result.Snapshot.ExportTarget;
            _formatVersion = result.Snapshot.FormatVersion;
        }
    }

    public ConvertedBytesDataSource(string bytesPath, string? manifestPath = null)
    {
        RuntimeCompatibility.NotNullOrWhiteSpace(bytesPath, nameof(bytesPath));
        _bytesPath = Path.GetFullPath(bytesPath);
        _manifestPath = manifestPath is null ? null : Path.GetFullPath(manifestPath);
        var result = ReadFile();
        using (result.Snapshot)
        {
            SchemaHash = result.Snapshot.SchemaHash;
            ExportTarget = result.Snapshot.ExportTarget;
            _formatVersion = result.Snapshot.FormatVersion;
        }
    }

    public ulong SchemaHash { get; }

    public ExportTargetId ExportTarget { get; }

    public SourceInfo Inspect()
    {
        if (_bytesPath is null)
        {
            var fingerprint = ContentFingerprint.FromBytes(_content!);
            return new SourceInfo(
                RuntimeSourceKind.ConvertedBytes,
                RuntimeSourceCapabilities.Read,
                _formatVersion,
                fingerprint.ToString(),
                fingerprint.Sha256);
        }

        var fileFingerprint = ContentFingerprint.FromFile(_bytesPath);
        return new SourceInfo(
            RuntimeSourceKind.ConvertedBytes,
            RuntimeSourceCapabilities.Read | RuntimeSourceCapabilities.Refresh,
            _formatVersion,
            fileFingerprint.ToString(),
            fileFingerprint.Sha256);
    }

    public SourceSnapshot Open() => Read().Snapshot;

    public SourceSnapshot Refresh() => Read().Snapshot;

    private ConvertedBytesReadResult Read() => _bytesPath is null
        ? ConvertedBytesReader.Read(_content!, _manifestJson)
        : ReadFile();

    private ConvertedBytesReadResult ReadFile()
    {
        var bytes = File.ReadAllBytes(_bytesPath!);
        string? manifest = null;
        if (_manifestPath is not null)
            manifest = File.ReadAllText(_manifestPath, Encoding.UTF8);
        return ConvertedBytesReader.Read(bytes, manifest);
    }
}
