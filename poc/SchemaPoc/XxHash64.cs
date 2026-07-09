namespace SchemaPoc;

/// <summary>xxHash64 参考实现(仅 PoC 用;正式版进 ExcelDb.Core)。</summary>
public static class XxHash64
{
    const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL,
                P3 = 1609587929392839161UL, P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;

    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        var len = data.Length;
        var pos = 0;
        ulong h;
        if (len >= 32)
        {
            ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            while (pos + 32 <= len)
            {
                v1 = Round(v1, Read64(data, pos));
                v2 = Round(v2, Read64(data, pos + 8));
                v3 = Round(v3, Read64(data, pos + 16));
                v4 = Round(v4, Read64(data, pos + 24));
                pos += 32;
            }
            h = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
            h = (h ^ Round(0, v1)) * P1 + P4;
            h = (h ^ Round(0, v2)) * P1 + P4;
            h = (h ^ Round(0, v3)) * P1 + P4;
            h = (h ^ Round(0, v4)) * P1 + P4;
        }
        else h = seed + P5;

        h += (ulong)len;
        while (pos + 8 <= len) { h = Rotl(h ^ Round(0, Read64(data, pos)), 27) * P1 + P4; pos += 8; }
        if (pos + 4 <= len) { h = Rotl(h ^ (Read32(data, pos) * P1), 23) * P2 + P3; pos += 4; }
        while (pos < len) { h = Rotl(h ^ (data[pos] * P5), 11) * P1; pos++; }

        h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
        return h;
    }

    static ulong Round(ulong acc, ulong input) => Rotl(acc + input * P2, 31) * P1;
    static ulong Rotl(ulong v, int n) => (v << n) | (v >> (64 - n));
    static ulong Read64(ReadOnlySpan<byte> d, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(i, 8));
    static ulong Read32(ReadOnlySpan<byte> d, int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(i, 4));
}
