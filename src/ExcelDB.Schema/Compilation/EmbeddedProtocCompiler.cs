using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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

    private static readonly MessageParser<FileDescriptorSet> DescriptorSetParser =
        CreateDescriptorSetParser();

    private static readonly Regex ImportStatementPattern = new(
        "^\\s*import\\s+(?:(?:public|weak)\\s+)?[\"'](?<path>[^\"'\\r\\n]+)[\"']\\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private readonly string _protocPath;
    private readonly Lazy<ISystemProtoCatalog> _systemProtoCatalog;

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
        : this(packageRoot, systemProtoCatalog: null)
    {
    }

    /// <summary>
    /// Uses the default package-local protoc with one explicitly supplied canonical
    /// system catalog. This is useful for composition roots and isolated tests.
    /// </summary>
    public EmbeddedProtocCompiler(ISystemProtoCatalog systemProtoCatalog)
        : this(AppContext.BaseDirectory, systemProtoCatalog)
    {
    }

    /// <summary>Uses one explicit protoc package root and canonical system catalog.</summary>
    public EmbeddedProtocCompiler(
        string packageRoot,
        ISystemProtoCatalog? systemProtoCatalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        if (!Path.IsPathFullyQualified(packageRoot))
        {
            throw new ArgumentException("The embedded schema-tool package root must be absolute.", nameof(packageRoot));
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        _protocPath = Path.Combine(normalizedRoot, "schema-tools", "windows-x64", "protoc.exe");
        _systemProtoCatalog = systemProtoCatalog is null
            ? new Lazy<ISystemProtoCatalog>(
                () => new PackageSystemProtoCatalog(normalizedRoot),
                LazyThreadSafetyMode.ExecutionAndPublication)
            : new Lazy<ISystemProtoCatalog>(
                () => systemProtoCatalog,
                LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The exact catalog used both for source filtering and protoc imports.</summary>
    public ISystemProtoCatalog SystemProtoCatalog
    {
        get
        {
            try
            {
                return _systemProtoCatalog.Value;
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or InvalidDataException
                                               or ArgumentException)
            {
                throw ProtocCompilationException.InvocationFailed(
                    "The executable system proto catalog is missing or could not be loaded.",
                    exception);
            }
        }
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
        var systemProtoCatalog = SystemProtoCatalog;
        ValidatePackagePayload(systemProtoCatalog);

        var operationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"exceldb-schema-{Guid.NewGuid():N}");
        var stagedSourceDirectory = Path.Combine(operationDirectory, "source");
        var stagedSystemImportsDirectory = Path.Combine(operationDirectory, "system");
        var descriptorOutputPath = Path.Combine(operationDirectory, "schema.descriptor.pb");

        try
        {
            await StageSourceSetAsync(sourceSet, stagedSourceDirectory, cancellationToken).ConfigureAwait(false);
            await StageSystemProtoCatalogAsync(
                    systemProtoCatalog,
                    stagedSystemImportsDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            using var process = new Process
            {
                StartInfo = CreateStartInfo(
                    sourceSet,
                    stagedSourceDirectory,
                    stagedSystemImportsDirectory,
                    descriptorOutputPath),
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
                var diagnostic = StabilizeDiagnostics(
                    standardOutput,
                    standardError,
                    sourceSet.RootDirectory,
                    stagedSourceDirectory,
                    stagedSystemImportsDirectory,
                    descriptorOutputPath);
                throw ProtocCompilationException.ProcessFailed(
                    process.ExitCode,
                    standardOutput,
                    standardError,
                    AppendMissingImportChains(
                        diagnostic,
                        sourceSet,
                        systemProtoCatalog));
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
        string stagedSystemImportsDirectory,
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
        startInfo.ArgumentList.Add(stagedSystemImportsDirectory);
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

    private void ValidatePackagePayload(ISystemProtoCatalog systemProtoCatalog)
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

        if (systemProtoCatalog.Files.Count == 0)
        {
            throw ProtocCompilationException.InvocationFailed(
                "The executable system proto catalog is empty.");
        }

        if (!systemProtoCatalog.TryGetFile("exceldb/options.proto", out _)
            || !systemProtoCatalog.TryGetFile("google/protobuf/descriptor.proto", out _))
        {
            throw ProtocCompilationException.InvocationFailed(
                "The executable system proto catalog is incomplete.");
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
        string stagedSystemImportsDirectory,
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
            .Replace(stagedSystemImportsDirectory, "<schema-system>", StringComparison.OrdinalIgnoreCase)
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

    private static async Task StageSystemProtoCatalogAsync(
        ISystemProtoCatalog systemProtoCatalog,
        string stagedSystemImportsDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagedSystemImportsDirectory);
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagedSystemImportsDirectory))
            + Path.DirectorySeparatorChar;

        foreach (var systemProto in systemProtoCatalog.Files
                     .OrderBy(static file => file.LogicalPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = systemProto.LogicalPath.Replace('/', Path.DirectorySeparatorChar);
            var destinationPath = Path.GetFullPath(Path.Combine(stagedSystemImportsDirectory, relativePath));
            if (!destinationPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Staged system proto path escapes its root: '{systemProto.LogicalPath}'.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(
                    destinationPath,
                    systemProto.CanonicalBytes.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string AppendMissingImportChains(
        string diagnostic,
        SchemaSourceSet sourceSet,
        ISystemProtoCatalog systemProtoCatalog)
    {
        var sourcePaths = sourceSet.Files
            .Select(static file => file.LogicalPath)
            .ToHashSet(StringComparer.Ordinal);
        var availablePaths = sourcePaths
            .Concat(systemProtoCatalog.Files.Select(static file => file.LogicalPath))
            .ToHashSet(StringComparer.Ordinal);
        var importsBySource = sourceSet.Files.ToDictionary(
            static file => file.LogicalPath,
            static file => ParseImports(file.Content.Span),
            StringComparer.Ordinal);
        foreach (var systemProto in systemProtoCatalog.Files)
            importsBySource[systemProto.LogicalPath] = ParseImports(systemProto.CanonicalBytes.Span);
        var missingImports = importsBySource.Values
            .SelectMany(static imports => imports)
            .Where(imported => !availablePaths.Contains(imported))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static imported => imported, StringComparer.Ordinal)
            .ToArray();
        if (missingImports.Length == 0)
            return diagnostic;

        var chains = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var missingImport in missingImports)
        {
            foreach (var sourcePath in sourcePaths.Order(StringComparer.Ordinal))
            {
                var path = new List<string>();
                if (TryFindImportChain(
                        sourcePath,
                        missingImport,
                        importsBySource,
                        new HashSet<string>(StringComparer.Ordinal),
                        path))
                {
                    chains.Add($"Import chain: {string.Join(" -> ", path)}");
                }
            }
        }

        if (chains.Count == 0)
            return diagnostic;

        return string.IsNullOrEmpty(diagnostic)
            ? string.Join('\n', chains)
            : $"{diagnostic}\n{string.Join('\n', chains)}";
    }

    private static bool TryFindImportChain(
        string currentPath,
        string missingImport,
        IReadOnlyDictionary<string, IReadOnlyList<string>> importsBySource,
        ISet<string> visiting,
        ICollection<string> result)
    {
        if (!visiting.Add(currentPath)
            || !importsBySource.TryGetValue(currentPath, out var imports))
        {
            return false;
        }

        try
        {
            foreach (var imported in imports.Order(StringComparer.Ordinal))
            {
                if (string.Equals(imported, missingImport, StringComparison.Ordinal))
                {
                    result.Add(currentPath);
                    result.Add(missingImport);
                    return true;
                }

                var childPath = new List<string>();
                if (TryFindImportChain(
                        imported,
                        missingImport,
                        importsBySource,
                        visiting,
                        childPath))
                {
                    result.Add(currentPath);
                    foreach (var path in childPath)
                        result.Add(path);
                    return true;
                }
            }

            return false;
        }
        finally
        {
            visiting.Remove(currentPath);
        }
    }

    private static IReadOnlyList<string> ParseImports(ReadOnlySpan<byte> sourceBytes)
    {
        var source = StripProtoComments(Encoding.UTF8.GetString(sourceBytes));
        return ImportStatementPattern.Matches(source)
            .Select(static match => match.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string StripProtoComments(string source)
    {
        var builder = new StringBuilder(source.Length);
        var inString = false;
        var stringDelimiter = '\0';
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (inString)
            {
                builder.Append(character);
                if (character == '\\' && index + 1 < source.Length)
                {
                    builder.Append(source[++index]);
                    continue;
                }

                if (character == stringDelimiter)
                    inString = false;
                continue;
            }

            if (character is '\"' or '\'')
            {
                inString = true;
                stringDelimiter = character;
                builder.Append(character);
                continue;
            }

            if (character == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index += 2;
                while (index < source.Length && source[index] is not ('\r' or '\n'))
                    index++;
                if (index < source.Length)
                    builder.Append(source[index]);
                continue;
            }

            if (character == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length
                       && !(source[index] == '*' && source[index + 1] == '/'))
                {
                    if (source[index] is '\r' or '\n')
                        builder.Append(source[index]);
                    index++;
                }

                if (index + 1 < source.Length)
                    index++;
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
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
