using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ExcelDb.Core.Diagnostics;
using ExcelDb.Core.Identity;
using ExcelDb.Core.Values;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;
using ExcelDb.Workbooks.References;

namespace ExcelDb.Runtime.Excel;

/// <summary>
/// Optional authoring/development source. It projects a workbook set to exactly one export target,
/// uses the canonical Workbooks importer, and never exposes a writeback surface.
/// </summary>
public sealed class ExcelDataSource : IWatchableDataSource
{
    private readonly ImmutableArray<string> _workbookPaths;
    private readonly CanonicalSchemaDescriptor _schema;
    private readonly CanonicalSchemaDescriptor _projectedSchema;
    private readonly IExcelWorkbookStore _store;
    private readonly CellFormatRegistry _cellFormats;
    private readonly ImmutableDictionary<int, CanonicalTableDescriptor> _tables;
    private readonly object _preparedSync = new();
    private SourceSnapshot? _preparedSnapshot;
    private Exception? _preparedFailure;
    private long _preparedSequence;

    public ExcelDataSource(
        IEnumerable<string> workbookPaths,
        CanonicalSchemaDescriptor schema,
        ExportTargetId exportTarget,
        IExcelWorkbookStore? store = null,
        CellFormatRegistry? cellFormats = null)
    {
        ArgumentNullException.ThrowIfNull(workbookPaths);
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        if (schema.SchemaHash == 0)
            throw new ArgumentException("The canonical schema hash must not be zero.", nameof(schema));
        if (exportTarget.IsEmpty)
            throw new ArgumentException("An explicit export target is required.", nameof(exportTarget));

        _workbookPaths = workbookPaths
            .Select(path =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                return Path.GetFullPath(path);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (_workbookPaths.IsDefaultOrEmpty)
            throw new ArgumentException("At least one workbook path is required.", nameof(workbookPaths));

        ExportTarget = exportTarget;
        _store = store ?? FileExcelWorkbookStore.Instance;
        _cellFormats = cellFormats ?? new CellFormatRegistry();
        _projectedSchema = ProjectSchema(schema, exportTarget);
        _tables = _projectedSchema.Tables.ToImmutableDictionary(static table => table.Id);
    }

    public ulong SchemaHash => _schema.SchemaHash;

    public ExportTargetId ExportTarget { get; }

    public IReadOnlyList<string> WorkbookPaths => _workbookPaths;

    public SourceInfo Inspect()
    {
        var files = ReadFiles(parse: false);
        var contentHash = ComputeContentHash(files);
        var capabilities = RuntimeSourceCapabilities.Read | RuntimeSourceCapabilities.Refresh;
        if (_store.SupportsWatch)
            capabilities |= RuntimeSourceCapabilities.Watch;
        return new SourceInfo(
            RuntimeSourceKind.Excel,
            capabilities,
            WorkbookProtocol.FormatVersion,
            contentHash,
            contentHash);
    }

    public SourceSnapshot Open() => BuildSnapshot();

    public SourceSnapshot Refresh()
    {
        lock (_preparedSync)
        {
            if (_preparedFailure is { } failure)
            {
                _preparedFailure = null;
                throw new InvalidOperationException("Background Excel candidate preparation failed.", failure);
            }
            if (_preparedSnapshot is { } prepared)
            {
                _preparedSnapshot = null;
                return prepared;
            }
        }

        return BuildSnapshot();
    }

    public IDisposable Watch(Action sourceChanged)
    {
        ArgumentNullException.ThrowIfNull(sourceChanged);
        if (!_store.SupportsWatch)
            throw new NotSupportedException("The configured workbook store does not support watching.");
        var subscription = new BackgroundWatchSubscription(this, sourceChanged);
        subscription.Attach(_store.Watch(_workbookPaths, subscription.Signal));
        return subscription;
    }

    private void PublishPrepared(long sequence, SourceSnapshot? snapshot, Exception? failure)
    {
        lock (_preparedSync)
        {
            if (sequence < _preparedSequence)
            {
                snapshot?.Dispose();
                return;
            }

            _preparedSequence = sequence;
            _preparedSnapshot?.Dispose();
            _preparedSnapshot = snapshot;
            _preparedFailure = failure;
        }
    }

    private SourceSnapshot BuildSnapshot()
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var files = ReadFiles(parse: true, diagnostics);
        var contentHash = ComputeContentHash(files);
        var importedRows = new List<ImportedRow>();
        var workbookIds = new HashSet<Guid>();

        foreach (var file in files.OrderBy(static file => file.Workbook!.WorkbookGuid))
        {
            var workbook = file.Workbook!;
            if (!workbookIds.Add(workbook.WorkbookGuid))
            {
                diagnostics.Add(Blocker(
                    "runtime.excel.duplicate-workbook",
                    file.Path,
                    $"Workbook guid {workbook.WorkbookGuid:N} occurs more than once."));
                continue;
            }

            var projectedWorkbook = ProjectWorkbook(workbook, _tables);
            var result = WorkbookImporter.Import(
                file.Path,
                file.Bytes,
                projectedWorkbook,
                _projectedSchema,
                cellFormats: _cellFormats);
            diagnostics.AddRange(result.Diagnostics);
            importedRows.AddRange(result.Rows);
        }

        AddCrossWorkbookDiagnostics(importedRows, diagnostics);
        var records = BuildRecords(importedRows, diagnostics);
        return new SourceSnapshot(
            SchemaHash,
            ExportTarget,
            contentHash,
            contentHash,
            records,
            diagnostics,
            WorkbookProtocol.FormatVersion);
    }

