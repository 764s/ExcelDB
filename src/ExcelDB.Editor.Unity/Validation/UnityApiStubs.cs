#if EXCELDB_DOTNET_VALIDATE
using System;

namespace UnityEngine
{
public class Object { }
public class ScriptableObject : Object { }
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
    public static T CreateInstance<T>() where T : EditorWindow, new() { return new T(); }
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
    public static void HelpBox(string message, MessageType type) { }
    public static void LabelField(string label, params GUILayoutOption[] options) { }
    public static void LabelField(string label, string value, params GUILayoutOption[] options) { }
    public static void LabelField(string label, GUIStyle style, params GUILayoutOption[] options) { }
    public static string TextField(string label, string text) { return text; }
    public static bool Toggle(string label, bool value) { return value; }
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
    public static void BeginChangeCheck() { }
    public static bool EndChangeCheck() { return false; }
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
    public static event Action<PlayModeStateChange> playModeStateChanged { add { } remove { } }
    public static event Func<bool> wantsToQuit { add { } remove { } }
    public static bool isPlaying { get; set; }
}
public static class AssemblyReloadEvents
{
    public static event Action beforeAssemblyReload { add { } remove { } }
}
}
#endif
