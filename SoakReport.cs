using System.Text.Json;
using System.Text.Json.Serialization;

namespace Testerss;

/// <summary>
/// Final outcome of a soak session: console summary plus a machine-readable
/// JSON report suitable for CI artifact collection.
/// </summary>
internal sealed record SoakReport(
    ulong MasterSeed,
    TimeSpan Elapsed,
    long Iterations,
    long TotalTasks,
    long Failures,
    IReadOnlyList<ulong> FailedSeeds,
    bool Cancelled)
{
    public bool Passed => Failures == 0;

    /// <summary>
    /// With zero failures in n independent trials, the one-sided upper bound on
    /// the per-iteration failure rate at 99.9% confidence is 1 - 0.001^(1/n)
    /// (rule of three, generalized).
    /// </summary>
    [JsonInclude]
    public double FailureRateUpperBound99_9 =>
        Passed ? 1.0 - Math.Pow(0.001, 1.0 / Math.Max(Iterations, 1)) : double.NaN;

    public void WriteConsoleSummary(SoakOptions options)
    {
        Console.WriteLine();
        if (options.WriteGraphs)
            Console.WriteLine($"Per-run progress graphs written to: {options.OutputDirectory}");
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"  master seed  : {MasterSeed}");
        Console.WriteLine($"  elapsed      : {Elapsed}");
        Console.WriteLine($"  iterations   : {Iterations}");
        Console.WriteLine($"  total tasks  : {TotalTasks:N0}");
        Console.WriteLine($"  failures     : {Failures}");
        if (Cancelled)
            Console.WriteLine("  note         : session cancelled early (Ctrl+C)");

        if (Passed)
        {
            Console.WriteLine($"  PASS: 99.9% confident per-iteration failure rate < {FailureRateUpperBound99_9:P4}");
        }
        else
        {
            Console.WriteLine("  FAIL: correctness violations detected. Reproduce with these iteration seeds:");
            foreach (ulong seed in FailedSeeds)
                Console.WriteLine($"    --seed {seed}  (single-iteration seed)");
        }
    }

    public void WriteJson(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonContext.Default.SoakReport);
        File.WriteAllText(path, json);
        Console.WriteLine($"JSON report written to: {path}");
    }
}

// Named float literals are required: unreached progress milestones (t25/t50/t75)
// and the failure-rate bound on a failing run are legitimately NaN, and the
// default number handling throws rather than emitting them.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(SoakReport))]
[JsonSerializable(typeof(IterationRecord))]
[JsonSerializable(typeof(MetricsSnapshot))]
internal sealed partial class JsonContext : JsonSerializerContext;

/// <summary>Compact (single-line) serialization for JSONL dataset rows.</summary>
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(IterationRecord))]
internal sealed partial class CompactJsonContext : JsonSerializerContext;
