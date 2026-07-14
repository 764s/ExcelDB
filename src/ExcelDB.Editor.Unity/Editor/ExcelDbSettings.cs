#if UNITY_EDITOR || EXCELDB_DOTNET_VALIDATE
using UnityEditor;
using UnityEngine;

namespace ExcelDb.Editor.Unity
{

internal static class ExcelDbSettings
{
    [SettingsProvider]
    private static SettingsProvider Create() => new SettingsProvider("Project/ExcelDB", SettingsScope.Project)
    {
        label = "ExcelDB",
        guiHandler = _ => Draw(),
    };

    private static void Draw()
    {
        var bridge = ExcelDbUnityEditorBridge.Current;
        if (bridge is null)
        {
            EditorGUILayout.HelpBox("The ExcelDB Unity host bridge is not configured.", MessageType.Error);
            return;
        }
        if (!bridge.HasProject)
        {
            EditorGUILayout.HelpBox("Initialize ExcelDB Project first.", MessageType.Info);
            if (GUILayout.Button("Initialize")) bridge.InitializeProject();
            return;
        }

        var values = bridge.ReadProjectSettings();
        if (values.Length != 5) return;
        EditorGUI.BeginChangeCheck();
        values[0] = EditorGUILayout.TextField("Schema Directory", values[0]);
        values[1] = EditorGUILayout.TextField("Generated Directory", values[1]);
        values[2] = EditorGUILayout.TextField("Workbooks", values[2]);
        values[3] = EditorGUILayout.TextField("Default Client Bytes", values[3]);
        values[4] = EditorGUILayout.TextField("Cache Directory", values[4]);
        if (EditorGUI.EndChangeCheck()) bridge.WriteProjectSettings(values);

        EditorGUILayout.Space();
        var metadata = bridge.ReadProjectSettingsMetadata();
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.TextField("Schema Hash", metadata.SchemaHash);
            EditorGUILayout.TextField("Tool Version", metadata.ToolVersion);
        }
    }
}
}
#endif
