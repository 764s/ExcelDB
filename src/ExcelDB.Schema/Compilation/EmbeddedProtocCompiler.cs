using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ExcelDb.Protocol;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ExcelDb.Schema.Compilation;

/// <summary>
/// Invokes only the protoc and system imports shipped beside the current package.
/// </summary>
public sealed class EmbeddedProtocCompiler
{
    private const string ProtocRelativePath = "schema-tools/windows-x64/protoc.exe";
    private const string SystemImportsRelativePath = "schema-system";

    private static readonly MessageParser<FileDescriptorSet> DescriptorSetParser =
        CreateDescriptorSetParser();

    private readonly string _protocPath;
    private readonly string _systemImportsDirectory;

    /// <summary>Uses the package rooted at <see cref="AppContext.BaseDirectory"/>.</summary>
    public EmbeddedProtocCompiler()
        : this(AppContext.BaseDirectory)
    {
    }

    /// <summary>
    /// Uses an explicitly supplied package root. This overload exists for isolated
    /// tests and requires an absolute path so tool lookup never depends on the CWD.
    /// </summary>
    public EmbeddedProtocCompiler(string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        if (!Path.IsPathFullyQualified(packageRoot))
        {
            throw new ArgumentException("The embedded schema-tool package root must be absolute.", nameof(packageRoot));
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        _protocPath = Path.Combine(normalizedRoot, "schema-tools", "windows-x64", "protoc.exe");
        _systemImportsDirectory = Path.Combine(normalizedRoot, SystemImportsRelativePath);
    }

    /// <summary>
    /// Compiles the supplied source snapshot to a descriptor set with imports,
    /// source locations, and ExcelDB custom options fully parsed.
    /// </summary>
    public async Task<ProtocCompilationResult> CompileAsync(
        SchemaSourceSet sourceSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePackagePayload();

        var operationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"exceldb-schema-{Guid.NewGuid():N}");
        var stagedSourceDirectory = Path.Combine(operationDirectory, "source");
        var descriptorOutputPath = Path.Combine(operationDirectory, "schema.descriptor.pb");

        try
        {
            await StageSourceSetAsync(sourceSet, stagedSourceDirectory, cancellationToken).ConfigureAwait(false);
            using var process = new Process
            {
                StartInfo = CreateStartInfo(sourceSet, stagedSourceDirectory, descriptorOutputPath),
            };

            try
            {
                if (!process.Start())
                {
                    throw ProtocCompilationException.InvocationFailed(
                        "The package-local protoc process did not start.");
                }
            }
            catch (ProtocCompilationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                throw ProtocCompilationException.InvocationFailed(
                    "The package-local protoc process could not be started.",
                    exception);
            }

            var standardOutputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
                throw;
            }

            var standardOutput = await standardOutputTask.ConfigureAwait(false);
            var standardError = await standardErrorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw ProtocCompilationException.ProcessFailed(
                    process.ExitCode,
                    standardOutput,
                    standardError,
                    StabilizeDiagnostics(
                        standardOutput,
                        standardError,
                        sourceSet.RootDirectory,
                        stagedSourceDirectory,
                        descriptorOutputPath));
            }

            if (!File.Exists(descriptorOutputPath))
            {
                throw ProtocCompilationException.InvocationFailed(
                    "The package-local protoc process succeeded without producing a descriptor set.",
                    exitCode: process.ExitCode,
                    standardOutput: standardOutput,
                    standardError: standardError);
            }

            var descriptorBytes = await File.ReadAllBytesAsync(
                    descriptorOutputPath,
                    cancellationToken)
                .ConfigureAwait(false);

            FileDescriptorSet descriptorSet;
            try
            {
                descriptorSet = DescriptorSetParser.ParseFrom(descriptorBytes);
            }
            catch (InvalidProtocolBufferException exception)
            {
                throw ProtocCompilationException.InvalidDescriptor(
                    process.ExitCode,
                    standardOutput,
                    standardError,
                    exception);
            }

