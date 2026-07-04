using Avalonia;
using SkillEditor.Avalonia.ViewModels;

namespace SkillEditor.Avalonia;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--smoke")
        {
            RunSmokeTest();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    static void RunSmokeTest()
    {
        var path = Path.Combine(Path.GetTempPath(), "skill-editor-avalonia-smoke-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            var document = new BehaviorTreeDocument { WorkbookPath = path };
            document.CreateSampleWorkbook();
            document.Validate();
            document.Save();
            Console.WriteLine("Smoke OK: " + path);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
