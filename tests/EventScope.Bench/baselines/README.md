# Benchmark baselines

First recorded run of `EventScope.Bench` (M1c). These are a **local, single-machine
reference point**, not a CI regression gate — see the note in `.github/workflows/ci.yml`
for why a hard ">20% regression fails" gate against numbers from this specific laptop would
be a false-failure generator against `windows-latest` runner hardware. Re-run and replace
these files when the storage layer changes materially, or once CI grows its own
runner-generated baselines.

## Machine

- CPU: 12th Gen Intel Core i5-12500H @ 2.50GHz, 12 physical / 16 logical cores
- RAM: 16 GB @ 4800 MHz
- OS: Windows 11 10.0.26200.9278 (25H2)
- .NET SDK: 10.0.400, runtime 10.0.11
- Date: 2026-08-30

## Job

Run with `-j Short` (`ShortRun`: 3 warmup + 3 measured iterations, 1 launch), not
BenchmarkDotNet's default job, to keep a full run under a minute. `ShortRun`'s wider
confidence intervals are visible in the reports (e.g. `SqliteBatchInsertBenchmarks` at
RowCount=5000 has an `Error` almost as large as its `Mean`) — these numbers are a reference
order-of-magnitude, not a precision measurement. Re-run with the default job for a tighter
interval if a future comparison needs one.

Command:

```
dotnet run -c Release --project tests/EventScope.Bench -- -j Short -f '*' -a tests/EventScope.Bench/baselines
```

## Results

### `SqliteBatchInsertBenchmarks.InsertRows`

| RowCount | Mean | Allocated |
|---|---|---|
| 5,000 | 230.4 ms | 8.11 MB |
| 50,000 | 362.3 ms | 80.65 MB |

50,000 rows in 362 ms is ~138k rows/sec into the batch writer's queue — well past the
10,000 msg/s acceptance target for M1's ingest path (build plan §6), with room to spare.

### `SegmentReadBenchmarks.ReadOneThousandRandomPayloads`

Re-measured 2026-08-31 after `SegmentReader` grew a decompressed-block cache (see
`Docs/PROGRESS.md`'s M1-remainder step 2). Original (M1c, no cache) alongside the current
numbers, same benchmark, same machine:

| PayloadSize | Mean (M1c) | Mean (now) | Allocated (M1c) | Allocated (now) |
|---|---|---|---|---|
| 256 B | 359.2 ms | 60.63 µs | 1.73 GB | 31.62 KB |
| 4096 B | 465.7 ms | 63.98 µs | 1.95 GB | 31.62 KB |

Per-read latency was already comfortably inside the "row selection renders body < 100 ms"
acceptance criterion before this change; it still is, now by roughly four more orders of
magnitude.

**Why the improvement is this large, honestly stated:** the benchmark's own setup packs
10,000 payloads into a small number of ~1 MB blocks (a few blocks for 256 B payloads, a few
dozen for 4096 B), all of which fit inside the cache's default 64-block capacity. Once
warmed up, essentially every one of the 1,000 random reads is a cache hit — an array lookup,
not a decompression — which is why allocation collapsed to ~31 KB total (BenchmarkDotNet's
own harness overhead, not `SegmentReader`) instead of merely shrinking. This is a fair
reflection of the real access pattern the M1 acceptance criterion cares about (a user
scrolling and selecting rows keeps re-touching a bounded working set of recent segments),
but it is **not** evidence that a full day-file deep scan touching more distinct blocks than
the cache holds pays nothing — that case still decodes every block at least once, just once
per block instead of once per read. `SegmentReadBenchmarks` doesn't yet cover that shape;
worth adding when M2's deep scan lands, since that's the criterion (§6: "≥ 500 MB/s
decompressed") this cache was added in anticipation of.
