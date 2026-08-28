# EventScope — build plan (.NET 10)

Derived from `eventscope-implementation-plan.md` and `eventscope-ui-spec.md`, with the
mockup at `Mockup preparation from spec\EventScope.dc.html` as the visual reference.

## Context

This directory currently holds two specs and a Claude Design mockup, and no code. The
goal is the whole application described by `eventscope-implementation-plan.md` — a
desktop tool for engineers debugging event-driven systems: it connects to Kafka /
Azure Service Bus / AWS SQS, streams messages into a searchable virtualized grid at
10,000 msg/s, persists them to a capped local store, and publishes synthetic test
events back.

All four milestones (M1–M4) are planned as one continuous build rather than
milestone-by-milestone approvals, and the target framework moves from **.NET 9 to
.NET 10**. SDK 10.0.400 is installed and verified.

Two things in the source material need noting up front:

- The implementation plan tells the reader to consult `eventscope-design-plan.md` for
  architectural rationale. **That file does not exist anywhere on disk.** Every
  architectural decision below is therefore derived from the implementation plan, the
  UI spec, and the mockup — not from that missing document. Nothing appears to be
  blocked by its absence, but any rationale it held is not reflected here.
- The mockup is a *visual* reference. Per the spec its markup, CSS and JS are **not**
  ported; only its numbers are, and those are extracted verbatim into §4.

## Decisions taken

| Decision | Choice | Why |
|---|---|---|
| Target framework | `net10.0` | The requested change. C# 14, `System.Threading.Lock`, `SearchValues`, `TimeProvider` all available. |
| UI framework | **Avalonia 11.3.20** | Matches the spec verbatim; mature documented API. Avalonia 12.1.1 does ship a native `net10.0` target, but has no `Avalonia.Diagnostics`/DevTools release and would mean rediscovering v12 breaking changes by compile error across a build this large. 11.3.20 targets `net8.0` and runs on `net10.0` unchanged. |
| Milestone scope | **M1 → M4, continuous** | No stop-for-approval between milestones; acceptance criteria still gate each one internally. |
| Broker verification | **Fake `IEventSource` only** | No live brokers on this machine. All throughput/memory/search/UI criteria are measurable against the synthetic source the spec already calls for. Real broker code is written and unit-tested, but integration tests are opt-in via env vars and skipped by default. |
| Workspace | **`git init` in place** | Repo at the project root with a .NET `.gitignore`; specs committed as the baseline; one commit per milestone so each is reviewable and revertible. |
| Compression | **LZ4** (`K4os`) | Kept over `ZLibStream` — deep-scan throughput (≥ 500 MB/s decompressed) depends on it. |
| Test framework | **xunit v3 everywhere**, bare `Avalonia.Headless` | Verified: `Avalonia.Headless.XUnit` 11.3.20 depends on `xunit.core 2.4.0` — it is xunit **v2** only, and v3 support landed on the 12.0.x line. Rather than split the suite across two xunit majors, the App tests reference plain `Avalonia.Headless` 11.3.20 and use a hand-rolled ~40-line fixture that owns the headless lifetime and pumps `Dispatcher.UIThread.RunJobs()` explicitly. Deterministic dispatcher pumping is wanted for the coalescer tests regardless. |

---

## 1. Solution layout

Created at the project root, alongside the existing spec files.

```
EventScope.sln
Directory.Build.props          net10.0, nullable, C# latest, warnings-as-errors
Directory.Packages.props       central package management, all versions pinned
.gitignore
src/
  EventScope.Core/             no broker, no UI references
    Abstractions/              IEventSource, IEventSink, SourceCapabilities
    Models/                    RawMessage, MessageHeader, OutgoingMessage
    Ingest/                    ByteBudget, IngestChannel
    Generation/                token lexer, GenerationPlanner, GenerationRunner
  EventScope.Storage/
    Segments/                  SegmentWriter, SegmentReader, LZ4 block framing
    Sqlite/                    schema, migrations, SqliteBatchWriter, SessionStore
    Search/                    tiered search, FTS queries, indexer catch-up
    Retention/                 day-file rolling, eviction, cap enforcement
  EventScope.Brokers.Kafka/
  EventScope.Brokers.ServiceBus/
  EventScope.Brokers.Sqs/
  EventScope.App/              Avalonia
    Views/  ViewModels/  Controls/  Collections/  Themes/
tests/
  EventScope.Core.Tests/
  EventScope.Storage.Tests/
  EventScope.App.Tests/
  EventScope.Bench/            BenchmarkDotNet
```

**Enforced isolation.** `EventScope.Core.Tests` asserts that
`typeof(IEventSource).Assembly.GetReferencedAssemblies()` contains no
`Confluent.Kafka`, `Azure.Messaging.*`, `AWSSDK.*`, or `Avalonia.*`.

---

## 2. Packages (all versions resolved against nuget.org, pinned centrally)

