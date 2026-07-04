using ExcelDb.Schema;

namespace SkillEditor.Avalonia.ViewModels;

public sealed class TableViewModel : NotifyObject
{
    bool _isDirty;

    public TableViewModel(TableSchema schema)
    {
        Schema = schema;
    }

    public TableSchema Schema { get; }
    public string Name => Schema.Name;

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (SetProperty(ref _isDirty, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string DisplayName => IsDirty ? Name + " *" : Name;

    public override string ToString() => DisplayName;
}
