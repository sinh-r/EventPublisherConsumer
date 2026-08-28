# EventScope — implementation plan

**Brief for Claude Code.** Build order, structure, and acceptance criteria.

Read `eventscope-design-plan.md` for architectural rationale before starting.
Read `eventscope-ui-spec.md` for layout and component behaviour.

> **On the mockup.** A Claude Design HTML/CSS/JS mockup exists. It is a visual
> reference only. **Do not port its markup, CSS, or JavaScript.** Read it for
> layout, density, states, and interaction, then implement natively in
> Avalonia/XAML. Section 6 below maps web concepts to Avalonia equivalents.

---

## 1. Solution structure

```
EventScope.sln
├── src/
│   ├── EventScope.Core/               no broker or UI dependencies
│   │   ├── Abstractions/              IEventSource, IEventSink, SourceCapabilities
│   │   ├── Models/                    RawMessage, MessageHeader, OutgoingMessage
│   │   └── Generation/                token parser, generator engine
│   ├── EventScope.Storage/
│   │   ├── Segments/                  append-only log, compression, reader
│   │   ├── Sqlite/                    schema, migrations, batch writer
│   │   ├── Search/                    tiered search, FTS queries
│   │   └── Retention/                 day-file rolling, eviction, cap enforcement
│   ├── EventScope.Brokers.Kafka/
│   ├── EventScope.Brokers.ServiceBus/
│   ├── EventScope.Brokers.Sqs/
│   └── EventScope.App/                Avalonia
│       ├── Views/
│       ├── ViewModels/
│       ├── Controls/
│       └── Collections/               virtualizing collection adapters
└── tests/
    ├── EventScope.Storage.Tests/
    ├── EventScope.Core.Tests/
    └── EventScope.Bench/              BenchmarkDotNet
```

`EventScope.Core` must not reference `Confluent.Kafka`, `Azure.Messaging.*`,
`AWSSDK.*`, or `Avalonia.*`. Enforce this with a test that asserts the assembly's
referenced assemblies.

## 2. Dependencies

| Package | Purpose |
|---|---|
| `Avalonia` 11.x + `Avalonia.Controls.DataGrid` | UI |
| `CommunityToolkit.Mvvm` | source-generated VMs, `[ObservableProperty]` |
| `Microsoft.Data.Sqlite` | SQLite (bundles `e_sqlite3` with FTS5 enabled) |
| `K4os.Compression.LZ4.Streams` | segment compression |
| `Confluent.Kafka` | M1 |
| `Azure.Messaging.ServiceBus` | M4 |
| `AWSSDK.SQS` | M4 |
| `BenchmarkDotNet` | tests only |

`System.Text.Json` (`JsonNode`) for all JSON. No Newtonsoft.

If you want to drop the LZ4 dependency, `System.IO.Compression.ZLibStream` at
`CompressionLevel.Fastest` is built in and adequate, roughly 3–4× slower to
decompress. LZ4 is preferred because deep search depends on fast decompression.

Verify at startup that `SELECT sqlite_version()` returns ≥ 3.34 (trigram
tokenizer requirement) and fail loudly if not.

---

## 3. Milestones

Each milestone ends with a runnable app. Do not start the next until acceptance
criteria pass.

### M1 — Kafka consumer, end to end

Kafka first deliberately. It is the strictest broker (partitions, offsets,
replay) and will force the abstraction to stay honest. Building ASB first bakes
peek-lock assumptions into the core.

**Tasks**

1. `SourceCapabilities` and `IEventSource` in Core.
2. `KafkaEventSource` — throwaway consumer group, `enable.auto.commit=false`,
   `auto.offset.reset=latest`.
3. Byte-bounded ingest channel. `BoundedChannelOptions` caps item count; enforce
   the 256 MB byte budget with an `Interlocked` counter and an async gate,
   because `Channel<T>` has no native byte bound.
4. Segment writer: 64 MB rolling files, LZ4 frame per 1 MB block, returns
   `(segmentId, offset, length)`.
5. SQLite schema + batch writer. One transaction per 500 messages or 200 ms.
   `PRAGMA journal_mode=WAL; synchronous=NORMAL; temp_store=MEMORY;`
6. `MessageHeader[]` ring buffer with string interning table for subject and
   correlation ID.
7. UI coalescer: `DispatcherTimer` at 60 ms flushing accumulated headers.
8. Avalonia shell: tab strip, toolbar, DataGrid, detail pane, status bar.
9. Segment reader: seek and decompress a single payload on row selection.