| Package | Version | Where |
|---|---|---|
| `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter` | 11.3.20 | App |
| `Avalonia.Diagnostics` | 11.3.20 | App, `Condition="'$(Configuration)'=='Debug'"` |
| `Avalonia.Controls.DataGrid` | 11.3.13 | App — versions independently of core Avalonia; depends on `Avalonia >= 11.3.13`, satisfied by 11.3.20 |
| `Avalonia.Controls.TreeDataGrid` | 11.3.2 | App — publisher JSON tree, and the grid fallback if the spike fails |
| `Avalonia.AvaloniaEdit` + `AvaloniaEdit.TextMate` | 11.4.1 | App — JSON body view |
| `Avalonia.Headless` | 11.3.20 | App tests — **not** `.XUnit`, see the test-framework decision above |
| `CommunityToolkit.Mvvm` | 8.4.2 | App |
| `Microsoft.Data.Sqlite` | 10.0.11 | Storage — pulls `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 |
| `K4os.Compression.LZ4.Streams` | 1.3.8 | Storage |
| `Confluent.Kafka` | 2.15.0 | Brokers.Kafka — has a native `net10.0` target |
| `Azure.Messaging.ServiceBus` | 7.20.2 | Brokers.ServiceBus |
| `AWSSDK.SQS` | 4.0.100.11 | Brokers.Sqs |
| `xunit.v3` 4.0.0, `xunit.runner.visualstudio` 4.0.0, `Microsoft.NET.Test.Sdk` 18.9.0 | | tests |
| `BenchmarkDotNet` | 0.15.8 | Bench |

`System.Text.Json` (`JsonNode`) for all JSON. No Newtonsoft.

**Startup capability probe**, by behaviour rather than version string, failing loudly
with a dialog rather than degrading silently:

```sql
SELECT sqlite_compileoption_used('ENABLE_FTS5');                    -- must be 1
CREATE VIRTUAL TABLE temp.__probe USING fts5(x, tokenize='trigram');-- must not throw
DROP TABLE temp.__probe;
```

The trigram tokenizer has been core FTS5 since SQLite 3.34 with no separate compile
flag, so FTS5 present implies trigram present — but the probe proves it rather than
assuming the bundled native build.

---

## 3. Architecture — the six hard parts

Everything else in this build is ordinary work. These six are where it can actually
fail, so each is specified concretely. The `DataGrid` and SQLite findings below were
verified against source and documentation, not inferred.

### 3.1 The virtualizing collection adapter — highest risk

The spec is emphatic: binding a `DataGrid` to `ObservableCollection<MessageViewModel>`
with 50k live view models defeats the entire memory design.

**What `DataGrid` actually requires.** `DataGridDataConnection` resolves items in three
tiers: `IDataGridCollectionView`, then non-generic **`IList`**, then bare `IEnumerable`.
Consequences:

- **`IReadOnlyList<T>` alone is useless.** It is neither non-generic `ICollection` nor
  `IList`, so it falls to the `IEnumerable` tier where `Count` becomes
  `Cast<object>().Count()` and item access enumerates from index 0 — O(n) per row
  realization, O(n²) per frame. **The non-generic `IList` is mandatory**; the fast path
  needs `Count`, `IndexOf`, and `this[int]`.
- **Do not implement `IDataGridCollectionView`.** It has no `Count`/`GetItemAt`, and the
  fast path type-checks the *concrete* `DataGridCollectionView`. You would get the
  ceremony with none of the benefit.
- **It does not auto-wrap**, unlike WPF. That is fortunate:
  `DataGridCollectionView.CopySourceToInternalList()` materializes the entire source.
- `DataGrid` does its own row realization and recycling independently of
  `VirtualizingStackPanel`, so **UI virtualization works over any `IEnumerable`** —
  `IList` only makes it fast. We supply the *data* virtualization ourselves.

```csharp
public sealed class MessageRowsView
    : IList,                                  // required for the fast path
      IReadOnlyList<MessageRowViewModel>,     // for our own typed code
      INotifyCollectionChanged
```

Backed by a UI-thread-owned ring of **struct** headers — no per-message allocation:

```csharp
[StructLayout(LayoutKind.Auto)]
public readonly struct MessageHeader        // ~56 bytes; 65_536 × 56 ≈ 3.6 MB
{
    public readonly long Sequence, EnqueuedTicks, RowId;
    public readonly int SegmentId, Offset, Length, SubjectId, CorrelationInternId;
    public readonly short Partition;
    public readonly MessageFlags Flags;     // byte
}
```

Preview strings, not headers, are the real memory cost: 65k × 120 chars ≈ 16 MB in a
parallel `string?[]`. Previews are **capped at 120 chars at ingest** and budgeted
explicitly against the 50 MB heap-growth criterion. They are not lazily fetched from
SQLite on realize — that would put a sync-over-async read on the UI thread.

**Follow / pinned windowing — the design that removes most of the problem.** `Count` is
a *window*, not the ring head:

- **Follow mode** (scrolled to top, nothing selected): the window slides with the ring
  head. Once the ring is full `Count` is constant, so new data changes *content at fixed
  indices*. The ~40 realized row VMs raise `INotifyPropertyChanged` on their bound
  properties and **zero collection notifications are emitted** — roughly 3.8k property
  notifications/sec, which is nothing.
- **Pinned mode** (a row is selected, or the user scrolled off the top): the window base
  and length freeze. New messages accumulate in the ring behind a "N new messages" chip
  — which is exactly what the mockup draws, and what the spec's Paused state means.
  **Zero notifications, ever.** Selection, scroll offset and detail-pane reads are all
  stable by construction.
- Only *transitions* — warm-up while `Count` still grows, ring wrap, pin/unpin, filter
  change — emit a single `Reset`.

**Row VM recycling** hooks `DataGrid.LoadingRow` / `UnloadingRow`; `UnloadingRow` is the
de-realization signal, and `e.Row.DataContext` returns to the pool. One hazard: `Reset`
restores selection **by object identity**, so a VM instance backing `SelectedItem` must
never be recycled into a different logical row. `IndexOf` is O(1) and exact —
`(baseSeq + windowLength - 1) - row.Sequence`.

Mutating `IList` members throw `NotSupportedException`; `IsReadOnly`/`IsFixedSize` are
true.

**Two non-obvious XAML settings that are load-bearing:**
- `RowHeight="26"` set explicitly — otherwise `DataGrid` estimates extent from realized
  rows and the scrollbar thumb jumps around during ingest.
- **`CanUserSortColumns="False"`** — a sort request forces a collection view, which calls
  `CopySourceToInternalList()` and materializes all 50k rows. This is a one-click cliff
  straight into the failure the spec is written to prevent.

**Spike first, before any UI is built on it.** Empty Avalonia app, `MessageRowsView` over
200,000 synthetic rows. Assert: (a) instrumenting `this[int]` shows ≤ ~60 distinct
indices touched per frame while scrolling, proving virtualization engaged; (b) after
scrolling to row 150,000, selecting, and forcing a `Reset`, selection survives and
scroll offset's behaviour is measured rather than assumed.

**Fallback if the spike fails:** `TreeDataGrid` in flat mode
(`FlatTreeDataGridSource<MessageRowViewModel>`, `TextColumn<T,TValue>` with lambda
accessors). Written from scratch for Avalonia, real virtualization, and lambda cell
accessors avoid per-cell `Binding` allocation — plausibly *faster* here. The package is
already a dependency for the publisher tree, so this costs no new dependency. Second
fallback is `ItemsRepeater` + `RecyclingElementFactory` with a hand-rolled column grid.

### 3.2 The byte-bounded ingest channel

`BoundedChannelOptions` caps item *count* only; the 256 MB byte budget needs its own
gate.

```csharp
public sealed class ByteBudget
{
    private readonly long _limit;
    private readonly Lock _gate = new();          // System.Threading.Lock
    private long _used, _peak;
    private TaskCompletionSource? _space;          // RunContinuationsAsynchronously

