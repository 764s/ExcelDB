#if UNITY_EDITOR || EXCELDB_DOTNET_VALIDATE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ExcelDb.Editor.Unity
{

internal sealed class ExcelDbBrowserWindow : EditorWindow
{
    private string _filter = string.Empty;
    private string _display = string.Empty;
    private string _selectedGuid = string.Empty;
    private string _selectedWorkbook = string.Empty;
    private string _selectedTable = string.Empty;
    private Vector2 _treeScroll;
    private Vector2 _rowScroll;
    private Vector2 _inspectorScroll;

    public static void ShowWindow()
    {
        GetWindow<ExcelDbBrowserWindow>("ExcelDB Browser");
    }

    public static void SelectGuid(string guid)
    {
        var window = GetWindow<ExcelDbBrowserWindow>("ExcelDB Browser");
        window._selectedGuid = guid ?? string.Empty;
        window.Repaint();
    }

    private void OnGUI()
    {
        var bridge = ExcelDbUnityEditorBridge.Current;
        if (bridge == null)
        {
            EditorGUILayout.HelpBox("The ExcelDB Unity host bridge is not configured.", MessageType.Error);
            return;
        }
        if (!bridge.HasProject)
        {
            EditorGUILayout.HelpBox("No ExcelDb.Project.json is mounted.", MessageType.Info);
            if (GUILayout.Button("Initialize Project with the shared C1 plan"))
                bridge.InitializeProject();
            return;
        }

        DrawToolbar(bridge);
        var rows = bridge.Search(_filter, _display);
        using (new EditorGUILayout.HorizontalScope())
        {
            DrawTree(bridge, rows);
            DrawRows(bridge, rows);
            DrawInspector(bridge);
        }
    }

    private void DrawToolbar(IExcelDbUnityEditorBridge bridge)
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            _filter = GUILayout.TextField(_filter, GUI.skin.FindStyle("ToolbarSearchTextField"), GUILayout.MinWidth(120));
            _display = GUILayout.TextField(_display, GUI.skin.FindStyle("ToolbarSearchTextField"), GUILayout.MinWidth(100));
            var counts = bridge.BrowserCounts;
            if (GUILayout.Button("Dirty " + counts.Dirty, EditorStyles.toolbarButton))
                ExcelDbPendingChangesWindow.ShowWindow();
            if (GUILayout.Button("Conflicts " + counts.Conflicted, EditorStyles.toolbarButton))
                ExcelDbConflictWindow.ShowWindow();
            if (GUILayout.Button("Errors " + counts.Error, EditorStyles.toolbarButton))
                ExcelDbReportWindow.ShowWindow();
            if (GUILayout.Button("Play Source", EditorStyles.toolbarButton))
                ExcelDbPlaySourceWindow.ShowWindow();
            if (GUILayout.Button("Save All", EditorStyles.toolbarButton))
            {
                if (bridge.Conflicts.Count != 0)
                    ExcelDbConflictWindow.ShowWindow();
                else
                    bridge.SaveAll();
            }
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
                bridge.Refresh();
        }
    }

    private void DrawTree(IExcelDbUnityEditorBridge bridge, IReadOnlyList<UnityEditorBrowserRow> rows)
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(Math.Max(180, position.width * .24f))))
        {
            EditorGUILayout.LabelField("Workbooks / Tables", EditorStyles.boldLabel);
            _treeScroll = EditorGUILayout.BeginScrollView(_treeScroll);
            foreach (var workbook in bridge.MountedWorkbooks)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(workbook, EditorStyles.label))
                    {
                        _selectedWorkbook = workbook;
                        _selectedTable = string.Empty;
                    }
                    if (GUILayout.Button("⋮", GUILayout.Width(24)))
                        ShowWorkbookMenu(bridge, workbook);
                }
                foreach (var table in rows.Where(row => row.Workbook == workbook)
                             .Select(row => row.Table).Distinct().OrderBy(value => value))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(18);
                        if (GUILayout.Button(table, EditorStyles.label))
                        {
                            _selectedWorkbook = workbook;
                            _selectedTable = table;
                        }
                        if (GUILayout.Button("+", GUILayout.Width(24)))
                            bridge.Create(table);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawRows(IExcelDbUnityEditorBridge bridge, IReadOnlyList<UnityEditorBrowserRow> rows)
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(Math.Max(260, position.width * .43f))))
        {
            EditorGUILayout.LabelField("Rows", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Key", EditorStyles.miniBoldLabel, GUILayout.Width(position.width * .13f));
                GUILayout.Label("Display name", EditorStyles.miniBoldLabel, GUILayout.Width(position.width * .13f));
                GUILayout.Label("Status", EditorStyles.miniBoldLabel);
            }
            _rowScroll = EditorGUILayout.BeginScrollView(_rowScroll);
            foreach (var row in rows)
            {
                if (_selectedWorkbook.Length != 0 && row.Workbook != _selectedWorkbook)
                    continue;
                if (_selectedTable.Length != 0 && row.Table != _selectedTable)
                    continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    var selected = row.Guid == _selectedGuid;
                    if (GUILayout.Toggle(selected, row.Key, "Button", GUILayout.Width(position.width * .13f)))
                        _selectedGuid = row.Guid;
                    GUILayout.Label(row.DisplayName, GUILayout.Width(position.width * .13f));
                    GUILayout.Label(row.Badges);
                    if (GUILayout.Button("⋮", GUILayout.Width(24)))
                        ShowRowMenu(bridge, row);
                }
                var rect = GUILayoutUtility.GetLastRect();
                var current = Event.current;
                if (current.type == EventType.MouseDrag && rect.Contains(current.mousePosition))
                {
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.SetGenericData(ExcelDbRowReferenceDrag.PayloadName, row.Guid);
                    DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
                    DragAndDrop.StartDrag(row.Table + "/" + row.Key);
                    current.Use();
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawInspector(IExcelDbUnityEditorBridge bridge)
    {
        using (new EditorGUILayout.VerticalScope())
        {
            EditorGUILayout.LabelField("Inspector", EditorStyles.boldLabel);
            if (_selectedGuid.Length == 0)
            {
                EditorGUILayout.HelpBox("Select a row to inspect it.", MessageType.Info);
                return;
            }
            _inspectorScroll = EditorGUILayout.BeginScrollView(_inspectorScroll);
            foreach (var line in bridge.InspectAsset(_selectedGuid))
                EditorGUILayout.SelectableLabel(line, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save")) bridge.SaveAsset(_selectedGuid);
                if (GUILayout.Button("Undo")) bridge.RevertAsset(_selectedGuid);
                if (GUILayout.Button("Excel")) bridge.OpenInExcel(_selectedGuid);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private static void ShowWorkbookMenu(IExcelDbUnityEditorBridge bridge, string workbook)
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("Open in Excel"), false, () => bridge.OpenWorkbook(workbook));
        menu.AddItem(new GUIContent("Unmount/Session only"), false, () => Unmount(bridge, workbook, false));
        menu.AddItem(new GUIContent("Unmount/Remove from Project json"), false, () => Unmount(bridge, workbook, true));
        menu.ShowAsContext();
    }

    private static void Unmount(IExcelDbUnityEditorBridge bridge, string workbook, bool persist)
    {
        if (bridge.GuardDirty("unmount"))
            bridge.UnmountWorkbook(workbook, persist);
    }

    private static void ShowRowMenu(IExcelDbUnityEditorBridge bridge, UnityEditorBrowserRow row)
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("Open in Excel"), false, () => bridge.OpenInExcel(row.Guid));
        menu.AddItem(new GUIContent("Save"), false, () => bridge.SaveAsset(row.Guid));
        menu.AddSeparator(string.Empty);
        menu.AddItem(new GUIContent("Rename…"), false, () => ExcelDbTextInputWindow.Show("Rename row", "New key", row.Key, value => bridge.Rename(row.Guid, value)));
        menu.AddItem(new GUIContent("Move…"), false, () => ExcelDbTextInputWindow.Show("Move row", "New path", row.Workbook + "/" + row.Table + "/" + row.Key, value => bridge.Move(row.Guid, value)));
        menu.AddItem(new GUIContent("Copy…"), false, () => ExcelDbTextInputWindow.Show("Copy row", "New path", row.Workbook + "/" + row.Table + "/" + row.Key + "_copy", value => bridge.Copy(row.Guid, value)));
        menu.AddItem(new GUIContent("Delete"), false, () =>
        {
            if (EditorUtility.DisplayDialog("Delete row", "Delete " + row.Key + "?", "Delete", "Cancel"))
                bridge.Delete(row.Guid);
        });
        menu.ShowAsContext();
    }
}

