using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExcelDb.Loading;
using ExcelDb.Schema;

namespace ExcelDb.Excel
{
    public sealed class ExcelTableLoader : ITableLoader
    {
        readonly string _path;
        readonly SchemaRegistry _registry;
        readonly ExcelTableLoaderOptions _options;
        readonly TableId[] _tables;
        XlsxWorkbook _workbook;
        FileFingerprint? _loadedFingerprint;

        public ExcelTableLoader(string path, SchemaRegistry registry, ExcelTableLoaderOptions? options = null)
        {
            _path = path;
            _registry = registry;
            _options = options ?? new ExcelTableLoaderOptions();
            _tables = registry.Tables.Select(table => table.Id).ToArray();

            if (File.Exists(path))
            {
                _workbook = XlsxWorkbook.Load(path);
                _loadedFingerprint = FileFingerprint.Read(path);
            }
            else if (_options.CreateIfMissing)
            {
                _workbook = XlsxWorkbook.Empty();
                _loadedFingerprint = null;
            }
            else
                throw new FileNotFoundException($"Excel workbook '{path}' does not exist.", path);
        }

        public string WorkbookPath => _path;

        public IReadOnlyCollection<TableId> Tables => _tables;

        public LoaderCapabilities Capabilities => LoaderCapabilities.Write;

        public event Action<TableId>? TableChanged;

        public TableContent Load(TableId table)
        {
            var schema = _registry.Get(table);
            return _workbook.TryGetSheet(schema.Name, out var sheet)
                ? ExcelTableMapper.Read(schema, sheet, _registry, _options)
                : new TableContent();
        }

        public void Write(TableId table, TableContent content)
        {
            EnsureNotExternallyModified();
            var schema = _registry.Get(table);
            _workbook.SetSheet(schema.Name, ExcelTableMapper.Write(schema, content, _options));
            _workbook.Save(_path, createBackup: true);
            _loadedFingerprint = FileFingerprint.Read(_path);
        }

        public void ReloadFromDisk()
        {
            if (!File.Exists(_path))
                throw new FileNotFoundException($"Excel workbook '{_path}' does not exist.", _path);

            _workbook = XlsxWorkbook.Load(_path);
            _loadedFingerprint = FileFingerprint.Read(_path);
            foreach (var table in _tables)
                TableChanged?.Invoke(table);
        }

        void EnsureNotExternallyModified()
        {
            if (!File.Exists(_path))
            {
                if (_loadedFingerprint.HasValue)
                    throw new IOException($"Excel workbook '{_path}' was deleted after it was opened.");
                return;
            }

            var current = FileFingerprint.Read(_path);
            if (!_loadedFingerprint.HasValue)
                throw new IOException($"Excel workbook '{_path}' was created by another process after this editor opened it.");
            if (!_loadedFingerprint.Value.Equals(current))
                throw new IOException($"Excel workbook '{_path}' was modified outside this editor. Reload it before saving.");
        }

        readonly struct FileFingerprint : IEquatable<FileFingerprint>
        {
            readonly DateTime _lastWriteUtc;
            readonly long _length;

            FileFingerprint(DateTime lastWriteUtc, long length)
            {
                _lastWriteUtc = lastWriteUtc;
                _length = length;
            }

            public static FileFingerprint Read(string path)
            {
                var file = new FileInfo(path);
                return new FileFingerprint(file.LastWriteTimeUtc, file.Length);
            }

            public bool Equals(FileFingerprint other) =>
                _lastWriteUtc == other._lastWriteUtc && _length == other._length;

            public override bool Equals(object? obj) => obj is FileFingerprint other && Equals(other);
            public override int GetHashCode() => (_lastWriteUtc.GetHashCode() * 397) ^ _length.GetHashCode();
        }
    }
}
