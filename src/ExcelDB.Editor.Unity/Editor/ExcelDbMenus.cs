#if UNITY_EDITOR || EXCELDB_DOTNET_VALIDATE
using UnityEditor;

namespace ExcelDb.Editor.Unity
{

internal static class ExcelDbMenus
{
    [MenuItem("ExcelDB/Browser", priority = 1)]
    private static void Browser() => ExcelDbBrowserWindow.ShowWindow();

    [MenuItem("ExcelDB/Refresh", priority = 20)]
    private static void Refresh() => ExcelDbUnityEditorBridge.Current?.Refresh();

    [MenuItem("ExcelDB/Save All", priority = 21)]
    private static void SaveAll()
    {
        var bridge = ExcelDbUnityEditorBridge.Current;
        if (bridge == null) return;
        if (bridge.Conflicts.Count != 0)
            ExcelDbConflictWindow.ShowWindow();
        else
            bridge.SaveAll();
    }

    [MenuItem("ExcelDB/Regenerate…", priority = 40)]
    private static void Regenerate() => ExcelDbPlanWindow.ShowPlan("generate");

    [MenuItem("ExcelDB/Normalize…", priority = 41)]
    private static void Normalize() => ExcelDbPlanWindow.ShowPlan("normalize");

    [MenuItem("ExcelDB/Data Prepare…", priority = 42)]
    private static void DataPrepare() => ExcelDbPlanWindow.ShowPlan("data-prepare");

    [MenuItem("ExcelDB/Convert", priority = 60)]
    private static void Convert() => ExcelDbUnityEditorBridge.Current?.ConvertClient();

    [MenuItem("ExcelDB/Open Report", priority = 61)]
    private static void Report() => ExcelDbReportWindow.ShowWindow();

    [MenuItem("ExcelDB/Mount Workbook…", priority = 80)]
    private static void Mount() => ExcelDbUnityEditorBridge.Current?.MountWorkbook();

    [MenuItem("ExcelDB/Settings…", priority = 100)]
    private static void Settings() => SettingsService.OpenProjectSettings("Project/ExcelDB");
}
}
#endif
