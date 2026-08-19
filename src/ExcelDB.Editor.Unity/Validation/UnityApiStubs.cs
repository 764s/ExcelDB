#if EXCELDB_DOTNET_VALIDATE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityEngine
{
[AttributeUsage(AttributeTargets.Field)]
public sealed class SerializeField : Attribute { }
public enum HideFlags { None = 0, HideAndDontSave = 61 }
public class Object
{
    public HideFlags hideFlags { get; set; }
    public static void DestroyImmediate(Object target) { }
}
public class ScriptableObject : Object
{
    public static T CreateInstance<T>() where T : ScriptableObject, new() { return new T(); }
}
public sealed class DefaultAsset : Object { }
public struct Vector2 { }
public struct Rect
{
    public float width;
    public bool Contains(Vector2 point) { return false; }
}
public sealed class GUIContent
{
    public GUIContent(string text) { }
}
public sealed class GUIStyle { }
public sealed class GUISkin
{
    public GUIStyle FindStyle(string name) { return new GUIStyle(); }
}
public sealed class GUILayoutOption { }
public static class GUI
{
    public static GUISkin skin { get; } = new GUISkin();
    public static bool enabled { get; set; }
    public static bool changed { get; set; }
}
public static class GUILayout
{
    public static string TextField(string text, GUIStyle style, params GUILayoutOption[] options) { return text; }
    public static bool Button(string text, params GUILayoutOption[] options) { return false; }
    public static bool Button(string text, GUIStyle style, params GUILayoutOption[] options) { return false; }
    public static bool Toggle(bool value, string text, string style, params GUILayoutOption[] options) { return value; }
    public static void Label(string text, params GUILayoutOption[] options) { }
    public static void Label(string text, GUIStyle style, params GUILayoutOption[] options) { }
    public static void Space(float pixels) { }
    public static void FlexibleSpace() { }
    public static GUILayoutOption Width(float value) { return new GUILayoutOption(); }
    public static GUILayoutOption MinWidth(float value) { return new GUILayoutOption(); }
    public static GUILayoutOption Height(float value) { return new GUILayoutOption(); }
}
public static class GUILayoutUtility
{
    public static Rect GetLastRect() { return new Rect(); }
}
public enum EventType { MouseDrag, DragUpdated, DragPerform }
public sealed class Event
{
    public static Event current { get; } = new Event();
    public EventType type { get; set; }
    public Vector2 mousePosition { get; set; }
    public int clickCount { get; set; }
    public void Use() { }
}
public static class Debug
{
    public static void LogError(object message) { }
}
}

namespace UnityEditor
{
using UnityEngine;

[AttributeUsage(AttributeTargets.Method)]
public sealed class MenuItemAttribute : Attribute
{
    public MenuItemAttribute(string itemName, bool isValidateFunction = false, int priority = 0) { }
    public int priority { get; set; }
}
[AttributeUsage(AttributeTargets.Method)]
public sealed class InitializeOnLoadMethodAttribute : Attribute { }
[AttributeUsage(AttributeTargets.Method)]
public sealed class SettingsProviderAttribute : Attribute { }
[AttributeUsage(AttributeTargets.Class)]
public sealed class CustomEditorAttribute : Attribute
{
    public CustomEditorAttribute(Type inspectedType) { }
}

public enum MessageType { None, Info, Warning, Error }
public enum SettingsScope { Project }
public enum PlayModeStateChange { EnteredEditMode, ExitingEditMode, EnteredPlayMode, ExitingPlayMode }

public class EditorWindow : ScriptableObject
{
    public Rect position { get; set; }
    public GUIContent titleContent { get; set; } = new GUIContent(string.Empty);
    public static T GetWindow<T>(string title) where T : EditorWindow, new() { return new T(); }
    public new static T CreateInstance<T>() where T : EditorWindow, new() { return new T(); }
    public void ShowAuxWindow() { }
    public void Repaint() { }
    public void Close() { }
}

public class Editor : ScriptableObject
{
    public Object target { get; set; } = new Object();
    public virtual void OnInspectorGUI() { }
    protected void DrawDefaultInspector() { }
}

public static class EditorStyles
{
    public static GUIStyle toolbar { get; } = new GUIStyle();
    public static GUIStyle toolbarButton { get; } = new GUIStyle();
    public static GUIStyle label { get; } = new GUIStyle();
    public static GUIStyle miniBoldLabel { get; } = new GUIStyle();
    public static GUIStyle boldLabel { get; } = new GUIStyle();
    public static GUIStyle wordWrappedLabel { get; } = new GUIStyle();
}

public static class EditorGUILayout
{
    public static string? LastHelpBoxMessage { get; private set; }
    public static MessageType LastHelpBoxType { get; private set; }
    public static string? LastLabel { get; private set; }
    public static string? LastLabelValue { get; private set; }
    public static string? LastToggleLabel { get; private set; }
    public static bool LastToggleValue { get; private set; }
    public static bool LastToggleHadMixedValue { get; private set; }
    public static bool? NextToggleValue { get; set; }

