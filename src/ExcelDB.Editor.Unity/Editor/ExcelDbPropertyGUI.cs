#if UNITY_EDITOR || EXCELDB_DOTNET_VALIDATE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using ExcelDb.Editor.Model;
using UnityEditor;
using UnityEngine;

namespace ExcelDb.Editor.Unity
{

public sealed class ExcelDbPropertyGUIState
{
    private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);

    public bool IsExpanded(string propertyPath)
    {
        return _expanded.Contains(propertyPath);
    }

    public void SetExpanded(string propertyPath, bool expanded)
    {
        if (expanded)
            _expanded.Add(propertyPath);
        else
            _expanded.Remove(propertyPath);
    }

    public void Clear()
    {
        _expanded.Clear();
    }
}

/// <summary>Converts an actual picker/drag row into a constraint-validated RowRef value.</summary>
public static class ExcelDbRowReferenceBinding
{
    public static bool TryCreateValue(
        EditorReferenceConstraint constraint,
        UnityEditorBrowserRow row,
        out EditorReferenceValue? value)
    {
        if (constraint == null)
            throw new ArgumentNullException(nameof(constraint));
        foreach (var allowed in constraint.AllowedTargetTables)
        {
            if (!string.Equals(allowed.FullName, row.Table, StringComparison.Ordinal))
                continue;
            value = new EditorReferenceValue(
                allowed.TableId,
                allowed.FullName,
                row.Guid,
                row.Key);
            return constraint.ResolveTarget(value) != null;
        }
        value = null;
        return false;
    }
}

/// <summary>Generic IMGUI projection for the host-neutral ExcelDB serialized-property model.</summary>
public static class ExcelDbPropertyGUI
{
    public static bool Draw(
        EditorSerializedObject serializedObject,
        ExcelDbPropertyGUIState state,
        Action<EditorSerializedProperty, EditorReferenceValue?> applyReference)
    {
        if (serializedObject == null)
            throw new ArgumentNullException(nameof(serializedObject));
        if (state == null)
            throw new ArgumentNullException(nameof(state));
        if (applyReference == null)
            throw new ArgumentNullException(nameof(applyReference));

        var changed = false;
        foreach (var property in serializedObject.RootProperties)
            changed |= DrawProperty(property, state, applyReference, 0);
        return changed;
    }

    private static bool DrawProperty(
        EditorSerializedProperty property,
        ExcelDbPropertyGUIState state,
        Action<EditorSerializedProperty, EditorReferenceValue?> applyReference,
        int recursionDepth)
    {
        if (recursionDepth > 32)
        {
            EditorGUILayout.HelpBox("Nested property depth exceeds the editor safety limit.", MessageType.Error);
            return false;
        }

        var previousIndent = EditorGUI.indentLevel;
        EditorGUI.indentLevel = Math.Max(previousIndent, property.Depth);
        var previousMixed = EditorGUI.showMixedValue;
        EditorGUI.showMixedValue = property.HasMultipleDifferentValues;
        try
        {
            // A generated property-group is a read-only projection container, but its leaf
            // properties and optional-presence bit remain independently editable.
            using (new EditorGUI.DisabledScope(property.IsReadOnly && !property.IsGroupContainer))
            {
                if (property.Kind == EditorPropertyKind.Unsupported)
                    return DrawUnsupported(property);
                if (property.Kind == EditorPropertyKind.Reference && property.HasReferenceAdapter)
                    return DrawReference(property, applyReference);
                if (property.IsArray || property.Kind == EditorPropertyKind.List)
                    return DrawList(property, state, applyReference, recursionDepth);
                if (property.Kind == EditorPropertyKind.Object || property.HasChildren)
                    return DrawObject(property, state, applyReference, recursionDepth);
                return DrawScalar(property);
            }
        }
        finally
        {
            EditorGUI.showMixedValue = previousMixed;
            EditorGUI.indentLevel = previousIndent;
        }
    }

    private static bool DrawUnsupported(EditorSerializedProperty property)
    {
        var display = property.HasMultipleDifferentValues
            ? "— mixed —"
            : FormatReadOnlyValue(property.BoxedValue);
        EditorGUILayout.LabelField(Label(property), display);
        EditorGUILayout.HelpBox(
            "This property is read-only in the ExcelDB Inspector. "
            + (property.UnsupportedReason
               ?? "The generated property shape is not supported by this editor."),
            MessageType.Warning);
        return false;
    }

