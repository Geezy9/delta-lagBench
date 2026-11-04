using System.Diagnostics;
using System.Reflection;
using deltalag;

namespace Testerss;

/// <summary>Result of a single verified soak iteration.</summary>
internal sealed record IterationResult(
    long Iteration,
    DagSpec Spec,
    ulong Expected,
    ulong Actual,
    TimeSpan Elapsed,
    IReadOnlyList<(double Ms, long Done)> Samples,
    IReadOnlyDictionary<int, int> ReadyNodeHistogram)
{
    public bool Passed => Expected == Actual;
    public double TasksPerSecond => Spec.TotalTasks / Math.Max(Elapsed.TotalSeconds, 1e-9);
}

/// <summary>
/// Drives the soak loop: generates a seeded DAG, executes it on the delta-lag
/// scheduler while sampling progress, then verifies the parallel checksum
/// against a deterministic single-threaded reference execution.
/// </summary>
internal sealed class SoakRunner(SoakOptions options, LiveMetrics? metrics = null)
{
    /// <summary>Ring buffer slots per node; must exceed MaxSlack + 1.</summary>
    private const int BufferSize = 64;

    /// <summary>Engine worker threads may never exceed this, regardless of core count or options.</summary>
    public const int HardThreadCap = 16;

    /// <summary>
    /// Version of the delta-lag engine assembly this harness is running against,
    /// read from its informational version (falls back to the assembly version).
    /// </summary>
    public static string EngineVersion { get; } = ResolveEngineVersion();