internal sealed class ExcelDbPlanWindow : EditorWindow
{
    private string _operation = string.Empty;
    private UnityEditorPlanView _plan;
    private bool _purge;
    private bool _rekey;
    private Vector2 _scroll;

    public static void ShowPlan(string operation)
    {
        var window = GetWindow<ExcelDbPlanWindow>("ExcelDB Plan");
        window._operation = operation;
        window._purge = false;
        window._rekey = false;
        window.Rebuild();
    }

    private void Rebuild()
    {
        var bridge = ExcelDbUnityEditorBridge.Current;
        _plan = bridge == null ? default(UnityEditorPlanView) : bridge.BuildPlan(_operation, _purge, _rekey);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField(_plan.Operation ?? _operation, EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Plan hash");
        EditorGUILayout.SelectableLabel(_plan.PlanHash ?? string.Empty, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        EditorGUI.BeginChangeCheck();
        _purge = EditorGUILayout.Toggle("Purge", _purge);
        _rekey = EditorGUILayout.Toggle("Rekey", _rekey);
        if (EditorGUI.EndChangeCheck())
            Rebuild();
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        DrawSection("Source fingerprints", _plan.Sources);
        DrawSection("Deterministic mutations", _plan.Mutations);
        DrawSection("Risks", _plan.Risks);
        EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);
        foreach (var diagnostic in _plan.Diagnostics ?? Array.Empty<UnityEditorReportLine>())
            EditorGUILayout.HelpBox(diagnostic.Severity + " " + diagnostic.Code + " " + diagnostic.Location + ": " + diagnostic.Message, MessageType.None);
        EditorGUILayout.EndScrollView();
        GUI.enabled = _plan.CanApply;
        if (GUILayout.Button("Apply exact displayed plan"))
        {
            var bridge = ExcelDbUnityEditorBridge.Current;
            if (bridge != null && _plan.PlanHash != null)
                bridge.ApplyDisplayedPlan(_plan.PlanHash);
        }
        GUI.enabled = true;
    }

    private static void DrawSection(string title, IReadOnlyList<string> lines)
    {
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        foreach (var line in lines ?? Array.Empty<string>())
            EditorGUILayout.SelectableLabel(line, GUILayout.Height(EditorGUIUtility.singleLineHeight));
    }
}

internal sealed class ExcelDbReportWindow : EditorWindow
{
    private int _selected;
    private Vector2 _listScroll;
    private Vector2 _detailScroll;

