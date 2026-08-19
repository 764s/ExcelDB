#if UNITY_EDITOR || EXCELDB_DOTNET_VALIDATE
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
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
    private ExcelDbAuthoringPropertyFactory? _propertyFactory;
    private ExcelDbUnityUndoBridge? _propertyUndo;
    private ExcelDbSerializedInspectorSession? _typedInspector;
    private readonly ExcelDbPropertyGUIState _propertyGuiState = new ExcelDbPropertyGUIState();
    private string _typedInspectorError = string.Empty;

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

    private void OnDisable()
    {
        DisposeTypedInspector();
        _propertyUndo?.Dispose();
        _propertyUndo = null;
        _propertyFactory = null;
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
            EditorGUILayout.HelpBox("No ExcelDB Project v2 is initialized.", MessageType.Info);
            if (GUILayout.Button("Initialize ExcelDB Project"))
                bridge.InitializeProject();
            return;
        }

        DrawToolbar(bridge);
        if (ExcelDbEditorLifecycle.ReloadLocked)
            EditorGUILayout.HelpBox(
                "Script assembly reload is waiting for ExcelDB changes to be saved or reverted.",
                MessageType.Warning);
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
                else if (_typedInspector != null && !_typedInspector.TryApplyPendingChanges())
                    _typedInspectorError =
                        "Save All was cancelled because the selected row has a concurrent edit conflict.";
                else
                {
                    bridge.SaveAll();
                    if (_typedInspector == null || _typedInspector.MarkSavedIfClean())
                        _typedInspectorError = string.Empty;
                    else
                        _typedInspectorError =
                            "Save All completed without confirming the selected row as clean.";
                }
            }
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
            {
                if (_typedInspector != null && !_typedInspector.TryApplyPendingChanges())
                    _typedInspectorError =
                        "Refresh was cancelled because the selected row has a concurrent edit conflict.";
                else
                {
                    bridge.Refresh();
                    _typedInspector?.RefreshFromResident();
                    _typedInspectorError = string.Empty;
                }
            }
        }
    }

    private void DrawTree(IExcelDbUnityEditorBridge bridge, IReadOnlyList<UnityEditorBrowserRow> rows)
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(Math.Max(180, position.width * .24f))))
        {
            EditorGUILayout.LabelField("Excel / Tables", EditorStyles.boldLabel);
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
                    DragAndDrop.SetGenericData(ExcelDbRowReferenceDrag.RowPayloadName, row);
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
            var typed = GetTypedInspector(bridge);
            if (typed == null)
            {
                if (_typedInspectorError.Length != 0)
                    EditorGUILayout.HelpBox(_typedInspectorError, MessageType.Warning);
                foreach (var line in bridge.InspectAsset(_selectedGuid))
                    EditorGUILayout.SelectableLabel(line, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!ExcelDbEditor.AssetDatabase.IsAuthoringEnabled))
                    {
                        if (GUILayout.Button("Save")) bridge.SaveAsset(_selectedGuid);
                        if (GUILayout.Button("Revert")) bridge.RevertAsset(_selectedGuid);
                    }
                    if (GUILayout.Button("Excel")) bridge.OpenInExcel(_selectedGuid);
                }
            }
            else
            {
                DrawTypedInspector(bridge, typed);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private ExcelDbSerializedInspectorSession? GetTypedInspector(IExcelDbUnityEditorBridge bridge)
    {
        if (_typedInspector != null
            && ReferenceEquals(_typedInspector.Host, bridge)
            && string.Equals(_typedInspector.Guid, _selectedGuid, StringComparison.Ordinal))
            return _typedInspector;

        DisposeTypedInspector();
        _typedInspectorError = string.Empty;
        if (!ExcelDbEditor.AssetDatabase.IsAuthoringEnabled)
        {
            _typedInspectorError =
                "ExcelDB is read-only in the current runtime mode. Typed editing requires EditorAuthoring mode.";
            return null;
        }
        _propertyFactory ??= new ExcelDbAuthoringPropertyFactory();
        _propertyUndo ??= new ExcelDbUnityUndoBridge();
        try
        {
            if (ExcelDbSerializedInspectorSession.TryCreate(
                    bridge,
                    _selectedGuid,
                    _propertyFactory,
                    _propertyUndo,
                    out var session))
                _typedInspector = session;
        }
        catch (Exception exception)
        {
            _typedInspectorError = exception.Message;
        }
        return _typedInspector;
    }

    private void DrawTypedInspector(
        IExcelDbUnityEditorBridge bridge,
        ExcelDbSerializedInspectorSession typed)
    {
        if (_typedInspectorError.Length != 0)
            EditorGUILayout.HelpBox(_typedInspectorError, MessageType.Warning);
        typed.SynchronizeBeforeDraw();
        var apply = typed.LastApplyResult;
        if (apply != null && apply.Status == ExcelDb.Editor.Model.EditorApplyStatus.RevisionConflict)
        {
            EditorGUILayout.HelpBox(
                "This row changed outside the inspector. Reload it before applying staged values.",
                MessageType.Warning);
            if (GUILayout.Button("Reload external values"))
                typed.RefreshFromResident();
        }
        else if (apply != null && apply.Status == ExcelDb.Editor.Model.EditorApplyStatus.StateConflict)
        {
            EditorGUILayout.HelpBox(
                "Another inspector changed this resident row. Reload it before applying these staged values.",
                MessageType.Warning);
            if (GUILayout.Button("Reload resident values"))
                typed.RefreshFromResident();
        }

        var captured = typed;
        var changed = ExcelDbPropertyGUI.Draw(
            typed.SerializedObject,
            _propertyGuiState,
            (property, value) =>
            {
                if (!ReferenceEquals(_typedInspector, captured))
                    return;
                captured.QueueReferenceChange(property, value);
                Repaint();
            });
        if (changed)
            typed.ApplyModifiedProperties("Edit ExcelDB property");

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(!typed.CanUndo))
            {
                if (GUILayout.Button("Undo"))
                    typed.PerformUndo();
            }
            using (new EditorGUI.DisabledScope(!typed.CanRedo))
            {
                if (GUILayout.Button("Redo"))
                    typed.PerformRedo();
            }
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Save"))
            {
                _typedInspectorError = typed.Save()
                    ? string.Empty
                    : "ExcelDB did not confirm a clean saved asset; the edit remains dirty.";
            }
            if (GUILayout.Button("Revert"))
            {
                typed.Revert();
                _typedInspectorError = string.Empty;
            }
            if (GUILayout.Button("Excel"))
                bridge.OpenInExcel(_selectedGuid);
        }
    }

    private void DisposeTypedInspector()
    {
        _typedInspector?.Dispose();
        _typedInspector = null;
        _propertyGuiState.Clear();
    }

    private static void ShowWorkbookMenu(IExcelDbUnityEditorBridge bridge, string workbook)
    {
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("Open in Excel"), false, () => bridge.OpenWorkbook(workbook));
        menu.AddItem(new GUIContent("Unmount/Session only"), false, () => Unmount(bridge, workbook, false));
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
    private Vector2 _scroll;

    public static void ShowPlan(string operation)
    {
        var window = GetWindow<ExcelDbPlanWindow>("ExcelDB Plan");
        window._operation = operation;
        window.Rebuild();
    }

    private void Rebuild()
    {
        var bridge = ExcelDbUnityEditorBridge.Current;
        _plan = bridge == null ? default(UnityEditorPlanView) : bridge.BuildPlan(_operation);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField(_plan.Operation ?? _operation, EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Plan hash");
        EditorGUILayout.SelectableLabel(_plan.PlanHash ?? string.Empty, GUILayout.Height(EditorGUIUtility.singleLineHeight));
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
                if (GUILayout.Button("Revert")) bridge?.RevertPending(item.Guid);
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
        if (selected == null)
            throw new ArgumentNullException(nameof(selected));
        ExcelDbRowReferencePickerWindow.Show(
            refTable,
            refGroup,
            row => selected(row.Guid));
    }

    public static void Show(
        string refTable,
        string refGroup,
        Action<UnityEditorBrowserRow> selected)
    {
        ExcelDbRowReferencePickerWindow.Show(refTable, refGroup, selected);
    }
}