    private ImmutableArray<RuntimeAssetRecord> BuildRecords(
        IReadOnlyCollection<ImportedRow> importedRows,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var indexable = importedRows
            .Where(static row => row.IsIndexable && row.Identity is not null)
            .OrderBy(static row => row.Identity!.Value, AssetIdentityOrder.Instance)
            .ToArray();
        foreach (var pending in importedRows.Where(static row => row.Identity is null))
        {
            diagnostics.Add(Blocker(
                RuntimeDiagnosticCodes.PendingIdentity,
                pending.Location,
                "A runtime-exported Excel row has a pending identity."));
        }

        var records = ImmutableArray.CreateBuilder<RuntimeAssetRecord>(indexable.Length);
        foreach (var row in indexable)
        {
            if (!_tables.TryGetValue(row.TableId, out var table))
                continue;
            if (string.IsNullOrEmpty(row.Key))
            {
                diagnostics.Add(Blocker(
                    RuntimeDiagnosticCodes.InvalidRow,
                    row.Location,
                    "A runtime-exported Excel row has no canonical key."));
                continue;
            }

            var fields = ImmutableArray.CreateBuilder<RuntimeFieldValue>();
            var dependencies = new HashSet<AssetIdentity>();
            foreach (var field in Flatten(table.Fields).OrderBy(static field => field.Id))
            {
                if (!row.Values.TryGetValue(field.PropertyPath, out var value))
                    continue;

                if (field.ReferenceTable is not null || field.ReferenceGroup is not null)
                {
                    if (value.State is CanonicalValueState.Missing)
                        continue;
                    if (value.State is CanonicalValueState.Null)
                    {
                        fields.Add(new RuntimeFieldValue(field.Id, ReadOnlySpan<byte>.Empty));
                        continue;
                    }
                    if (value.State == CanonicalValueState.Invalid)
                    {
                        diagnostics.Add(Blocker(
                            RuntimeDiagnosticCodes.InvalidRow,
                            row.Location,
                            $"Runtime reference field '{field.PropertyPath}' is invalid."));
                        continue;
                    }

                    var resolution = RowReferenceResolver.Resolve(
                        field,
                        value.Text!,
                        _projectedSchema,
                        indexable);
                    if (resolution.Succeeded)
                    {
                        var dependency = resolution.Identity;
                        dependencies.Add(dependency);
                        fields.Add(new RuntimeFieldValue(
                            field.Id,
                            Encoding.UTF8.GetBytes(dependency.ToString())));
                    }
                    else
                    {
                        diagnostics.Add(Blocker(
                            RuntimeDiagnosticCodes.DanglingReference,
                            row.Location,
                            resolution.Error!));
                    }
                    continue;
                }

                if (!TryEncodeValue(value, out var bytes))
                {
                    if (value.State == CanonicalValueState.Invalid)
                    {
                        diagnostics.Add(Blocker(
                            RuntimeDiagnosticCodes.InvalidRow,
                            row.Location,
                            $"Runtime field '{field.PropertyPath}' is invalid."));
                    }
                    continue;
                }

                fields.Add(new RuntimeFieldValue(field.Id, bytes));
            }

            records.Add(new RuntimeAssetRecord(
                row.Identity!.Value,
                row.Key,
                fields,
                dependencies,
                $"{table.SheetName}/{row.Key}"));
        }

        return records.ToImmutable();
    }