    public static void ShowWindow()
    {
        GetWindow<ExcelDbReportWindow>("ExcelDB Reports");
    }

    private void OnGUI()
    {
        var bridge = ExcelDbUnityEditorBridge.Current;
        var reports = bridge?.RecentReports ?? Array.Empty<UnityEditorReportView>();
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(Math.Max(220, position.width * .35f))))
            {
                _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
                for (var index = 0; index < reports.Count; index++)
                {
                    var report = reports[index];
                    var caption = (report.Ok ? "OK " : "FAIL ") + report.Operation + " — " + report.Summary;
                    if (GUILayout.Toggle(_selected == index, caption, "Button"))
                        _selected = index;
                }
                EditorGUILayout.EndScrollView();
            }
            using (new EditorGUILayout.VerticalScope())
            {
                if (_selected >= reports.Count)
                    _selected = Math.Max(0, reports.Count - 1);
                if (reports.Count != 0 && bridge != null)
                    DrawReport(bridge, reports[_selected], _selected);
            }
        }
    }

    private void DrawReport(IExcelDbUnityEditorBridge bridge, UnityEditorReportView report, int reportIndex)
    {
        EditorGUILayout.LabelField(report.Operation, EditorStyles.boldLabel);
        if (report.Target.Length != 0)
            EditorGUILayout.LabelField("Target", report.Target);
        foreach (var artifact in report.Artifacts ?? Array.Empty<string>())
            EditorGUILayout.SelectableLabel(artifact, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);
        var diagnostics = report.Diagnostics ?? Array.Empty<UnityEditorReportLine>();
        for (var index = 0; index < diagnostics.Count; index++)
        {
            var line = diagnostics[index];
            if (GUILayout.Button(line.Severity + " " + line.Code + " " + line.Location + ": " + line.Message, EditorStyles.wordWrappedLabel)
                && Event.current.clickCount == 2)
                bridge.LocateDiagnostic(reportIndex, index);
        }
        EditorGUILayout.EndScrollView();
        if (GUILayout.Button("Export json…"))
        {
            var path = EditorUtility.SaveFilePanel("Export ExcelDB report", string.Empty, report.Operation + ".json", "json");
            if (path.Length != 0)
                bridge.ExportReportJson(reportIndex, path);
        }
    }
}

internal sealed class ExcelDbConflictWindow : EditorWindow
{
    private Vector2 _scroll;

