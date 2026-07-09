// ExcelDbEngine 类型桩:仅供验证生成代码可编译,不是运行时实现。
#nullable disable
using System.Collections.Generic;

namespace ExcelDbEngine
{
    // 无 Object/ScriptableObject 基类:生成类是普通 C# 类,资产语义由库侧注册表承载(M2 D4/Δ11)。
    public readonly struct Ref<T> where T : class
    {
        public readonly string Key;
        public Ref(string key) => Key = key;
        public bool IsNull => string.IsNullOrEmpty(Key);
        public bool TryGet(out T target) { target = null; return false; }
    }

    public readonly struct UnityRef
    {
        public readonly string Guid;
        public readonly string MainAssetPath;
        public UnityRef(string guid, string mainAssetPath) { Guid = guid; MainAssetPath = mainAssetPath; }
    }

    public readonly struct LocalizedTextRef
    {
        public readonly string Key;
        public LocalizedTextRef(string key) => Key = key;
        public string Resolve() => Key;
    }

    public sealed class Curve
    {
        public List<(float X, float Y)> Points = new List<(float, float)>();
        public float Evaluate(float x) => 0f;
    }

    public struct Rng
    {
        ulong _state;
        public Rng(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        public uint Next() { _state ^= _state << 13; _state ^= _state >> 7; _state ^= _state << 17; return (uint)_state; }
    }

    public sealed class WeightedList<T> : List<T>
    {
        public T Select(ref Rng rng) => Count > 0 ? this[(int)(rng.Next() % Count)] : default;
    }

    public sealed class Expression<T>
    {
        public string Source;
        public T Eval<TSymbols>(in TSymbols symbols) => default;
    }
}
