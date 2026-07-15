using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace ExcelDb.Tooling.Plans;

internal static class PathFacts
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static PathObservation Observe(
        string declaredRoot,
        string relativePath,
        PlanRootKind rootKind = PlanRootKind.Project)
    {
        var fullPath = ResolveContained(declaredRoot, relativePath);
        if (File.Exists(fullPath))
        {
            using var stream = File.OpenRead(fullPath);
            return new PathObservation(
                Normalize(relativePath),
                ObservedPathKind.File,
                stream.Length,
                Convert.ToHexStringLower(SHA256.HashData(stream)),
                rootKind);
        }

        return new PathObservation(
            Normalize(relativePath),
            Directory.Exists(fullPath) ? ObservedPathKind.Directory : ObservedPathKind.Missing,
            Root: rootKind);
    }

    public static bool Matches(string declaredRoot, PathObservation observation) =>
        Observe(declaredRoot, observation.RelativePath, observation.Root) == observation;

    public static string ResolveContained(string projectRoot, string relativePath)
    {
        var root = CanonicalRoot(projectRoot);
        var fullPath = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), root);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathFullyQualified(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Mutation path escapes the project root: '{relativePath}'.");
        }

        EnsureNoReparseEscape(root, fullPath, relativePath);
        return fullPath;
    }

    public static bool IsContained(string outerRoot, string candidate)
    {
        var outer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outerRoot));
        var fullCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(outer, fullCandidate);
        return !Path.IsPathFullyQualified(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);
    }

    public static string CanonicalRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || Path.GetPathRoot(root) == root)
            throw new InvalidDataException($"A filesystem root cannot be used as a MutationPlan declared root: '{path}'.");
        EnsureExistingAncestorsNoReparse(root, path);
        return root;
    }

    public static string Normalize(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return string.IsNullOrEmpty(normalized) ? "." : normalized;
    }

    public static void ClearReadOnly(string path)
    {
        if (!File.Exists(path))
            return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void EnsureNoReparseEscape(string root, string fullPath, string displayPath)
    {
        var current = root;
        EnsureNotReparsePoint(current, displayPath);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
            return;

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;
            EnsureNotReparsePoint(current, displayPath);
        }
    }

    private static void EnsureExistingAncestorsNoReparse(string path, string displayPath)
    {
        var filesystemRoot = Path.GetPathRoot(path)
            ?? throw new InvalidDataException($"MutationPlan declared root has no filesystem root: '{displayPath}'.");
        var current = filesystemRoot;
        var relative = Path.GetRelativePath(filesystemRoot, path);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;
            EnsureNotReparsePoint(current, displayPath);
        }
    }

    private static void EnsureNotReparsePoint(string path, string displayPath)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Mutation path crosses a symbolic link, junction, or reparse point: '{displayPath}'.");
        }
    }
}