    public bool TryAcquire(int bytes);                                  // no lock, no alloc
    public ValueTask AcquireAsync(int bytes, CancellationToken ct);     // writer only
    public void Release(int bytes);                                     // reader only, never awaits
    public void Complete();
}
```

- Fast path is `Interlocked.Add`, roll back and return false if over. No allocation.
- `AcquireAsync` **rechecks the counter inside the lock** after enlisting — this is what
  kills the lost-wakeup where a `Release` lands between the check and the register.
- `Release` completes the TCS **under the same lock**, with a low-water mark at ¾ of the
  limit so the gate doesn't thrash open and closed.
- Channel: `SingleReader`/`SingleWriter = true`, `FullMode = Wait`, and
  **`AllowSynchronousContinuations = false`** — mandatory. Combined with
  `RunContinuationsAsynchronously` on the TCS, it stops `SetResult` from inlining the
  broker loop onto the reader thread and stalling the drain.

Not `SemaphoreSlim` — its unit is a permit, not a byte, and there is no `Wait(n)`. Not
`IValueTaskSource` — the slow path only runs during saturation and with a single writer
there is at most one waiter, so it is one `Task` per saturation episode.

| Failure mode | Guard |
|---|---|
| Lost wakeup | recheck inside the lock; release completes under it |
| Continuation inlining stalls the drain | `RunContinuationsAsynchronously` **and** `AllowSynchronousContinuations=false` |
| Permanent deadlock on a message ≥ the whole budget | admit unconditionally when `bytes >= limit && used == 0` |
| Leaked reservation when the write throws after acquire | `catch { Release(bytes); throw; }` |
| Leaked reservation on segment-write failure | reader releases in `finally`, beside `ArrayPool.Return` |
| Shutdown hang | `Complete()` cancels the parked TCS |

**Deadlock-freedom argument, to be stated in a comment:** only the writer parks on the
budget, only the reader releases. One directed edge writer→reader, no back-edge, no
cycle.

**Two budgets, not one.** The byte budget back-pressures broker→disk: Kafka stops
calling `Consume`, lag builds, which is correct. The UI coalescer is *deliberately
lossy*. That split is precisely how "zero messages lost from disk" and "UI drop count is
accurate" hold at the same time.

### 3.3 The UI coalescer

**Verified `DataGrid` notification handling — this determines the design:**

| Action | Actual behaviour |
|---|---|
| `Add`, multi-item | **Silently inserts exactly one row.** The `Debug.Assert(e.NewItems.Count == 1)` is compiled out of the shipped Release assembly. Row count desyncs and the grid renders garbage or throws later. **Never emit this.** |
| `Replace` | throws `NotSupportedException` unconditionally |
| `Move` | no case at all — silently ignored, desync |
| `Reset` | the only correct batched action; with `AutoGenerateColumns="False"` it takes the `recycleRows: true` branch and reuses row visuals. **Preserves selection by object identity; does not preserve scroll offset.** |

Per §3.1 the steady state emits *no* collection notification at all, so `Reset` only
fires on transitions and selection/scroll survival is a non-problem outside them.

```csharp
public interface IUiTicker { event Action Tick; void Start(); void Stop(); }
// DispatcherTimerTicker (production)  |  ManualTicker (tests)
```

The coalescer double-buffers: `Enqueue` (ingest thread) takes the lock only to bump an
index; `OnTick` (UI thread) takes the lock, swaps the two buffer pairs and zeroes the
count — **O(1) under the lock** — then appends off-lock. Staging is bounded; overflow in
a single tick increments `UiDropped` rather than growing.

`DispatcherTimer` at **60 ms**, `DispatcherPriority.Background` — **not** `Normal` or
`Send`. Input and render must outrank ingest to hold the 100 ms frame budget.

One more guard from the source: `DataGrid` throws `CannotChangeItemsWhenLoadingRows` if
a collection change is raised from inside a `LoadingRow`/`UnloadingRow` handler. Never
raise from there, never pump the dispatcher from there.

Where a `Reset` *is* forced, selection is anchored by **message sequence, not row index**
(indices shift as the ring evicts), and restored via `ScrollIntoView` — never by setting
`ScrollViewer.Offset`, which desyncs `DataGrid`'s own scroll bookkeeping.

### 3.4 External-content FTS5 and the indexer

`body_fts` and `ident_fts` are `content='messages'` external-content tables, populated
outside the ingest transaction so indexing never stalls a write.

**The contract rule that will bite.** External-content FTS stores only the index and
re-reads column values from `messages` by rowid for `snippet()`/`highlight()`/`bm25()`.
If an indexed column changes after indexing, results are silently wrong and
`integrity-check` fails. Indexed columns here are `body_head`, `message_id`,
`correlation_id`. **Never `UPDATE` any of them.**

The specific trap is retention: it is very tempting to `UPDATE messages SET body_head =
NULL` on eviction to reclaim space. **Do not.** Setting `flags |= 4` is safe — `flags` is
not indexed in either table. This is also why the spec forbids `DELETE` triggers and FTS
`'delete'` rows: day files are dropped whole, so they would be wrong, not merely
redundant.

**Catch-up batch — one transaction, window computed once so both tables index an
identical row set:**

```sql
BEGIN IMMEDIATE;                          -- take the write lock up front, no mid-txn upgrade
SELECT COALESCE(MAX(id), :hwm) FROM (
    SELECT id FROM messages WHERE id > :hwm ORDER BY id LIMIT 2000);      -- -> :newHwm