    private static string ResolveEngineVersion()
    {
        var assembly = typeof(Scheduler).Assembly;
        string? informational = assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // strip any "+commithash" build metadata suffix
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    /// <summary>Cores left free for the OS, the sampler thread, and the dashboard.</summary>
    public const int ReservedCores = 2;

    private readonly int _maxThreads = Math.Min(
        options.MaxThreads ?? Math.Clamp(Environment.ProcessorCount - ReservedCores, 2, HardThreadCap - ReservedCores),
        HardThreadCap);

    public int MaxThreads => _maxThreads;

    /// <summary>
    /// Runs iterations until the duration elapses or cancellation is requested.
    /// At least one iteration always runs. Returns the overall report.
    /// </summary>
    public SoakReport Run(CancellationToken cancellationToken)
    {
        if (options.WriteGraphs)
            Directory.CreateDirectory(options.OutputDirectory);

        using var dataset = new DatasetWriter(options.OutputDirectory);
        var wall = Stopwatch.StartNew();
        var lastReport = Stopwatch.StartNew();
        ulong seedState = options.MasterSeed;

        long iterations = 0, failures = 0, totalTasks = 0;
        var failedSeeds = new List<ulong>();

        do
        {
            ulong iterSeed = DeterministicHash.SplitMix64(ref seedState);
            var spec = DagGenerator.Generate(iterSeed, _maxThreads, options.Quick);
            metrics?.BeginIteration(spec);

            var result = RunIteration(++iterations, spec);
            totalTasks += spec.TotalTasks;
            metrics?.CompleteIteration(result);

            dataset.Append(new IterationRecord(
                iterations, spec.Seed, spec.Cycles, spec.Slack, spec.Threads,
                spec.Strategy.ToString(), spec.EdgeProbability, result.Passed,
                EngineVersion,
                GraphMetrics.Compute(spec),
                EngineResponse.Compute(
                    result.Samples, spec.TotalTasks, result.Elapsed, result.ReadyNodeHistogram)));

            if (!result.Passed)
            {
                failures++;
                failedSeeds.Add(iterSeed);
                Console.WriteLine(
                    $"[FAIL] iter={iterations} {spec} expected={result.Expected:X16} actual={result.Actual:X16}");
            }
            else if (lastReport.Elapsed > TimeSpan.FromMinutes(1) || options.Quick)
            {
                Console.WriteLine(
                    $"[{wall.Elapsed:hh\\:mm\\:ss}] iter={iterations} ok ({spec}) " +
                    $"{result.Elapsed.TotalMilliseconds:F1} ms, {result.TasksPerSecond / 1e6:F2} M tasks/s, failures={failures}");
                lastReport.Restart();
            }
        } while (wall.Elapsed < options.Duration && !cancellationToken.IsCancellationRequested);

        wall.Stop();
        Console.WriteLine($"Dataset appended: {dataset.JsonlPath}");
        Console.WriteLine($"                  {dataset.CsvPath}");
        return new SoakReport(
            options.MasterSeed, wall.Elapsed, iterations, totalTasks, failures, failedSeeds,
            cancellationToken.IsCancellationRequested);
    }

    private IterationResult RunIteration(long iteration, DagSpec spec)
    {
        // --- shared state written by node tasks ---
        var vals = new ulong[spec.NodeCount][];      // ring buffers of published values
        var checksums = new ulong[spec.NodeCount];   // per-node XOR accumulators
        for (int i = 0; i < spec.NodeCount; i++) vals[i] = new ulong[BufferSize];

        var builder = new GraphBuilder();
        for (int i = 0; i < spec.NodeCount; i++)
        {
            int node = i;
            int[] myParents = spec.Parents[i];
            builder.AddNode(wu =>
            {
                ulong acc = 0;
                foreach (int p in myParents) acc += vals[p][wu % BufferSize];
                ulong v = DeterministicHash.Mix(spec.Seed, (ulong)node, (ulong)wu, acc);
                vals[node][wu % BufferSize] = v;
                checksums[node] ^= v + (ulong)wu * 0x2545F4914F6CDD1DUL;
            });
        }
        foreach (var (from, to) in spec.Edges) builder.AddEdge(from, to);

        var ctx = builder.Build();
        var scheduler = new Scheduler();

        // --- sample total WorkDone over time while the scheduler runs ---
        var samples = new List<(double Ms, long Done)>(1024);
        var readyNodeHistogram = new Dictionary<int, int>();
        var nodes = ctx.NodeArray;
        bool samplerStop = false;
        var sw = Stopwatch.StartNew();

        void SampleProgress()
        {
            long done = 0;
            int readyNodes = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                done += nodes[i].WorkDone;
                if (IsReady(i, spec.Cycles, spec.Slack, nodes,
                        ctx.Edges, ctx.Offsets, ctx.ParentEdges, ctx.ParentOffsets))
                    readyNodes++;
            }

            samples.Add((sw.Elapsed.TotalMilliseconds, done));
            readyNodeHistogram[readyNodes] = readyNodeHistogram.GetValueOrDefault(readyNodes) + 1;
            metrics?.ReportProgress(done);
        }

        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref samplerStop))
            {
                SampleProgress();
                Thread.Sleep(1);
            }
        })
        { IsBackground = true, Name = "soak-sampler" };

        sampler.Start();
        scheduler.Run(ctx, cycles: spec.Cycles, slack: spec.Slack, threads: spec.Threads, strategy: spec.Strategy);
        sw.Stop();
        Volatile.Write(ref samplerStop, true);
        sampler.Join();
        samples.Add((sw.Elapsed.TotalMilliseconds, spec.TotalTasks));
        readyNodeHistogram[0] = readyNodeHistogram.GetValueOrDefault(0) + 1;

        ulong actual = DeterministicHash.Fold(checksums);
        ulong expected = ComputeReferenceChecksum(spec);

        if (options.WriteGraphs)
        {
            string svgPath = Path.Combine(options.OutputDirectory, $"iter{iteration:D6}_seed{spec.Seed}.svg");
            SvgChartWriter.WriteProgressChart(svgPath, samples, spec.TotalTasks, $"iter {iteration} | {spec}");
        }

        return new IterationResult(iteration, spec, expected, actual, sw.Elapsed, samples, readyNodeHistogram);
    }

    private static bool IsReady(
        int nodeIndex,
        int cycles,
        int slack,
        Node[] nodes,
        int[] edges,
        int[] offsets,
        int[] parentEdges,
        int[] parentOffsets)
    {
        int workDone = nodes[nodeIndex].WorkDone;
        if (workDone >= cycles || nodes[nodeIndex].WorkClaimed != workDone)
            return false;

        int requiredParentWork = workDone + 1;
        for (int i = parentOffsets[nodeIndex]; i < parentOffsets[nodeIndex + 1]; i++)
            if (nodes[parentEdges[i]].WorkDone < requiredParentWork)
                return false;

        int minimumChildWork = workDone - slack;
        for (int i = offsets[nodeIndex]; i < offsets[nodeIndex + 1]; i++)
            if (nodes[edges[i]].WorkDone < minimumChildWork)
                return false;

        return true;
    }

    /// <summary>
    /// Deterministic single-threaded reference execution. Nodes are processed in
    /// index order, which is a topological order by construction of the DAG.
    /// </summary>
    private static ulong ComputeReferenceChecksum(DagSpec spec)
    {
        var refVals = new ulong[spec.NodeCount][];
        var refChecksums = new ulong[spec.NodeCount];
        for (int i = 0; i < spec.NodeCount; i++) refVals[i] = new ulong[BufferSize];

        for (int wu = 0; wu < spec.Cycles; wu++)
            for (int i = 0; i < spec.NodeCount; i++)
            {
                ulong acc = 0;
                foreach (int p in spec.Parents[i]) acc += refVals[p][wu % BufferSize];
                ulong v = DeterministicHash.Mix(spec.Seed, (ulong)i, (ulong)wu, acc);
                refVals[i][wu % BufferSize] = v;
                refChecksums[i] ^= v + (ulong)wu * 0x2545F4914F6CDD1DUL;
            }

        return DeterministicHash.Fold(refChecksums);
    }
}