/// <summary>Captures and verifies the canonical filtered input sets embedded in MutationPlan v2.</summary>
public static class InputSetSnapshot
{
    public static InputSetObservation Capture(
        string declaredRoot,
        string inputRootPath,
        InputSetKind kind,
        PlanRootKind root = PlanRootKind.Project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputRootPath);
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(root))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown input-set or declared-root kind.");

        var canonicalDeclaredRoot = PathFacts.CanonicalRoot(declaredRoot);
        var relativeRoot = Path.IsPathRooted(inputRootPath)
            ? Path.GetRelativePath(canonicalDeclaredRoot, Path.GetFullPath(inputRootPath))
            : inputRootPath;
        relativeRoot = PathFacts.Normalize(relativeRoot);
        var absoluteRoot = PathFacts.ResolveContained(canonicalDeclaredRoot, relativeRoot);
        var rootObservation = PathFacts.Observe(canonicalDeclaredRoot, relativeRoot, root);
        if (rootObservation.Kind != ObservedPathKind.Directory)
        {
            return Create(
                canonicalDeclaredRoot,
                relativeRoot,
                kind,
                rootObservation.Kind,
                rootObservation.Length,
                rootObservation.Sha256,
                [],
                root);
        }

        var entries = kind switch
        {
            InputSetKind.SchemaProto => CaptureSchemaProtoEntries(absoluteRoot),
            InputSetKind.ExcelWorkbook => CaptureExcelWorkbookEntries(absoluteRoot),
            InputSetKind.SystemProtoMirror => CaptureSystemProtoMirrorEntries(absoluteRoot),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown input-set kind."),
        };
        return Create(
            canonicalDeclaredRoot,
            relativeRoot,
            kind,
            ObservedPathKind.Directory,
            0,
            null,
            entries,
            root);
    }

    /// <summary>
    /// Creates an observation from bytes already frozen by a domain reader. This
    /// avoids a second read silently becoming the plan's authority baseline.
    /// </summary>
    public static InputSetObservation Create(
        string declaredRoot,
        string relativeRoot,
        InputSetKind kind,
        ObservedPathKind rootKind,
        long rootLength,
        string? rootSha256,
        IEnumerable<InputSetEntry> entries,
        PlanRootKind root = PlanRootKind.Project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeRoot);
        ArgumentNullException.ThrowIfNull(entries);
        var observation = new InputSetObservation(
            relativeRoot,
            kind,
            rootKind,
            rootLength,
            rootSha256,
            entries.ToImmutableArray(),
            string.Empty,
            root);
        return Canonicalize(PathFacts.CanonicalRoot(declaredRoot), observation);
    }

    public static bool Matches(string declaredRoot, InputSetObservation expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var actual = Capture(
            declaredRoot,
            expected.RelativeRoot,
            expected.Kind,
            expected.Root);
        return Equivalent(actual, expected);
    }

    internal static InputSetObservation Canonicalize(
        string declaredRoot,
        InputSetObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!Enum.IsDefined(observation.Root)
            || !Enum.IsDefined(observation.Kind)
            || !Enum.IsDefined(observation.RootKind))
        {
            throw new InvalidDataException("Input-set observation contains an unknown kind.");
        }

        var canonicalRoot = PathFacts.CanonicalRoot(declaredRoot);
        var absoluteInputRoot = PathFacts.ResolveContained(
            canonicalRoot,
            PathFacts.Normalize(observation.RelativeRoot));
        var relativeRoot = PathFacts.Normalize(Path.GetRelativePath(canonicalRoot, absoluteInputRoot));
        var rootLength = observation.RootLength;
        var rootSha256 = NormalizeHash(observation.RootSha256);
        if (observation.RootKind == ObservedPathKind.File)
        {
            if (rootLength < 0 || rootSha256 is null)
                throw new InvalidDataException("A file input-set root requires a non-negative length and SHA-256.");
        }
        else if (rootLength != 0 || rootSha256 is not null)
        {
            throw new InvalidDataException("A missing or directory input-set root cannot carry file evidence.");
        }

        var canonicalEntries = (observation.Entries.IsDefault
                ? ImmutableArray<InputSetEntry>.Empty
                : observation.Entries)
            .Select(entry => CanonicalizeEntry(absoluteInputRoot, entry))
            .OrderBy(static entry => entry.RelativePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Kind)
            .ThenBy(static entry => entry.Length)
            .ThenBy(static entry => entry.Sha256, StringComparer.Ordinal)
            .ToImmutableArray();
        if (observation.RootKind != ObservedPathKind.Directory && canonicalEntries.Length != 0)
            throw new InvalidDataException("Only a directory input-set root can contain members.");
        if (canonicalEntries.Select(static entry => entry.RelativePath).Distinct(StringComparer.Ordinal).Count()
            != canonicalEntries.Length)
        {
            throw new InvalidDataException("Input-set observation contains duplicate member paths.");
        }

        var digest = ComputeDigest(
            observation.Kind,
            observation.RootKind,
            rootLength,
            rootSha256,
            canonicalEntries);
        return observation with
        {
            RelativeRoot = relativeRoot,
            RootLength = rootLength,
            RootSha256 = rootSha256,
            Entries = canonicalEntries,
            Digest = digest,
        };
    }

    internal static bool Equivalent(InputSetObservation left, InputSetObservation right) =>
        left.Root == right.Root
        && left.Kind == right.Kind
        && left.RootKind == right.RootKind
        && left.RootLength == right.RootLength
        && string.Equals(left.RootSha256, right.RootSha256, StringComparison.Ordinal)
        && string.Equals(PathFacts.Normalize(left.RelativeRoot), PathFacts.Normalize(right.RelativeRoot), StringComparison.Ordinal)
        && string.Equals(left.Digest, right.Digest, StringComparison.Ordinal)
        && left.Entries.AsSpan().SequenceEqual(right.Entries.AsSpan());

    private static InputSetEntry CanonicalizeEntry(string inputRoot, InputSetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Enum.IsDefined(entry.Kind) || entry.Kind != ObservedPathKind.File)
            throw new InvalidDataException("Input-set members must be files.");
        if (entry.Length < 0)
            throw new InvalidDataException("Input-set file length cannot be negative.");
        var absolutePath = PathFacts.ResolveContained(inputRoot, PathFacts.Normalize(entry.RelativePath));
        var relativePath = PathFacts.Normalize(Path.GetRelativePath(inputRoot, absolutePath));
        if (relativePath == ".")
            throw new InvalidDataException("An input-set member cannot be its root.");
        return entry with
        {
            RelativePath = relativePath,
            Sha256 = NormalizeHash(entry.Sha256)
                ?? throw new InvalidDataException($"Input-set file '{relativePath}' has no SHA-256."),
        };
    }

    private static ImmutableArray<InputSetEntry> CaptureSchemaProtoEntries(string root) =>
        CaptureRecursively(
            root,
            (path, name, attributes) =>
                (attributes & FileAttributes.Directory) == 0
                && string.Equals(Path.GetExtension(name), ".proto", StringComparison.OrdinalIgnoreCase)
                && !IsReservedSystemProtoPath(
                    PathFacts.Normalize(Path.GetRelativePath(root, path))),
            skipHiddenAndToolEntries: false);

    private static ImmutableArray<InputSetEntry> CaptureExcelWorkbookEntries(string root) =>
        CaptureRecursively(
            root,
            static (_, name, attributes) =>
                (attributes & (FileAttributes.Directory | FileAttributes.System)) == 0
                && !name.StartsWith("~$", StringComparison.Ordinal)
                && string.Equals(Path.GetExtension(name), ".xlsx", StringComparison.OrdinalIgnoreCase),
            skipHiddenAndToolEntries: true);

    private static ImmutableArray<InputSetEntry> CaptureSystemProtoMirrorEntries(string schemaRoot)
    {
        var entries = ImmutableArray.CreateBuilder<InputSetEntry>();
        foreach (var reservedRoot in new[] { "exceldb", "google/protobuf" })
        {
            var directory = Path.Combine(
                schemaRoot,
                reservedRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(directory))
                continue;
            EnsureNotReparse(directory, schemaRoot);
            entries.AddRange(CaptureRecursively(
                directory,
                static (_, _, attributes) => (attributes & FileAttributes.Directory) == 0,
                skipHiddenAndToolEntries: false,
                relativeRoot: schemaRoot));
        }
        return entries.OrderBy(static entry => entry.RelativePath, StringComparer.Ordinal).ToImmutableArray();
    }

    private static ImmutableArray<InputSetEntry> CaptureRecursively(
        string root,
        Func<string, string, FileAttributes, bool> includeFile,
        bool skipHiddenAndToolEntries,
        string? relativeRoot = null)
    {
        EnsureNotReparse(root, root);
        var entries = ImmutableArray.CreateBuilder<InputSetEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                var name = Path.GetFileName(entry);
                if (skipHiddenAndToolEntries
                    && ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) != 0
                        || name.StartsWith(".", StringComparison.Ordinal)))
                {
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Input-set traversal through a reparse point is not allowed: '{entry}'.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                if (!includeFile(entry, name, attributes))
                    continue;
                entries.Add(ObserveFile(relativeRoot ?? root, entry));
            }
        }
        return entries.OrderBy(static entry => entry.RelativePath, StringComparer.Ordinal).ToImmutableArray();
    }

    private static InputSetEntry ObserveFile(string root, string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        var length = stream.Length;
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new InputSetEntry(
            PathFacts.Normalize(Path.GetRelativePath(root, path)),
            ObservedPathKind.File,
            length,
            hash);
    }

    private static void EnsureNotReparse(string path, string root)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            return;
        throw new InvalidDataException(
            $"Input-set traversal through a reparse point is not allowed: '{Path.GetRelativePath(root, path)}'.");
    }

    private static bool IsReservedSystemProtoPath(string relativePath) =>
        relativePath.StartsWith("exceldb/", StringComparison.OrdinalIgnoreCase)
        || relativePath.StartsWith("google/protobuf/", StringComparison.OrdinalIgnoreCase);

    private static string ComputeDigest(
        InputSetKind kind,
        ObservedPathKind rootKind,
        long rootLength,
        string? rootSha256,
        ImmutableArray<InputSetEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendByte(hash, (byte)kind);
        AppendByte(hash, (byte)rootKind);
        AppendInt64(hash, rootLength);
        AppendString(hash, rootSha256 ?? string.Empty);
        AppendInt64(hash, entries.Length);
        foreach (var entry in entries)
        {
            AppendString(hash, entry.RelativePath);
            AppendByte(hash, (byte)entry.Kind);
            AppendInt64(hash, entry.Length);
            AppendString(hash, entry.Sha256 ?? string.Empty);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendByte(IncrementalHash hash, byte value)
    {
        Span<byte> bytes = stackalloc byte[1];
        bytes[0] = value;
        hash.AppendData(bytes);
    }

    private static string? NormalizeHash(string? value)
    {
        if (value is null)
            return null;
        if (value.Length != 64 || value.Any(static character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Input-set SHA-256 is invalid.");
        return value.ToLowerInvariant();
    }
}

/// <summary>Public read-only verifier for a path observation carried across assembly boundaries.</summary>
public static class PathObservationSnapshot
{
    public static PathObservation Capture(
        string declaredRoot,
        string path,
        PlanRootKind root = PlanRootKind.Project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalRoot = PathFacts.CanonicalRoot(declaredRoot);
        var relative = Path.IsPathRooted(path)
            ? Path.GetRelativePath(canonicalRoot, Path.GetFullPath(path))
            : path;
        return PathFacts.Observe(canonicalRoot, PathFacts.Normalize(relative), root);
    }

    public static bool Matches(string declaredRoot, PathObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredRoot);
        ArgumentNullException.ThrowIfNull(observation);
        return PathFacts.Matches(declaredRoot, observation);
    }
}
