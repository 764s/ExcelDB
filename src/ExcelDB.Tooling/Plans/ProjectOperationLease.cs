using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ExcelDb.Tooling.Plans;

/// <summary>
/// A project-scoped, cross-process lease shared by recovery and mutation writers.
/// The lease is a FileShare.None handle, so it is not thread-affine and can safely span awaits.
/// Callers use the explicit UnderLease cores instead of recursively acquiring it.
/// </summary>
public sealed class ProjectOperationLease : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private const int RetryMilliseconds = 40;
    private List<FileStream>? _streams;

    private ProjectOperationLease(List<FileStream> streams) => _streams = streams;

    public static ProjectOperationLease Acquire(string projectRoot) =>
        AcquireMany([projectRoot], DefaultTimeout);

    public static ProjectOperationLease Acquire(string projectRoot, TimeSpan timeout)
        => AcquireMany([projectRoot], timeout);

    public static ProjectOperationLease AcquireMany(IEnumerable<string> declaredRoots) =>
        AcquireMany(declaredRoots, DefaultTimeout);

    public static ProjectOperationLease AcquireMany(IEnumerable<string> declaredRoots, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(declaredRoots);
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var roots = declaredRoots
            .Select(PathFacts.CanonicalRoot)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(static root => OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root, StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0)
            throw new ArgumentException("At least one declared root is required.", nameof(declaredRoots));

        var leaseDirectory = GetLeaseDirectory();
        Directory.CreateDirectory(leaseDirectory);
        var stopwatch = Stopwatch.StartNew();
        var streams = new List<FileStream>(roots.Length);
        try
        {
            foreach (var root in roots)
            {
                var leasePath = Path.Combine(leaseDirectory, BuildLeaseFileName(root));
                IOException? lastContention = null;
                while (true)
                {
                    try
                    {
                        streams.Add(new FileStream(
                            leasePath,
                            FileMode.OpenOrCreate,
                            FileAccess.ReadWrite,
                            FileShare.None,
                            bufferSize: 1,
                            FileOptions.None));
                        lastContention = null;
                        break;
                    }
                    catch (IOException exception)
                    {
                        lastContention = exception;
                        if (timeout != Timeout.InfiniteTimeSpan && stopwatch.Elapsed >= timeout)
                            break;
                        var remaining = timeout == Timeout.InfiniteTimeSpan
                            ? RetryMilliseconds
                            : Math.Min(RetryMilliseconds, Math.Max(1, (int)(timeout - stopwatch.Elapsed).TotalMilliseconds));
                        Thread.Sleep(remaining);
                    }
                }

                if (lastContention is not null)
                {
                    throw new ProjectBusyException(
                        $"Declared root '{root}' is busy in another ExcelDB operation; retry after it completes.",
                        lastContention);
                }
            }

            return new ProjectOperationLease(streams);
        }
        catch
        {
            DisposeStreams(streams);
            throw;
        }
    }

    public void Dispose()
    {
        var streams = Interlocked.Exchange(ref _streams, null);
        if (streams is not null)
            DisposeStreams(streams);
    }

    private static string GetLeaseDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            local = Path.GetTempPath();
        return Path.Combine(local, "ExcelDB", "leases");
    }

    private static string BuildLeaseFileName(string canonicalRoot)
    {
        var identity = OperatingSystem.IsWindows()
            ? canonicalRoot.ToUpperInvariant()
            : canonicalRoot;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return hash + ".lease";
    }

    private static void DisposeStreams(List<FileStream> streams)
    {
        for (var index = streams.Count - 1; index >= 0; index--)
            streams[index].Dispose();
    }
}

public sealed class ProjectBusyException : IOException
{
    public ProjectBusyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
