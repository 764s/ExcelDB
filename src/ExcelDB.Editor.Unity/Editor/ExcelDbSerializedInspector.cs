#if UNITY_EDITOR || EXCELDB_DOTNET_VALIDATE
using System;
using System.Linq;
using ExcelDb.Editor.Model;
using ExcelEditorUtility = ExcelDbEditor.EditorUtility;

namespace ExcelDb.Editor.Unity
{

/// <summary>
/// Concrete typed-inspector session shared by the browser UI and host integration tests. The host
/// resolves a resident row and generated binding; this class owns staging, authoring dirty state,
/// save baselines, revision refresh and the Unity Undo proxy.
/// </summary>
public sealed class ExcelDbSerializedInspectorSession : IDisposable
{
    private readonly IExcelDbUnityEditorBridge _host;
    private readonly ExcelDbUnityUndoBridge _undo;
    private readonly bool _ownsUndo;
    private bool _pendingApply;
    private bool _disposed;

    private ExcelDbSerializedInspectorSession(
        IExcelDbUnityEditorBridge host,
        string guid,
        EditorSerializedObject serializedObject,
        ExcelDbUnityUndoBridge undo,
        bool ownsUndo)
    {
        _host = host;
        Guid = guid;
        SerializedObject = serializedObject;
        _undo = undo;
        _ownsUndo = ownsUndo;
        _undo.Bind(serializedObject.History);
    }

    public string Guid { get; }

    public IExcelDbUnityEditorBridge Host => _host;

    public EditorSerializedObject SerializedObject { get; }

    public bool CanUndo => _undo.CanUndo;

    public bool CanRedo => _undo.CanRedo;

    public EditorApplyResult? LastApplyResult { get; private set; }

    public static bool TryCreate(
        IExcelDbUnityEditorBridge host,
        string guid,
        ExcelDbAuthoringPropertyFactory factory,
        out ExcelDbSerializedInspectorSession? session)
    {
        var undo = new ExcelDbUnityUndoBridge();
        try
        {
            if (TryCreate(host, guid, factory, undo, ownsUndo: true, out session))
                return true;
            undo.Dispose();
            return false;
        }
        catch
        {
            undo.Dispose();
            throw;
        }
    }

    public static bool TryCreate(
        IExcelDbUnityEditorBridge host,
        string guid,
        ExcelDbAuthoringPropertyFactory factory,
        ExcelDbUnityUndoBridge undo,
        out ExcelDbSerializedInspectorSession? session)
    {
        if (undo == null)
            throw new ArgumentNullException(nameof(undo));
        return TryCreate(host, guid, factory, undo, ownsUndo: false, out session);
    }

    private static bool TryCreate(
        IExcelDbUnityEditorBridge host,
        string guid,
        ExcelDbAuthoringPropertyFactory factory,
        ExcelDbUnityUndoBridge undo,
        bool ownsUndo,
        out ExcelDbSerializedInspectorSession? session)
    {
        if (host == null)
            throw new ArgumentNullException(nameof(host));
        if (guid == null)
            throw new ArgumentNullException(nameof(guid));
        if (factory == null)
            throw new ArgumentNullException(nameof(factory));

        session = null;
        if (!ExcelDbEditor.AssetDatabase.IsAuthoringEnabled)
            return false;
        var capability = host as IExcelDbUnityPropertyBridge;
        if (capability == null
            || !capability.TryResolveEditorAsset(guid, out var residentAsset, out var binding))
            return false;
        if (residentAsset == null || binding == null)
            throw new InvalidOperationException(
                "The typed-property bridge returned success without a resident asset and binding.");

        session = new ExcelDbSerializedInspectorSession(
            host,
            guid,
            factory.Create(binding, residentAsset),
            undo,
            ownsUndo);
        return true;
    }

    /// <summary>
    /// Called before drawing. A RowRef picker may stage a value after the browser OnGUI returned;
    /// apply that value before Update can refresh the working snapshot.
    /// </summary>
    public EditorApplyResult? SynchronizeBeforeDraw()
    {
        ThrowIfDisposed();
        if (_pendingApply || SerializedObject.HasModifiedProperties)
        {
            _pendingApply = false;
            return ApplyModifiedProperties("Edit ExcelDB property");
        }

        SerializedObject.Update();
        return null;
    }

    public void QueueReferenceChange(
        EditorSerializedProperty property,
        EditorReferenceValue? value)
    {
        ThrowIfDisposed();
        if (property == null)
            throw new ArgumentNullException(nameof(property));
        property.ReferenceValue = value;
        _pendingApply = true;
    }

    public EditorApplyResult ApplyModifiedProperties(string label)
    {
        ThrowIfDisposed();
        LastApplyResult = _undo.ApplyModifiedProperties(SerializedObject, label);
        return LastApplyResult;
    }

    /// <summary>
    /// Saves the selected asset and advances the serialized clean baseline only after Authoring
    /// confirms that every resident target is clean. MarkSaved also accepts the new revision token,
    /// preserving Apply -&gt; Save -&gt; Undo as a valid history path.
    /// </summary>
    public bool Save()
    {
        ThrowIfDisposed();
        if (!TryApplyPendingChanges())
            return false;
        _host.SaveAsset(Guid);
        return MarkSavedIfClean();
    }

    public bool MarkSavedIfClean()
    {
        ThrowIfDisposed();
        if (SerializedObject.Targets.Any(target => ExcelEditorUtility.IsDirty(target.Value)))
            return false;

        SerializedObject.MarkSaved();
        ExcelDbEditorLifecycle.SynchronizeReloadLock();
        return true;
    }

    public void Revert()
    {
        ThrowIfDisposed();
        _pendingApply = false;
        _host.RevertAsset(Guid);
        SerializedObject.Update();
        LastApplyResult = null;
        ExcelDbEditorLifecycle.SynchronizeReloadLock();
    }

    public void RefreshFromResident()
    {
        ThrowIfDisposed();
        _pendingApply = false;
        SerializedObject.Update();
        LastApplyResult = null;
    }

    public void PerformUndo()
    {
        ThrowIfDisposed();
        _undo.PerformUndo();
    }

    public void PerformRedo()
    {
        ThrowIfDisposed();
        _undo.PerformRedo();
    }

    public bool TryApplyPendingChanges()
    {
        ThrowIfDisposed();
        if (!_pendingApply && !SerializedObject.HasModifiedProperties)
            return true;
        _pendingApply = false;
        var status = ApplyModifiedProperties("Edit ExcelDB property").Status;
        return status == EditorApplyStatus.Applied || status == EditorApplyStatus.NoChanges;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsUndo)
            _undo.Dispose();
        SerializedObject.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ExcelDbSerializedInspectorSession));
    }
}
}
#endif
