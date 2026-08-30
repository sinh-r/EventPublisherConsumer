# M1 acceptance criteria — measured (M1c)

Build plan §6 lists five acceptance criteria for M1. All five are measured here against real
code, not assumed — see `tests/EventScope.App.Tests/AcceptanceCriteriaTests.cs`,
`tests/EventScope.Acceptance.Tests/StorageAcceptanceCriteriaTests.cs`, and
`build/Measure-M1Acceptance.ps1`. Machine: see `../README.md` (same laptop, same session,
2026-08-30).

| Criterion | Result | Source |
|---|---|---|
| 10,000 msg/s for 60s, no frame over 100 ms | **Marginal — 1 of 2,697 samples over budget** (p50 18.3 ms, p99 37.3 ms, max 119.6 ms) | `gui-frame-time.csv` |
| Heap growth under 50 MB across that run | **Fails — ~470–500 MB growth measured**, not 50 MB | `gui-heap-growth.csv`, see below |
| 50,000-row scroll under 16 ms/frame | **Pass** (p50 3.5–3.6 ms, p99 4.7–8.1 ms, max 4.7–10.2 ms across repeated runs) | `scroll-frame-time.csv` |
| Row selection renders body under 100 ms | **Pass, comfortably** (p50 0.5–1.8 ms, max 7.6–41.3 ms depending on run) | `cold-segment-read-latency.csv` |
| Zero messages lost from disk under saturation | **Pass** — 20,000/20,000 messages landed on disk against a deliberately starved 16 KB byte budget | `saturation-zero-loss.csv` |

Three of five pass cleanly. Two do not, and are recorded honestly rather than smoothed over:

## Heap growth: ~470–500 MB, not under 50 MB

`gui-heap-growth.csv` is the raw `dotnet-counters` output from a 60s run of the real
`EventScope.exe` (`build/Measure-M1Acceptance.ps1`, `FakeEventSource` at the default 10k
msg/s). Three independent counters agree, ruling out a single-metric measurement fluke:

- Managed heap total (sum of gen0+gen1+gen2+poh+loh at each collection): **51 MB → 521 MB**
- Process working set: **277 MB → 771 MB**
- GC committed size: **63 MB → 564 MB**

**Not yet diagnosed as leak vs. GC lag vs. by-design buffering**, and that distinction
matters a lot for what (if anything) needs fixing — this is a measurement, not a root-cause
analysis, and root-causing it is out of this pass's scope (M1c is Kafka + measurement, not a
memory-tuning pass). Candidates worth checking first in a follow-up:

- The ingest channel's byte budget defaults to 256 MB (`IngestPipeline`'s
  `byteBudgetLimit` parameter) — that alone is close to half the observed growth if the
  budget is running near-full during a 10k msg/s burst, which would be expected buffering,
  not a leak.
- Gen2 collections are infrequent by design; 60 seconds may simply not be long enough for
  the GC to reclaim garbage that a longer run would show getting collected. A longer
  (5–10 minute) run with the same counters would distinguish "hasn't collected yet" from
  "won't collect."
- `MessageRowsView`'s ring buffers are fixed-capacity (65,536 rows by default) and shouldn't
  grow unboundedly — worth confirming they aren't, rather than assuming.

## Frame time: 1 sample over 100 ms out of 2,697

Not a clean pass, but close: p50 and p99 are both well inside budget (18.3 ms / 37.3 ms), and
only one 60ms-scale sample crossed 100 ms across the full minute — consistent with a single
GC pause rather than a sustained problem. Worth re-measuring alongside whatever the heap
investigation above finds, since the two are plausibly related (a large gen2 collection is
exactly the kind of event that would produce one slow frame).

## Also observed, not yet explained: slow shutdown

After the 60s measurement window closed and streaming stopped, `EventScope.exe` did not exit
on its own within 30 seconds (`build/Measure-M1Acceptance.ps1` had to force-stop it). Not
one of the five build-plan criteria, but worth flagging: a user who presses Stop or closes
the window after a sustained high-throughput run may see the same delay.
`IngestPipeline.DisposeAsync` cancels the ingest channel and awaits the drain task — whether
that's legitimately draining a large in-flight backlog, or something is stuck, isn't known
without a dedicated investigation.