INSERT INTO body_fts(rowid, body_head)
  SELECT id, body_head FROM messages
   WHERE id > :hwm AND id <= :newHwm AND body_head IS NOT NULL;
INSERT INTO ident_fts(rowid, message_id, correlation_id)
  SELECT id, message_id, correlation_id FROM messages
   WHERE id > :hwm AND id <= :newHwm;
UPDATE index_state SET value = :newHwm WHERE name = 'fts_hwm';
COMMIT;
```

Advancing the high-water mark **in the same transaction** as the inserts is load-bearing:
FTS5 does not dedupe, so re-inserting a rowid after a crash creates duplicate index
entries. The window is computed separately rather than taken from the inserted set
because `body_head IS NULL` rows are skipped in `body_fts` but must still advance the
mark.

Idle maintenance: `INSERT INTO body_fts(body_fts) VALUES('merge', -16)` keeps query
latency flat; `('optimize')` on close.

**Trigram gotcha:** queries shorter than 3 characters match nothing. One- and two-char
correlation-ID searches route to the in-memory ring filter or a `LIKE '%x%'` scan, and
the UI says which.

**Index lag** is `MAX(messages.id) − hwm`, converted to ms via `received_ticks` of row
`hwm+1`, surfaced in the status bar. Every FTS result set is stamped with its `IndexHwm`
so the UI can state whether results are current — the spec makes this a first-class
metric.

**WAL checkpoint starvation.** `wal_checkpoint` cannot advance while any reader holds a
read transaction, and deep scan holds long ones — unchecked, `-wal` grows without bound
and blows the storage cap from a direction the cap accounting doesn't see. Mitigations:
`PRAGMA journal_size_limit = 64MB`; page search results instead of holding a reader open
across an `IProgress` stream; **count `-wal` bytes toward the cap**; run
`wal_checkpoint(TRUNCATE)` from the writer thread only when idle.

### 3.5 The two-pass generator engine

`{{ref:$.path}}` resolves against values generated in the *same* message, so generation
cannot be a single left-to-right walk.

**Pass 1 — plan.** Lex every leaf's generator to tokens; each `RefToken` becomes an edge
`dependency → dependent`. Graph in CSR form (`int[] edgeStart, edgeTarget, inDegree`)
rather than `List<int>[]`. Topological order by **iterative Kahn — never recursive DFS**,
because the acceptance criterion is literally "not stack-overflowed" and no `catch`
recovers from `StackOverflowException`. Self-edges are *not* skipped: `$.a` referencing
`$.a` is a valid 1-cycle and must be reported.

Kahn tells you only *that* a cycle exists. To name it, run **iterative Tarjan SCC**
(explicit stack) over the residual subgraph; every SCC of size > 1 plus every self-loop
is a cycle, reported as a closed walk:

```csharp
public sealed record PlanDiagnostics(
    IReadOnlyList<RefCycle> Cycles,          // $.a → $.b → $.c → $.a, each hop with its TextSpan + line
    IReadOnlyList<UnresolvedRef> Unresolved);
