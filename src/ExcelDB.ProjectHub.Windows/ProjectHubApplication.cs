using ExcelDb.Pipeline;

namespace ExcelDb.ProjectHub.Windows;

public static class ProjectHubApplication
{
    /// <summary>Runs the native Windows Project Hub on the calling STA thread.</summary>
    public static void Run(IExcelDbProjectService service, string initialDirectory)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(initialDirectory);
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("The ExcelDB Project Hub must run on an STA thread.");

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ProjectHubForm(service, initialDirectory));
    }
}
