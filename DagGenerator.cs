using deltalag;

namespace Testerss;

/// <summary>
/// A fully-specified random test case: a DAG plus scheduler run parameters,
/// derived deterministically from a single iteration seed.
/// </summary>
internal sealed record DagSpec(
    ulong Seed,
    int NodeCount,
    int Cycles,
    int Slack,
    int Threads,
    PartitionStrategy Strategy,
    double EdgeProbability,
    IReadOnlyList<(int From, int To)> Edges,
    int[][] Parents)
{
    public long TotalTasks => (long)NodeCount * Cycles;

    public override string ToString() =>
        $"seed={Seed} nodes={NodeCount} edges={Edges.Count} cycles={Cycles} " +
        $"slack={Slack} threads={Threads} strategy={Strategy}";
}

/// <summary>
/// Generates seeded random DAG test cases. Edges only go from lower to higher
/// node index, so the graph is acyclic by construction and index order is a
/// valid topological order.
/// </summary>
internal static class DagGenerator
{
    public const int MaxSlack = 16;

    /// <summary>
    /// Produces a test case from <paramref name="seed"/>. The RNG consumption
    /// order is part of the reproducibility contract — do not reorder draws.
    /// </summary>
    public static DagSpec Generate(ulong seed, int maxThreads, bool quick)
    {
        var rng = new DeterministicRng(seed);

        int nodeCount = 2 + (int)(rng.Next() % 255);                        // 2..256 nodes
        int cycles = quick ? 1_000 : 1_000 + (int)(rng.Next() % 99_001);    // 1k..100k work units
        int slack = (int)(rng.Next() % (MaxSlack + 1));                     // 0..16
        int threads = 1 + (int)(rng.Next() % (ulong)maxThreads);
        var strategy = (rng.Next() & 1) == 0 ? PartitionStrategy.Stride : PartitionStrategy.ChunkedLinear;
        double edgeProb = 0.02 + (rng.Next() % 1000) / 1000.0 * 0.18;       // 2%..20% density

        var parents = new List<int>[nodeCount];
        for (int i = 0; i < nodeCount; i++) parents[i] = [];
        var edges = new List<(int From, int To)>();
        for (int i = 0; i < nodeCount; i++)
            for (int j = i + 1; j < nodeCount; j++)
                if (rng.Next() % 1_000_000 < (ulong)(edgeProb * 1_000_000))
                {
                    edges.Add((i, j));
                    parents[j].Add(i);
                }

        return new DagSpec(
            seed, nodeCount, cycles, slack, threads, strategy, edgeProb,
            edges, [.. parents.Select(p => p.ToArray())]);
    }
}