```

An unknown ref path is **not** a cycle — it is `UnresolvedRef`, reported with its span
and line so the editor can render `Invalid: unresolved {{ref:$.missing}} at line 8`
inline, before publish, exactly as the mockup draws it.

**Pass 2 — fill.** `GenerationRunner` holds a reused `string?[] _values` indexed by node
index and a scratch char buffer. Walking the topological order guarantees every ref
target is already filled, so resolution is a straight array read — no dictionary, no
recursion, no re-entrancy.

**Plan caching is the performance story.** The plan depends only on tree structure and
token text, never on generated values. Compute once, invalidate on edit (debounced 150 ms
for inline validation). A burst of 1,000 is one plan plus 1,000 `Fill` calls over
`template.DeepClone()` — which is what makes 1,000 distinct GUIDs cheap.
`Guid.CreateVersion7()` gives time-sortable synthetic IDs; `Random.Shared` for
`{{int}}`/`{{pick}}`.

### 3.6 Threading model

| Unit | Kind | Owns |
|---|---|---|
| Broker consume loop | 1/connection. Kafka: dedicated `LongRunning` task (`Consume()` is blocking sync). ASB/SQS: pooled async loop | client, offsets, `ArrayPool` rentals |
| Ingest reader loop | 1/connection, `SingleReader=true` | decode, preview + `body_head` extraction, interning |
| Segment writer | **inline on the ingest reader** | file handle, 1 MB LZ4 block buffer, 64 MB roll |
| SQLite batch writer | **1 thread per `.db` file — owns the only write connection** | schema, transactions (500 rows / 200 ms), `subjects` interning |
| FTS indexer | **on the batch writer's thread and connection**, between batches | `index_state.fts_hwm` |
| Retention | own task, `PeriodicTimer(30s)` | file deletion, byte accounting |
| Search / deep scan | pooled tasks, one **read-only** connection per query per day file | cancellation, `IProgress<T>` |
| UI dispatcher | the UI thread | all VMs, the ring, `MessageRowsView`, coalescer timer, selection |

**Three folds, each deliberate.** The segment writer runs inline on the ingest reader
because it returns `(segmentId, offset, length)` synchronously and that tuple is exactly
what the SQLite row needs — separating them buys a handoff and nothing else. The FTS
indexer runs on the writer's thread and connection because a second write connection
means `BEGIN IMMEDIATE` contention, `SQLITE_BUSY` storms, a `busy_timeout`, and a retry
loop that stalls ingest unpredictably; same-thread means zero contention and
self-regulating lag. Budget it: after each ingest commit, if queue depth is low, run
index batches until a 10 ms-per-200 ms budget is spent.

**Every place two units would collide on SQLite:**

1. **Two connection tabs writing the same day `.db`** — the most likely real bug.
   `SessionStore` owns a **per-day-file singleton** `SqliteBatchWriter`; tabs enqueue to
   it and never construct their own.
2. **Indexer vs ingest** — resolved by construction above.
3. **Retention's `flags |= 4` update vs ingest** — retention must post it as a `WriteOp`
   on the batch writer's queue. Easy to get wrong because retention *feels* like a
   background file operation.
4. **Checkpointer vs long-lived search readers** — see §3.4.
5. **Deleting a day `.db` on Windows** — `File.Delete` throws `IOException` while any
   pooled connection holds the handle. Must `Close()` **and**
   `SqliteConnection.ClearPool(...)`, then delete `.db`, `.db-wal`, `.db-shm`. This is
   Windows-specific and would not reproduce on a Linux CI runner.
6. **`SqliteConnection` is not thread-safe** even under a lock — it carries statement and
   reader state. One connection per thread, enforced by a debug-only thread guard that
   `Interlocked.CompareExchange`s the managed thread id on entry and throws on mismatch.

**Day rollover (M2).** Both writers stay alive across the boundary. Rollover is a
`WriteOp` on the *old* writer's queue: it commits, seals itself, and later enqueues route
to the new one. Driven by `TimeProvider` so the fake-clock test is real.

### .NET 10 / C# 14 choices that beat what the spec assumes

| Use | Where |
|---|---|
| `System.Threading.Lock` | `ByteBudget`, coalescer swap — faster than `Monitor` on `object` |
| `TimeProvider` / `FakeTimeProvider` | rollover, retention, lag. The M2 criterion says "fake the clock" — this is in the BCL, don't hand-roll `IClock` |
| **`RandomAccess.Read(SafeFileHandle, Span<byte>, long)`** | segment reader and deep scan — positional thread-safe reads from *one* shared handle. No `FileStream`, no seek lock, no per-reader handle. The correct primitive, and the spec doesn't mention it |
| `SearchValues<string>` (ordinal-ignore-case, new in .NET 10) | the live in-memory ring filter — SIMD substring search across 50k previews, which is what makes the "instant" scope instant |
| `PeriodicTimer` | retention loop, lag sampler — no drift |
| `FrozenDictionary` | `GenerationPlan.IndexByPath`, subject interning |
| `JsonNode.DeepClone()` | burst publish — clone rather than re-parse per copy |
| `Guid.CreateVersion7()` | `{{guid}}` |
| `[ObservableProperty]` on **partial properties** (Toolkit 8.4 + C# 13) | `MessageRowViewModel` — no field-naming dance |
| `params ReadOnlySpan<T>` | token emit helpers, `AppendRange` |

The C# 14 `field` keyword is cosmetic in this codebase; not worth reaching for.

---

## 4. Visual specification

Extracted from `EventScope.dc.html`. **Important:** the mockup does not use its bundled
Nocturne `styles.css` for layout — it declares private tokens in a `<style>` block and
styles every element inline. The inline values are authoritative; Nocturne's `--space-*`
scale and 4/8/14 radii are *not* what the mockup draws. Port these numbers.

### 4.1 Colour tokens → `Themes/Tokens.axaml`

A `ResourceDictionary` with `ThemeVariant` scopes for Dark and Light. Dark is the primary
theme and the app default.

| Resource key | Role | Dark | Light |
|---|---|---|---|
| `Bg` | window, grid, search bar, tab strip | `#161826` | `#eceef7` |
| `Surf` | toolbar, status bar, detail/publisher pane, grid header, active tab | `#1c1e2c` | `#f7f8fd` |
| `Surf2` | inset controls: inputs, selects, pickers | `#232532` | `#ffffff` |
| `Raise` | dialogs, settings, toast, overlays | `#2a2c3b` | `#ffffff` |
| `Text` | primary text | `#e9e9ed` | `#232532` |
| `Muted` | secondary text, meter fill | `#9397ab` | `#5f6377` |
| `Dim` | tertiary text, gutters, placeholders | `#75798c` | `#82869a` |
| `Line` | primary 1px border | `#E9E9ED` @ 13% | `#232532` @ 16% |
| `Line2` | hairline, zebra fill, meter track | `#E9E9ED` @ 7% | `#232532` @ 7% |
| `Accent` | active segment, JSON keys, progress | `#9184d9` | `#5d5294` |
| `AccentDim` | selected-row ring, hover borders | `#5d5294` | `#796cbf` |
| `AccentTint` | accent wash | `#9184D9` @ 14% | `#5D5294` @ 11% |
| `Green` | connected, valid, JSON strings | `#57b98d` | `#2f7d5c` |
| `Amber` | warning, large payload, search hits, JSON numbers | `#d7a44f` | `#8c6215` |
| `AmberTint` | amber wash | `#D7A44F` @ 15% | `#8C6215` @ 14% |
| `Red` | destructive, error, dead-letter | `#d9736f` | `#a5423e` |
| `RedTint` | red wash | `#D9736F` @ 13% | `#A5423E` @ 11% |
| `Hover` | universal row/button hover | `#E9E9ED` @ 5% | `#232532` @ 4.5% |
| `Sel` | selected grid-row fill | `#9184D9` @ 16% | `#5D5294` @ 12% |

