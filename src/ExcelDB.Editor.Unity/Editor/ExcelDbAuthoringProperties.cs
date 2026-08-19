#if UNITY_EDITOR || EXCELDB_DOTNET_VALIDATE
using System;
using System.Collections.Generic;
using System.Linq;
using ExcelDb.Editor.Model;
using UnityEditor;
using UnityEngine;
using ExcelAssetDatabase = ExcelDbEditor.AssetDatabase;
using ExcelEditorUtility = ExcelDbEditor.EditorUtility;
using UnityEditorUtility = UnityEditor.EditorUtility;

namespace ExcelDb.Editor.Unity
{

/// <summary>
/// Creates host-neutral staged property views over resident ExcelDB assets. Generated code or the
/// trusted host supplies only the binding; identity, revision and shared history are owned here.
/// </summary>
public sealed class ExcelDbAuthoringPropertyFactory
{
    private readonly EditorUndoHistory _history;

    public ExcelDbAuthoringPropertyFactory(int undoDepth = 100)
    {
        _history = new EditorUndoHistory(undoDepth);
    }

    public EditorUndoHistory History
    {
        get { return _history; }
    }

    public EditorSerializedObject Create(EditorObjectBinding binding, object residentAsset)
    {
        EnsureAuthoringEnabled();
        if (binding == null)
            throw new ArgumentNullException(nameof(binding));
        if (residentAsset == null)
            throw new ArgumentNullException(nameof(residentAsset));
        if (!binding.TargetType.IsInstanceOfType(residentAsset))
            throw new ArgumentException("The resident asset does not match the editor binding type.", nameof(residentAsset));
        if (!ExcelAssetDatabase.Contains(residentAsset))
            throw new ArgumentException("The object is not a resident ExcelDB asset.", nameof(residentAsset));

        var path = ExcelAssetDatabase.GetAssetPath(residentAsset);
        var guid = ExcelAssetDatabase.AssetPathToGUID(path);
        if (guid.Length == 0)
            throw new InvalidOperationException("The resident ExcelDB asset has no stable GUID.");
        var target = new EditorEditTarget(
            guid,
            residentAsset,
            () => ExcelAssetDatabase.GetAssetRevisionToken(residentAsset));
        return new EditorSerializedObject(binding, new[] { target }, _history);
    }

    public EditorSerializedObject Create(EditorObjectBinding binding, IEnumerable<object> residentAssets)
    {
        EnsureAuthoringEnabled();
        if (binding == null)
            throw new ArgumentNullException(nameof(binding));
        if (residentAssets == null)
            throw new ArgumentNullException(nameof(residentAssets));

        var targets = residentAssets.Select(asset =>
        {
            if (asset == null || !binding.TargetType.IsInstanceOfType(asset))
                throw new ArgumentException("Every resident asset must match the editor binding type.", nameof(residentAssets));
            if (!ExcelAssetDatabase.Contains(asset))
                throw new ArgumentException("Every object must be a resident ExcelDB asset.", nameof(residentAssets));
            var path = ExcelAssetDatabase.GetAssetPath(asset);
            var guid = ExcelAssetDatabase.AssetPathToGUID(path);
            if (guid.Length == 0)
                throw new InvalidOperationException("A resident ExcelDB asset has no stable GUID.");
            return new EditorEditTarget(guid, asset, () => ExcelAssetDatabase.GetAssetRevisionToken(asset));
        }).ToArray();
        return new EditorSerializedObject(binding, targets, _history);
    }

    private static void EnsureAuthoringEnabled()
    {
        if (!ExcelAssetDatabase.IsAuthoringEnabled)
            throw new InvalidOperationException(
                "ExcelDB typed editing is available only in EditorAuthoring mode.");
    }

    internal static void MarkDirty(IEnumerable<EditorEditTarget> targets)
    {
        foreach (var target in targets
                     .GroupBy(static item => item.Identity, StringComparer.Ordinal)
                     .Select(static group => group.First()))
            ExcelEditorUtility.SetDirty(target.Value);
    }

    internal static void MarkDirty(EditorSerializedObject serializedObject, IReadOnlyList<string> identities)
    {
        var changed = new HashSet<string>(identities, StringComparer.Ordinal);
        MarkDirty(serializedObject.Targets.Where(target => changed.Contains(target.Identity)));
    }
}

[Serializable]
internal sealed class ExcelDbUnityUndoState : ScriptableObject
{
    [SerializeField] private long _generation = 1;
    [SerializeField] private long _position;

    public EditorHistoryToken Token
    {
        get { return new EditorHistoryToken(_generation, _position); }
    }