    private static bool DrawScalar(EditorSerializedProperty property)
    {
        var label = Label(property);
        var value = property.BoxedValue;
        var valueType = Nullable.GetUnderlyingType(property.ValueType) ?? property.ValueType;
        EditorGUI.BeginChangeCheck();
        object? next = value;

        if (valueType == typeof(string))
            next = EditorGUILayout.TextField(label, value as string ?? string.Empty);
        else if (valueType == typeof(bool))
            next = EditorGUILayout.Toggle(label, value is bool current && current);
        else if (valueType == typeof(int))
            next = EditorGUILayout.IntField(label, value is int current ? current : 0);
        else if (valueType == typeof(long))
            next = EditorGUILayout.LongField(label, value is long current ? current : 0L);
        else if (valueType == typeof(float))
            next = EditorGUILayout.FloatField(label, value is float current ? current : 0f);
        else if (valueType == typeof(double))
            next = EditorGUILayout.DoubleField(label, value is double current ? current : 0d);
        else if (valueType.IsEnum)
        {
            var names = Enum.GetNames(valueType);
            var values = Enum.GetValues(valueType);
            var selected = 0;
            for (var index = 0; index < values.Length; index++)
            {
                if (Equals(values.GetValue(index), value))
                {
                    selected = index;
                    break;
                }
            }
            selected = EditorGUILayout.Popup(label, selected, names);
            next = values.GetValue(selected);
        }
        else if (IsInteger(valueType))
        {
            var current = value == null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            var edited = EditorGUILayout.LongField(label, current);
            next = Convert.ChangeType(edited, valueType, CultureInfo.InvariantCulture);
        }
        else
        {
            EditorGUILayout.LabelField(label, value?.ToString() ?? "null");
        }

        if (!EditorGUI.EndChangeCheck())
            return false;
        property.BoxedValue = next;
        return true;
    }

    private static bool DrawObject(
        EditorSerializedProperty property,
        ExcelDbPropertyGUIState state,
        Action<EditorSerializedProperty, EditorReferenceValue?> applyReference,
        int recursionDepth)
    {
        var path = property.PropertyPath;
        var expanded = state.IsExpanded(path);
        var presenceChanged = false;
        var hasMixedPresence = property.HasPresence
            && property.HasMultipleDifferentPresenceValues;
        var isPresent = !property.HasPresence || property.HasPresenceValue;
        using (new EditorGUILayout.HorizontalScope())
        {
            var nextExpanded = EditorGUILayout.Foldout(expanded, Label(property), true);
            state.SetExpanded(path, nextExpanded);
            expanded = nextExpanded;

            if (property.HasPresence)
            {
                GUILayout.FlexibleSpace();
                var previousMixed = EditorGUI.showMixedValue;
                EditorGUI.showMixedValue = hasMixedPresence;
                try
                {
                    EditorGUI.BeginChangeCheck();
                    using (new EditorGUI.DisabledScope(property.IsReadOnly && !property.IsGroupContainer))
                        isPresent = EditorGUILayout.Toggle("Present", isPresent);
                    if (EditorGUI.EndChangeCheck())
                    {
                        property.SetPresence(isPresent);
                        presenceChanged = true;
                        hasMixedPresence = false;
                    }
                }
                finally
                {
                    EditorGUI.showMixedValue = previousMixed;
                }
            }
        }

        if (hasMixedPresence)
        {
            EditorGUILayout.HelpBox(
                "The selected rows do not agree on whether this optional value is present. "
                + "Choose Present or clear it before editing child properties.",
                MessageType.Info);
            return presenceChanged;
        }
        if (!isPresent || !expanded)
            return presenceChanged;

        if (property.BoxedValue == null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Value", "null");
                if (GUILayout.Button("Create", GUILayout.Width(58)))
                {
                    property.BoxedValue = Activator.CreateInstance(property.ValueType);
                    return true;
                }
            }
            return false;
        }

