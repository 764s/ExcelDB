using ExcelDb.Runtime;

namespace ExcelDbEditor;

public static class AssetDatabase
{
    public static event Action<ImportReport>? workbookImported
    {
        add => AssetDatabaseSession.Current.WorkbookImported += value;
        remove => AssetDatabaseSession.Current.WorkbookImported -= value;
    }

    public static void MountWorkbook(string path) => AssetDatabaseSession.Current.MountWorkbook(path);
    public static void UnmountWorkbook(string path) => AssetDatabaseSession.Current.UnmountWorkbook(path);
    public static void Refresh(ImportAssetOptions options = ImportAssetOptions.Default) => AssetDatabaseSession.Current.Refresh(options);
    public static void ImportAsset(string path, ImportAssetOptions options = ImportAssetOptions.Default) => AssetDatabaseSession.Current.ImportAsset(path, options);
    public static T? LoadAssetAtPath<T>(string assetPath) where T : class => AssetDatabaseSession.Current.Load<T>(assetPath);
    public static object? LoadAssetAtPath(string assetPath, Type type) => AssetDatabaseSession.Current.Load(assetPath, type);
    public static object[] LoadAllAssetsAtPath(string assetPath) => AssetDatabaseSession.Current.LoadAll(assetPath);
    public static Type? GetMainAssetTypeAtPath(string assetPath) => AssetDatabaseSession.Current.GetMainType(assetPath);
    public static string[] FindAssets(string filter) => AssetDatabaseSession.Current.Find(filter, null);
    public static string[] FindAssets(string filter, string[] searchInFolders) => AssetDatabaseSession.Current.Find(filter, searchInFolders);
    public static bool Contains(object obj) => AssetDatabaseSession.Current.Contains(obj);
    public static string GetAssetPath(object assetObject) => AssetDatabaseSession.Current.GetPath(assetObject);
    public static bool IsValidFolder(string path) => AssetDatabaseSession.Current.IsValidFolder(path);
    public static string AssetPathToGUID(string path) => AssetDatabaseSession.Current.GuidFromPath(path) is var guid && !guid.Empty() ? guid.ToString() : string.Empty;
    public static GUID GUIDFromAssetPath(string path) => AssetDatabaseSession.Current.GuidFromPath(path);
    public static string GUIDToAssetPath(string guid) => GUID.TryParse(guid, out var parsed) ? GUIDToAssetPath(parsed) : string.Empty;
    public static string GUIDToAssetPath(GUID guid) => AssetDatabaseSession.Current.PathFromGuid(guid);
    public static void CreateAsset(object asset, string path) => AssetDatabaseSession.Current.Create(asset, path);
    public static bool DeleteAsset(string path) => AssetDatabaseSession.Current.Delete(path);
    public static bool DeleteAssets(string[] paths, List<string> outFailedPaths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(outFailedPaths);
        var succeeded = true;
        foreach (var path in paths)
        {
            if (DeleteAsset(path))
                continue;
            outFailedPaths.Add(path);
            succeeded = false;
        }
        return succeeded;
    }
    public static string RenameAsset(string pathName, string newName) => AssetDatabaseSession.Current.Rename(pathName, newName);
    public static string MoveAsset(string oldPath, string newPath) => AssetDatabaseSession.Current.Move(oldPath, newPath);
    public static bool CopyAsset(string path, string newPath) => AssetDatabaseSession.Current.Copy(path, newPath);
    public static string GenerateUniqueAssetPath(string path) => AssetDatabaseSession.Current.GenerateUniquePath(path);
    public static void StartAssetEditing() => AssetDatabaseSession.Current.StartEditing();
    public static void StopAssetEditing() => AssetDatabaseSession.Current.StopEditing();
    public static void SaveAssets() => AssetDatabaseSession.Current.SaveAll();
    public static void SaveAssetIfDirty(object obj) => AssetDatabaseSession.Current.SaveOne(obj);
    public static void SaveAssetIfDirty(GUID guid) => AssetDatabaseSession.Current.SaveOne(guid);
    public static string[] GetDependencies(string pathName) => AssetDatabaseSession.Current.Dependencies(pathName, recursive: true);
    public static string[] GetDependencies(string pathName, bool recursive) => AssetDatabaseSession.Current.Dependencies(pathName, recursive);
    public static string[] GetLabels(object obj) => AssetDatabaseSession.Current.GetLabels(obj);
    public static void SetLabels(object obj, string[] labels) => AssetDatabaseSession.Current.SetLabels(obj, labels);
    public static void ClearLabels(object obj) => AssetDatabaseSession.Current.SetLabels(obj, []);
    public static bool OpenAsset(object target) => AssetDatabaseSession.Current.Open(target);
    public static RuntimeQueryStatus GetConflicts(Span<ConflictRecord> buffer, out int count) => AssetDatabaseSession.Current.GetConflicts(buffer, out count);
    public static bool ResolveConflict(ConflictId id, ConflictResolutionAction action) => AssetDatabaseSession.Current.ResolveConflict(id, action);
}