    public void Set(EditorHistoryToken token)
    {
        _generation = token.Generation;
        _position = token.Position;
    }
}

/// <summary>
/// Bridges the host-neutral history to Unity's native Undo stack through a hidden serialized proxy.
/// Unity owns keyboard/menu ordering; ExcelDB owns the actual resident snapshots.
/// </summary>
public sealed class ExcelDbUnityUndoBridge : IDisposable
{
    private readonly ExcelDbUnityUndoState _state;
    private EditorUndoHistory? _history;
    private bool _handling;
    private bool _disposed;

    public ExcelDbUnityUndoBridge()
    {
        _state = ScriptableObject.CreateInstance<ExcelDbUnityUndoState>();
        _state.hideFlags = HideFlags.HideAndDontSave;
        Undo.undoRedoPerformed += OnUnityUndoRedo;
    }

    public bool CanUndo
    {
        get { return _history?.CanUndo ?? false; }
    }

    public bool CanRedo
    {
        get { return _history?.CanRedo ?? false; }
    }

    public void Bind(EditorUndoHistory history)
    {
        ThrowIfDisposed();
        if (history == null)
            throw new ArgumentNullException(nameof(history));
        if (ReferenceEquals(_history, history))
            return;

        if (_history != null)
            _history.Changed -= OnHistoryChanged;
        Undo.ClearUndo(_state);
        _history = history;
        _history.Changed += OnHistoryChanged;
        SetState(_history.CaptureToken());
    }

    public EditorApplyResult ApplyModifiedProperties(EditorSerializedObject serializedObject, string label)
    {
        ThrowIfDisposed();
        if (serializedObject == null)
            throw new ArgumentNullException(nameof(serializedObject));
        Bind(serializedObject.History);

        var history = serializedObject.History;
        var before = history.CaptureToken();
        _handling = true;
        EditorApplyResult result;
        try
        {
            result = serializedObject.ApplyModifiedProperties(label);
        }
        finally
        {
            _handling = false;
        }

        if (result.Status != EditorApplyStatus.Applied)
            return result;

        var after = history.CaptureToken();
        var generationChanged = before.Generation != after.Generation;
        var nativeBefore = generationChanged
            ? new EditorHistoryToken(after.Generation, Math.Max(0, after.Position - 1))
            : before;
        RecordNativeUndo(label, nativeBefore, after, generationChanged);
        ExcelDbAuthoringPropertyFactory.MarkDirty(serializedObject, result.ChangedTargets);
        ExcelDbEditorLifecycle.SynchronizeReloadLock();
        return result;
    }

    public void PerformUndo()
    {
        ThrowIfDisposed();
        Undo.PerformUndo();
    }

    public void PerformRedo()
    {
        ThrowIfDisposed();
        Undo.PerformRedo();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Undo.undoRedoPerformed -= OnUnityUndoRedo;
        if (_history != null)
            _history.Changed -= OnHistoryChanged;
        Undo.ClearUndo(_state);
        UnityEngine.Object.DestroyImmediate(_state);
    }

    private void RecordNativeUndo(
        string label,
        EditorHistoryToken before,
        EditorHistoryToken after,
        bool resetHistory)
    {
        if (resetHistory)
            Undo.ClearUndo(_state);
        SetState(before);
        Undo.IncrementCurrentGroup();
        var group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName(label);
        Undo.RecordObject(_state, label);
        SetState(after);
        UnityEditorUtility.SetDirty(_state);
        Undo.FlushUndoRecordObjects();
        Undo.CollapseUndoOperations(group);
    }

    private void OnUnityUndoRedo()
    {
        if (_handling || _history == null || _disposed)
            return;

        _handling = true;
        EditorHistoryResult result;
        try
        {
            result = _history.TryMoveTo(_state.Token);
        }
        finally
        {
            _handling = false;
        }

        if (!result.Succeeded)
        {
            Undo.ClearUndo(_state);
            SetState(_history.CaptureToken());
            Debug.LogError(
                "ExcelDB rejected Unity Undo/Redo because the resident revision or history token changed ("
                + result.Status + ").");
            return;
        }

        ExcelDbAuthoringPropertyFactory.MarkDirty(result.AppliedTargets);
        SetState(_history.CaptureToken());
        ExcelDbEditorLifecycle.SynchronizeReloadLock();
    }

    private void OnHistoryChanged(object? sender, EditorHistoryChangedEventArgs args)
    {
        if (_handling || _history == null)
            return;

        // A history mutation outside this bridge has no matching Unity proxy record. Resetting this
        // proxy prevents an older native token from replaying an ambiguous branch.
        Undo.ClearUndo(_state);
        SetState(_history.CaptureToken());
    }

    private void SetState(EditorHistoryToken token)
    {
        _state.Set(token);
        UnityEditorUtility.SetDirty(_state);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ExcelDbUnityUndoBridge));
    }
}
}
#endif