Theme-invariant: modal scrim `#0A0B12` @ 58%. Shadows — overlay `0 18px 40px #000 @45%`,
dialog `0 24px 60px #000 @50%`, toast `0 12px 28px #000 @40%`.

### 4.2 Typography

- Sans: **Inter**, via `Avalonia.Fonts.Inter` — exact.
- Mono: family string `"JetBrains Mono, Cascadia Mono, Consolas"` — see §7.
- Only weights **400** and **500** are used. Nothing is bold.
- Every ID, timestamp, payload and size figure is mono; labels, buttons, menus sans.

Sizes by region: grid rows **12.5 mono**; grid column header **11 sans, uppercase,
letter-spacing 0.04em**; toolbar buttons **12.5 sans**; throughput readout and result
count **12 mono**; status bar **11.5 mono**; tab labels **12.5 sans**; detail body lines
**12.5 mono** with an **11 mono** gutter; token chips **10.5 mono**; keyboard hints
**10.5 mono @ 65% opacity**; uppercase section labels **11 sans, ls 0.09em**; dialog
titles **14 sans w500**.

### 4.3 Geometry

Tab strip **36**, connection toolbar **48**, warning/error banner **36**, search bar
**44**, grid header row **24**, grid row **26**, splitter **4** (`row-resize`), detail
pane default **320** clamp **120–620**, detail tab row **34**, publisher panel default
**380** clamp **160–640**, status bar **28**. Root canvas **1600 × 1000**, degrading to
1280×800.

Grid columns: Time **100** · Subject **180** · Correlation ID **260** · Size **70**
right-aligned · Part **48** right-aligned, *Kafka only* · pinned fields **150** each ·
Preview **\*** (star). Correlation ID **mid**-ellipsises at 33 chars — end-ellipsis is
wrong here and the spec says so explicitly.

Publisher: `60% / 40%` split; tree columns `* / 92 / 150 / 210 / 78` (Key / Type / Value /
Generator / actions); indent guide 16px per level as a 1px `Line2` left rule. Detail
properties `260 / *`. Envelope rows `150 / *` at 25px. Deep-search results
`104 / 190 / * / 74` at 25px. Launcher `340 / *`.

Radii: **5** is the default control radius (buttons, inputs, selects, segmented groups);
**8** dialogs/cards/overlays; **7** dev panel; **6** toast, notice boxes, empty-state icon
squares; **4** small buttons and chips; **3** icon-button hit targets and inline inputs;
**2** search-hit highlight. Borders are 1px except **2px** for the active-tab underline,
the grid row left edge, the publisher generator edge, and the focus ring.

### 4.4 Row states — the critical table

Base row: 26px, mono 12.5, `border-left: 2px solid transparent` — the gutter is *always*
reserved so dead-letter marking causes no reflow.

| State | Styling |
|---|---|
| default, even | transparent |
| default, odd | `Line2` — a 7% zebra, deliberately near-invisible |
| hover | `Hover` |
| selected | `Sel` fill **+** 1px inset `AccentDim` ring; overrides zebra |
| large (>64 KB) | Size cell in `Amber`; preview replaced by italic `Dim` *payload not previewed* |
| evicted | whole row at **0.55 opacity**; preview replaced by italic `Dim` *payload evicted* |
| dead-lettered | left edge `2px solid Red` |
| search hit | matched substring gets `AmberTint` background, `Amber` foreground, 2px radius |

Cell colours: Time `Muted` · Subject `Text` · Correlation ID `Muted` · Size `Text`/`Amber`
· Part `Muted` · Preview `Dim` with ellipsis.

Implemented as `DataGridRow` style selectors on VM properties via `Classes.large`,
`Classes.evicted`, `Classes.deadLettered`, per the spec's mapping table.

### 4.5 Components

- **Status dot** — 6px circle (8px for the toolbar destructive-read dot). Green connected
  · Amber connecting/saturated · Red error · Dim idle.
