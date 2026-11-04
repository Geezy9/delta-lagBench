namespace Testerss;

/// <summary>
/// How the engine responded to one graph: wall time, throughput, and the shape
/// of the progress curve derived from the background sampler.
/// </summary>
internal sealed record EngineResponse(
    double ElapsedMs,
    double TasksPerSecond,
    double TimeTo25PctMs,
    double TimeTo50PctMs,
    double TimeTo75PctMs,
    double PeakInstantaneousTasksPerSec,
    double ProgressLinearity,
    int SampleCount,
    IReadOnlyDictionary<int, int> ReadyNodeHistogram)
{
    /// <summary>
    /// Derives response metrics from the (ms, workDone) progress samples.
    /// ProgressLinearity is the mean absolute deviation of the measured curve
    /// from the ideal linear ramp, normalized to [0, 1] (0 = perfectly linear).
    /// </summary>
    public static EngineResponse Compute(
        IReadOnlyList<(double Ms, long Done)> samples,
        long totalTasks,
        TimeSpan elapsed,
        IReadOnlyDictionary<int, int> readyNodeHistogram)
    {
        double elapsedMs = elapsed.TotalMilliseconds;
        double t25 = double.NaN, t50 = double.NaN, t75 = double.NaN;
        double peakRate = 0;
        double deviationSum = 0;

        (double Ms, long Done) prev = (0, 0);
        foreach (var s in samples)
        {
            double frac = (double)s.Done / totalTasks;
            if (double.IsNaN(t25) && frac >= 0.25) t25 = s.Ms;
            if (double.IsNaN(t50) && frac >= 0.50) t50 = s.Ms;
            if (double.IsNaN(t75) && frac >= 0.75) t75 = s.Ms;

            double dtSec = (s.Ms - prev.Ms) / 1000.0;
            if (dtSec > 1e-6)
                peakRate = Math.Max(peakRate, (s.Done - prev.Done) / dtSec);

            double idealFrac = elapsedMs <= 0 ? 1 : Math.Min(s.Ms / elapsedMs, 1);
            deviationSum += Math.Abs(frac - idealFrac);
            prev = s;
        }

        return new EngineResponse(
            ElapsedMs: elapsedMs,
            TasksPerSecond: totalTasks / Math.Max(elapsed.TotalSeconds, 1e-9),
            TimeTo25PctMs: t25,
            TimeTo50PctMs: t50,
            TimeTo75PctMs: t75,
            PeakInstantaneousTasksPerSec: peakRate,
            ProgressLinearity: samples.Count > 0 ? deviationSum / samples.Count : 0,
            SampleCount: samples.Count,
            ReadyNodeHistogram: readyNodeHistogram);
    }
}

/// <summary>One flattened dataset row: test case + graph shape + engine response.</summary>
internal sealed record IterationRecord(
    long Iteration,
    ulong Seed,
    int Cycles,
    int Slack,
    int Threads,
    string Strategy,
    double EdgeProbability,
    bool Passed,
    string EngineVersion,
    GraphMetrics Graph,
    EngineResponse Engine);

/// <summary>
/// Append-only dataset sink. Writes every iteration as one JSONL row (full
/// fidelity, for pipelines) and one CSV row (for spreadsheets / quick charts).
/// </summary>
internal sealed class DatasetWriter : IDisposable
{
    private readonly StreamWriter _jsonl;
    private readonly StreamWriter _csv;

    public string JsonlPath { get; }
    public string CsvPath { get; }

    public DatasetWriter(string directory)
    {
        Directory.CreateDirectory(directory);
        JsonlPath = Path.Combine(directory, "iterations.jsonl");
        CsvPath = Path.Combine(directory, "iterations.csv");

        _jsonl = new StreamWriter(JsonlPath, append: true);
        bool csvExisted = File.Exists(CsvPath) && new FileInfo(CsvPath).Length > 0;
        _csv = new StreamWriter(CsvPath, append: true);
        if (!csvExisted)
            _csv.WriteLine(
                "iteration,seed,cycles,slack,threads,strategy,edge_probability,passed,engine_version," +
                "node_count,edge_count,density,depth,critical_path_length,longest_dependency_chain," +
                "max_level_width,avg_level_width,level_histogram,partition_skew," +
                "root_count,leaf_count,max_fan_in,avg_fan_in,max_fan_out,avg_fan_out," +
                "isolated_nodes,theoretical_parallelism,scc_count,largest_scc,scc_histogram," +
                "elapsed_ms,tasks_per_sec,t25_ms,t50_ms,t75_ms,peak_tasks_per_sec,progress_linearity," +
                "sample_count,ready_node_histogram");
    }

    public void Append(IterationRecord r)
    {
        _jsonl.WriteLine(System.Text.Json.JsonSerializer.Serialize(r, CompactJsonContext.Default.IterationRecord));

        var g = r.Graph;
        var e = r.Engine;
        _csv.WriteLine(string.Join(',',
            r.Iteration, r.Seed, r.Cycles, r.Slack, r.Threads, r.Strategy,
            r.EdgeProbability.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
            r.Passed, r.EngineVersion,
            g.NodeCount, g.EdgeCount, Inv(g.Density), g.Depth, g.CriticalPathLength, g.LongestDependencyChain,
            g.MaxLevelWidth, Inv(g.AvgLevelWidth), FormatHistogram(g.LevelHistogram), Inv(g.PartitionSkew),
            g.RootCount, g.LeafCount, g.MaxFanIn, Inv(g.AvgFanIn), g.MaxFanOut, Inv(g.AvgFanOut),
            g.IsolatedNodeCount, Inv(g.TheoreticalParallelism),
            g.SccCount, g.LargestSccSize, FormatHistogram(g.SccSizeHistogram),
            Inv(e.ElapsedMs), Inv(e.TasksPerSecond), Inv(e.TimeTo25PctMs), Inv(e.TimeTo50PctMs),
            Inv(e.TimeTo75PctMs), Inv(e.PeakInstantaneousTasksPerSec), Inv(e.ProgressLinearity), e.SampleCount,
            FormatHistogram(e.ReadyNodeHistogram)));

        // flush per iteration so data survives crashes / Ctrl+C mid-soak
        _jsonl.Flush();
        _csv.Flush();
    }

    private static string Inv(double v) => v.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Formats "size:count" pairs sorted by size, e.g. "1:80|3:2" (CSV-safe, no commas).</summary>
    private static string FormatHistogram(IReadOnlyDictionary<int, int> histogram) =>
        string.Join('|', histogram.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

    public void Dispose()
    {
        _jsonl.Dispose();
        _csv.Dispose();
    }
}