public static class ExcelDbRowReferenceDrag
{
    public const string PayloadName = "ExcelDB.RowReference.Guid";
    public const string RowPayloadName = "ExcelDB.RowReference.Row";

    public static string CurrentGuid
    {
        get { return DragAndDrop.GetGenericData(PayloadName) as string ?? string.Empty; }
    }

    public static bool HandleDrop(Rect dropArea, string ownerGuid, string propertyPath)
    {
        return HandleDrop(
            dropArea,
            guid => ExcelDbUnityEditorBridge.Current?.AssignRowReference(ownerGuid, propertyPath, guid));
    }

    public static bool TryGetCurrentRow(out UnityEditorBrowserRow row)
    {
        var value = DragAndDrop.GetGenericData(RowPayloadName);
        if (value is UnityEditorBrowserRow typed && typed.Guid.Length != 0 && typed.Table.Length != 0)
        {
            row = typed;
            return true;
        }
        row = default;
        return false;
    }

    public static bool HandleDrop(Rect dropArea, Action<string> accepted)
    {
        if (accepted == null)
            throw new ArgumentNullException(nameof(accepted));
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
        accepted(guid);
        current.Use();
        return true;
    }

    public static bool HandleDrop(Rect dropArea, Action<UnityEditorBrowserRow> accepted)
    {
        if (accepted == null)
            throw new ArgumentNullException(nameof(accepted));
        var current = Event.current;
        if (!dropArea.Contains(current.mousePosition) || !TryGetCurrentRow(out var row))
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
        accepted(row);
        current.Use();
        return true;
    }
}