    public static void ResetValidationState()
    {
        LastHelpBoxMessage = null;
        LastHelpBoxType = MessageType.None;
        LastLabel = null;
        LastLabelValue = null;
        LastToggleLabel = null;
        LastToggleValue = false;
        LastToggleHadMixedValue = false;
        NextToggleValue = null;
        EditorGUI.NextChangeCheckResult = false;
    }

    public sealed class HorizontalScope : IDisposable
    {
        public HorizontalScope(params GUILayoutOption[] options) { }
        public HorizontalScope(GUIStyle style, params GUILayoutOption[] options) { }
        public void Dispose() { }
    }
    public sealed class VerticalScope : IDisposable
    {
        public VerticalScope(params GUILayoutOption[] options) { }
        public VerticalScope(GUIStyle style, params GUILayoutOption[] options) { }
        public void Dispose() { }
    }
    public static void HelpBox(string message, MessageType type)
    {
        LastHelpBoxMessage = message;
        LastHelpBoxType = type;
    }
    public static void LabelField(string label, params GUILayoutOption[] options)
    {
        LastLabel = label;
        LastLabelValue = null;
    }
    public static void LabelField(string label, string value, params GUILayoutOption[] options)
    {
        LastLabel = label;
        LastLabelValue = value;
    }
    public static void LabelField(string label, GUIStyle style, params GUILayoutOption[] options) { }
    public static string TextField(string label, string text) { return text; }
    public static int IntField(string label, int value) { return value; }
    public static long LongField(string label, long value) { return value; }
    public static float FloatField(string label, float value) { return value; }
    public static double DoubleField(string label, double value) { return value; }
    public static int DelayedIntField(string label, int value) { return value; }
    public static int Popup(string label, int selectedIndex, string[] displayedOptions) { return selectedIndex; }
    public static bool Foldout(bool foldout, string content, bool toggleOnLabelClick = false) { return foldout; }
    public static bool Toggle(string label, bool value)
    {
        LastToggleLabel = label;
        LastToggleHadMixedValue = EditorGUI.showMixedValue;
        var result = NextToggleValue ?? value;
        NextToggleValue = null;
        LastToggleValue = result;
        return result;
    }
    public static Vector2 BeginScrollView(Vector2 scrollPosition) { return scrollPosition; }
    public static void EndScrollView() { }
    public static void SelectableLabel(string text, params GUILayoutOption[] options) { }
    public static void Space() { }
}

public static class EditorGUI
{
    public sealed class DisabledScope : IDisposable
    {
        public DisabledScope(bool disabled) { }
        public void Dispose() { }
    }
    public static bool showMixedValue { get; set; }
    public static int indentLevel { get; set; }
    public static bool NextChangeCheckResult { get; set; }
    public static void BeginChangeCheck() { }
    public static bool EndChangeCheck()
    {
        var result = NextChangeCheckResult;
        NextChangeCheckResult = false;
        return result;
    }
}
public static class EditorGUIUtility
{
    public static float singleLineHeight { get; } = 18f;
}

public sealed class SettingsProvider
{
    public SettingsProvider(string path, SettingsScope scope) { }
    public string label { get; set; } = string.Empty;
    public Action<string> guiHandler { get; set; } = delegate { };
}
public static class SettingsService
{
    public static void OpenProjectSettings(string path) { }
}
public static class AssetDatabase
{
    public static string GetAssetPath(Object target) { return string.Empty; }
}
public static class EditorUtility
{
    public static bool DisplayDialog(string title, string message, string ok, string cancel = "") { return false; }
    public static int DisplayDialogComplex(string title, string message, string ok, string cancel, string alternate) { return 2; }
    public static string SaveFilePanel(string title, string directory, string defaultName, string extension) { return string.Empty; }
    public static void SetDirty(Object target) { }
}
public static class Undo
{
    private sealed class NativeRecord
    {
        public NativeRecord(Object target, FieldValue[] before, FieldValue[] after)
        {
            Target = target;
            Before = before;
            After = after;
        }