- **Segmented control** — 30px tall, 1px `Line`, radius 5, `Surf2` ground, segments
  separated by a `Line2` left border. Selected: `Accent` fg, `AccentTint` bg, 1px inset
  `Accent` ring. Destructive-selected (Consume): the same in `Red`. Disabled (Peek on
  SQS): 0.4 opacity + tooltip — Avalonia shows tooltips on disabled controls, unlike WPF,
  so no wrapper hack is needed.
- **Token chip** — 15px tall, `0 6px`, radius 8, `AccentTint` bg, `Accent` fg, mono 10.5.
  Host cell 20px, radius 3, `Surf2`, 1px `Line2`.
- **Inline meter** — 92 × 5px track in `Line2`, radius 3, fill `Muted`, switching to
  `Amber` above 90% of cap.
- **Buttons** — accent-outline primary (30px, radius 5, transparent bg, `Accent`
  border+text, `AccentTint` hover); destructive-outline identical in `Red`;
  neutral-outline with `Line` border; small variants 22/23/24/25px radius 4; icon buttons
  14–20px radius 3. Focus ring 2px `Accent`, 1px offset.
- **Toast** — bottom-right at `right 16 / bottom 42`, `Raise` bg, radius 6, 120ms
  fade-and-rise, **2000ms** auto-dismiss. A `Panel` overlay in the root grid, not a
  `Window`.
- **JSON colouring** — key `Accent` · string `Green` · number `Amber` · literal `Muted` ·
  punctuation `Muted` · gutter `Dim`. Body line height 19px.

### 4.6 Animation

Spinner `0.7s linear infinite` (10/11/12px rings, 1.5px `Line` with `Accent` top).
Fade-and-rise `120ms ease-out` on toast, overlays and streaming result rows; `130ms` on
dialogs. Nothing else animates — the UI spec caps transitions at 150ms and rules out
everything decorative.

### 4.7 States to reach

The mockup exposes 13 states via a dev switcher. The real app reaches these naturally,
and `EventScope.App.Tests` drives each as a view-model state assertion: cold start,
connecting, streaming, paused, saturated, error, empty search, deep search running, near
cap, mixed grid, publisher, split view, SQS destructive.

---

## 5. Build order

One continuous pass. Each stage ends compiling and green; each milestone ends with a
runnable app and a commit. Acceptance criteria are checked at the end of each milestone,
not deferred to the end.

### Stage 0 — scaffold
`git init`, `.gitignore`, commit the specs as the baseline. `Directory.Build.props`
(`net10.0`, `Nullable=enable`, `TreatWarningsAsErrors`, `LangVersion=latest`,
`InvariantGlobalization`), `Directory.Packages.props` with every version from §2, the
eight projects, and the Core-isolation assembly test — which must fail first, then pass,
so it is proven to actually assert something.

### Stage 1 — the DataGrid spike (§3.1)
Half a day, before any UI is built. It determines whether M1's grid is `DataGrid` or
`TreeDataGrid` in flat mode, and everything else in the plan is independent of the
answer. **This is the single highest-ROI step in the project.**

### M1 — Kafka consumer, end to end
1. `SourceCapabilities`, `IEventSource`, `RawMessage`, `MessageHeader`, `OutgoingMessage`.
2. `FakeEventSource` **before** the Kafka one — every performance criterion is measured
   against it, so it is infrastructure, not a test double.
3. `ByteBudget` + ingest channel (§3.2), segment writer over `RandomAccess`, SQLite schema
   + `SqliteBatchWriter`, header ring with per-file string interning.
4. `KafkaEventSource` — throwaway consumer group, `enable.auto.commit=false`,
   `auto.offset.reset=latest`.
5. `MessageRowsView` with follow/pinned windowing and row-VM recycling, then
   `IngestCoalescer` (§3.3).
6. Avalonia shell: theme tokens, tab strip, toolbar, grid, detail pane, status bar, all at
   the §4 numbers, with `RowHeight="26"` and `CanUserSortColumns="False"` set.
7. Segment reader — async payload read off the UI thread, 50 ms delay before any spinner
   so fast reads don't flicker.

### M2 — storage discipline and search
Day-file rolling with both writers alive across midnight; retention service on idle
(posting `flags` updates as `WriteOp`s, never touching indexed columns); `body_fts`
(unicode61) and `ident_fts` (trigram) as external-content tables fed by the interleaved
indexer (§3.4); tiered search — in-memory `SearchValues` ring filter per keystroke, FTS
newest-first with early exit, deep scan streaming segments with `IProgress<T>` and
cancellation; pinned fields via `GENERATED ALWAYS AS (json_extract(...)) VIRTUAL` + index
+ dynamic column; settings view for cap, retention, indexed prefix.

Index lag goes in the status bar and every result set states whether the index is current.
`-wal` bytes count toward the cap.

### M3 — publisher
Token lexer, `GenerationPlanner` (iterative Kahn + Tarjan SCC) and `GenerationRunner` with
plan caching (§3.5); `JsonNode`-backed tree with an observable flattened projection;
`TreeDataGrid` editor with type dropdown and chip-rendering token input; preview with
generated-field amber edge markers plus an envelope tab; schema inference from a consumed
message (GUID regex → `{{guid}}`, ISO-8601 → `{{now:iso}}`, numeric → `{{int:min..max}}`
bracketing the observed value); publish and burst with per-copy regeneration.

### M4 — Service Bus and SQS
`ServiceBusEventSource` (peek by default, receive mode explicitly armed, sessions and
dead-letter subqueue, capability flags set); `SqsEventSource`
(`CanPeekNonDestructively = false`, persistent warning banner bound to that flag).

