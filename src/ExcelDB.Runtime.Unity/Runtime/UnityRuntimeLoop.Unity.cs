#if UNITY_2022_3_OR_NEWER
using System;
using System.Threading;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ExcelDb.Runtime.Unity
{

/// <summary>
/// PlayerLoop bridge. Attach once to a persistent GameObject; background watcher signals are
/// coalesced and published from Update on the Unity main thread.
/// </summary>
public sealed class UnityPlayerLoop : MonoBehaviour, IUnityRuntimeLoop
{
    private int _mainThreadId;
    private int _requested;

    public bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public event Action PublishTick;

    private void Awake()
    {
        _mainThreadId = Environment.CurrentManagedThreadId;
        DontDestroyOnLoad(gameObject);
    }

    public void RequestPublishTick() => Interlocked.Exchange(ref _requested, 1);

    private void Update()
    {
        if (Interlocked.Exchange(ref _requested, 0) != 0)
            PublishTick?.Invoke();
    }
}

#if UNITY_EDITOR
/// <summary>EditorApplication.update bridge for authoring sessions outside PlayerLoop.</summary>
public sealed class UnityEditorLoop : IUnityRuntimeLoop, IDisposable
{
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;
    private int _requested;
    private bool _disposed;

    public UnityEditorLoop()
    {
        EditorApplication.update += OnUpdate;
    }

    public bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public event Action PublishTick;

    public void RequestPublishTick() => Interlocked.Exchange(ref _requested, 1);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        EditorApplication.update -= OnUpdate;
    }

    private void OnUpdate()
    {
        if (Interlocked.Exchange(ref _requested, 0) != 0)
            PublishTick?.Invoke();
    }
}
#endif
}
#endif