        var changed = presenceChanged;
        foreach (var child in property.Children)
            changed |= DrawProperty(child, state, applyReference, recursionDepth + 1);
        return changed;
    }

    private static bool DrawList(
        EditorSerializedProperty property,
        ExcelDbPropertyGUIState state,
        Action<EditorSerializedProperty, EditorReferenceValue?> applyReference,
        int recursionDepth)
    {
        var path = property.PropertyPath;
        var expanded = state.IsExpanded(path);
        using (new EditorGUILayout.HorizontalScope())
        {
            var nextExpanded = EditorGUILayout.Foldout(expanded, Label(property), true);
            state.SetExpanded(path, nextExpanded);
            expanded = nextExpanded;
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("Size", property.HasMultipleDifferentArraySizes ? "—" : property.ArraySize.ToString(CultureInfo.InvariantCulture));
        }
        if (!expanded)
            return false;

        // A multi-object list has no safe element projection until every target agrees on its
        // length. EditorSerializedProperty intentionally reports the first size for display, but
        // indexing it would fail for shorter targets. Let the user normalize the size first.
        if (property.HasMultipleDifferentArraySizes)
        {
            EditorGUILayout.HelpBox(
                "The selected rows have different list sizes. Set one common size before editing elements.",
                MessageType.Info);
            EditorGUI.BeginChangeCheck();
            var unifiedSize = EditorGUILayout.DelayedIntField("Common size", property.ArraySize);
            if (EditorGUI.EndChangeCheck())
            {
                property.ArraySize = Math.Max(0, unifiedSize);
                return true;
            }
            return false;
        }

        var changed = false;
        var fixedSize = property.ValueType.IsArray || property.IsReadOnly;
        using (new EditorGUI.DisabledScope(fixedSize))
        {
            var size = property.ArraySize;
            var nextSize = EditorGUILayout.DelayedIntField("Size", size);
            if (nextSize != size)
            {
                property.ArraySize = Math.Max(0, nextSize);
                changed = true;
            }
            if (GUILayout.Button("Add element"))
            {
                property.InsertArrayElementAtIndex(property.ArraySize);
                changed = true;
            }
        }

        var count = property.ArraySize;
        for (var index = 0; index < count; index++)
        {
            var element = property.GetArrayElementAtIndex(index);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                    changed |= DrawProperty(element, state, applyReference, recursionDepth + 1);
                using (new EditorGUI.DisabledScope(fixedSize || index == 0))
                {
                    if (GUILayout.Button("↑", GUILayout.Width(24)))
                    {
                        property.MoveArrayElement(index, index - 1);
                        return true;
                    }
                }
                using (new EditorGUI.DisabledScope(fixedSize || index + 1 >= count))
                {
                    if (GUILayout.Button("↓", GUILayout.Width(24)))
                    {
                        property.MoveArrayElement(index, index + 1);
                        return true;
                    }
                }
                using (new EditorGUI.DisabledScope(fixedSize))
                {
                    if (GUILayout.Button("−", GUILayout.Width(24)))
                    {
                        property.DeleteArrayElementAtIndex(index);
                        return true;
                    }
                }
            }
        }
        return changed;
    }

    private static bool DrawReference(
        EditorSerializedProperty property,
        Action<EditorSerializedProperty, EditorReferenceValue?> applyReference)
    {
        var constraint = property.ReferenceConstraint!;
        var mixed = property.HasMultipleDifferentValues;
        var current = mixed ? null : property.ReferenceValue;
        var display = mixed
            ? "— mixed —"
            : current == null
                ? "None"
                : (constraint.ReferenceGroup == null
                    ? current.DisplayKey ?? current.TargetIdentity
                    : current.TargetTable + "/" + (current.DisplayKey ?? current.TargetIdentity));

        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(Label(property), display);
            if (GUILayout.Button("Select", GUILayout.Width(58)))
            {
                ExcelDbRowReferencePicker.Show(
                    constraint.ReferenceTable ?? string.Empty,
                    constraint.ReferenceGroup ?? string.Empty,
                    (UnityEditorBrowserRow row) =>
                    {
                        if (ExcelDbRowReferenceBinding.TryCreateValue(constraint, row, out var value))
                            applyReference(property, value);
                    });
            }
            using (new EditorGUI.DisabledScope(!constraint.AllowNull))
            {
                if (GUILayout.Button("Clear", GUILayout.Width(50)))
                {
                    property.ReferenceValue = null;
                    return true;
                }
            }
        }

        var dropArea = GUILayoutUtility.GetLastRect();
        var dropped = false;
        ExcelDbRowReferenceDrag.HandleDrop(dropArea, (UnityEditorBrowserRow row) =>
        {
            if (ExcelDbRowReferenceBinding.TryCreateValue(constraint, row, out var value))
            {
                property.ReferenceValue = value;
                dropped = true;
            }
        });
        return dropped;
    }

    private static string Label(EditorSerializedProperty property)
    {
        if (!string.IsNullOrWhiteSpace(property.DisplayName))
            return property.DisplayName!;
        var path = property.PropertyPath;
        var dot = path.LastIndexOf('.');
        return dot < 0 ? path : path.Substring(dot + 1);
    }

    private static bool IsInteger(Type type)
    {
        return type == typeof(byte)
               || type == typeof(sbyte)
               || type == typeof(short)
               || type == typeof(ushort)
               || type == typeof(uint);
    }

    private static string FormatReadOnlyValue(object? value)
    {
        if (value == null)
            return "null";
        if (value is string text)
            return text;
        if (value is IEnumerable sequence)
        {
            var items = new List<string>();
            var truncated = false;
            foreach (var item in sequence)
            {
                if (items.Count == 4)
                {
                    truncated = true;
                    break;
                }
                items.Add(Convert.ToString(item, CultureInfo.InvariantCulture) ?? "null");
            }
            return "[" + string.Join(", ", items) + (truncated ? ", …" : string.Empty) + "]";
        }
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name;
    }
}
}
#endif