    public static void ShowWindow()
    {
        GetWindow<ExcelDbConflictWindow>("ExcelDB Conflicts");
    }

    private void OnGUI()
    {
        var bridge = ExcelDbUnityEditorBridge.Current;
        var conflicts = bridge?.Conflicts ?? Array.Empty<UnityEditorConflictView>();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Reload all from Excel")) ResolveAll(bridge, conflicts, true);
            if (GUILayout.Button("Keep all editor values")) ResolveAll(bridge, conflicts, false);
        }
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        foreach (var conflict in conflicts)
        {
            EditorGUILayout.LabelField(conflict.PropertyPath + " — " + conflict.AssetGuid, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Base", conflict.BaseValue);
            EditorGUILayout.LabelField("Mine", conflict.MineValue);
            EditorGUILayout.LabelField("Theirs", conflict.TheirValue);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload from Excel")) bridge?.ResolveConflict(conflict.Id, true);
                if (GUILayout.Button("Keep editor value")) bridge?.ResolveConflict(conflict.Id, false);
            }
            EditorGUILayout.Space();
        }
        EditorGUILayout.EndScrollView();
        GUI.enabled = conflicts.Count == 0;
        if (GUILayout.Button("Save All")) bridge?.SaveAll();
        GUI.enabled = true;
    }

    private static void ResolveAll(IExcelDbUnityEditorBridge? bridge, IReadOnlyList<UnityEditorConflictView> conflicts, bool reload)
    {
        if (bridge == null) return;
        foreach (var conflict in conflicts)
            bridge.ResolveConflict(conflict.Id, reload);
    }
}

internal sealed class ExcelDbPendingChangesWindow : EditorWindow
{
    private Vector2 _scroll;

    public static void ShowWindow()
    {
        GetWindow<ExcelDbPendingChangesWindow>("ExcelDB Pending Changes");
    }

    private void OnGUI()
    {
        var bridge = ExcelDbUnityEditorBridge.Current;
        var pending = bridge?.PendingChanges ?? Array.Empty<UnityEditorPendingChangeView>();
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        foreach (var item in pending)
        {
            if (GUILayout.Button(item.Kind + " " + item.Path, EditorStyles.boldLabel)
                && Event.current.clickCount == 2)
                ExcelDbBrowserWindow.SelectGuid(item.Guid);
            foreach (var field in item.Fields ?? Array.Empty<string>())
                EditorGUILayout.SelectableLabel(field, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save row")) bridge?.SavePending(item.Guid);
                if (GUILayout.Button("Undo")) bridge?.RevertPending(item.Guid);
            }
            EditorGUILayout.Space();
        }
        EditorGUILayout.EndScrollView();
    }
}

internal sealed class ExcelDbPlaySourceWindow : EditorWindow
{
    public static void ShowWindow()
    {
        GetWindow<ExcelDbPlaySourceWindow>("ExcelDB Play Source");
    }

    private void OnGUI()
    {
        var bridge = ExcelDbUnityEditorBridge.Current;
        if (bridge == null)
        {
            EditorGUILayout.HelpBox("The ExcelDB Unity host bridge is not configured.", MessageType.Error);
            return;
        }
        var state = bridge.GetPlayState();
        if (!EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox("The selection applies to the next Play session only and is never persisted.", MessageType.Info);
            if (GUILayout.Button("Choose source for next Play")) bridge.PrepareNextPlaySource();
            if (GUILayout.Button("Cancel next Play source request")) bridge.ClearPlaySourceRequest();
            return;
        }
        EditorGUILayout.LabelField("Source", state.Source);
        EditorGUILayout.LabelField("Target", state.Target);
        if (!state.CanSwitch)
            EditorGUILayout.HelpBox(state.DisabledReason, MessageType.Warning);
        GUI.enabled = state.CanSwitch;
        if (GUILayout.Button("Switch data source…")) bridge.SwitchPlaySource();
        GUI.enabled = true;
        var nextHotReload = EditorGUILayout.Toggle("Hot reload", state.HotReload);
        if (nextHotReload != state.HotReload)
            bridge.SetHotReload(nextHotReload);
    }
}

public static class ExcelDbRowReferencePicker
{
    public static void Show(string refTable, string refGroup, Action<string> selected)
    {
        ExcelDbRowReferencePickerWindow.Show(refTable, refGroup, selected);
    }
}

public static class ExcelDbRowReferenceDrag
{
    public const string PayloadName = "ExcelDB.RowReference.Guid";

