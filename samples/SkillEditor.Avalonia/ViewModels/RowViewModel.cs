using System.Collections.ObjectModel;
using ExcelDb;
using ExcelDb.Excel;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace SkillEditor.Avalonia.ViewModels;

public sealed class RowViewModel : NotifyObject
{
    readonly EditorDocument _document;
    readonly Dictionary<string, string> _cells = new(StringComparer.Ordinal);

    public RowViewModel(EditorDocument document, RowId id, IReadOnlyList<FieldDescriptor> fields, IMessage row)
    {
        _document = document;
        RowId = id;
        Fields = fields;
        Reload(row);
    }

    public RowId RowId { get; }
    public int IdNumber => RowId.Id;
    public IReadOnlyList<FieldDescriptor> Fields { get; }
    public ReadOnlyDictionary<string, string> Cells => new(_cells);

    public string this[string fieldName]
    {
        get => _cells.TryGetValue(fieldName, out var value) ? value : string.Empty;
        set
        {
            if (_cells.TryGetValue(fieldName, out var current) && current == value)
                return;

            if (!_document.TrySetCell(this, fieldName, value))
            {
                RaiseCellChanged(fieldName);
                return;
            }

            RaiseCellChanged(fieldName);
        }
    }

    public void Reload(IMessage row)
    {
        foreach (var field in Fields)
            _cells[field.Name] = ExcelFieldCodec.FormatField(row, field);

        OnPropertyChanged(nameof(Cells));
        OnPropertyChanged("Item[]");
    }

    internal void RaiseCellChanged(string fieldName)
    {
        OnPropertyChanged("Item[]");
        OnPropertyChanged("Item[" + fieldName + "]");
        OnPropertyChanged(nameof(Cells));
    }
}
