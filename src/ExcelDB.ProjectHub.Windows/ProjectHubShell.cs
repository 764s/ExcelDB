using System.Diagnostics;

namespace ExcelDb.ProjectHub.Windows;

public interface IProjectHubShell
{
    void Open(string path);
}

public sealed class WindowsProjectHubShell : IProjectHubShell
{
    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("The requested path does not exist.", path);

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }
}
