using System.Collections.Concurrent;

namespace Testerss;

/// <summary>Compact summary of a finished iteration for the dashboard's recent-runs table.</summary>
internal sealed record IterationSummary(
    long Iteration,
    ulong Seed,
    int Nodes,
    int Edges,
    int Cycles,
    int Threads,
    int Slack,
    string Strategy,
    double ElapsedMs,
    double TasksPerSec,
    bool Passed);

/// <summary>
/// Lock-free live metrics shared between the soak engine and the dashboard.
/// Engine threads only perform Volatile/Interlocked writes (sub-nanosecond);
/// all aggregation and process/GC inspection happens on the dashboard's HTTP
/// thread so the engine is never slowed down.
/// </summary>
internal sealed class LiveMetrics
{
    private long _iterations;
    private long _failures;
    private long _totalTasksCompleted;
    private long _currentDone;
    private long _currentTotal;
    private long _sessionStartTicks = DateTime.UtcNow.Ticks;
    private volatile string _currentSpec = "(starting)";

    private readonly ConcurrentQueue<IterationSummary> _recent = new();
    private const int RecentCapacity = 50;

    public void BeginIteration(DagSpec spec)
    {
        _currentSpec = spec.ToString();
        Volatile.Write(ref _currentTotal, spec.TotalTasks);
        Volatile.Write(ref _currentDone, 0);
    }

    /// <summary>Called from the sampler thread (already running); a single volatile store.</summary>
    public void ReportProgress(long done) => Volatile.Write(ref _currentDone, done);

    public void CompleteIteration(IterationResult result)
    {
        Interlocked.Increment(ref _iterations);
        Interlocked.Add(ref _totalTasksCompleted, result.Spec.TotalTasks);
        if (!result.Passed) Interlocked.Increment(ref _failures);

        var spec = result.Spec;
        _recent.Enqueue(new IterationSummary(
            Volatile.Read(ref _iterations), spec.Seed, spec.NodeCount, spec.Edges.Count,
            spec.Cycles, spec.Threads, spec.Slack, spec.Strategy.ToString(),
            result.Elapsed.TotalMilliseconds, result.TasksPerSecond, result.Passed));
        while (_recent.Count > RecentCapacity && _recent.TryDequeue(out _)) { }
    }

    /// <summary>Builds a full snapshot. Runs on the dashboard thread only.</summary>
    public MetricsSnapshot Snapshot()
    {
        var uptime = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Volatile.Read(ref _sessionStartTicks));
        long iterations = Volatile.Read(ref _iterations);
        long totalTasks = Volatile.Read(ref _totalTasksCompleted);
        long curDone = Volatile.Read(ref _currentDone);
        long curTotal = Volatile.Read(ref _currentTotal);

        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();

        return new MetricsSnapshot(
            UptimeSeconds: uptime.TotalSeconds,
            Iterations: iterations,
            Failures: Volatile.Read(ref _failures),
            TotalTasksCompleted: totalTasks,
            AvgTasksPerSec: totalTasks / Math.Max(uptime.TotalSeconds, 1e-9),
            CurrentSpec: _currentSpec,
            CurrentDone: curDone,
            CurrentTotal: curTotal,
            CurrentProgressPct: curTotal > 0 ? 100.0 * curDone / curTotal : 0,
            WorkingSetMb: proc.WorkingSet64 / (1024.0 * 1024.0),
            GcHeapMb: GC.GetTotalMemory(false) / (1024.0 * 1024.0),
            GcCommittedMb: gcInfo.TotalCommittedBytes / (1024.0 * 1024.0),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2),
            PauseTimePct: gcInfo.PauseTimePercentage,
            ThreadCount: proc.Threads.Count,
            TotalCpuSeconds: proc.TotalProcessorTime.TotalSeconds,
            ThreadPoolThreads: ThreadPool.ThreadCount,
            PendingWorkItems: ThreadPool.PendingWorkItemCount,
            ProcessorCount: Environment.ProcessorCount,
            Recent: [.. _recent.Reverse()]);
    }
}

/// <summary>Point-in-time view of everything the dashboard displays.</summary>
internal sealed record MetricsSnapshot(
    double UptimeSeconds,
    long Iterations,
    long Failures,
    long TotalTasksCompleted,
    double AvgTasksPerSec,
    string CurrentSpec,
    long CurrentDone,
    long CurrentTotal,
    double CurrentProgressPct,
    double WorkingSetMb,
    double GcHeapMb,
    double GcCommittedMb,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double PauseTimePct,
    int ThreadCount,
    double TotalCpuSeconds,
    int ThreadPoolThreads,
    long PendingWorkItems,
    int ProcessorCount,
    IReadOnlyList<IterationSummary> Recent);