Then the capability audit: **no `if (broker == …)` anywhere in the view layer.** Every
broker-specific control binds `IsEnabled`/`IsVisible` to a capability flag. A test
enumerates `SourceCapabilities`' properties and asserts each has a bound UI element, so
"every capability flag has a UI element that observably responds to it" is machine-checked
rather than eyeballed.

### Stage 5 — polish
Remaining mockup surfaces: connection manager and the three per-broker forms with `Test
connection`'s three states, deep-search overlay, large-payload confirmation, toast, empty
states, light theme, and the full keyboard map (`⌘F` search, `Ctrl+2` publisher, `⌘R`
start/stop, `⌘C` copy body, `⇧⌘C` copy as cURL, `⌘T` use as template, `⌘⏎` publish, `⌘N`
new connection, `Esc` close).

---

## 6. Verification

### Automated
- `dotnet test` — Core, Storage, App view-model tests, all xunit v3.
- Storage tests run against **real SQLite files in a temp directory, never in-memory**:
  WAL behaviour, file size and day rollover are the things under test and in-memory hides
  all three. Every storage test ends with
  `INSERT INTO body_fts(body_fts) VALUES('integrity-check')` — the canonical
  external-content contract validator.
- A test-collection fixture hooks `TaskScheduler.UnobservedTaskException` and **fails the
  run** if anything fires. This project is all background loops, which otherwise swallow
  failures silently.
- `dotnet run -c Release --project tests/EventScope.Bench` — batch insert rate, FTS
  latency at 100k/500k/1M rows, segment read latency, deep-scan throughput, coalescer
  overhead. Baselines committed to `tests/EventScope.Bench/baselines/`, >20% regression
  fails.
- Broker integration tests are opt-in via `EVENTSCOPE_KAFKA_BOOTSTRAP`,
  `EVENTSCOPE_ASB_CONNECTION`, `EVENTSCOPE_SQS_QUEUE_URL` and skip by default.

### The chaos soak — the test that would actually catch a threading bug
Fake source at 10k msg/s for 60 s, storage cap small enough to force eviction, a search
every 200 ms, day rollover forced by `FakeTimeProvider`. Asserts: zero `SqliteException`
with `SQLITE_BUSY`; row counts across day files equal emitted − evicted;
`integrity-check` passes on every file; `-wal` never exceeds `journal_size_limit`.

### Acceptance criteria, and how each is measured

| Criterion | Measurement |
|---|---|
| 10,000 msg/s for 60s, no frame > 100 ms | `FakeEventSource` at 10k/s; frame times from a render-tick histogram written to CSV |
| Heap growth < 50 MB across that run | `dotnet-counters collect` on `System.Runtime`; assert `gc-heap-size` delta |
| 50,000-row scroll < 16 ms/frame | scripted scroll driver over `MessageRowsView`, same histogram |
| Row selection renders body < 100 ms | stopwatch around the async segment read |
| Zero messages lost from disk under saturation | ingest N, count rows in SQLite, assert equality; UI drop count asserted separately and independently |
| FTS first page < 200 ms over 500k | bench, seeded corpus |
| Trigram infix on correlation ID < 300 ms | bench; a 2-char query test asserts the `LIKE` fallback is taken |
| Deep scan ≥ 500 MB/s decompressed | bench |
| Footprint never exceeds cap by > one segment | retention test writing past the cap, polling on-disk bytes including `-wal` |
| Day rollover loses no messages | `FakeTimeProvider` — no `DateTime.Now` anywhere in Core or Storage |
| Retention delete causes no ingest stall | delete a 20-day file mid-ingest, assert no gap in the inter-arrival histogram |
| Publish round trip | consume → "Use as publish template" → publish → consume back, assert same shape |
| Burst of 1,000 `{{guid}}` → 1,000 distinct | `HashSet.Count == 1000` |
| `{{ref}}` cycles reported, not stack-overflowed | a **100,000-node chain** completes; an injected back-edge is reported as an SCC |
| Invalid tokens surface inline pre-publish | view-model validation test |
| Adding a broker needs zero `EventScope.App` changes | capability-binding audit test + a `no if (broker ==` source assertion |
| SQS warning not permanently dismissible in-session | view-model test: dismiss collapses to the persistent toolbar dot, never to nothing |

### Manual
`dotnet run --project src/EventScope.App` against `FakeEventSource`, walking the 13 mockup
states (§4.7) side by side with `EventScope.dc.html` open in a browser, in both themes, at
1600×1000 and 1280×800.

---

## 7. Assumptions and deviations

- **Mono font.** The mockup specifies JetBrains Mono. It is not installed on this machine,
  and neither GitHub nor NuGet serves it reachably from this shell, so the family resolves
  as `"JetBrains Mono, Cascadia Mono, Consolas"` — Cascadia Mono ships with Windows and is
  metrically close. Dropping the JetBrains Mono TTFs into
  `src/EventScope.App/Assets/Fonts/` later restores exact fidelity with no code change.
  Inter comes from `Avalonia.Fonts.Inter`, so sans is exact.
- **`eventscope-design-plan.md` is absent**, as noted in Context. Architecture is derived
  from the remaining documents plus the verified `DataGrid` and SQLite behaviour in §3.
- **No live brokers.** Broker sources are written and unit-tested against mocked client
  surfaces; they are not proven against a wire in this pass.
- **`TimeProvider` everywhere.** No `DateTime.Now` in Core or Storage — the midnight
  rollover criterion is untestable otherwise.
- **The grid control is provisional until Stage 1.** If the spike shows `DataGrid` won't
  virtualize acceptably over `MessageRowsView`, the grid becomes `TreeDataGrid` in flat
  mode. No other part of the plan changes.