            return new ProtocCompilationResult(
                descriptorSet,
                descriptorBytes,
                process.ExitCode,
                standardOutput,
                standardError);
        }
        finally
        {
            TryDeleteDirectory(operationDirectory);
        }
    }

    private ProcessStartInfo CreateStartInfo(
        SchemaSourceSet sourceSet,
        string stagedSourceDirectory,
        string descriptorOutputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _protocPath,
            WorkingDirectory = stagedSourceDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add(_systemImportsDirectory);
        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add(stagedSourceDirectory);
        startInfo.ArgumentList.Add("--include_imports");
        startInfo.ArgumentList.Add("--include_source_info");
        startInfo.ArgumentList.Add($"--descriptor_set_out={descriptorOutputPath}");

        foreach (var source in sourceSet.Files)
        {
            startInfo.ArgumentList.Add(source.LogicalPath);
        }

        return startInfo;
    }

    private void ValidatePackagePayload()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "The current formal M1 toolchain slice is packaged only for windows-x64.");
        }

        if (!File.Exists(_protocPath))
        {
            throw ProtocCompilationException.InvocationFailed(
                $"Embedded protoc is missing at package path '{ProtocRelativePath}'.");
        }

        if (!Directory.Exists(_systemImportsDirectory))
        {
            throw ProtocCompilationException.InvocationFailed(
                $"Embedded schema imports are missing at package path '{SystemImportsRelativePath}'.");
        }
    }

    private static MessageParser<FileDescriptorSet> CreateDescriptorSetParser()
    {
        var extensions = new ExtensionRegistry
        {
            OptionsExtensions.Table,
            OptionsExtensions.Field,
            OptionsExtensions.EnumValue,
            OptionsExtensions.Defaults,
        };

        return FileDescriptorSet.Parser.WithExtensionRegistry(extensions);
    }

    private string StabilizeDiagnostics(
        string standardOutput,
        string standardError,
        string schemaRoot,
        string stagedSourceDirectory,
        string descriptorOutputPath)
    {
        static string NormalizeNewLines(string value) =>
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

        var combined = string.IsNullOrWhiteSpace(standardError)
            ? standardOutput
            : string.IsNullOrWhiteSpace(standardOutput)
                ? standardError
                : $"{standardError.TrimEnd()}\n{standardOutput}";

        return NormalizeNewLines(combined)
            .Replace(descriptorOutputPath, "<descriptor-out>", StringComparison.OrdinalIgnoreCase)
            .Replace(stagedSourceDirectory, "<schema-root>", StringComparison.OrdinalIgnoreCase)
            .Replace(_systemImportsDirectory, "<schema-system>", StringComparison.OrdinalIgnoreCase)
            .Replace(schemaRoot, "<schema-root>", StringComparison.OrdinalIgnoreCase)
            .Replace(_protocPath, "<protoc>", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Cancellation remains the primary outcome; disposal is the fallback.
        }
    }

    private static async Task StageSourceSetAsync(
        SchemaSourceSet sourceSet,
        string stagedSourceDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagedSourceDirectory);
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedSourceDirectory))
            + Path.DirectorySeparatorChar;

        foreach (var source in sourceSet.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = source.LogicalPath.Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = Path.GetFullPath(Path.Combine(stagedSourceDirectory, relativePath));
            if (!destinationPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Staged schema path escapes its root: '{source.LogicalPath}'.");

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, source.Content.ToArray(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A successful package-local protoc invocation.</summary>
public sealed class ProtocCompilationResult
{
    internal ProtocCompilationResult(
        FileDescriptorSet descriptorSet,
        byte[] descriptorBytes,
        int exitCode,
        string standardOutput,
        string standardError)
    {
        DescriptorSet = descriptorSet;
        DescriptorBytes = descriptorBytes;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public FileDescriptorSet DescriptorSet { get; }

    public ReadOnlyMemory<byte> DescriptorBytes { get; }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }
}

/// <summary>A stable failure boundary for invoking or reading package-local protoc.</summary>
public sealed class ProtocCompilationException : Exception
{
    private ProtocCompilationException(
        string message,
        int? exitCode,
        string standardOutput,
        string standardError,
        string diagnostic,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        Diagnostic = diagnostic;
    }

    public int? ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    /// <summary>Normalized protoc output suitable for deterministic reports.</summary>
    public string Diagnostic { get; }

    internal static ProtocCompilationException InvocationFailed(
        string message,
        Exception? innerException = null,
        int? exitCode = null,
        string standardOutput = "",
        string standardError = "") =>
        new(
            message,
            exitCode,
            standardOutput,
            standardError,
            string.Empty,
            innerException);

    internal static ProtocCompilationException ProcessFailed(
        int exitCode,
        string standardOutput,
        string standardError,
        string diagnostic)
    {
        var message = string.IsNullOrEmpty(diagnostic)
            ? $"Embedded protoc failed with exit code {exitCode}."
            : $"Embedded protoc failed with exit code {exitCode}.\n{diagnostic}";

        return new ProtocCompilationException(
            message,
            exitCode,
            standardOutput,
            standardError,
            diagnostic);
    }

    internal static ProtocCompilationException InvalidDescriptor(
        int exitCode,
        string standardOutput,
        string standardError,
        Exception innerException) =>
        new(
            "Embedded protoc produced an invalid descriptor set.",
            exitCode,
            standardOutput,
            standardError,
            string.Empty,
            innerException);
}
