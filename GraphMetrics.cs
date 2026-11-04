namespace Testerss;

/// <summary>
/// Structural metrics describing the shape of a generated DAG. Captured for
/// every iteration so engine behavior can be correlated with graph topology.
/// </summary>
internal sealed record GraphMetrics(
    int NodeCount,
    int EdgeCount,
    double Density,
    int Depth,
    int CriticalPathLength,
    int LongestDependencyChain,
    int MaxLevelWidth,
    double AvgLevelWidth,
    IReadOnlyDictionary<int, int> LevelHistogram,
    double PartitionSkew,
    int RootCount,
    int LeafCount,
    int MaxFanIn,
    double AvgFanIn,
    int MaxFanOut,
    double AvgFanOut,
    int IsolatedNodeCount,
    double TheoreticalParallelism,
    int SccCount,
    int LargestSccSize,
    IReadOnlyDictionary<int, int> SccSizeHistogram)
{
    /// <summary>
    /// Computes metrics from a spec. Level = longest path from any root
    /// (valid because edges only go from lower to higher index).
    /// </summary>
    public static GraphMetrics Compute(DagSpec spec)
    {
        int n = spec.NodeCount;
        var fanOut = new int[n];
        foreach (var (from, _) in spec.Edges) fanOut[from]++;

        // longest-path level per node, in topological (index) order
        var level = new int[n];
        int depth = 1;
        for (int i = 0; i < n; i++)
        {
            int lv = 0;
            foreach (int p in spec.Parents[i])
                lv = Math.Max(lv, level[p] + 1);
            level[i] = lv;
            depth = Math.Max(depth, lv + 1);
        }

        var widthPerLevel = new int[depth];
        foreach (int lv in level) widthPerLevel[lv]++;
        var levelHistogram = widthPerLevel
            .Select((width, levelIndex) => (levelIndex, width))
            .ToDictionary(item => item.levelIndex, item => item.width);

        int roots = 0, leaves = 0, isolated = 0, maxFanIn = 0, maxFanOut = 0;
        long totalFanIn = 0;
        for (int i = 0; i < n; i++)
        {
            int fi = spec.Parents[i].Length;
            totalFanIn += fi;
            maxFanIn = Math.Max(maxFanIn, fi);
            maxFanOut = Math.Max(maxFanOut, fanOut[i]);
            if (fi == 0) roots++;
            if (fanOut[i] == 0) leaves++;
            if (fi == 0 && fanOut[i] == 0) isolated++;
        }

        long maxPossibleEdges = (long)n * (n - 1) / 2;

        var (sccCount, largestScc, sccHistogram) = ComputeSccs(n, spec.Edges);

        return new GraphMetrics(
            NodeCount: n,
            EdgeCount: spec.Edges.Count,
            Density: maxPossibleEdges == 0 ? 0 : (double)spec.Edges.Count / maxPossibleEdges,
            Depth: depth,
            CriticalPathLength: depth,
            LongestDependencyChain: depth - 1,
            MaxLevelWidth: widthPerLevel.Max(),
            AvgLevelWidth: (double)n / depth,
            LevelHistogram: levelHistogram,
            PartitionSkew: ComputePartitionSkew(n, spec.Threads, spec.Strategy),
            RootCount: roots,
            LeafCount: leaves,
            MaxFanIn: maxFanIn,
            AvgFanIn: (double)totalFanIn / n,
            MaxFanOut: maxFanOut,
            AvgFanOut: (double)totalFanIn / n, // every edge contributes one fan-in and one fan-out
            IsolatedNodeCount: isolated,
            TheoreticalParallelism: (double)n / depth,
            SccCount: sccCount,
            LargestSccSize: largestScc,
            SccSizeHistogram: sccHistogram);
    }

    private static double ComputePartitionSkew(int nodeCount, int threadCount, deltalag.PartitionStrategy strategy)
    {
        var partitionSizes = new int[threadCount];
        if (strategy == deltalag.PartitionStrategy.Stride)
        {
            for (int thread = 0; thread < threadCount; thread++)
                partitionSizes[thread] = thread < nodeCount
                    ? 1 + (nodeCount - 1 - thread) / threadCount
                    : 0;
        }
        else
        {
            int chunkSize = (nodeCount + threadCount - 1) / threadCount;
            for (int thread = 0; thread < threadCount; thread++)
            {
                int start = thread * chunkSize;
                partitionSizes[thread] = Math.Clamp(nodeCount - start, 0, chunkSize);
            }
        }

        double average = (double)nodeCount / threadCount;
        return (partitionSizes.Max() - partitionSizes.Min()) / average;
    }

    /// <summary>
    /// Iterative Tarjan strongly-connected-components. For the current generator
    /// every graph is a DAG, so this doubles as an acyclicity invariant check
    /// (count == n, largest == 1); it stays correct if cyclic graphs are ever added.
    /// </summary>
    private static (int Count, int Largest, Dictionary<int, int> Histogram) ComputeSccs(
        int n, IReadOnlyList<(int From, int To)> edges)
    {
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = [];
        foreach (var (from, to) in edges) adj[from].Add(to);

        var index = new int[n];
        var lowLink = new int[n];
        var onStack = new bool[n];
        Array.Fill(index, -1);
        var stack = new Stack<int>();
        int nextIndex = 0, count = 0, largest = 0;
        var histogram = new Dictionary<int, int>();

        // explicit DFS frames: (node, child cursor) to avoid recursion limits
        var frames = new Stack<(int Node, int Cursor)>();

        for (int start = 0; start < n; start++)
        {
            if (index[start] != -1) continue;
            frames.Push((start, 0));

            while (frames.Count > 0)
            {
                var (v, cursor) = frames.Pop();
                if (cursor == 0)
                {
                    index[v] = lowLink[v] = nextIndex++;
                    stack.Push(v);
                    onStack[v] = true;
                }

                bool descended = false;
                for (int c = cursor; c < adj[v].Count; c++)
                {
                    int w = adj[v][c];
                    if (index[w] == -1)
                    {
                        frames.Push((v, c + 1)); // resume after the child returns
                        frames.Push((w, 0));
                        descended = true;
                        break;
                    }
                    if (onStack[w])
                        lowLink[v] = Math.Min(lowLink[v], index[w]);
                }
                if (descended) continue;

                // all children done: close the SCC if v is a root, then propagate lowlink
                if (lowLink[v] == index[v])
                {
                    int size = 0;
                    int w;
                    do
                    {
                        w = stack.Pop();
                        onStack[w] = false;
                        size++;
                    } while (w != v);

                    count++;
                    largest = Math.Max(largest, size);
                    histogram[size] = histogram.GetValueOrDefault(size) + 1;
                }

                if (frames.Count > 0)
                {
                    var parent = frames.Peek();
                    lowLink[parent.Node] = Math.Min(lowLink[parent.Node], lowLink[v]);
                }
            }
        }

        return (count, largest, histogram);
    }
}
