using ExcelDb;

namespace SkillEditor.Avalonia.ViewModels;

public sealed class BehaviorNodeViewModel : NotifyObject
{
    double _x;
    double _y;
    bool _isSelected;

    public BehaviorNodeViewModel(RowId id, string key, string title, string kind, string action, int x, int y, IReadOnlyList<RowId> children)
    {
        Id = id;
        Key = key;
        Title = string.IsNullOrWhiteSpace(title) ? key : title;
        Kind = string.IsNullOrWhiteSpace(kind) ? "Action" : kind;
        Action = action;
        _x = x;
        _y = y;
        Children = children;
    }

    public RowId Id { get; }
    public string Key { get; }
    public string Title { get; }
    public string Kind { get; }
    public string Action { get; }
    public IReadOnlyList<RowId> Children { get; }

    public double X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
