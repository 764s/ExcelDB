using System.Collections.Immutable;
using ExcelDb.Compatibility.Migrations;
using ExcelDb.Core.Identity;
using ExcelDb.Core.IO;
using ExcelDb.Core.Values;
using ExcelDb.Schema.Descriptors;
using ExcelDb.Workbooks.Formatting;
using ExcelDb.Workbooks.Importing;
using ExcelDb.Workbooks.Model;
using ExcelDb.Workbooks.OpenXml;

namespace ExcelDb.Pipeline;

/// <summary>Real XLSX adapter for the M8 planner/coordinator transaction boundary.</summary>
public sealed class XlsxMigrationWorkbookParticipant : IMigrationWorkbookParticipant
{
    private readonly string _path;
    private readonly CanonicalSchemaDescriptor _sourceSchema;
    private readonly CanonicalSchemaDescriptor _targetSchema;
    private readonly CellFormatRegistry _formats;

    public XlsxMigrationWorkbookParticipant(
        string path,
        CanonicalSchemaDescriptor sourceSchema,
        CanonicalSchemaDescriptor targetSchema,
        CellFormatRegistry? formats = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _sourceSchema = sourceSchema ?? throw new ArgumentNullException(nameof(sourceSchema));
        _targetSchema = targetSchema ?? throw new ArgumentNullException(nameof(targetSchema));
        _formats = formats ?? new CellFormatRegistry();
        var physical = XlsxWorkbookCodec.Read(File.ReadAllBytes(_path));
        WorkbookId = physical.WorkbookGuid.ToString("N");
    }

    public string WorkbookId { get; }

