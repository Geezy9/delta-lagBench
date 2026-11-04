namespace Testerss;

/// <summary>
/// Immutable, validated command-line options for the soak test harness.
/// </summary>
internal sealed record SoakOptions
{
    public double Hours { get; init; } = 8.0;
    public ulong MasterSeed { get; init; } = (ulong)Environment.TickCount64 * 0x9E3779B97F4A7C15UL;
    public bool Quick { get; init; }
    public bool WriteGraphs { get; init; } = true;
    public string OutputDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "graphs");
    public string ReportPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "soak-report.json");
    public bool Dashboard { get; init; } = true;
    public int DashboardPort { get; init; } = 8777;

    /// <summary>Optional override for the engine thread budget; always clamped to the hard cap of 16.</summary>
    public int? MaxThreads { get; init; }

    public TimeSpan Duration => Quick ? TimeSpan.Zero : TimeSpan.FromHours(Hours);

    public const string HelpText = """
        delta-lag overnight soak test

        Usage: dotnet run [-- options]

        Options:
          --hours <n>       Soak duration in hours (default: 8).
          --seed <n>        Master seed (ulong). Defaults to a time-derived seed.
                            Reuse a printed seed to reproduce a full session.
          --quick           Run a single fast iteration batch and exit.
          --no-graphs       Skip writing per-iteration SVG progress charts.
          --out <dir>       Directory for SVG charts (default: <app>/graphs).
          --report <file>   Path for the JSON summary report (default: <app>/soak-report.json).
          --no-dashboard    Disable the live monitoring dashboard.
          --port <n>        Dashboard HTTP port (default: 8777).
          --max-threads <n> Engine thread budget, 1-16 (default: cores minus 2, capped at 14).
          --help            Show this help.

        Exit codes: 0 = all iterations verified, 1 = correctness failures, 2 = invalid arguments.
        """;

    /// <summary>Parses args; throws <see cref="ArgumentException"/> on invalid input.</summary>
    public static SoakOptions Parse(string[] args)
    {
        var options = new SoakOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hours":
                    options = options with { Hours = ParseDouble(args, ref i) };
                    break;
                case "--seed":
                    options = options with { MasterSeed = ParseULong(args, ref i) };
                    break;
                case "--quick":
                    options = options with { Quick = true };
                    break;
                case "--no-graphs":
                    options = options with { WriteGraphs = false };
                    break;
                case "--out":
                    options = options with { OutputDirectory = RequireValue(args, ref i) };
                    break;
                case "--report":
                    options = options with { ReportPath = RequireValue(args, ref i) };
                    break;
                case "--no-dashboard":
                    options = options with { Dashboard = false };
                    break;
                case "--port":
                    options = options with { DashboardPort = (int)ParseULong(args, ref i) };
                    break;
                case "--max-threads":
                    options = options with { MaxThreads = (int)ParseULong(args, ref i) };
                    break;
                case "--help" or "-h" or "-?":
                    throw new HelpRequestedException();
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}'. Use --help for usage.");
            }
        }

        if (options.Hours is <= 0 or > 1000 && !options.Quick)
            throw new ArgumentException("--hours must be in (0, 1000].");

        if (options.DashboardPort is < 1 or > 65535)
            throw new ArgumentException("--port must be in [1, 65535].");

        if (options.MaxThreads is < 1 or > SoakRunner.HardThreadCap)
            throw new ArgumentException($"--max-threads must be in [1, {SoakRunner.HardThreadCap}].");

        return options;
    }

    private static string RequireValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Option '{args[i]}' requires a value.");
        return args[++i];
    }

    private static double ParseDouble(string[] args, ref int i)
    {
        string name = args[i];
        string raw = RequireValue(args, ref i);
        return double.TryParse(raw, out double v)
            ? v
            : throw new ArgumentException($"Option '{name}' expects a number, got '{raw}'.");
    }

    private static ulong ParseULong(string[] args, ref int i)
    {
        string name = args[i];
        string raw = RequireValue(args, ref i);
        return ulong.TryParse(raw, out ulong v)
            ? v
            : throw new ArgumentException($"Option '{name}' expects an unsigned integer, got '{raw}'.");
    }
}

/// <summary>Signals that the user asked for --help; not an error.</summary>
internal sealed class HelpRequestedException : Exception;
