namespace ExcelDbEditor;

/// <summary>
/// Authoring dirty-state facade for generated ExcelDB objects. Unlike
/// <see cref="AssetDatabase.SaveAssetIfDirty(object)"/>, SetDirty never writes a workbook;
/// it only protects the resident edit until an explicit save or discard publish point.
/// </summary>
public static class EditorUtility
{
    public static void SetDirty(object target) => AssetDatabaseSession.Current.MarkDirty(target);

    public static bool IsDirty(object target) => AssetDatabaseSession.Current.IsAssetDirty(target);
}
