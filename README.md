# delta-lag

> Lock-free, parallel DAG scheduler for C# — distance constraints keep nodes in sync without locks.

Status: Experimental. Not production‑ready. APIs may change.

**Prerequisites:** .NET 10+

```bash
git clone https://github.com/Geezy9/delta-lag.git
cd delta-lag && dotnet build && dotnet run
```
## Why

TLDR: Fast, Deterministic, lightweight.

Most task schedulers do a great job maximizing throughput, but they pay for it with layers of locks, queues, and work-stealing algorithms. That’s great for general workloads, but overkill when you want predictable, low-overhead parallelism.

Lock-free algorithms like ring buffers show how far you can get with nothing but atomic counters. delta-lag extends that idea to an entire DAG: every node progresses like a position in a ring buffer, coordinated only by distance constraints.

No locks. No stealing. No global queues.

## Benchmark Snapshot

Representative run processing 300,000 tasks:

| Engine | Tasks Processed | Time Taken | Memory Wasted (GC) | Efficiency |
|---|---:|---:|---:|---|
| TPL Dataflow | 300,000 | 15.6 ms | 2,371 KB | Baseline |
| Delta-lag | 300,000 | 4.2 ms | 0 KB | 3.7x Faster |

## Warning:

When utilizing the slack parameter, please be aware that this library does not provide buffering for `Slot<T>`. Therefore, external buffering mechanisms must be implemented.

## Quick Example with a ring buffer.

```csharp
using deltalag;

const int BufferSize = 8;
int[] buffer = new int[BufferSize];

var builder = new GraphBuilder();

// Producer node: Writes data into ring buffer
// Each work-unit gets assigned to a buffer slot using modulo
int producer = builder.AddNode(workUnit =>
{
    int index = workUnit % BufferSize;
    buffer[index] = workUnit;
});

// Consumer node: Reads data from ring buffer
// Uses the same modulo logic to read from the correct slot
int consumer = builder.AddNode(workUnit =>
{
    int index = workUnit % BufferSize;
    int value = buffer[index];
    
    // NOTE: Console.WriteLine is blocking and slow - used here only for demonstration.
    // In production code, use non-blocking operations to avoid performance degradation.
    Console.WriteLine($"Consumed {value} from slot {index}");
});

// Define dependency: Consumer must execute after Producer for each work-unit
// This prevents the consumer from reading before the producer writes
builder.AddEdge(producer, consumer);

// Build compiles the graph and pre-allocates all scheduling structures upfront.
// Run consumes the WorkContext with zero setup allocations at scheduling time.
// - cycles:   32 work-units will be processed
// - slack:    0 means strict ordering (no lookahead)
// - threads:  2 parallel execution threads
// - strategy: controls how nodes are distributed across threads
var ctx = builder.Build();
// if you need to mutate the state of the graph you can do so by modifying the returned WorkContext before passing it to a run.
var scheduler = new Scheduler();
scheduler.Run(ctx, cycles: 32, slack: 0, threads: 2, strategy: PartitionStrategy.Stride);

```

Nodes share data through `Slot<T>` — no message passing, no boxing. The scheduler enforces ordering structurally via claimed work and published completion using atomic CAS operations.

---

## Partition Strategies

`PartitionStrategy` controls how graph nodes are divided across threads. The right choice depends on the shape of your graph and how work is distributed.

### `PartitionStrategy.Stride` *(recommended default)*

Each thread is assigned every N-th node, where N is the thread count.

- Thread 0 → nodes 0, 4, 8, 12, …
- Thread 1 → nodes 1, 5, 9, 13, …
- Thread 2 → nodes 2, 6, 10, 14, …

**Best for:** pipelines and DAGs where nodes are roughly uniform in cost. Stride interleaving naturally spreads hot nodes across threads and tends to reduce contention on adjacent nodes.

### `PartitionStrategy.ChunkedLinear`

Each thread is assigned a contiguous block of nodes.

- Thread 0 → nodes 0–15
- Thread 1 → nodes 16–31
- Thread 2 → nodes 32–47

**Best for:** graphs where nodes have strong spatial locality or when cache-line affinity within a range matters. Can perform worse than Stride when nodes at the boundary of a pipeline are frequently contended.

### Choosing a strategy

| Scenario | Recommended strategy |
|---|---|
| Linear pipeline, uniform work | `Stride` |
| Dense DAG with spatial locality | `ChunkedLinear` |
| Unsure | `Stride` — generally lower p95 latency |

Both strategies produce identical, deterministic output. The difference is purely in how threads are assigned nodes; correctness is unaffected.







## What Changed Recently 

> Open Changelog.md for full breakdown

The latest updates include:
- `Scheduler.Run()` now accepts a `PartitionStrategy` parameter — choose between `Stride` (interleaved, lower p95 on pipelines) and `ChunkedLinear` (contiguous blocks, favours spatial locality).
- `GraphBuilder.Build()` now returns a `WorkContext` with all scheduling structures pre-allocated. `Scheduler.Run()` accepts a `WorkContext` directly, eliminating setup allocations at scheduling time.
- The `Node` class is now cache-aligned to reduce false sharing and improve performance under contention.
---

## How Scheduling Works

Before a node fires, `CanFire` checks two constraints:

```
parent.WorkDone >= node.WorkDone + 1    // parent must be ahead
child.WorkDone  >= node.WorkDone - slack // node can't lap children
```

If the node is eligible, the scheduler:

1. computes the next work unit with `CanFire`
2. atomically claims that work unit with `TryClaimWork`
3. runs `Task(workUnit)`
4. publishes completion with `PublishWorkDone(workUnit + 1)`

- `slack = 0` → strict lock-step (safe, no stale reads)  
- `slack > 0` → pipeline parallelism (producer runs ahead; consumer may read older values)

---

## API

**`GraphBuilder`**
| Method | Description |
|---|---|
| `int AddNode(Action<int>? task)` | Add a node. The task receives the current work unit index. Returns the node index. |
| `void AddEdge(int from, int to)` | Add a directed edge. |
| `WorkContext Build()` | Compile to CSR-encoded graph and pre-allocate all scheduling structures. |

**`Scheduler`**
| Method | Description |
|---|---|
| `void Run(WorkContext ctx, int cycles, int slack, int threads, PartitionStrategy strategy)` | Run the pre-built work context using the specified partition strategy. |

**`PartitionStrategy`**
| Value | Description |
|---|---|
| `Stride` | Distributes nodes across threads in an interleaved pattern (every N-th node per thread). Generally lower p95 latency on linear pipelines. |
| `ChunkedLinear` | Distributes nodes across threads as contiguous blocks. Favours spatial locality within a range. |

**`Scheduler.WorkContext`**
| Member | Description |
|---|---|
| `Graph Graph` | The underlying CSR-encoded graph. |
| `Node[] NodeArray` | Pre-materialized node array. |
| `int[] Edges` | Pre-materialized forward edge array. |
| `int[] Offsets` | Pre-materialized CSR offset array. |
| `int[] ParentEdges` | Pre-built reverse edge array for parent lookups. |
| `int[] ParentOffsets` | Pre-built reverse CSR offsets for parent lookups. |

**`Node`**
| Member | Description |
|---|---|
| `int WorkClaimed` | The highest work unit atomically reserved for execution. |
| `int WorkDone` | The highest fully published work unit. |
| `bool TryClaimWork(int expected)` | Atomically claims a work unit. |
| `void PublishWorkDone(int newValue)` | Publishes completion after task output is visible. |

**`Slot<T>`** — `T Value` — shared data cell captured by closures.



## License

MIT
