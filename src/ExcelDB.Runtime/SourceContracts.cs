using ExcelDb.Core.Identity;

namespace ExcelDb.Runtime;

public readonly record struct SourceInfo(
    RuntimeSourceKind Kind,
    RuntimeSourceCapabilities Capabilities,
    int FormatVersion,
    string Revision,
    string ContentHash);

public interface IDataSource
{
    ulong SchemaHash { get; }

    ExportTargetId ExportTarget { get; }

    SourceInfo Inspect();

    SourceSnapshot Open();
}

public interface IRefreshableDataSource : IDataSource
{
    SourceSnapshot Refresh();
}

public interface IWatchableDataSource : IRefreshableDataSource
{
    IDisposable Watch(Action sourceChanged);
}
