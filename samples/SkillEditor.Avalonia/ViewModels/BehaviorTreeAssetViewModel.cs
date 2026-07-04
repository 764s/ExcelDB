using ExcelDb;

namespace SkillEditor.Avalonia.ViewModels;

public sealed class BehaviorTreeAssetViewModel : NotifyObject
{
    public BehaviorTreeAssetViewModel(RowId id, string key, string title)
    {
        Id = id;
        Key = key;
        Title = string.IsNullOrWhiteSpace(title) ? key : title;
    }

    public RowId Id { get; }
    public string Key { get; }
    public string Title { get; }
    public string DisplayName => Title + "  (" + Key + ")";

    public override string ToString() => DisplayName;
}