**Acceptance**

- Sustains 10,000 msg/s for 60 seconds with UI responsive (no frame over 100 ms)
- Managed heap growth under 50 MB across that run, verified with `dotnet-counters`
- Scrolling 50,000 rows stays under 16 ms per frame
- Selecting any row renders its body in under 100 ms
- Zero messages lost from disk under saturation; UI drop count is accurate

### M2 — Storage discipline and search

**Tasks**

1. Day-file rolling. `SessionStore` opens `{date}.db` and creates it on first
   write past midnight. Keep both files open briefly across the boundary.
2. Retention service. Background task on idle: delete day files older than N
   days; while total bytes > cap, evict oldest segment and set
   `payload_evicted = 1` on affected rows; when a day has no segments, delete
   its `.db`.
3. `body_fts` (unicode61, capped head) and `ident_fts` (trigram) as
   external-content tables, populated on a background indexer, not in the ingest
   transaction.
4. Tiered search: in-memory ring filter per keystroke; FTS query against day
   files newest-first with early exit; deep scan streaming segments with
   `IProgress<T>` and cancellation.
5. Pinned fields: `ALTER TABLE ... GENERATED ALWAYS AS (json_extract(...))
   VIRTUAL` plus index, dynamic DataGrid column, null-resolution warning.
6. Settings view for cap, retention, and indexed-prefix.

**Acceptance**

- FTS query over 500k rows returns first page in under 200 ms
- Trigram infix search on correlation ID returns in under 300 ms
- Deep scan sustains ≥ 500 MB/s of decompressed throughput
- Total on-disk footprint never exceeds the configured cap by more than one
  segment size
- Day rollover at midnight loses no messages — test by faking the clock
- Retention deletion of a 20-day-old file causes no measurable ingest stall

### M3 — Publisher

**Tasks**

1. Generator token parser and evaluator. `{{ref:$.path}}` resolves against
   values already generated in the same message, so evaluation is two-pass:
   topological order by reference, then fill.
2. `JsonNode`-backed tree model with an observable flattened projection for the
   grid.
3. Tree editor view: hierarchical rows, type dropdown, generator token input
   with chip rendering.
4. Preview: resolved payload with generated-field markers, plus envelope tab.
5. Schema inference from a consumed message — walk `JsonNode`, infer generator
   per leaf by shape (GUID regex → `{{guid}}`, ISO-8601 → `{{now:iso}}`,
   numeric → `{{int:min..max}}` bracketing the observed value).
6. Publish and burst-publish with per-copy regeneration.

**Acceptance**

- Round trip: consume a message, "Use as publish template", publish, and the
  republished message consumes back with the same shape
- Burst of 1,000 with `{{guid}}` produces 1,000 distinct IDs
- `{{ref}}` cycles are detected and reported, not stack-overflowed
- Invalid generator tokens surface inline before publish, not at publish time

### M4 — Service Bus and SQS

**Tasks**

1. `ServiceBusEventSource` — `PeekAsync` default, receive mode explicitly armed,
   sessions and dead-letter subqueue support, capability flags set accordingly.
2. `SqsEventSource` — `CanPeekNonDestructively = false`, persistent warning
   banner wired to that flag.
3. Capability-driven UI: every broker-specific control binds its `IsEnabled` and
   `IsVisible` to a capability flag. No `if (broker == "kafka")` anywhere in the
   view layer.

**Acceptance**

- Adding a broker requires zero changes to `EventScope.App`
- Every capability flag has a UI element that observably responds to it
- SQS warning cannot be permanently dismissed within a session

---

## 4. Schema

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous  = NORMAL;
PRAGMA temp_store   = MEMORY;

CREATE TABLE messages (
    id              INTEGER PRIMARY KEY,
    enqueued_ticks  INTEGER NOT NULL,
    received_ticks  INTEGER NOT NULL,
    segment_id      INTEGER NOT NULL,
    offset          INTEGER NOT NULL,
    length          INTEGER NOT NULL,
    message_id      TEXT,
    correlation_id  TEXT,
    subject_id      INTEGER REFERENCES subjects(id),
    partition       INTEGER,
    flags           INTEGER NOT NULL DEFAULT 0,
    preview         TEXT,          -- first 128 bytes
    body_head       TEXT           -- first N KB, N configurable
);

