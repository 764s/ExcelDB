using System;

namespace UnityEngine
{
    public sealed class GameObject
    {
    }

    public abstract class MonoBehaviour
    {
        protected GameObject gameObject { get; } = new GameObject();

        protected static void DontDestroyOnLoad(GameObject target)
        {
        }
    }
}

namespace UnityEditor
{
    public static class EditorApplication
    {
        public static event Action update
        {
            add { }
            remove { }
        }
    }
}

namespace Unity.Profiling
{
    public readonly struct ProfilerMarker
    {
        public ProfilerMarker(string name)
        {
        }

        public void Begin()
        {
        }

        public void End()
        {
        }
    }
}