    private ImmutableArray<SourceFile> ReadFiles(
        bool parse,
        ImmutableArray<Diagnostic>.Builder? diagnostics = null)
    {
        var files = ImmutableArray.CreateBuilder<SourceFile>(_workbookPaths.Length);
        foreach (var path in _workbookPaths)
        {
            try
            {
                var bytes = _store.ReadAllBytes(path);
                var workbook = parse ? XlsxWorkbookCodec.Read(bytes) : null;
                files.Add(new SourceFile(path, bytes, workbook));
            }
            catch (Exception exception) when (diagnostics is not null && !IsFatal(exception))
            {
                diagnostics.Add(Blocker(
                    "runtime.excel.read-failed",
                    path,
                    $"Could not read the Excel workbook: {exception.Message}"));
            }
        }

        return files.ToImmutable();
    }

    private static string ComputeContentHash(IEnumerable<SourceFile> files)
    {
        var fingerprints = files
            .Select(static file => Convert.ToHexString(SHA256.HashData(file.Bytes)).ToLowerInvariant())
            .OrderBy(static hash => hash, StringComparer.Ordinal);
        var canonical = string.Join('\n', fingerprints);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static CanonicalSchemaDescriptor ProjectSchema(
        CanonicalSchemaDescriptor schema,
        ExportTargetId target)
    {
        var tables = schema.Tables
            .Where(table => table.Kind == CanonicalTableKind.Asset
                && table.ExportTargets.Contains(target.Value, StringComparer.Ordinal))
            .Select(table => table with { Fields = ProjectFields(table.Fields, target) })
            .OrderBy(static table => table.Id)
            .ToImmutableArray();
        return schema with { Tables = tables };
    }

    private static ImmutableArray<CanonicalFieldDescriptor> ProjectFields(
        ImmutableArray<CanonicalFieldDescriptor> fields,
        ExportTargetId target) =>
        fields
            .Where(field => field.ExportTargets.Contains(target.Value, StringComparer.Ordinal))
            .Select(field => field with { Children = ProjectFields(field.Children, target) })
            .OrderBy(static field => field.Id)
            .ToImmutableArray();

    private static WorkbookDefinition ProjectWorkbook(
        WorkbookDefinition workbook,
        IReadOnlyDictionary<int, CanonicalTableDescriptor> targetTables)
    {
        var tables = workbook.Tables
            .Where(table => targetTables.ContainsKey(table.TableId))
            .Select(table =>
            {
                var includedPaths = Flatten(targetTables[table.TableId].Fields)
                    .Select(static field => field.PropertyPath)
                    .ToHashSet(StringComparer.Ordinal);
                return table with
                {
                    Columns = table.Columns
                        .Where(column => includedPaths.Contains(column.PropertyPath))
                        .ToImmutableArray(),
                };
            })
            .OrderBy(static table => table.TableId)
            .ToImmutableArray();
        return workbook with { Tables = tables };
    }

    private static IEnumerable<CanonicalFieldDescriptor> Flatten(
        IEnumerable<CanonicalFieldDescriptor> fields)
    {
        foreach (var field in fields)
        {
            if (field.Children.IsDefaultOrEmpty
                || field.ExpandMode != CanonicalExpandMode.ExpandedColumns)
            {
                yield return field;
                continue;
            }

            foreach (var child in Flatten(field.Children))
                yield return child;
        }
    }

    private static bool TryEncodeValue(CanonicalValue value, out byte[] bytes)
    {
        switch (value.State)
        {
            case CanonicalValueState.Value:
            case CanonicalValueState.Defaulted:
                bytes = Encoding.UTF8.GetBytes(value.Text!);
                return true;
            case CanonicalValueState.Null:
                bytes = Encoding.UTF8.GetBytes(WorkbookProtocol.ExplicitNullToken);
                return true;
            default:
                bytes = [];
                return false;
        }
    }

    private static void AddCrossWorkbookDiagnostics(
        IEnumerable<ImportedRow> rows,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (var duplicate in rows
                     .Where(static row => row.Identity is not null && row.IsIndexable)
                     .GroupBy(static row => row.Identity!.Value)
                     .Where(static group => group.Count() > 1))
        {
            diagnostics.Add(Blocker(
                RuntimeDiagnosticCodes.DuplicateIdentity,
                string.Join(", ", duplicate.Select(static row => row.Location)),
                $"Identity {duplicate.Key} occurs in more than one workbook."));
        }

        foreach (var duplicate in rows
                     .Where(static row => row.Key is not null && row.IsIndexable)
                     .GroupBy(static row => (row.TableId, row.Key))
                     .Where(static group => group.Count() > 1))
        {
            diagnostics.Add(Blocker(
                RuntimeDiagnosticCodes.DuplicateKey,
                string.Join(", ", duplicate.Select(static row => row.Location)),
                $"Key '{duplicate.Key.Key}' occurs in more than one workbook for table {duplicate.Key.TableId}."));
        }
    }

    private static Diagnostic Blocker(string code, string location, string message) =>
        new(code, DiagnosticSeverity.Blocker, location, message);

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private sealed record SourceFile(string Path, byte[] Bytes, WorkbookDefinition? Workbook);

    private sealed class BackgroundWatchSubscription : IDisposable
    {
        private readonly ExcelDataSource _owner;
        private readonly Action _changed;
        private readonly CancellationTokenSource _cancellation = new();
        private IDisposable? _storeSubscription;
        private long _sequence;
        private int _disposed;

        public BackgroundWatchSubscription(ExcelDataSource owner, Action changed)
        {
            _owner = owner;
            _changed = changed;
        }

        public void Attach(IDisposable storeSubscription)
        {
            _storeSubscription = storeSubscription
                ?? throw new ArgumentNullException(nameof(storeSubscription));
        }

        public void Signal()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            var sequence = Interlocked.Increment(ref _sequence);
            _ = Task.Run(() => Prepare(sequence), _cancellation.Token);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _storeSubscription?.Dispose();
            _cancellation.Cancel();
            _cancellation.Dispose();
        }

        private void Prepare(long sequence)
        {
            SourceSnapshot? snapshot = null;
            Exception? failure = null;
            try
            {
                snapshot = _owner.BuildSnapshot();
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                failure = exception;
            }

            if (_cancellation.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            {
                snapshot?.Dispose();
                return;
            }

            _owner.PublishPrepared(sequence, snapshot, failure);
            _changed();
        }
    }

    private sealed class AssetIdentityOrder : IComparer<AssetIdentity>
    {
        public static AssetIdentityOrder Instance { get; } = new();

        public int Compare(AssetIdentity left, AssetIdentity right)
        {
            var table = left.TableId.CompareTo(right.TableId);
            return table != 0 ? table : left.RowGuid.Value.CompareTo(right.RowGuid.Value);
        }
    }
}