    public static string CurrentGuid
    {
        get { return DragAndDrop.GetGenericData(PayloadName) as string ?? string.Empty; }
    }

    public static bool HandleDrop(Rect dropArea, string ownerGuid, string propertyPath)
    {
        var current = Event.current;
        if (!dropArea.Contains(current.mousePosition))
            return false;
        var guid = CurrentGuid;
        if (guid.Length == 0)
            return false;
        if (current.type == EventType.DragUpdated)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            current.Use();
            return false;
        }
        if (current.type != EventType.DragPerform)
            return false;
        DragAndDrop.AcceptDrag();
        ExcelDbUnityEditorBridge.Current?.AssignRowReference(ownerGuid, propertyPath, guid);
        current.Use();
        return true;
    }
}

internal sealed class ExcelDbRowReferencePickerWindow : EditorWindow
{
    private string _refTable = string.Empty;
    private string _refGroup = string.Empty;
    private string _filter = string.Empty;
    private Action<string>? _selected;
    private Vector2 _scroll;

    public static void Show(string refTable, string refGroup, Action<string> selected)
    {
        var window = CreateInstance<ExcelDbRowReferencePickerWindow>();
        window._refTable = refTable ?? string.Empty;
        window._refGroup = refGroup ?? string.Empty;
        window._selected = selected;
        window.titleContent = new GUIContent("ExcelDB RowRef");
        window.ShowAuxWindow();
    }

    private void OnGUI()
    {
        _filter = EditorGUILayout.TextField("Search", _filter);
        var rows = ExcelDbUnityEditorBridge.Current?.SearchRowReferences(_filter, _refTable, _refGroup)
                   ?? Array.Empty<UnityEditorBrowserRow>();
        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        foreach (var row in rows)
        {
            if (GUILayout.Button(row.Table + "/" + row.Key + " — " + row.DisplayName))
            {
                _selected?.Invoke(row.Guid);
                Close();
            }
        }
        EditorGUILayout.EndScrollView();
    }
}

internal sealed class ExcelDbTextInputWindow : EditorWindow
{
    private string _label = string.Empty;
    private string _value = string.Empty;
    private Action<string>? _accepted;

    public static void Show(string title, string label, string initial, Action<string> accepted)
    {
        var window = CreateInstance<ExcelDbTextInputWindow>();
        window.titleContent = new GUIContent(title);
        window._label = label;
        window._value = initial;
        window._accepted = accepted;
        window.ShowAuxWindow();
    }

    private void OnGUI()
    {
        _value = EditorGUILayout.TextField(_label, _value);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled = !string.IsNullOrWhiteSpace(_value);
            if (GUILayout.Button("Apply"))
            {
                _accepted?.Invoke(_value);
                Close();
            }
            GUI.enabled = true;
            if (GUILayout.Button("Cancel")) Close();
        }
    }
}

internal static class ExcelDbEditorLifecycle
{
    [InitializeOnLoadMethod]
    private static void Install()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        EditorApplication.wantsToQuit += OnWantsToQuit;
    }

    private static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        var bridge = ExcelDbUnityEditorBridge.Current;
        if (state == PlayModeStateChange.ExitingEditMode && bridge != null)
        {
            if (!bridge.GuardDirty("play") || !AllowStaleClient(bridge) || !bridge.PrepareNextPlaySource())
            {
                bridge.ClearPlaySourceRequest();
                EditorApplication.isPlaying = false;
            }
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            bridge?.ClearPlaySourceRequest();
        }
    }

    private static bool AllowStaleClient(IExcelDbUnityEditorBridge bridge)
    {
        var freshness = bridge.GetClientFreshness();
        if (freshness.IsFresh)
            return true;
        var choice = EditorUtility.DisplayDialogComplex(
            "ExcelDB client data is stale",
            freshness.Reason,
            "Convert Client and enter",
            "Enter with stale data",
            "Cancel");
        if (choice == 1)
            return true;
        if (choice != 0)
            return false;
        bridge.ConvertClient();
        var refreshed = bridge.GetClientFreshness();
        if (refreshed.IsFresh)
            return true;
        EditorUtility.DisplayDialog("ExcelDB convert failed", refreshed.Reason, "Cancel Play");
        return false;
    }

    private static void OnBeforeAssemblyReload()
    {
        ExcelDbUnityEditorBridge.Current?.GuardDirty("assembly-reload");
    }

    private static bool OnWantsToQuit()
    {
        return ExcelDbUnityEditorBridge.Current?.GuardDirty("quit") ?? true;
    }
}
}
#endif
