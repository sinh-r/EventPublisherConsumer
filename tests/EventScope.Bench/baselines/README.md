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

| PayloadSize | Mean | Allocated |
|---|---|---|
| 256 B | 359.2 ms | 1.73 GB |
| 4096 B | 465.7 ms | 1.95 GB |

Per-read latency (≈360–470 µs for 1,000 reads) is comfortably inside the "row selection
renders body < 100 ms" acceptance criterion.

**The allocation figure is the real finding here, not a benchmark artifact.** ~1.7–2 GB
across 1,000 reads of a 256–4096 byte payload means each read allocates roughly
1.7–2 MB on average — far more than the payload itself. `SegmentReader.ReadAsync`
(`src/EventScope.Storage/Segments/SegmentReader.cs`) allocates a fresh `compressed` buffer
and a fresh `uncompressed` buffer sized to the **entire containing block** (up to the
1 MB block size from `SegmentFormat`) on every call, decompresses the whole block, then
slices out just the requested `header.Length` bytes — there is no decompressed-block cache.
Against 10,000 payloads packed into relatively few 1 MB blocks, 1,000 *random*-offset reads
mostly miss whatever the previous read touched, so most reads pay a full block
decompression. This does not violate any M1 acceptance criterion (latency is still well
under budget) and is not fixed as part of M1c — it's Storage-internals tuning, out of this
pass's scope — but it is worth a line in a future M2 pass, since the deep-scan acceptance
criterion (§6: "≥ 500 MB/s decompressed") and any UI feature that reads many rows in quick
succession (bulk export, multi-select copy) will feel this allocation rate before they feel
the latency.
