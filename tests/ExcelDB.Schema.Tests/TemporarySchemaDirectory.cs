using System.Text;

namespace ExcelDB.Schema.Tests;

internal sealed class TemporarySchemaDirectory : IDisposable
{
    private TemporarySchemaDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporarySchemaDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"exceldb-schema-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return new TemporarySchemaDirectory(path);
    }

    public void Write(string logicalPath, string contents)
    {
        var fullPath = System.IO.Path.Combine(Path, logicalPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var directory = System.IO.Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(fullPath, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
