# delta-lag Benchmark Solution

Benchmarking and inspection tooling for validating `delta-lag` scheduler correctness and behavior at scale.

This repository contains:

- **`bmark`**: a .NET 10 soak-test harness that generates seeded random DAG workloads, runs the scheduler, verifies results against a deterministic reference, and emits machine-readable datasets.
- **`Inspector`**: a Blazor WebAssembly app for importing those datasets, diffing CSV/JSON records, tagging graph behaviors, and exploring iterations visually.

## Status

Experimental and actively evolving.

## Requirements

- .NET 10 SDK

## Solution Layout

| Project | Type | Purpose |
|---|---|---|
| `bmark` | Console app | Runs deterministic soak tests and writes `iterations.csv` + `iterations.jsonl` datasets |
| `Inspector` | Blazor WebAssembly | Loads benchmark datasets in-browser and provides analysis/search tooling |

## Quick Start

```bash
dotnet build
```

### Run a quick benchmark pass

```bash
dotnet run --project bmark.csproj -- --quick
```

The benchmark writes output under the app directory, including:

- `graphs/iterations.csv`
- `graphs/iterations.jsonl`
- `soak-report.json`

### Launch the Inspector (Blazor WebAssembly)

```bash
dotnet run --project Inspector/Inspector.csproj
```

Then open the URL shown in the terminal, import the CSV + JSON/JSONL pair, and review tags, differences, and per-iteration graphs.

## Runtime Options (bmark)

```text
--hours <n>         Soak duration in hours
--seed <n>          Master seed for reproducibility
--quick             Run one fast iteration batch
--no-graphs         Skip per-iteration SVG chart output
--out <dir>         Output directory for graphs and dataset files
--report <file>     Path for summary JSON report
--no-dashboard      Disable live HTTP dashboard
--port <n>          Dashboard port (default: 8777)
--max-threads <n>   Engine thread budget (1-16)
```

## Guides

### 1) Reproducible benchmark workflow

1. Run a quick pass to validate your environment:
   - `dotnet run --project bmark.csproj -- --quick`
2. Run a longer soak when needed:
   - `dotnet run --project bmark.csproj -- --hours 8`
3. Keep the reported master seed so runs can be reproduced exactly.
4. Import the generated CSV + JSON/JSONL files into the Inspector.
5. Review diff results, tags, and iteration-level visualizations before comparing performance claims.

### 2) Dataset review and conclusion-building

Use the Inspector’s **Library** and **Search** pages to slice results by topology and scheduler behavior (for example, `shape:bushy`, `pathology:join-heavy`, or `scheduler:over-threaded`). Looking for edge-case records—especially flagged or invalid slices—usually helps sharpen conclusions and avoids overfitting to average-case runs.

## Reproducibility and Correctness Model

- DAGs are generated from deterministic seeds.
- Every parallel iteration is verified against a deterministic single-threaded reference checksum.
- Failures include iteration seed information to support exact replay.

## Contributing

Issues and pull requests are welcome. For changes affecting benchmark integrity, include:

- the command used,
- seed(s) used,
- generated report snippets or dataset diffs.

## License

MIT License. See `LICENSE.txt`.
