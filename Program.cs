// delta-lag overnight benchmark / validity soak test entry point.
//
// Seeded random DAG generation; every parallel run is verified against a
// deterministic single-threaded reference execution via checksums.
// Any failure is reproducible from the printed iteration seed.
//
// Usage: dotnet run [-- --help]

using Testerss;

SoakOptions options;
try
{
    options = SoakOptions.Parse(args);
}
catch (HelpRequestedException)
{
    Console.WriteLine(SoakOptions.HelpText);
    return 0;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine(SoakOptions.HelpText);
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // finish the current iteration, then produce the summary
    cts.Cancel();
    Console.WriteLine("Cancellation requested; finishing current iteration...");
};

var metrics = new LiveMetrics();
DashboardServer? dashboard = null;
if (options.Dashboard)
{
    try
    {
        dashboard = new DashboardServer(metrics, options.DashboardPort);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"warning: dashboard failed to start on port {options.DashboardPort} ({ex.Message}); continuing without it.");
    }
}

try
{

var runner = new SoakRunner(options, metrics);

Console.WriteLine("delta-lag overnight soak test");
Console.WriteLine($"  engine      : delta-lag v{SoakRunner.EngineVersion}");
Console.WriteLine($"  master seed : {options.MasterSeed}");
Console.WriteLine($"  duration    : {(options.Quick ? "quick (1 iteration batch)" : options.Duration.ToString())}");
Console.WriteLine($"  max threads : {runner.MaxThreads}");
if (dashboard is not null)
    Console.WriteLine($"  dashboard   : {dashboard.Url}");
Console.WriteLine();

var report = runner.Run(cts.Token);

report.WriteConsoleSummary(options);
report.WriteJson(options.ReportPath);

return report.Passed ? 0 : 1;
}
finally
{
    dashboard?.Dispose();
}
