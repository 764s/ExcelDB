using System.Buffers.Binary;

namespace ExcelDb.Schema.Hashing;

internal static class XxHash64
{
    private const ulong Prime1 = 11400714785074694791UL;
    private const ulong Prime2 = 14029467366897019727UL;
    private const ulong Prime3 = 1609587929392839161UL;
    private const ulong Prime4 = 9650029242287828579UL;
    private const ulong Prime5 = 2870177450012600261UL;

    public static ulong Hash(ReadOnlySpan<byte> source, ulong seed = 0)
    {
        var offset = 0;
        ulong hash;

        if (source.Length >= 32)
        {
            var v1 = seed + Prime1 + Prime2;
            var v2 = seed + Prime2;
            var v3 = seed;
            var v4 = seed - Prime1;
            var limit = source.Length - 32;

            do
            {
                v1 = Round(v1, Read64(source, offset));
                offset += 8;
                v2 = Round(v2, Read64(source, offset));
                offset += 8;
                v3 = Round(v3, Read64(source, offset));
                offset += 8;
                v4 = Round(v4, Read64(source, offset));
                offset += 8;
            }
            while (offset <= limit);

            hash = RotateLeft(v1, 1)
                + RotateLeft(v2, 7)
                + RotateLeft(v3, 12)
                + RotateLeft(v4, 18);
            hash = MergeRound(hash, v1);
            hash = MergeRound(hash, v2);
            hash = MergeRound(hash, v3);
            hash = MergeRound(hash, v4);
        }
        else
        {
            hash = seed + Prime5;
        }

        hash += (ulong)source.Length;

        while (offset <= source.Length - 8)
        {
            var lane = Round(0, Read64(source, offset));
            hash ^= lane;
            hash = RotateLeft(hash, 27) * Prime1 + Prime4;
            offset += 8;
        }

        if (offset <= source.Length - 4)
        {
            hash ^= BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]) * Prime1;
            hash = RotateLeft(hash, 23) * Prime2 + Prime3;
            offset += 4;
        }

        while (offset < source.Length)
        {
            hash ^= source[offset] * Prime5;
            hash = RotateLeft(hash, 11) * Prime1;
            offset++;
        }

        hash ^= hash >> 33;
        hash *= Prime2;
        hash ^= hash >> 29;
        hash *= Prime3;
        hash ^= hash >> 32;
        return hash;
    }

    private static ulong Read64(ReadOnlySpan<byte> source, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);

    private static ulong Round(ulong accumulator, ulong input)
    {
        accumulator += input * Prime2;
        accumulator = RotateLeft(accumulator, 31);
        return accumulator * Prime1;
    }

    private static ulong MergeRound(ulong accumulator, ulong value)
    {
        accumulator ^= Round(0, value);
        return accumulator * Prime1 + Prime4;
    }

    private static ulong RotateLeft(ulong value, int count) =>
        (value << count) | (value >> (64 - count));
}
