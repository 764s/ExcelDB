#if UNITY_EDITOR || EXCELDB_DOTNET_VALIDATE
using UnityEditor;
using UnityEngine;

namespace ExcelDb.Editor.Unity
{

[CustomEditor(typeof(DefaultAsset))]
internal sealed class ExcelDbWorkbookInspector : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var bridge = ExcelDbUnityEditorBridge.Current;
        if (bridge == null)
            return;
        var path = UnityEditor.AssetDatabase.GetAssetPath(target);
        var summary = bridge.InspectWorkbook(path);
        if (!summary.IsWorkbook)
            return;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("ExcelDB Workbook", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Mounted", summary.IsMounted ? "Yes" : "No");
        EditorGUILayout.LabelField("Tables", summary.TableCount.ToString());
        EditorGUILayout.LabelField("Rows", summary.RowCount.ToString());
        EditorGUILayout.LabelField("Expected schema hash", summary.ExpectedSchemaHash);
        EditorGUILayout.LabelField("Workbook schema hash", summary.ActualSchemaHash);
        if (summary.ExpectedSchemaHash.Length != 0
            && summary.ActualSchemaHash.Length != 0
            && summary.ExpectedSchemaHash != summary.ActualSchemaHash)
            EditorGUILayout.HelpBox("Workbook schema is stale.", MessageType.Warning);
        if (GUILayout.Button("Open ExcelDB Browser"))
            ExcelDbBrowserWindow.ShowWindow();
        if (GUILayout.Button("Open in Excel"))
            bridge.OpenWorkbook(path);
    }
}
}
#endif
