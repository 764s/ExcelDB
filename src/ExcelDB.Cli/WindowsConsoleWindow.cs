using System.Runtime.InteropServices;

namespace ExcelDb.Cli;

internal static partial class WindowsConsoleWindow
{
    /// <summary>
    /// A double-clicked console executable owns a one-process console. Detach only
    /// in that case; a shared cmd/PowerShell console must remain attached.
    /// </summary>
    public static void DetachIfOwned()
    {
        if (!OperatingSystem.IsWindows() || GetConsoleWindow() == IntPtr.Zero)
            return;
        var processes = new uint[2];
        if (GetConsoleProcessList(processes, (uint)processes.Length) == 1)
            _ = FreeConsole();
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();
}
