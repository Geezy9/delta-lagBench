namespace Testerss;

/// <summary>
/// Deterministic hashing primitives used by both the parallel run and the
/// single-threaded reference execution. These must remain bit-for-bit stable
/// across versions so that printed iteration seeds stay reproducible.
/// </summary>
internal static class DeterministicHash
{
    /// <summary>SplitMix64 step: advances <paramref name="state"/> and returns the next value.</summary>
    public static ulong SplitMix64(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Mixes the iteration seed, node index, work unit, and parent accumulator into a node value.</summary>
    public static ulong Mix(ulong seed, ulong node, ulong workUnit, ulong parentAcc)
    {
        ulong z = seed ^ (node * 0x9E3779B97F4A7C15UL) ^ (workUnit * 0xBF58476D1CE4E5B9UL) ^ parentAcc;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Folds per-node checksums into a single order-sensitive digest (FNV-style with rotation).</summary>
    public static ulong Fold(ReadOnlySpan<ulong> checksums)
    {
        ulong h = 0x811C9DC5CBF29CE4UL;
        foreach (ulong c in checksums)
        {
            h ^= c;
            h *= 0x100000001B3UL;
            h = (h << 13) | (h >> 51);
        }
        return h;
    }
}

/// <summary>Minimal seeded PRNG (SplitMix64) with a stable consumption order.</summary>
internal struct DeterministicRng(ulong seed)
{
    private ulong _state = seed;

    public ulong Next() => DeterministicHash.SplitMix64(ref _state);

    /// <summary>Returns a value in [min, max] inclusive.</summary>
    public int NextInt(int min, int maxInclusive) =>
        min + (int)(Next() % (ulong)(maxInclusive - min + 1));
}