CREATE TABLE subjects (id INTEGER PRIMARY KEY, name TEXT UNIQUE);

CREATE INDEX ix_msg_time ON messages(enqueued_ticks);
CREATE INDEX ix_msg_corr ON messages(correlation_id);

CREATE VIRTUAL TABLE body_fts USING fts5(
    body_head,
    content = 'messages', content_rowid = 'id',
    tokenize = 'unicode61'
);

CREATE VIRTUAL TABLE ident_fts USING fts5(
    message_id, correlation_id,
    content = 'messages', content_rowid = 'id',
    tokenize = 'trigram'
);
```

No `prefix=` on `body_fts`. Prefix indexes roughly double its size, and
`ident_fts` already covers the infix cases that matter.

`flags` bits: `1 = IsLarge`, `2 = IsDeadLettered`, `4 = PayloadEvicted`.

Because day files are dropped whole, no `DELETE` triggers and no FTS `'delete'`
command rows are needed. Do not add them.

---

## 5. Critical implementation notes

**Do not bind the DataGrid to `ObservableCollection<MessageViewModel>`.** 50k
view model objects defeats the whole memory design. Implement a custom
`IReadOnlyList<T>` + `INotifyCollectionChanged` adapter over `MessageHeader[]`
that materializes a lightweight row VM only for realized (visible) rows and
recycles them. This is the single most important correctness detail in the UI
layer.

**The coalescer must batch the notification, not the data.** Append headers to
the ring buffer from the writer thread, then raise a single
`NotifyCollectionChangedAction.Reset` or a batched `Add` range on the UI thread
per tick. One notification per tick, never per message.

**Never read a payload on the UI thread.** Segment reads go through an async
path with a 50 ms delay before showing a spinner, so fast reads don't flicker.

**Index lag is a first-class metric.** The background indexer will fall behind
during bursts. Surface the lag in the status bar and make search results state
whether the index is current.

**Interning table is per-day-file.** Subject IDs are not stable across files, so
cross-day search must join through `subjects` in each file, not assume shared
IDs.

---

## 6. Mockup to Avalonia mapping

| Mockup (HTML/CSS/JS) | Avalonia |
|---|---|
| Tab strip | `TabControl` with custom `ItemContainerTheme` |
| Message grid | `DataGrid`, `AutoGenerateColumns=False`, `IsReadOnly=True` |
| Sticky header | built into `DataGrid` |
| Row state classes | `DataGridRow` style selectors on VM properties via `Classes.large`, `Classes.evicted` |
| Drag-resizable panes | `Grid` + `GridSplitter` |
| Segmented control | `ItemsControl` of `RadioButton` with a custom theme |
| Token chip input | custom `TemplatedControl` over `TextBox` with an overlay `ItemsControl` |
| JSON tree editor | `TreeDataGrid` (`Avalonia.Controls.TreeDataGrid` package) — closest fit, use hierarchical mode |
| Syntax-highlighted JSON | `AvaloniaEdit` with a JSON syntax definition, or a custom `TextBlock` with `Inlines` for small payloads |
| Collapsible detail pane | `Grid` row with animated `Height` + `GridSplitter` |
| Toast | `Popup` or a `Panel` overlay in the root `Grid`, not a window |
| Inline meter | `ProgressBar` with a custom theme, or a two-`Border` composition |
| Modal overlay (deep search) | `Panel` in the root grid with `IsVisible` binding, not a `Window` |
| CSS custom properties | `ResourceDictionary` with `ThemeVariant` scopes for light/dark |
| Tooltips on disabled controls | `ToolTip.Tip` — note Avalonia shows tooltips on disabled controls, unlike WPF |

Copy the mockup's *numbers* — row heights, column widths, font sizes, padding —
directly. Those are the part worth preserving verbatim.

---

## 7. Test strategy

- **Storage tests** run against real SQLite files in a temp directory, never
  in-memory, because WAL, file size, and rollover behaviour are the things under
  test.
- **A fake `IEventSource`** that emits at a configurable rate with configurable
  payload sizes drives all throughput tests without a broker.
- **Benchmarks** in `EventScope.Bench` cover: batch insert rate, FTS query
  latency at 100k/500k/1M rows, segment read latency, deep scan throughput,
  and coalescer overhead. Record baselines and fail CI on >20% regression.
- **Broker integration tests** are opt-in via environment variables and skipped
  by default. Do not require a live broker to run the suite.