internal sealed class ExcelDbRowReferencePickerWindow : EditorWindow
{
    private string _refTable = string.Empty;
    private string _refGroup = string.Empty;
    private string _filter = string.Empty;
    private Action<UnityEditorBrowserRow>? _selected;
    private Vector2 _scroll;

    public static void Show(
        string refTable,
        string refGroup,
        Action<UnityEditorBrowserRow> selected)
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
                _selected?.Invoke(row);
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
    private static bool _reloadLocked;

    internal static bool ReloadLocked
    {
        get { return _reloadLocked; }
    }

    [InitializeOnLoadMethod]
    private static void Install()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        EditorApplication.update += SynchronizeReloadLock;
        CompilationPipeline.compilationStarted += OnCompilationStarted;
        AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        EditorApplication.wantsToQuit += OnWantsToQuit;
    }

    private static void OnCompilationStarted(object context)
    {
        SynchronizeReloadLock();
    }

    internal static void SynchronizeReloadLock()
    {
        var bridge = ExcelDbUnityEditorBridge.Current;
        var shouldLock = bridge != null
                         && (bridge.BrowserCounts.Dirty != 0
                             || bridge.PendingChanges.Count != 0
                             || bridge.Conflicts.Count != 0);
        if (shouldLock == _reloadLocked)
            return;

        if (shouldLock)
        {
            EditorApplication.LockReloadAssemblies();
            _reloadLocked = true;
        }
        else
        {
            _reloadLocked = false;
            EditorApplication.UnlockReloadAssemblies();
        }
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
        var bridge = ExcelDbUnityEditorBridge.Current;
        if (bridge != null
            && (bridge.BrowserCounts.Dirty != 0
                || bridge.PendingChanges.Count != 0
                || bridge.Conflicts.Count != 0))
        {
            throw new InvalidOperationException(
                "ExcelDB assembly reload reached the final callback with pending authoring state. "
                + "The proactive reload lock must remain held until the changes are saved or reverted.");
        }
    }

    private static bool OnWantsToQuit()
    {
        return ExcelDbUnityEditorBridge.Current?.GuardDirty("quit") ?? true;
    }
}
}
#endif
