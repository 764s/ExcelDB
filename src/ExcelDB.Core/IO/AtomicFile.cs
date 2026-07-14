using ExcelDb.Core.Compatibility;

namespace ExcelDb.Core.IO;

public static class AtomicFile
{
    public static void WriteAllBytes(string destinationPath, ReadOnlySpan<byte> content)
    {
        Guard.NotNullOrWhiteSpace(destinationPath, nameof(destinationPath));
        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Destination has no parent directory: '{fullPath}'.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

#if NETSTANDARD2_1
            if (File.Exists(fullPath))
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, fullPath);
#else
            File.Move(temporaryPath, fullPath, overwrite: true);
#endif
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