        public Object Target { get; }
        public FieldValue[] Before { get; }
        public FieldValue[] After { get; }
    }

    private readonly struct FieldValue
    {
        public FieldValue(FieldInfo field, object? value)
        {
            Field = field;
            Value = value;
        }

        public FieldInfo Field { get; }
        public object? Value { get; }
    }

    private static readonly List<NativeRecord> UndoRecords = new List<NativeRecord>();
    private static readonly List<NativeRecord> RedoRecords = new List<NativeRecord>();
    private static Object? _pendingTarget;
    private static FieldValue[]? _pendingBefore;

    public static event Action? undoRedoPerformed;
    public static int GetCurrentGroup() { return 0; }
    public static void IncrementCurrentGroup() { }
    public static void SetCurrentGroupName(string name) { }
    public static void RecordObject(Object target, string name)
    {
        _pendingTarget = target;
        _pendingBefore = Capture(target);
    }
    public static void RegisterCompleteObjectUndo(Object target, string name) { RecordObject(target, name); }
    public static void CollapseUndoOperations(int groupIndex) { }
    public static void FlushUndoRecordObjects()
    {
        if (_pendingTarget == null || _pendingBefore == null)
            return;
        UndoRecords.Add(new NativeRecord(_pendingTarget, _pendingBefore, Capture(_pendingTarget)));
        RedoRecords.Clear();
        _pendingTarget = null;
        _pendingBefore = null;
    }
    public static void PerformUndo()
    {
        if (UndoRecords.Count == 0)
            return;
        var index = UndoRecords.Count - 1;
        var record = UndoRecords[index];
        UndoRecords.RemoveAt(index);
        Restore(record.Target, record.Before);
        RedoRecords.Add(record);
        undoRedoPerformed?.Invoke();
    }
    public static void PerformRedo()
    {
        if (RedoRecords.Count == 0)
            return;
        var index = RedoRecords.Count - 1;
        var record = RedoRecords[index];
        RedoRecords.RemoveAt(index);
        Restore(record.Target, record.After);
        UndoRecords.Add(record);
        undoRedoPerformed?.Invoke();
    }
    public static void ClearUndo(Object target)
    {
        UndoRecords.RemoveAll(record => ReferenceEquals(record.Target, target));
        RedoRecords.RemoveAll(record => ReferenceEquals(record.Target, target));
        if (ReferenceEquals(_pendingTarget, target))
        {
            _pendingTarget = null;
            _pendingBefore = null;
        }
    }

    private static FieldValue[] Capture(Object target)
    {
        return target.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static field => !field.IsStatic)
            .Select(field => new FieldValue(field, field.GetValue(target)))
            .ToArray();
    }

    private static void Restore(Object target, IEnumerable<FieldValue> values)
    {
        foreach (var value in values)
            value.Field.SetValue(target, value.Value);
    }
}
public sealed class GenericMenu
{
    public void AddItem(GUIContent content, bool on, Action action) { }
    public void AddSeparator(string path) { }
    public void ShowAsContext() { }
}
public static class DragAndDrop
{
    public static Object[] objectReferences { get; set; } = Array.Empty<Object>();
    public static DragAndDropVisualMode visualMode { get; set; }
    public static void PrepareStartDrag() { }
    public static void AcceptDrag() { }
    public static void SetGenericData(string type, object data) { }
    public static object GetGenericData(string type) { return new object(); }
    public static void StartDrag(string title) { }
}
public enum DragAndDropVisualMode { None, Link }
public static class EditorApplication
{
    public static event Action update { add { } remove { } }
    public static event Action<PlayModeStateChange> playModeStateChanged { add { } remove { } }
    public static event Func<bool> wantsToQuit { add { } remove { } }
    public static bool isPlaying { get; set; }
    public static void LockReloadAssemblies() { }
    public static void UnlockReloadAssemblies() { }
}
public static class AssemblyReloadEvents
{
    public static event Action beforeAssemblyReload { add { } remove { } }
}
}

namespace UnityEditor.Compilation
{
using System;

public static class CompilationPipeline
{
    public static event Action<object> compilationStarted { add { } remove { } }
}
}
#endif
