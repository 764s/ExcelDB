namespace ExcelDb.Cli;

public static class Program
{
    [STAThread]
    public static Task<int> Main(string[] args) => ExcelDbToolHost.RunAsync(args);
}
