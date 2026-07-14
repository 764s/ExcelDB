namespace ExcelDb.Cli;

public interface IToolConsole
{
    bool IsInteractive { get; }

    void Write(string text);

    void WriteLine(string text = "");

    string? ReadLine();
}

internal sealed class SystemToolConsole : IToolConsole
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public void Write(string text) => Console.Write(text);

    public void WriteLine(string text = "") => Console.WriteLine(text);

    public string? ReadLine() => Console.ReadLine();
}