    public MigrationWorkbookSnapshot ReadCurrent()
    {
        var bytes = File.ReadAllBytes(_path);
        var workbook = XlsxWorkbookCodec.Read(bytes);
        var schema = workbook.SchemaHash switch
        {
            var value when value == _sourceSchema.SchemaHash => _sourceSchema,
            var value when value == _targetSchema.SchemaHash => _targetSchema,
            _ => throw new InvalidDataException(
                $"Workbook uses schema {workbook.SchemaHash:x16}; expected {_sourceSchema.SchemaHash:x16} or {_targetSchema.SchemaHash:x16}."),
        };
        var import = WorkbookImporter.Import(_path, bytes, workbook, schema, cellFormats: _formats);
        if (import.Diagnostics.Any(static item => item.IsFailure))
        {
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                import.Diagnostics.Where(static item => item.IsFailure).Select(static item => item.Message)));
        }

        var tables = schema.Tables.ToDictionary(static table => table.Id);
        var cells = ImmutableArray.CreateBuilder<MigrationCell>();
        foreach (var row in import.Rows.OrderBy(static item => item.TableId).ThenBy(static item => item.RowNumber))
        {
            if (row.Identity is null)
                throw new InvalidDataException($"Migration cannot include pending row {row.Location}.");
            if (!tables.TryGetValue(row.TableId, out var table))
                continue;
            foreach (var value in row.Values)
            {
                var field = Flatten(table.Fields).FirstOrDefault(field =>
                    string.Equals(field.PropertyPath, value.Key, StringComparison.Ordinal));
                if (field is null)
                    continue;
                cells.Add(new MigrationCell(
                    new MigrationCellAddress(
                        row.TableId,
                        row.Identity.Value.ToString(),
                        EffectiveFieldPath(field)),
                    value.Value));
            }
        }

        var snapshot = new MigrationWorkbookSnapshot(
            WorkbookId,
            ContentFingerprint.FromBytes(bytes).Sha256,
            workbook.SchemaHash,
            cells.ToImmutable(),
            workbook.EffectiveMigrationMarkers.Select(ParseMarker).ToImmutableHashSet());
        return snapshot.AppliedMigrations.Count == 0
            ? snapshot
            : snapshot with { Revision = MigrationPlanSerializer.ComputeSnapshotRevision(snapshot) };
    }

    public IPreparedMigrationWorkbook Prepare(
        MigrationWorkbookSnapshot expected,
        MigrationWorkbookSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(candidate);
        var current = ReadCurrent();
        if (!MigrationSnapshotComparer.SemanticallyEquals(current, expected))
            throw new InvalidOperationException("Workbook changed after the migration plan was sealed.");
        if (!string.Equals(expected.WorkbookId, WorkbookId, StringComparison.Ordinal)
            || !string.Equals(candidate.WorkbookId, WorkbookId, StringComparison.Ordinal))
            throw new InvalidOperationException("Migration workbook identity changed.");

        var sourceBytes = File.ReadAllBytes(_path);
        var source = XlsxWorkbookCodec.Read(sourceBytes);
        var projected = ProjectToTargetSchema(source);
        var candidateCells = candidate.Cells.ToDictionary(static cell => cell.Address.StableKey, StringComparer.Ordinal);
        var targetTables = _targetSchema.Tables.ToDictionary(static table => table.Id);
        var tables = projected.Tables.Select(table =>
        {
            if (!targetTables.TryGetValue(table.TableId, out var schemaTable))
                return table;
            var fieldsByPath = Flatten(schemaTable.Fields).ToDictionary(FieldPathKey, StringComparer.Ordinal);
            var rows = table.Rows.Select(row =>
            {
                if (row.RowGuid is null)
                    throw new InvalidDataException($"Migration cannot write pending row {table.SheetName}!{row.SourceRowNumber}.");
                var identity = new AssetIdentity(table.TableId, row.RowGuid.Value).ToString();
                var values = row.Cells.ToBuilder();
                foreach (var pair in fieldsByPath)
                {
                    var address = new MigrationCellAddress(table.TableId, identity, EffectiveFieldPath(pair.Value));
                    if (!candidateCells.TryGetValue(address.StableKey, out var migrationCell))
                        continue;
                    var written = CanonicalCellWriter.Write(migrationCell.Value, pair.Value, _targetSchema, _formats);
                    if (!written.Succeeded)
                        throw new InvalidDataException($"Cannot write migrated value {address.StableKey}: {written.Error}");
                    if (string.IsNullOrEmpty(written.PhysicalText))
                        values.Remove(pair.Value.PropertyPath);
                    else
                        values[pair.Value.PropertyPath] = new WorkbookCell(written.PhysicalText);
                }
                return row with { Cells = values.ToImmutable() };
            }).ToImmutableArray();
            return table with { Rows = rows };
        }).ToImmutableArray();
        projected = projected with
        {
            SchemaHash = candidate.SchemaHash,
            Tables = tables,
            MigrationMarkers = candidate.AppliedMigrations
                .OrderBy(static item => item.Id, StringComparer.Ordinal)
                .ThenBy(static item => item.Version)
                .Select(FormatMarker)
                .ToImmutableArray(),
        };
        var stagedBytes = XlsxWorkbookCodec.Project(sourceBytes, projected);
        var stagedPath = _path + $".migration-{Guid.NewGuid():N}.stage";
        File.WriteAllBytes(stagedPath, stagedBytes);

        var verification = new XlsxMigrationWorkbookParticipant(
            stagedPath,
            _sourceSchema,
            _targetSchema,
            _formats).ReadCurrent();
        if (!MigrationSnapshotComparer.SemanticallyEquals(verification, candidate))
        {
            File.Delete(stagedPath);
            throw new InvalidDataException("Staged XLSX does not reproduce the sealed candidate snapshot.");
        }
        return new Prepared(WorkbookId, _path, stagedPath, ContentFingerprint.FromBytes(sourceBytes));
    }

    private WorkbookDefinition ProjectToTargetSchema(WorkbookDefinition source)
    {
        var sourceTables = source.Tables.ToDictionary(static table => table.TableId);
        var result = new List<WorkbookTable>();
        foreach (var target in _targetSchema.Tables
                     .Where(static table => table.Kind == CanonicalTableKind.Asset)
                     .OrderBy(static table => table.Id))
        {
            var layout = WorkbookLayout.CreateTable(target);
            if (!sourceTables.TryGetValue(target.Id, out var old))
            {
                result.Add(layout);
                continue;
            }
            var oldByPath = old.Columns.ToDictionary(static column => column.FieldPath, StringComparer.Ordinal);
            var rows = old.Rows.Select(row =>
            {
                var cells = ImmutableDictionary.CreateBuilder<string, WorkbookCell>(StringComparer.Ordinal);
                foreach (var column in layout.Columns)
                {
                    if (oldByPath.TryGetValue(column.FieldPath, out var oldColumn)
                        && row.Cells.TryGetValue(oldColumn.PropertyPath, out var value))
                        cells[column.PropertyPath] = value;
                }
                return row with { Cells = cells.ToImmutable() };
            }).ToImmutableArray();
            result.Add(old with
            {
                ProtoName = target.Name,
                SheetName = target.SheetName,
                Columns = layout.Columns,
                Rows = rows,
                IsRetiredPreserved = false,
            });
        }
        result.AddRange(source.Tables.Where(table => !result.Any(candidate => candidate.TableId == table.TableId)));
        return source with { Tables = result.OrderBy(static table => table.TableId).ToImmutableArray() };
    }

    private static IEnumerable<CanonicalFieldDescriptor> Flatten(IEnumerable<CanonicalFieldDescriptor> fields)
    {
        foreach (var field in fields)
        {
            yield return field;
            foreach (var child in Flatten(field.Children))
                yield return child;
        }
    }

    private static ImmutableArray<int> EffectiveFieldPath(CanonicalFieldDescriptor field) =>
        field.FieldIdPath.IsDefaultOrEmpty ? [field.Id] : field.FieldIdPath;

    private static string FieldPathKey(CanonicalFieldDescriptor field) =>
        string.Join('.', EffectiveFieldPath(field));

    private static MigrationKey ParseMarker(string marker)
    {
        var separator = marker.LastIndexOf('@');
        return separator > 0 && int.TryParse(marker.AsSpan(separator + 1), out var version) && version > 0
            ? new MigrationKey(marker[..separator], version)
            : throw new InvalidDataException($"Invalid migration marker '{marker}'.");
    }

    private static string FormatMarker(MigrationKey key) => $"{key.Id}@{key.Version}";

    private sealed class Prepared : IPreparedMigrationWorkbook
    {
        private readonly string _livePath;
        private readonly string _stagedPath;
        private readonly ContentFingerprint _expected;
        private byte[]? _backup;
        private bool _committed;
        private bool _disposed;

        public Prepared(string workbookId, string livePath, string stagedPath, ContentFingerprint expected)
        {
            WorkbookId = workbookId;
            _livePath = livePath;
            _stagedPath = stagedPath;
            _expected = expected;
        }

        public string WorkbookId { get; }

        public void Commit()
        {
            ThrowIfDisposed();
            if (_committed)
                return;
            if (ContentFingerprint.FromFile(_livePath) != _expected)
                throw new InvalidOperationException("Workbook changed between migration prepare and commit.");
            _backup = File.ReadAllBytes(_livePath);
            AtomicFile.WriteAllBytes(_livePath, File.ReadAllBytes(_stagedPath));
            _committed = true;
        }

        public void Rollback()
        {
            ThrowIfDisposed();
            if (!_committed)
                return;
            if (_backup is null)
                throw new InvalidOperationException("Migration backup is unavailable.");
            AtomicFile.WriteAllBytes(_livePath, _backup);
            _committed = false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (File.Exists(_stagedPath))
                    File.Delete(_stagedPath);
            }
            catch (IOException)
            {
            }
            _backup = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Prepared));
        }
    }
}
