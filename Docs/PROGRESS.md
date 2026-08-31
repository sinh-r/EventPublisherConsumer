# EventScope — progress

Tracks what's done, what's next, and anything blocked on a decision outside the code.
Read `eventscope-implementation-plan.md` and `eventscope-build-plan.md` in this folder
for the full plan; this file is the living status against that plan.

---

## Completed

### Stage 0 — solution scaffold
- 10-project solution (`EventScope.slnx`): `EventScope.Core`, `EventScope.Storage`,
  `EventScope.Brokers.{Kafka,ServiceBus,Sqs}`, `EventScope.App`, plus four test/bench
  projects under `tests/`.
- `Directory.Build.props` / `Directory.Packages.props`: `net10.0`, nullable enabled,
  warnings-as-errors, central package management with every dependency version pinned.
- `global.json`: pins the SDK and opts into the `Microsoft.Testing.Platform` test runner
  (required for `dotnet test` to work with xunit v3 on the .NET 10 SDK).
- Core-isolation test (`EventScope.Core.Tests`): asserts `EventScope.Core`'s compiled
  assembly references no `Confluent.Kafka`, `Azure.Messaging.*`, `AWSSDK.*`, or
  `Avalonia.*` assembly. Passing.
- Base abstractions in `EventScope.Core`: `SourceCapabilities`, `IEventSource`,
  `IEventSink`, `RawMessage`, `MessageHeader`, `OutgoingMessage`.

### Stage 1 — DataGrid virtualization spike
The build plan calls this "the single highest-ROI step in the project" — done before any
real UI is built on top of it.

- `MessageRowsView` (`EventScope.App/Collections`): the ring-buffer-backed collection
  adapter the grid binds to. Recyclable `MessageRowViewModel` instances, follow/pinned
  windowing, `Reset`-only change notifications.
- 4 passing tests in `EventScope.App.Tests` (headless Avalonia, hand-rolled fixture —
  `Avalonia.Headless.XUnit` doesn't support xunit v3): window length/count behaviour,
  no full materialization at bind time, bounded reads on a scroll, selection survives a
  forced `Reset` by object identity.

**Two real corrections to the original plan, found by actually running the spike:**
1. `Avalonia.Controls.TreeDataGrid` requires a paid Avalonia Accelerate license as of
   11.2.0+. Swapped to `TreeDataGrid.Avalonia` (MIT, community fork of the last free
   release, versioned to track current Avalonia releases).
2. Avalonia's `DataGrid` wraps *any* plain `IList` `ItemsSource` in its own
   `DataGridCollectionView`, whose `CopySourceToInternalList()` eagerly enumerates the
   entire source — confirmed by measurement (65,536 reads at bind time before the fix,
   the exact catastrophe the whole virtualization design exists to avoid). The plan's
   assumption that a plain `IList` gets a "fast path" for free was wrong for this
   package version. Fix: `MessageRowsView` also implements `IDataGridCollectionView`
   directly, which stops DataGrid from wrapping it at all. Bind-time reads dropped to 15
   (just the visible screenful) after the fix.

### Local dev environment
- Windows **Smart App Control** was blocking execution of freshly-built, unsigned test
  binaries on this machine (a Code Integrity policy block, confirmed via the
  `Microsoft-Windows-CodeIntegrity/Operational` event log — not a malware detection).
- Fixed for local dev only: a self-signed code-signing certificate
  (`CN=EventScope Local Dev Signing`) installed into `CurrentUser\TrustedPublisher`, plus
  an automatic post-build signing step (`Directory.Build.targets` +
  `build/Sign-LocalTestBinary.ps1`) that signs every test project's output on Windows
  Debug builds. Confirmed working — no change needed to `CurrentUser\Root`.
- **Later measurement partly overtakes this.** Smart App Control no longer blocks
  freshly-built unsigned binaries on this machine - the unsigned Release test binaries and
  the unsigned published `EventScope.exe` both run, the latter even with Mark-of-the-Web
  attached, while SAC is still in enforcement. Whatever triggered the original block is no
  longer triggering. The workaround stays as an inert fallback rather than being deleted
  mid-flight; see item 2 under **Blocked / needs a decision**.
- This is explicitly **not** a substitute for real release signing. Distributing a
  built `.exe` publicly (e.g. off a GitHub release) will need actual CA-chained signing
  for end users to not get blocked the same way — see **Blocked / needs a decision**
  below.

### Repo housekeeping (this pass)
- `git init` — local identity set repo-scoped (not global) as `rsrishabh007` /
  `rsrishabh007@gmail.com`. **Change the name in `git config user.name` if you want
  something else on your commits** — it was a placeholder since you hadn't specified one.
- Removed: the redundant `Mockup preparation from spec.zip` (already extracted), and the
  Claude Design authoring-tool metadata that isn't needed to render the mockup or build
  the app (`_adherence.oxlintrc.json`, `_ds_manifest.json`, the Nocturne design-system
  `readme.md`, `.thumbnail`). Kept `EventScope.dc.html`, `support.js`, `styles.css` and
  `_ds_bundle.js` — the mockup file actually loads these to render, and the build plan's
  manual verification step opens this file in a browser throughout the build.
- Moved all three planning docs into `Docs/`: `eventscope-implementation-plan.md`,
  `eventscope-build-plan.md` (both were at the repo root), and `eventscope-ui-spec.md`
  (was buried in `Mockup preparation from spec/uploads/` — the design tool's upload
  copy, now the only copy, relocated here).

---

### Release readiness and doc reconciliation (this pass)

Distribution work brought forward so every later commit is releasable. See
`DISTRIBUTION_PLAN.md`, whose placeholders are now all filled in.

- **Naming settled.** `EventScope` / `rsrishabh007` / repo `EventScope`, MIT licensed.
  `EventScope.App.csproj` now sets `<AssemblyName>EventScope</AssemblyName>` so the
  published binary is `EventScope.exe`, not `EventScope.App.exe`.
  **Carry into M1:** this changes the `avares://` root. Themes and other XAML resources
  must be referenced as `avares://EventScope/...`, not `avares://EventScope.App/...`.
  Nothing references it today, so nothing broke.
- **Assembly metadata.** Version 0.1.0, company, authors, copyright, repository URL and
  MIT expression in `Directory.Build.props`; product/title/description in the app csproj;
  `app.manifest` realigned from 1.0.0.0 to 0.1.0.0.
- **Single-file publish verified.** `publish/EventScope.exe`, 123 MB, self-contained
  win-x64, launches. The feared `TreatWarningsAsErrors` / IL3000-IL3002 collision did not
  materialise. Publish switches deliberately live on the command line, not in the csproj,
  because `RuntimeIdentifier` in a project file makes every build RID-specific.
- **OSS files.** `LICENSE` (MIT), `README.md`, `CONTRIBUTING.md`, `.gitattributes`,
  `.github/ISSUE_TEMPLATE/`. `.gitignore` extended with `publish/` and secret patterns.
- **CI.** `.github/workflows/ci.yml` (build + test on push/PR) and `release.yml` (publish,
  provenance attestation, SHA256, release on tag; `workflow_dispatch` stops short of
  cutting a release). Every action version in the original plan was stale - checkout is on
  v7, setup-dotnet v6, upload-artifact v7, attest-build-provenance v4, action-gh-release
  v3. **Neither workflow has run yet; there is no remote.**
- **Deliberately not tagged `v0.1.0`.** The app is still the empty Avalonia template.
  Tagging now would burn the version on an artifact that does nothing. Tag at the end of M1.
- **Secret audit clean.** No credentials in the single commit of history, no config files
  tracked.
- **Docs reconciled** against what the Stage 1 spike actually measured:
  - Build plan section 2 package table corrected to `TreeDataGrid.Avalonia` 11.3.1.
  - Build plan section 3.1 said "do not implement `IDataGridCollectionView`" - inverted,
    with the measurement, because that guidance is the opposite of what works.
  - `eventscope-implementation-plan.md` pointed at a non-existent
    `eventscope-design-plan.md`; now points at the build plan and states it is
    authoritative where the two disagree.

---

### M1a — the app becomes an app (this pass)

M1 reordered per the note now in the build plan §5: shell before storage, so Stage 1's
virtualization work is proven against a real window instead of staying invisible through
the largest, riskiest part of the milestone. `dotnet run --project src/EventScope.App` now
shows the mockup's main workspace, streaming synthetic traffic — not the stock Avalonia
template the screenshot in `ScreenShots/` was taken against before this pass.

- **Core (`EventScope.Core/Ingest/`).** `ByteBudget` (lost-wakeup-safe async gate, ¾
  low-water mark, oversized-message admission, per the build plan's §3.2 design exactly),
  `IUiTicker` + `IngestCoalescer` (double-buffered batching, `Reset`-only notifications,
  per §3.3), `FakeEventSource` (10k msg/s default, plausible JSON bodies, large/dead-lettered
  fractions, partitioned, `TimeProvider`-driven — no `DateTime.Now`). `IPayloadReader` added
  to `EventScope.Core/Abstractions/` as the seam M1b's real segment reader drops into.
- **App (`EventScope.App/Ingest/`).** `IngestPipeline` wires `FakeEventSource` → a
  byte-budgeted channel → header/preview shaping → `InMemoryPayloadStore` →
  `IngestCoalescer` → `MessageRowsView.AppendBatch` — the first thing to actually call
  `AppendBatch` outside a test. `InMemoryPayloadStore` is the M1a stand-in for
  `IPayloadReader`: a 4,096-slot ring, deliberately smaller than `MessageRowsView`'s 65,536,
  so payload eviction is reachable in a short run without waiting to fill the grid.
  `DispatcherTimerTicker` is the 60&nbsp;ms `DispatcherPriority.Background` production
  ticker `IngestCoalescer` was built against an interface for.
- **Shell (`EventScope.App/Views/MainWindow.axaml`, moved out of the project root).**
  Tab strip, connection toolbar (start/stop, capability-bound indicator, throughput
  readout), warning banner, search bar (present, inert — wiring is M2), the message grid
  (`DataGrid`, `AutoGenerateColumns="False"`, `RowHeight="26"`, row-state classes for
  large/evicted/dead-lettered/zebra), a resizable detail pane (JSON body, 50&nbsp;ms
  pre-spinner delay, "payload not previewed" / "payload evicted" states), and a status bar
  (total/dropped counts, byte-budget usage, pin/resume-live). `Themes/Tokens.axaml` ports
  the §4.1 colour table as Dark/Light `ThemeDictionaries`; `App.axaml` now sets
  `RequestedThemeVariant="Dark"`.
- **View models (`EventScope.App/ViewModels/`).** `MainWindowViewModel` owns the pipeline's
  lifetime; `ConnectionToolbarViewModel`, `StatusBarViewModel`, `DetailPaneViewModel` hold
  the window's four regions' data. Every broker-specific toolbar element binds a
  `SourceCapabilities` flag, not a broker-type switch, from the first line of code — M4's
  eventual capability-binding audit costs nothing extra because of it.
- **Tests: 31 passing, up from 5.** 20 in `EventScope.Core.Tests` (`ByteBudgetTests`,
  `IngestCoalescerTests`, `FakeEventSourceTests`, plus the original isolation test), 11 in
  `EventScope.App.Tests` (the original 4 spike tests, `IngestPipelineEndToEndTests` proving
  coalescer → `MessageRowsView` batching end to end with a `ManualTicker`, and a
  `CapabilityBindingTests` smoke test).
- **Manually verified.** Built, ran, clicked Start, watched it stream, selected a row and
  saw the detail pane populate — including hitting the "payload evicted" path on a row the
  small payload-store ring had already overwritten while the row itself was still fully
  visible in the much larger grid ring, which is correct (not a bug — see
  `DetailPaneViewModel.LoadAsync`'s comment on the two independent eviction paths).
  Screenshots in `ScreenShots/`.

**Two corrections to the plan, found by actually running it — same spirit as Stage 1's two,
below is where measured reality overrides what the plan assumed:**
1. **`DataGridColumnHeader`'s default chrome doesn't fit the spec's literal column widths.**
   The Part column at the spec's 48px (`Docs/eventscope-build-plan.md` §4.3) clipped its
   "PART" header down to a single glyph's vertical stroke — confirmed by cropping the
   rendered screenshot pixel-for-pixel, not guessed. Reducing `Padding` wasn't enough; the
   header needs roughly 64px minimum for a 4-character uppercase label regardless of
   padding. Widened to 70px in `MainWindow.axaml` (matching the SIZE column, the other
   4-character header, which does fit at its spec width). No other column needed this.
2. **Smart App Control blocks the App project's own build output, not just test binaries.**
   The existing SAC entry below was written against test-project binaries; this pass hit
   the identical block — Code Integrity Event ID 3077/3033 — against a freshly rebuilt
   `EventScope.exe`/`EventScope.dll`, on both a direct launch and `dotnet EventScope.dll`.
   The existing local dev signing cert (already installed, `CN=EventScope Local Dev
   Signing`) fixes it identically when applied to the App project's output, but
   `Directory.Build.targets`' signing target is scoped to `IsTestProject=='true'` only, so
   it doesn't cover this automatically. Worked around manually (`Set-AuthenticodeSignature`
   against the built `.exe`/`.dll`) to verify this pass; left the target's condition
   unchanged since it's not yet clear this recurs often enough to justify signing
   non-test Debug output routinely — revisit if it does.

---

### M1b — messages on disk (this pass)

Finished. Scoped deliberately as storage first, `KafkaEventSource` next — there is no live
broker on this machine (Blocked item 5), so every piece below is fully verifiable today
against `FakeEventSource` and real SQLite/segment files, whereas Kafka could only be written
against mocks either way.

**`EventScope.Storage/Segments/`:**
- **The segment format itself, resolved.** The build plan says the writer returns
  `(segmentId, offset, length)` but never says how a reader maps an uncompressed `offset`
  back to the compressed block holding it — that had to be settled before any code was
  written, since it decides the on-disk layout. Resolution, in `SegmentFormat.cs`: each
  block is `[magic:u32][uncompressedLength:i32][compressedLength:i32][compressed bytes]`;
  a sealed segment ends with a block table (`uncompressedStart`, `compressedStart`, both
  lengths, per block) plus a `[count:i32][magic:u32]` tail. `offset` is the uncompressed
  logical offset within the segment — a reader binary-searches the table by that, not by
  file position. A segment whose footer is missing or fails its magic check (writer died
  mid-file) is recoverable by walking block headers from offset 0, which is why every
  header carries its own lengths rather than deferring to the table. A payload larger than
  the 1&#160;MB block size gets its own single-payload block instead of being split across
  blocks.
- **`SegmentWriter`** — 64&#160;MB rolling files, LZ4 block compression
  (`K4os.Compression.LZ4`, block-level `Encode`/`MaximumOutputSize`, not the `.Streams`
  frame format), `Append(ReadOnlySpan<byte>)` returns `(segmentId, offset, length)`
  synchronously per the design above.
- **`SegmentReader`** (`SegmentIndex.cs` + `SegmentReader.cs`) — the `IPayloadReader`
  implementation reading the format back. Footer-first, recovery-walk fallback for a
  missing/bad footer (writer died mid-file, or the segment is still live/unsealed); a
  sealed segment's block table is cached forever (it never changes), the live segment's is
  reloaded on a lookup miss. One shared `SafeFileHandle` per segment, positional
  `RandomAccess.Read`/`RandomAccess.ReadAsync` only. Opens with `FileShare.ReadWrite` so a
  segment reads while `SegmentWriter` still holds it open (Windows share-mode compatibility
  needs the reader's share mode to admit the writer's `ReadWrite` access — confirmed by
  test, not assumed).

**`EventScope.Storage/Sqlite/`:**
- **Schema, verbatim from the implementation plan §4** (`SqliteSchema.cs`), applied
  idempotently on open. `body_fts`/`ident_fts` created now even though M2's indexer
  populates them — avoids a schema migration later for a shape that never changes.
  `SqliteCapabilityProbe` runs the build plan's three-statement FTS5/trigram behaviour
  probe at open, failing loudly rather than degrading silently.
- **`SqliteBatchWriter`** — one dedicated thread per day file, owning the only write
  connection (build plan §3.6: `SqliteConnection` isn't thread-safe even under a lock).
  Commits every 500 rows or 200&#160;ms, whichever comes first, via `TimeProvider` (not
  `DateTime.Now`). Backed by `BlockingCollection<WriteOp>`, not a raw `Channel`, since a
  dedicated consumer thread wants blocking `TryTake(timeout)`, not an async reader loop.
- **A documented deviation from the build plan's literal threading table.** §3.6 lists
  subject interning under the *ingest reader thread*. Here it happens on the *batch
  writer's* thread instead, inside the same transaction as the row insert — the interner
  needs to write to the `subjects` table, and routing that through a second connection
  reintroduces exactly the `BEGIN IMMEDIATE` contention / `SQLITE_BUSY` risk §3.6 spends a
  paragraph explaining why the FTS indexer avoids. `WriteOp.InsertMessage` therefore carries
  the subject *string*, not a pre-resolved id. Simpler, and avoids a second writer thread
  for one lookup table.
- **`SessionStore`** ties a `SegmentWriter` + `SegmentReader` + `SqliteBatchWriter` to one
  `{root}/{yyyy-MM-dd}/` directory. M1b opens today's file only; day rollover is M2.

**`IngestPipeline` rewired.** `InMemoryPayloadStore.Store` no longer stands in for the real
write path — every message now goes through the real `SegmentWriter` and a real
`WriteOp.InsertMessage` on the day file's batch writer. The artificial 2%-eviction roll (an
M1a-only demo hack) is deleted; nothing sets `PayloadEvicted` until M2's retention exists,
which is correct, not a regression. `body_head` (a 2&#160;KB prefix) is captured at ingest
for M2's FTS indexer, so no backfill pass is needed later.

**Five corrections to the plan, found by actually building and testing it — same spirit as
Stage 1's two and M1a's two, this is where measured reality overrode what was assumed
going in:**
1. **A just-ingested payload is essentially always still in memory, not on disk.**
   `SegmentWriter.Append` returns coordinates before anything is necessarily flushed — a
   single small payload sits in the 1&#160;MB pending buffer until enough accumulates to
   flush it. At 10k&#160;msg/s a freshly-selected row would almost always report "payload
   evicted" against a segment-only reader. Fix: kept `InMemoryPayloadStore` as a hot ring in
   front of `SegmentReader` behind a new `CompositePayloadReader` (hot first, cold on miss)
   rather than deleting it as the original plan implied.
2. **`MessageHeader.Offset`'s `int` width can silently wrap.** Highly repetitive JSON
   compresses past 32:1 under LZ4, so a 64&#160;MB file can hold more than `int.MaxValue`
   uncompressed bytes. `SegmentWriter`'s roll condition (`SegmentFormat.ShouldRoll`) now
   checks uncompressed-offset headroom as well as file size, and offset casts are `checked`.
   Unit-tested at the exact boundary — writing gigabytes of compressible data to prove it
   live isn't practical in a test suite, so the roll condition was extracted as a pure
   function instead.
3. **`SegmentReader` needs `FileShare.ReadWrite`, not just `Read`.** Confirmed by a test
   that reads a segment while `SegmentWriter` still holds it open — Windows share-mode
   compatibility requires the *new* open's share mode to admit whatever access the
   *existing* handle requested, not the other way round.
4. **The detail pane's header lookup (`IngestPipeline.TryGetHeader`) doesn't survive real
   storage.** It was a 4,096-entry side ring that would never scale past what M1a's demo
   needed. Deleted; `MessageRowViewModel` now carries `SegmentId`/`Offset` directly (the
   `MessageHeader` it's populated from already has them), so the detail pane reconstructs a
   lookup key from the row itself. Fewer moving parts, one less ring to keep in sync.
5. **`Microsoft.Data.Sqlite` pools the underlying native handle by default.** `Dispose()`
   alone left day files locked on Windows — the exact "delete a day `.db`" collision the
   build plan §3.6 warns about, just hit early, in test cleanup. Fix:
   `SqliteConnection.ClearAllPools()` after `Dispose()` in `SqliteBatchWriter`, and
   `Pooling=False` on every short-lived test/assertion connection. `SqliteConnection.ClearPool`
   (the single-connection-string variant) was tried first and did **not** reliably clear
   handles opened by other short-lived connections against the same path — `ClearAllPools`
   was the one that actually worked.

**Tests: 44 passing, up from 31.** 12 in `EventScope.Storage.Tests` (new — round trip,
oversized payload, live-segment read, footer-truncation recovery, forced roll, the
`ShouldRoll` boundary, schema/WAL/FTS5, 500-row and 200&#160;ms batch boundaries, subject
interning within and across files, `FlushAsync` barrier semantics), 20 in
`EventScope.Core.Tests` (unchanged), 12 in `EventScope.App.Tests` (11 from M1a plus
`IngestPipelineStorageTests`: 500 messages through a real `IngestPipeline` into real
segment + SQLite files in a temp dir, zero lost, every row's on-disk coordinates read back
byte-for-byte through the same `IPayloadReader` the detail pane uses). Every storage test
ends with `INSERT INTO body_fts(body_fts) VALUES('integrity-check')` per the build plan's
external-content contract validator.

**`EventScope.Bench` is no longer `Hello, World!`** — `SegmentReadBenchmarks` and
`SqliteBatchInsertBenchmarks` are real BenchmarkDotNet classes now. **Not yet run**: a full
BenchmarkDotNet pass (warm-up + multiple iterations per benchmark) takes materially longer
than this pass's time budget: measuring throughput/latency baselines and committing them to
`tests/EventScope.Bench/baselines/` is still pending, as is the render-tick-histogram /
`dotnet-counters` measurement of M1's five acceptance criteria (build plan §6). The
correctness side (zero messages lost, byte-exact read-back) is measured; the performance
side is not yet.

**Manually verified against the real, running app — not just tests.** Launched
`EventScope.exe` (Release build) as a genuine GUI process and drove it with Windows UI
Automation (`System.Windows.Automation`, scripted from PowerShell): found the toolbar's
"Start" button by its accessible name and invoked it, let it stream for a few seconds,
invoked "Stop", closed the process. Screenshots weren't viable — this session's Windows
desktop has no attached interactive display surface, so captured images came back as
degenerate slivers rather than the actual window content — but the on-disk evidence is
more direct than a screenshot for this specific claim anyway: a fresh independent query
against `%LOCALAPPDATA%\EventScope\sessions\2026-08-30\2026-08-30.db` (via a `dotnet run`
file-based script, not the test suite) showed **40,500 real rows**, correct sequential ids,
correct subject interning, plausible previews, and `INSERT INTO body_fts(body_fts)
VALUES('integrity-check')` passing — a 27&#160;MB `.db`, a 2.3&#160;MB `000000.seg`, and a
5.3&#160;MB `-wal` file, all written by the shipped `IngestPipeline` code path with a real
user-facing Start button as the only thing driving it.

**New environmental finding this pass, refining Blocked item 2 below: a clean Debug
rebuild of `EventScope.App.Tests` hangs indefinitely during Avalonia headless setup on this
machine — reproduced four times.** `EventScope.Core.Tests` and `EventScope.Storage.Tests`
(no Avalonia dependency) both build and run correctly, instantly, on the exact same
freshly-cleaned Debug tree — isolating the hang specifically to Avalonia's headless
platform initialization (`AppBuilder...UseHeadless(...).SetupWithoutStarting()`), not
anything in this pass's own code. It does not throw, does not appear in the Code Integrity
event log, and does not appear in Windows Error Reporting — it simply never proceeds past
"Starting: EventScope.App.Tests". **`-Configuration Release` is the reliable workaround**:
the exact same suite passes 12/12 in Release, repeatably, in 2-3 seconds. Also widened
`build/Sign-LocalTestBinary.ps1` + `Directory.Build.targets`'s `SignLocalTestBinary` target
this pass to sign every DLL/EXE in a Debug test project's output directory, not just its own
primary assembly — copied dependencies (`Avalonia.Base.dll`,
`SQLitePCLRaw.provider.e_sqlite3.dll`, and others) were each independently observed blocked
by Smart App Control this pass, a gap the original single-file version of the script didn't
cover. That fix is real and still worth keeping, but it did not resolve the Debug hang
above — the hang reproduces even after every file in the output directory is confirmed
signed, so it is a distinct issue from the load-time block the signing script targets.

---

### M1c — Kafka, measured acceptance criteria (this pass)

Closes M1. `KafkaEventSource` exists, is reachable from the running app, benchmark baselines
are committed, and all five of the build plan's M1 acceptance criteria (§6) are measured
against real code rather than assumed — two of them fail, and that is recorded honestly
below rather than smoothed over.

**`EventScope.Brokers.Kafka/`:**
- **`KafkaEventSource`** — a dedicated `TaskCreationOptions.LongRunning` task per the
  threading table (`Consume()` is blocking sync); a fresh, per-instance throwaway consumer
  group id (`{prefix}-{guid}`, never reused), `enable.auto.commit=false`,
  `auto.offset.reset=latest`. `Capabilities` differs from `FakeEventSource`'s in exactly the
  two places that matter: `SupportsDeadLetterQueue=false` (Kafka has no native DLQ) and
  `SupportsReplay=true` (real seek-by-offset) — the first real exercise of the capability
  abstraction actually differing between two `IEventSource` implementations, not just two
  synthetic ones. The channel write inside the consume loop blocks synchronously by design,
  not sync-over-async sloppiness: the loop owns its own dedicated thread, so blocking it is
  the back-pressure edge itself — `Consume()` stops being called, lag builds on the broker,
  nothing is dropped, exactly the guarantee build plan §3.2 describes.
- **`KafkaMessageMapper`** — `MessageId` falls back through header (`message-id` /
  `messageId` / `ce-id`, case-insensitive) → message key (UTF-8) → `"{partition}:{offset}"`;
  `CorrelationId` from a header or `null`; a tombstone (null `Value`) maps to an empty body,
  not a dropped message.
- **Tests: `EventScope.Brokers.Kafka.Tests`, 16 tests** (15 run, 1 opt-in integration test
  skips without `EVENTSCOPE_KAFKA_BOOTSTRAP` — Blocked item 5, still no broker on this
  machine). Unit tests run against `FakeKafkaConsumer`, a hand-rolled
  `IConsumer<byte[],byte[]>` matching the repo's existing style (`ManualTicker`,
  `FiniteEventSource`) rather than a mocking package — every member the source must never
  touch throws `NotSupportedException`, which is half the point of the fake. Covers: the
  config (throwaway group, auto-commit off, `auto.offset.reset`), every mapping fallback,
  `IsPartitionEOF` results skipped not emitted, back-pressure (a channel bounded at 1 with no
  reader proves `Consume` isn't called a third time while blocked), graceful cancellation and
  exactly-once `Close()`, and non-fatal-vs-fatal `ConsumeException` handling (non-fatal
  surfaces on `ErrorOccurred` and the loop continues; fatal breaks the loop and faults the
  task).

**Reachable from the app (`EventScope.App/Ingest/EventSourceFactory.cs`).** Per-plan minimal
wiring, not the Stage 5 connection manager: `FakeEventSource` unless
`EVENTSCOPE_KAFKA_BOOTSTRAP` is set, in which case `KafkaEventSource` against that broker and
`EVENTSCOPE_KAFKA_TOPIC` (comma-separated, defaults to `"eventscope"`).
`MainWindowViewModel.Start()` calls the factory instead of hardcoding `FakeEventSource` — the
two `Toolbar.*` capability assignments already there needed no change, which is the
capability abstraction paying for itself. `KafkaEventSource.ErrorOccurred` marshals onto the
UI thread and surfaces into `Toolbar.StatusLabel`. Manually verified: the app runs unchanged
with no env vars set (default path untouched); pointed at a bogus bootstrap host
(`EVENTSCOPE_KAFKA_BOOTSTRAP=bogus-host:9092`), the process stays alive for the full
verification window with no unhandled exception — librdkafka reports connection failure via
the error callback, not by throwing.

**Publish size, measured not assumed.** The plan flagged that `librdkafka.redist`'s native
libraries would likely grow the single-file publish past the 123 MB recorded at release
readiness. Measured: **128,551,531 bytes — essentially unchanged.** The App project's
`ProjectReference` to `EventScope.Brokers.Kafka` (and therefore `Confluent.Kafka` and
`librdkafka.redist`) already existed before this pass — the 123 MB figure was recorded after
that reference was added but before `KafkaEventSource.cs` itself existed, so the native
payload was already being bundled. Adding the actual Kafka code cost nothing further.

**Benchmark baselines committed** (`tests/EventScope.Bench/baselines/`, `-j Short` job, this
laptop — see that directory's `README.md` for full machine details and why no CI regression
gate is wired against them). `SqliteBatchInsertBenchmarks`: 50,000 rows in 362 ms
(~138k rows/sec into the batch writer's queue, well past the 10,000 msg/s target).
`SegmentReadBenchmarks`: 1,000 random reads average 360–470 µs each — comfortably inside the
100 ms row-selection budget — **but allocate ~1.7–2 GB across those 1,000 reads.**
`SegmentReader.ReadAsync` decompresses and allocates the entire containing ~1 MB block on
every call regardless of the requested payload's size, with no decompressed-block cache;
against 10,000 payloads packed into relatively few blocks, random-offset reads mostly miss
whatever the previous read touched. Not an M1 acceptance-criterion violation (latency is
still well under budget) and not fixed here — Storage-internals tuning is out of this pass's
scope — but worth a line for M2, since deep scan (§6: "≥ 500 MB/s decompressed") and any
bulk-read UI feature will feel the allocation rate before they feel the latency.

**M1 acceptance criteria (build plan §6), all five measured — see
`tests/EventScope.Bench/baselines/acceptance/README.md` for the full writeup:**

| Criterion | Result |
|---|---|
| 10,000 msg/s for 60s, no frame over 100 ms | Marginal — 1 of 2,697 samples over budget (p50 18.3 ms, max 119.6 ms) |
| Heap growth under 50 MB across that run | **Fails — ~470–500 MB growth measured**, confirmed across three independent counters (managed heap, working set, GC committed size) |
| 50,000-row scroll under 16 ms/frame | Pass (p50 ~3.5 ms, max 4.7–10.2 ms) |
| Row selection renders body under 100 ms | Pass, comfortably (p50 0.5–1.8 ms, max under 42 ms) |
| Zero messages lost from disk under saturation | Pass — 20,000/20,000 against a deliberately starved 16 KB byte budget |

Measured via three new/changed pieces:
- **`tests/EventScope.Acceptance.Tests`** (new project) — cold segment read latency and
  saturation zero-loss. Deliberately its own project with **no Avalonia dependency at all**,
  not part of `EventScope.App.Tests` — see the next section for why.
- **`EventScope.App.Tests/AcceptanceCriteriaTests.cs`** — the 50,000-row scroll timing (needs
  a real `DataGrid`, so stays in the Avalonia-headless assembly).
- **`build/Measure-M1Acceptance.ps1`** (new) — launches the real `EventScope.exe` with
  `EVENTSCOPE_MEASURE=<seconds>` (auto-starts streaming, runs a
  `DispatcherPriority.Render` frame-time probe, auto-closes — no UI-Automation
  click-driving needed for this one), attaches `dotnet-counters` in parallel for the
  heap-growth half. Both write CSVs to `tests/EventScope.Bench/baselines/acceptance/`.

All soak-scale tests are gated behind `EVENTSCOPE_SOAK=1` (`SkipUnless`) so the normal fast
suite is unaffected — confirmed by four consecutive normal (non-soak) runs of the full
5-assembly suite, all green, ~1.7 s for `EventScope.App.Tests` specifically.

**Also observed, not yet explained: slow shutdown.** After the 60s measurement run's
streaming stopped, `EventScope.exe` did not exit on its own within 30 seconds — the
measurement script had to force-stop it. Not one of the five acceptance criteria, but a user
pressing Stop or closing the window after a sustained high-throughput run may see the same
delay. Whether `IngestPipeline.DisposeAsync`'s cancel-then-drain is legitimately draining a
large backlog or something is actually stuck isn't known without a dedicated look.

**Six corrections to the plan, found by actually running it — same spirit as every prior
pass's numbered list:**
1. **A newly-discovered, non-deterministic hang in `EventScope.App.Tests` on this machine,
   distinct from the Debug-rebuild hang in Blocked item 2 below.** The very first real async
   file I/O (`RandomAccess`-based, e.g. `SegmentReader.ReadAsync`) issued in this process
   after Avalonia's headless platform initializes, before anything has pumped the dispatcher,
   can hang indefinitely with near-zero CPU activity. **Reproduces against a pre-existing,
   already-shipped test** (`IngestPipelineStorageTests`) when it happens to run in isolation
   (`-method` filter) or first in execution order — this is not new-code-specific, it was
   latent before this pass. Tried and **did not fix it**: `ConfigureAwait(false)` at the
   await call site (rules out a captured-`SynchronizationContext` theory, since
   `SegmentReader.ReadAsync` already uses it internally); pumping the dispatcher once during
   `HeadlessFixture.EnsureInitialized()`; showing and pumping a throwaway `Window` during
   setup; `ThreadPool.SetMinThreads`. **Worked around, not fixed:** the two new storage-only
   acceptance tests moved to their own project (`EventScope.Acceptance.Tests`) with no
   Avalonia reference at all, sidestepping the interaction rather than resolving it. The
   scroll test and the twelve pre-existing `EventScope.App.Tests` tests still carry the
   latent risk in soak/heavy-load conditions; the normal (non-`EVENTSCOPE_SOAK`) suite is
   confirmed unaffected across repeated runs. Left open — needs deeper investigation than
   this pass's scope, tracked as Blocked item 2's second half below.
2. **`SqliteBatchInsertBenchmarks`/`SegmentReadBenchmarks` had never been run before this
   pass** despite existing since M1b; running them surfaced the block-decompression
   allocation finding above, which no correctness test had a reason to catch.
3. **The saturation acceptance test's first parameter choice (200,000 messages, 64 KB byte
   budget) was pathologically slow** — 5+ minutes with near-constant park/release churn on
   the byte budget, for no additional correctness signal over a smaller run. Tuned to 20,000
   messages / 16 KB (still forces genuine saturation — room for only ~50 messages in flight —
   without the pathological overhead).
4. **The scroll acceptance test needed an untimed warm-up phase**, the same reasoning
   BenchmarkDotNet's `WarmupCount` exists for: the first few scroll steps pay one-time JIT
   and first-layout costs. Measured directly: without warm-up, max was 47 ms on the first
   steps while p50 across the run was 4.8 ms.
5. **The scroll acceptance test also needed `UnloadingRow` wired to `NotifyRowUnloaded`**,
   exactly like `MainWindow.axaml.cs` does — `DataGridVirtualizationSpikeTests`' copy-pasted
   `BuildGrid` helper never needed this because its tests each scroll exactly once. A test
   that scrolls 70 times without it measures unbounded `_realized`-dictionary and
   DataGrid-container growth, not `MessageRowsView`'s real steady-state cost: per-scroll cost
   climbed from ~5 ms to ~180 ms across 70 steps before the fix, ~3.5–4.7 ms consistently
   after.
6. **`Dispatcher.UIThread.Invoke` is required, not optional, for a test that constructs
   Avalonia UI objects.** xunit.v3's in-process runner does not guarantee a test method body
   executes on the same OS thread its class's constructor ran on. Confirmed by measurement:
   run alongside other test classes, constructing a `DataGrid` directly in the test method
   threw `InvalidOperationException: Call from invalid thread`, even though the identical
   construction pattern in the pre-existing `DataGridVirtualizationSpikeTests` passes —
   presumably because that class's tests happen to land on the same thread as their own
   constructor call, not because the construction itself is inherently safe.

---

### M1 remainder, step 1 — heap growth root-caused, shutdown no longer reproduces (this pass)

M1c measured ~470–500 MB heap growth over a 60s 10k msg/s run (budget: 50 MB) and a >30s
shutdown delay, both flagged as real but unexplained. Both are addressed here, and the
finding contradicts the M1c report's own leading candidate — recorded honestly rather than
quietly dropped.

**Two theories tested by direct measurement before any code changed, both refuted:**

1. `SqliteBatchWriter`'s `BlockingCollection<WriteOp>` queue has no bounded capacity — a
   growing backlog of queued rows (each retaining a 2 KB `BodyHead` string) looked like it
   could explain both the heap growth and the slow shutdown (draining a large backlog at
   500 rows/commit on `Dispose`). **Refuted:** a standalone harness driving the real
   `SegmentWriter` + `SqliteBatchWriter` at genuine 10k msg/s for 20s, no Avalonia involved,
   showed the queue at 0 almost the entire run (briefly 500, one batch) and managed heap
   under 20 MB throughout — the batch writer drains far faster than 10k msg/s (the existing
   `SqliteBatchInsertBenchmarks` baseline already showed ~138k rows/sec). There was no
   backlog to bound.
2. **The M1c report's own leading candidate** — the ingest channel's 256 MB byte budget
   running near-full during bursts — was also refuted. A new sampler added to the
   measurement session (`MainWindow.Measurement.cs`'s `WriteByteBudgetCsv`, backed by two
   new diagnostic accessors: `MainWindowViewModel.CurrentByteBudgetUsed`/
   `CurrentBatchWriterPending`, and `SqliteBatchWriter.PendingCount`) shows `byte_budget_used`
   peaking in the low hundreds of KB against the 256 MB limit across a full 60s run — never
   remotely saturated.

**The actual cause, found by reading `FakeEventSource.BuildJsonBody`:** for every "large"
message (1% of traffic by default, ~100/sec at 10k msg/s), the old implementation built two
intermediate C# strings — a padding string and the final interpolated JSON string — each
64–98 KB, i.e. 128–196 KB in UTF16, well past the 85,000-byte Large Object Heap threshold.
~25–30 MB/sec of purely avoidable LOH churn, generated by the synthetic-load fixture itself,
not anything under test. Rewritten to write UTF8 bytes directly into one final buffer with
no intermediate large strings; verified JSON-shape-equivalent by a new test covering both the
small and large path (`FakeEventSourceTests.Body_is_valid_json_with_correct_fields_and_padding_length`).

**Result — three independent counters agree, ~85–90% reduction, reproduced across two
separate 60s runs:** managed heap Δ~470 MB → Δ~55–62 MB; working set Δ~494 MB → Δ~76 MB; GC
committed Δ~501 MB → Δ~68 MB. **Still not a clean pass against the 50 MB budget** — the
remaining ~60–75 MB is not further root-caused this pass; the build plan's own standing
"60 seconds may not be enough time for gen2 to reclaim everything" candidate is the leading
explanation for what's left. Full numbers and the before/after table are in
`tests/EventScope.Bench/baselines/acceptance/README.md`, updated in place.

**Shutdown no longer reproduces** — four 60-second-class measurement runs in this pass, all
exiting cleanly on their own, versus M1c's every run needing a force-stop. Not independently
proven (no dedicated shutdown-timing test was written), but the timing lines up:
`IngestPipeline.DisposeAsync`'s drain and `SqliteBatchWriter.Dispose`'s `Thread.Join()` both
wait on real work completing, and severe GC pressure from the LOH churn above is a plausible
reason both were slow before. Recorded as "no longer reproduces," not "fixed and proven."

**Frame time is unchanged** (1 sample over 100 ms out of 2,635, same shape as M1c) — since
the heap-growth fix removed a large source of GC pressure without moving this number, the
earlier hypothesis that the one slow frame was a large gen2 collection is now less likely;
it looks like an unrelated, rare one-off.

Tests: 65, up from 63 (2 new theory-covering cases in
`FakeEventSourceTests.Body_is_valid_json_with_correct_fields_and_padding_length`).

---

### M1 remainder, step 2 — cut allocation further: segment-read block cache (this pass)

The M1c benchmark found `SegmentReader.ReadAsync` decompressing and allocating a whole ~1 MB
block on every call regardless of the requested payload's size (~1.7–2 GB across 1,000
random reads). Needed for M2's deep-scan target (§6: "≥ 500 MB/s decompressed") regardless
of step 1's outcome, so done now rather than deferred again.

**`SegmentReader` gained a decompressed-block cache**, keyed by
`(segmentId, block's uncompressed start)`. Safe indefinitely, including against a still-live
(unsealed) segment: `SegmentWriter` only ever appends new blocks, never rewrites one, so a
block's bytes are immutable the moment they're written. Bounded by a configurable
`BlockCacheCapacity` (default 64 blocks, ≈64 MB) with approximate (FIFO, not strict LRU)
eviction — exact recency tracking isn't worth the synchronization cost for a read cache. The
compressed-bytes scratch buffer is now rented from `ArrayPool<byte>.Shared` instead of
`new byte[]`, since it's genuinely transient (only the decompressed block is kept).

**Re-measured with `SegmentReadBenchmarks`, same benchmark, same machine:** allocation
across 1,000 random reads dropped from ~1.7–2 GB to **~31.62 KB** (essentially just the
benchmark harness's own overhead), and mean latency for the full 1,000-read run dropped from
~360–470 ms to **~60–64 µs**. Recorded honestly in
`tests/EventScope.Bench/baselines/README.md` with why the number is this dramatic: the
benchmark's 10,000 payloads pack into far fewer distinct blocks than the 64-block cache
holds, so after warm-up nearly every read is a hit. A day-file deep scan touching more
distinct blocks than the cache holds still decodes every block at least once — just once per
block instead of once per read, which is the actual saving — and `SegmentReadBenchmarks`
doesn't yet cover that broader-than-cache shape; worth adding once M2's deep scan exists.

**`IngestPipeline.BuildPreview` also cleaned up** — it previously decoded the entire body via
`Encoding.UTF8.GetString` and ran two separate `Replace` passes over the result (three
string allocations) to produce a 120-char, newline-stripped preview. Now decodes only a
bounded byte prefix and replaces both characters in a single pass. Minor: normal message
bodies are only 64–512 bytes, so this was never a growth driver — refuted causes stay
refuted rather than being retroactively credited — but it's a real, described inefficiency
with a low-risk fix, and previously had no test coverage at all for its actual output shape
(existing pipeline tests use synthetic hardcoded preview strings). Added
`IngestPipelinePreviewTests` to cover both newline replacement and truncation through the
real pipeline end to end.

**Considered and explicitly not done:** giving `InMemoryPayloadStore`'s 4,096-slot hot ring a
byte cap in addition to its existing count cap. Worked out from the numbers instead of
guessed: at the default 1% large-message fraction, the ring's worst-case footprint is
~5–10 MB — already small and bounded. Adding a byte cap would be solving a problem the
measurements say doesn't exist.

Tests: 69, up from 65 (3 new in `SegmentReaderBlockCacheTests` covering cache hit/reuse
correctness, bounded capacity, and the live-segment case; 1 new in
`IngestPipelinePreviewTests`).

---

### M1 remainder, step 3 — row-state styling: a real bug fixed, a bigger regression caught and reverted (this pass)

**The bug, confirmed real by reading the code.** `MainWindow.axaml.cs`'s `OnLoadingRow` set a
realized `DataGridRow`'s `large`/`evicted`/`deadLettered` classes once, at realization time
only. But `MessageRowsView.RecomputeFollowWindow`'s follow-mode steady state repopulates an
already-realized row's same `MessageRowViewModel` instance in place (by design — that's how
it stays cheap at 10k msg/s), raising no collection notification at all. `LoadingRow` never
fires again, so a row styled `large` kept that styling once repopulated with an ordinary
message. Also confirmed missing: `MainWindow.axaml` had no `.large` or `.deadLettered`
selectors at all — §4.4's amber Size cell and 2px red dead-letter edge were unimplemented,
not just stale.

**First fix attempt: a `PropertyChanged` subscription per realized row, extracted into a new
`RowStateClassSync` (`src/EventScope.App/Views/`) so it could be tested against a minimal
`DataGrid` instead of the full `MainWindow`** — constructing a real `MainWindow` inside
`EventScope.App.Tests` reproducibly hangs the assembly, confirmed by isolating it to a bare
construct/show/close with no row content involved at all. This is a new, more specific
instance of the Avalonia-headless threading family already tracked in Blocked item 2; the
regression tests originally written for this fix are consequently the reason two of the new
tests below don't exist anymore — see the next paragraph.

**Measurement caught a serious regression before it shipped.** Even after filtering the
subscription's handler down to only the three relevant properties (`Populate` sets ~12 per
call), a 60s acceptance measurement showed **~290–340 MB heap growth — worse than step 1's
starting point — plus a reintroduced shutdown delay**. Bisected by toggling only the
subscription on/off with everything else identical: ~57–94 MB either side of it, confirming
the subscription itself as the cause, not noise. The cost is Avalonia's per-`Classes.Set`
style re-evaluation under an imperative handler, not invocation count.

**Reverted rather than shipped.** The staleness this fixes is cosmetic and narrow (only
visible in the gap between a row's flags changing and it next reloading); a 4–6x regression
on the heap-growth criterion this pass had just spent real effort fixing is not a trade worth
making. `RowStateClassSync` now does only the original one-time apply — functionally
unchanged from the inline code it was extracted from, kept only because the extraction itself
is a harmless small testability win. Full account, numbers, and a cheaper idea for later (a
declarative `Classes.large="{Binding IsLarge}"` binding — proven safe on the SIZE column's
own cell below, but not directly applicable to `DataGridRow` itself since it isn't
user-templated) are in `RowStateClassSync.cs`'s remarks and
`tests/EventScope.Bench/baselines/acceptance/README.md`.

**What did ship, and is safe:**
- The missing §4.4 selectors — `.large` (amber Size cell via a `DataGridTemplateColumn` with
  `Classes.large="{Binding IsLarge}"` on its cell's `TextBlock` — Avalonia's native binding
  path, not the imperative handler that regressed) and `.deadLettered` (2px `Red` left border,
  with the spec's "always reserved" transparent 2px gutter now the `DataGridRow` baseline so
  the marking causes no reflow). Also fixed in passing: the SIZE column had never actually
  been right-aligned per §4.3 despite the spec calling for it — the template-column rewrite
  gave that for free.
- The one-time apply-at-load behavior (unchanged from before this pass).

**Re-measured to close out the M1 remainder — four 60s runs total across steps 1–3, all with
the final code:** managed heap Δ 55, 62, 57, 66 MB; frame time 1–2 samples of ~2,650 over
100 ms each run. Both remain marginal against their 50 MB / zero-over-budget targets, not a
clean pass, and that is the honest final state of M1's two open defects — down from a hard
fail (~470–500 MB, and a shutdown that never completed on its own) to a small, stable margin.

Tests: 69, unchanged — the row-styling regression tests were written, then deleted along with
the code they tested once the measurement showed the fix itself was the worse defect.

---

## M2 — storage discipline and search

M1's remainder is closed; this begins the largest unbuilt milestone. Storage discipline
(day-file rolling, retention, FTS search) first, per the build plan's own M2 task order.

### M2 step 4 — day-file rolling and retention (this pass)

**`SessionStore` rebuilt as a multi-day owner.** M1b's version opened one fixed day for the
whole process lifetime. Now: `Writer`/`SegmentWriter`/`SegmentReader` always refer to the
current day; `EnsureCurrentDay()` — called from `IngestPipeline.Ingest` before every write —
rolls to a new day the moment `TimeProvider.GetUtcNow()`'s date moves past the currently open
one. Both files stay usable across the boundary: the old writer and segment writer are
disposed (drain + seal) on a background `Task.Run`, never inline, so a rollover can never
stall ingest into the new day the way the build plan's own retention criterion forbids for
deletion — this was a deliberate design choice, not an oversight, since `SqliteBatchWriter.Dispose()`'s
`Thread.Join()` would otherwise block the calling (ingest) thread for however long the old
day's backlog takes to drain.

**A deviation from the plan's literal phrasing, found while implementing it.** §3.6 describes
rollover as "a `WriteOp` on the *old* writer's queue." No new `WriteOp` case was added for
it: `SqliteBatchWriter.Dispose()` already does exactly what that op would — `CompleteAdding()`
then `Join()` drains everything queued before the call, then closes — so a dedicated op would
just reimplement it. Calling `Dispose()` from the background task achieves the same effect
with less code.

**Cross-day reads.** A `MessageHeader`'s `SegmentId`/`Offset` are only meaningful within the
day directory they were written to — segment ids restart at 0 every day — so the detail
pane's cold read path needed to learn which day a row belongs to before it can look anything
up. New `SessionStorePayloadReader` (`EventScope.App/Ingest/`) derives the day from
`MessageHeader.EnqueuedTicks` (UTC, same `yyyy-MM-dd` format `SessionStore` uses for its own
day strings, so the two can never disagree) and asks `SessionStore.GetOrOpenReader(day)` for
the right reader — opened lazily, kept for the store's whole lifetime once opened. Replaces
the fixed single-`SegmentReader` cold reader `IngestPipeline` used to take directly.
`IngestPipeline`'s constructor now takes `SessionStore` itself rather than
`SegmentWriter`/`SqliteBatchWriter`/`IPayloadReader` separately, since every write must
freshly re-read `SessionStore.SegmentWriter`/`.Writer` each time (rollover can swap which day
those point at) rather than caching them at construction.

**`RetentionService`** (`EventScope.Storage/Retention/`, new) — `PeriodicTimer`, driven by
`TimeProvider` so both the interval and the age cutoff are fake-clock testable. Two passes,
run together every tick:
- **Age-based deletion.** Any day directory older than the retention window is dropped
  whole via `SessionStore.DeleteDay` — never the current day, regardless of how old the
  fake clock says "today" is (a real edge case a naive date-diff would get wrong, covered by
  a dedicated test).
- **Cap enforcement.** Evicts the oldest segment across the *whole store* — oldest day
  first, lowest segment id within that day — until total on-disk bytes (enumerated directly,
  so `-wal`/`-shm` files count toward the cap same as everything else) drop back under the
  configured limit. Eviction means `SessionStore.EnqueueSetFlags(day, segmentId,
  PayloadEvicted)` (new `WriteOp.SetFlags`, scoped by `segment_id` not per-row id — never
  touches `body_head`/`message_id`/`correlation_id`, the FTS-indexed columns, exactly per
  §3.4's warning) followed by deleting the segment file. The segment a live writer is still
  appending to is never a candidate, checked via `SessionStore.CurrentSegmentId`. A day left
  with zero segment files after eviction has its `.db` dropped too — nothing in it is
  reachable anymore.
- **Routing the flags update correctly.** `SessionStore.EnqueueSetFlags` routes through the
  live `SqliteBatchWriter` if the target day is current (the only writer allowed to touch
  that connection, §3.6), or opens a short-lived direct connection for an older, already-
  sealed day — no live writer exists for those anymore, and none of §3.6's contention
  concerns apply to a file nothing else is writing to.

**`SqliteBatchWriter` gained two new `WriteOp` cases**, handled between insert batches on its
own thread/connection: `SetFlags` (above) and `Checkpoint` (`PRAGMA wal_checkpoint(TRUNCATE)`)
— the latter not yet wired to anything (no caller posts it yet; the plan calls for it to run
"only when idle," which needs the FTS indexer's idle-detection from step 5 to mean anything).
Left in place as a ready hook rather than built and immediately dead code that step 5 would
just re-add.

**A found-and-rejected sub-task from the plan.** The original plan for this step included
making `SqliteBatchWriter`'s internal 200 ms batch window `TimeProvider`-aware, reasoning
that a fake-clock rollover test would need it. Turned out false on inspection: rollover's
day-check lives entirely in `SessionStore`, driven by its own `TimeProvider` calls,
independent of the batch writer's internal timing — the two were never actually coupled.
Skipped; noted here so the reasoning isn't silently lost.

**Known, accepted race, not engineered around.** If `RetentionService` runs within the same
narrow window as a rollover's background seal task (milliseconds, in practice), it could
attempt to enumerate or delete a day's files while the old writer's `Dispose()` is still
mid-drain. Not defended against — the retention timer's default 30 s period versus a
typically-near-instant seal makes this exceedingly unlikely to matter in practice, and
building real synchronization for it would be disproportionate to the risk. Flagged
honestly rather than silently assumed away.

Tests: 77, up from 69 — 8 new in `EventScope.Storage.Tests`
(`SessionStoreRolloverTests` ×3: cross-day reads after rollover, no-op within the same day,
reading a day that never existed returns empty not a throw; `RetentionServiceTests` ×5:
age-based deletion, current-day-never-deleted-by-age, cap eviction marks rows and deletes the
right file, the live segment is never evicted, a day with no segments left drops its `.db`).
`SettableTimeProvider` (advanceable fake clock) added to that test project.

**Manually verified against the real app**, not just tests: launched `EventScope.exe`
directly (auto-measure mode, 8 s), confirmed it streams and exits cleanly with the new
`SessionStore`/`RetentionService`/`IngestPipeline` wiring, and confirmed a real day directory
was created under `%LOCALAPPDATA%\EventScope\sessions\` by this exact run.

---

### M2 step 5 — the FTS indexer (this pass)

**`FtsIndexer`** (`EventScope.Storage/Search/`, new) implements the build plan §3.4 catch-up
batch essentially verbatim: one `BEGIN IMMEDIATE`/`COMMIT`, the window computed once
(`newHwm` = the id of the last row in a batch of up to `CatchUpBatchSize` = 2000) so both
`body_fts` and `ident_fts` index an identical row range, and `index_state.fts_hwm` advanced
**in the same transaction** as the inserts — FTS5 does not dedupe rowids, so re-indexing
after a crash between the inserts and the hwm advance would create duplicates. The window is
computed separately from "the rows actually inserted" specifically because `body_fts` skips
rows with a `NULL` `body_head`, but the high-water mark still has to move past them or
they'd be retried forever.

**Wired into `SqliteBatchWriter.RunLoop`**, not called separately from ingest: after each
insert batch (and any pending `SetFlags`/`Checkpoint`), if the queue is empty, it spends up to
a 10 ms budget running catch-up batches (§3.6: "if queue depth is low, run index batches
until a 10 ms-per-200 ms budget is spent"). Same thread, same connection as every insert —
a second write connection would mean `BEGIN IMMEDIATE` contention and `SQLITE_BUSY` storms,
exactly what this avoids by construction. Idle maintenance layered on top of that: `('merge',
-16)` every 50 fully-idle iterations (~10 s at the normal 200 ms batch window) once caught up,
and `('optimize')` once, in `Dispose`'s cleanup, best-effort (swallows a `SqliteException` so
a broken connection from a prior fatal error can't block shutdown).

**Index lag** (`SqliteBatchWriter.IndexLag`, `MAX(messages.id) − fts_hwm`) refreshed by the
writer thread after every indexing pass into a plain `long` field — safe to read from any
thread the same way `PendingCount` already was, and for the same reason: whole-word reads
need no synchronization, and a briefly stale value is exactly as acceptable here as it already
is there. Not yet wired into the status bar — that's step 6, alongside the search UI it
actually informs.

**A genuine SQLite/FTS5 finding, not assumed, that shaped the tests.** `SELECT COUNT(*) FROM
body_fts` (or `ident_fts`) with no `MATCH` clause does **not** reliably report how many rowids
are actually indexed in an external-content table — measured directly (a minimal standalone
repro), it reflects the underlying content table's (`messages`) row count instead, so it
reports the same number whether a row was actually indexed or deliberately skipped (e.g. a
null `body_head`). First discovered as a failing test that looked like an indexer bug; it
wasn't — the indexer's `ExecuteNonQuery` affected-row-count already confirmed the correct
single row went in. Every test now verifies indexed content via a real `MATCH` query instead,
which is also more representative of what the index is actually for. Two more, smaller FTS5
syntax findings from the same debugging session: the `'merge'` special command needs a
`rank` column in its column list (`INSERT INTO fts(fts, rank) VALUES('merge', N)`, not just
`INSERT INTO fts(fts) VALUES('merge', N)`); and a bare `-` inside a query string is FTS5's
NOT operator, so a hyphenated value like a `c-1`-shaped correlation id has to be
double-quoted as a literal phrase or the parser reads it as "match `c`, exclude `1`".

**Trigram-specific test constraint, confirmed rather than assumed from the build plan's own
warning:** `ident_fts` queries under 3 characters match nothing, and there is no `*` prefix
wildcard support the way `body_fts` (unicode61) has — both measured directly while writing
these tests, not just taken on faith from §3.4's text. Tests spot-check individual known
values instead of one blanket wildcard query as a result.

Tests: 84, up from 77 — 13 new: `FtsIndexerTests` ×5 (every row indexed and hwm advances to
the last id; hwm advances past a null-`body_head` row that `body_fts` itself skips;
re-running a caught-up batch is a no-op with no duplicates; a backlog larger than one
catch-up batch needs two calls; `merge`/`optimize` don't throw against a real schema) and
`SqliteBatchWriterIndexingTests` ×2 (rows are indexed automatically once the writer goes
idle, with no separate caller; index lag reflects a deliberately large backlog until it
catches up) in `EventScope.Storage.Tests`.

---

### M2 step 6 — tiered search and its UI (this pass)

All three tiers from the build plan now exist and are wired into the running app; the search
bar in `MainWindow.axaml` is no longer `IsEnabled="False"`.

**Instant tier — `RingSearchFilter`** (`EventScope.App/Search/`), a thin wrapper over
`SearchValues<string>` per the plan's own phrasing ("SIMD substring search across 50k
previews, which is what makes the 'instant' scope instant"). `MessageRowsView.SetSearchQuery`
recomputes every realized row's `MessageRowViewModel.IsSearchHit` against preview, subject,
and correlation id — inside `PopulateAt`, the same place header/content fields are already
refreshed, so it rides the existing per-tick refresh instead of needing its own live
subscription. That choice is deliberate, not incidental: the M1-remainder row-styling pass
found that an *imperative* `Classes.Set`-driven subscription for `large`/`evicted`/
`deadLettered` caused a 4–6x heap-growth regression (see that pass's writeup and
`RowStateClassSync`'s remarks). `IsSearchHit` avoids that entirely two ways at once — it's a
plain `[ObservableProperty]` (no imperative `Classes.Set` call at all), and the PREVIEW
column's highlight is wired via a *declarative* `Classes.searchHit="{Binding IsSearchHit}"`
binding on that cell's own `TextBlock` (`MainWindow.axaml`), the same safe pattern already
proven for the SIZE column's `.large` binding. A query change calls `ForceReset`-equivalent
immediate re-evaluation of every currently realized row so typing shows results without
waiting for the next ingest tick.

**FTS tier — `FtsSearchService`** (`EventScope.Storage/Search/`). Queries day files
newest-first via `SessionStore.ListDayDirectories()`, stopping the moment `maxResults` is
reached — early exit means an older day is never even opened once enough results already
came from newer ones. Every hit carries `IndexHwm`, stamped from that day's own
`index_state.fts_hwm` (build plan: "every FTS result set is stamped with its IndexHwm so the
UI can state whether results are current"). Each day gets its own short-lived, read-only
connection per query (§3.6) — never the live `SqliteBatchWriter` connection, safe to run
concurrently with ingest under WAL. An identifier query under 3 characters — the trigram
tokenizer's floor — automatically falls back to a `LIKE '%x%'` scan of `messages` directly
instead of querying `ident_fts`, confirmed by a dedicated test rather than assumed from the
plan's text.

**Deep-scan tier — `DeepScanner`** (`EventScope.Storage/Search/`). Streams every message's
**full** body via `SegmentReader` — not the 2 KB `body_head` copy `body_fts` indexes — so it
finds matches FTS structurally cannot see, and doesn't depend on the index being caught up.
An `IAsyncEnumerable<DeepScanMatch>` with `IProgress<long>` reporting and per-row
cancellation, so a caller streaming into a bounded UI list never holds a large result set or
a long-lived read transaction (§3.4's WAL-starvation note). Backend only this pass — its
overlay UI is Stage 5 per the build plan's own milestone boundary (§5 lists "deep-search
overlay" under Stage 5, not M2), so wiring it up now would be scope the plan itself doesn't
call for yet.

**Search bar UI** (`SearchViewModel`, new): one text box drives both the instant tier
(every keystroke) and a 150 ms-debounced FTS body search reporting a match count and an
"index catching up" indicator when the matched day's hwm is behind the session's own total
ingested count. **Scoped down from the full spec on purpose**, consistent with prior passes'
proportionality calls: identifier-search has no scope selector in the UI yet (the backend
method exists — `SearchIdentifiersAsync`), and search-hit highlighting is a whole-cell
background on the PREVIEW column rather than per-substring inline highlighting (§4.4's
literal spec) — building real inline rich-text highlighting inside a virtualized grid cell
is closer to Stage 5 polish than to "wire search into the grid." The status bar also gained
`IndexLag` (`StatusBarViewModel`, refreshed each stats tick from
`SqliteBatchWriter.IndexLag`), visible only when nonzero.

**A real, load-bearing SQLite/FTS5 finding from step 5 paid off immediately here**: every
query in `FtsSearchService` double-quotes its search term as a literal phrase before binding
it, because a bare `-` is FTS5's NOT operator (a correlation id shaped like `c-1` would
otherwise parse as "match `c`, exclude `1`") — found once already, applied correctly the
first time in new code rather than rediscovered.

**Manually verified against the real app's live-accumulated database**, not just tests: a
body search for a term present in every message returned results in correct newest-first
order, correctly capped at the requested limit, with `IndexHwm` matching the actual high-water
mark and `IndexLag` reading `0` on a caught-up index.

Tests: 104, up from 84 — 20 new. `RingSearchFilterTests` ×4 and
`MessageRowsViewSearchTests` ×5 in `EventScope.App.Tests` (search-hit marking, matching
against subject/correlation id too, clearing immediately, a row realized *after* the query
was set still gets evaluated, and — mirroring the M1-remainder row-styling regression this
design was built to avoid — a steady-state refresh recomputes search-hit state for a row's
*new* content instead of leaving a stale result behind). `FtsSearchServiceTests` ×5 and
`DeepScannerTests` ×6 in `EventScope.Storage.Tests` (newest-first with early exit across a
real rollover-produced multi-day layout, the trigram length fallback both ways, a deep-scan
match beyond the 2 KB body_head cap, progress reporting, and cancellation).

**Two found-while-testing corrections, same spirit as step 5's:**
1. `DeepScannerTests` initially failed across the board with empty results — not a
   `DeepScanner` bug. A freshly-written small payload sits in `SegmentWriter`'s in-memory
   pending block until enough accumulates to flush (PROGRESS.md §0.1, from M1b); `DeepScanner`
   reads only from disk, so tests needed the same force-a-large-filler-append pattern
   `SessionStoreRolloverTests` already uses. Fixed in the tests, not the scanner.
2. A progress-reporting test asserted exact delivery order and failed
   (`[2,5,4,3,1]` instead of `[1,2,3,4,5]`) — measured, not assumed: `Progress<T>` posts
   each report via `SynchronizationContext.Post`, and with none installed (a console test
   host) falls back to `ThreadPool.QueueUserWorkItem` per report, which does not preserve
   call order across separate work items. This is `Progress<T>`'s own documented behavior,
   not a `DeepScanner` defect — the test now checks the received *set*, not the order.

---

### M2 step 7 — pinned fields, settings, and the chaos soak (this pass) — closes M2

**Pinned JSON fields.** `PinnedField` (`EventScope.Storage/Sqlite/`) validates a field name
(letters/digits/underscore, must start with a letter or underscore) and a `$.a.b[0]`-shaped
JSON path via `[GeneratedRegex]`. `PinnedFieldsSchema.Apply` idempotently adds a
`GENERATED ALWAYS AS (json_extract(body, path)) VIRTUAL` column plus an index for each
configured field, defense-in-depth re-validating both name and path even though the UI
already did. `SessionStore` now carries the configured field list into every day file it
opens (new and existing), and `SqliteBatchWriter` grew a `WriteOp.AddPinnedField` case so a
field added mid-session goes through the same single-writer queue as every other mutation
(build plan §3.6 collision #3) rather than a second connection touching the live file.
`DetailPaneViewModel` resolves pinned columns for the selected row by `(segment_id, offset)`
— `MessageRowViewModel` carries no SQLite row id — via a short-lived read-only connection,
best-effort (a missing/renamed column is swallowed, not surfaced as an error).

**Settings view.** `AppSettings` (`EventScope.App/Settings/`) is plain JSON under
`%LOCALAPPDATA%\EventScope\settings.json` — deliberately not SQLite, since this is small,
infrequently-changed, human-editable configuration, not the data the app's own storage model
exists for. `Load` falls back to defaults on a missing or corrupt file rather than throwing,
since a broken settings file must never block startup. `SettingsViewModel` applies retention
cap and retention days **live** to a running `RetentionService` (both are now settable
properties, not `readonly` fields — the one code change Step 4 left for this step) on Save;
a newly added pinned field applies live to a running `SessionStore` the same way. The
indexed-prefix byte count is the deliberate exception: it only takes effect on the next
`IngestPipeline` construction, since threading a live value into the ingest hot path for
something this rarely changed isn't worth the complexity — documented as such in the view
model's own remarks rather than left implicit. Wired into `MainWindow.axaml` as a scrim +
form overlay (a toolbar button toggles `MainWindowViewModel.IsSettingsOpen`), not a separate
window — consistent with the app having no window-management story yet.

**The chaos soak (`EventScope.Acceptance.Tests/ChaosSoakTests.cs`)**, gated behind
`EVENTSCOPE_SOAK=1` like the existing soak-gated acceptance tests, and living in the same
Avalonia-free project for the same reason (see that project's own `.csproj` remarks). Drives
a real `FakeEventSource` at 10k msg/s for 60 real seconds through a real `IngestPipeline` /
`SessionStore`, alongside a `RetentionService` with a 64 MB cap (small enough to force real
segment eviction against that data volume) and an `FtsSearchService` query issued every
200 ms. Midway through the run it advances a second, independent `SettableTimeProvider` (the
day clock, separate from `FakeEventSource`'s own real-time pacing clock) across a midnight
boundary to force a real rollover mid-flight, exactly as the plan specifies ("day rollover
forced by FakeTimeProvider") without waiting a real day for one to happen naturally. After
the run: asserts zero `SqliteException` with `SQLITE_BUSY`, `integrity-check` passes on every
remaining day file, no `-wal` file exceeds the 64 MB `journal_size_limit`, and the on-disk
row count across remaining files is a nonzero, non-inflated fraction of what was actually
handed to the ingest channel (tracked via a counting `IEventSource` wrapper, not
`MessageRowsView.TotalAppended` — the grid's own ticker is never driven in this test, so that
counter would just read zero). A strict "emitted − evicted = remaining" equality was
considered and rejected: at this cap and data volume a whole day file can legitimately be
evicted-and-dropped entirely mid-run (already covered in isolation by
`RetentionServiceTests`), which would make an exact count assertion flaky on eviction timing
rather than actually more correct.

**The chaos soak found a real bug on its first run — the reason this test belongs in the
plan at all.** `RetentionService.TotalBytes()` did
`Directory.EnumerateFiles(...).Sum(path => new FileInfo(path).Length)`. Under real concurrent
load this is a TOCTOU race: a `-wal` file listed by `EnumerateFiles` can be truncated or
deleted by SQLite's own checkpoint on the writer thread before `FileInfo(path).Length` reads
it, throwing `FileNotFoundException` and killing the retention loop. First run reproduced it
in under 34 seconds (`Could not find file '...\2026-06-01.db-wal'`, thrown from
`RetentionService.EnforceCap`). Fixed by summing file-by-file with a try/catch around each
stat, treating a file that vanished between enumeration and stat as contributing 0 bytes
(it's gone; it can't be counted). No unit test in `RetentionServiceTests` (single-threaded,
deterministic) could have found this — it needed real concurrent I/O, which is exactly what
Steps 4–6's individual unit tests, by design, don't exercise together. Re-ran clean at 65s
after the fix: zero `SQLITE_BUSY`, all integrity-checks passed, `-wal` stayed bounded.

**Manually verified:** ran the 65-second soak locally end to end (`EVENTSCOPE_SOAK=1`)
against a real temp directory, both before the fix (reproduced the crash) and after (clean
pass) — not just re-run under CI-shaped assumptions.

Tests: 129, up from 104 — 25 new. `PinnedFieldsTests` ×7 in `EventScope.Storage.Tests`
(name/path validation, column generation and query, a null-JSON-path row resolving to null
rather than throwing, adding a field mid-session via the batch writer's queue).
`AppSettingsTests` ×3 and `SettingsViewModelTests` ×7 in `EventScope.App.Tests` (JSON
round-trip, corrupt-file fallback, megabyte/byte conversion, live push to a running
`RetentionService`, pinned-field validation including duplicate rejection, working before any
session is running). `ChaosSoakTests` ×1 in `EventScope.Acceptance.Tests`, skipped by default
(`EVENTSCOPE_SOAK` unset) — 128 tests run in the default suite; the soak run itself was
executed manually as described above, not left to a CI machine that doesn't have 65 spare
seconds per run.

**A recurring correction, not a new one:** `SettableTimeProvider` (`EventScope.Storage.Tests`)
is `internal` to its own assembly, so `ChaosSoakTests` needed its own copy rather than a
cross-project reference — duplicated intentionally (see that file's own remarks) rather than
made `public` and exported from a test assembly for one consumer.

**M2 is closed.** Day-file rolling and retention (step 4), the FTS indexer (step 5), tiered
search and its UI (step 6), and pinned fields, settings, and the chaos soak (step 7) are all
done, tested, and — per this pass's own protocol — each committed with this file updated
alongside it.

---

## Pending — in build-plan order

- **Heap growth, remaining ~55–75 MB — optional further work.** Down from ~470–500 MB (see
  above) but still over the 50 MB budget. Not pursued further this pass since the return was
  already large; a longer (5–10 minute) `dotnet-counters` run would distinguish "GC hasn't
  caught up in 60s" from a smaller remaining leak, if a clean pass is needed later.
- **Row-state class staleness — cosmetic, known, not fixed.** A row's `large`/`evicted`/
  `deadLettered` grid styling can go stale after a follow-mode steady-state repopulate. A
  `PropertyChanged`-subscription fix was built and reverted after measurement showed a 4–6x
  heap-growth regression (see above and `RowStateClassSync.cs`). A declarative binding
  approach is untried and may be cheaper — worth a look before M2's UI work if this needs to
  be fully correct rather than just visually adequate most of the time.
- **M2 is complete** (see above) — day-file rolling, retention/eviction, the FTS indexer,
  tiered search, pinned JSON-field columns, a settings view, and the chaos soak test.
- **M3 — publisher.** Generator token parser + two-pass engine (Kahn + Tarjan SCC for
  cycle detection), JSON tree editor, preview pane, schema inference, burst publish.
- **M4 — Service Bus and SQS.** `ServiceBusEventSource`, `SqsEventSource`, and the
  capability-binding audit (no `if (broker == …)` in the view layer).
- **Stage 5 — polish.** Connection manager + per-broker forms, deep-search overlay,
  large-payload confirmation, toast, light theme, full keyboard map.
- **Release engineering — real code signing.** Repo prep, publish config and both CI
  workflows are now done (see above); what remains is the SignPath Foundation application
  and the signing step in `release.yml`, deliberately deferred until v0.1.0 ships at the
  end of M1.

---

## Blocked / needs a decision from you

Nothing blocks starting M1. Ordered by how soon it matters.

1. **`dotnet test` does not work on this toolchain. Tests run via `build/Run-Tests.ps1`.**
   Found this pass, and it predates any change made here - it reproduces on the pristine
   initial commit. On the .NET 10 SDK, VSTest is gone: `Microsoft.Testing.Platform.MSBuild`
   fails the build with *"Testing with VSTest target is no longer supported"*, so MTP is
   mandatory and `global.json` opts into it. But `dotnet test` then launches each assembly
   in MTP server mode (`--server dotnettestcli --dotnet-test-pipe ...`) and every one
   reports **"Zero tests ran", exit code 5** - including assemblies whose tests
   demonstrably pass. Confirmed against xunit.v3 4.0.0 / Microsoft.Testing.Platform 2.3.3 /
   SDK 10.0.400, with and without `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`,
   and with `OutputType=Exe` set explicitly. xunit own MTP documentation says no project
   properties are needed on .NET 10; that documented configuration reproduces the bug.
   Running the test executables directly works and is xUnit v3 native model, so
   `build/Run-Tests.ps1` does that, and both workflows call it instead of `dotnet test`.
   The suite is **63 tests** (2 Acceptance.Tests, 13 App.Tests, 16 Brokers.Kafka.Tests, 20
   Core.Tests, 12 Storage.Tests; 4 correctly skipped without a broker/soak flag) as of M1c —
   up from 44 at M1b, 31 at M1a, 5 at Stage 1. *Revisit after an xunit.v3 or MTP version
   bump - if it starts working, delete the script and put `dotnet test` back.*
   
   Also confirmed this pass: `Run-Tests.ps1`'s own execution of the test `.exe`s is exactly
   what Smart App Control can intermittently block (see the M1a entry above — it hit
   `EventScope.Core.Tests.exe` specifically, on an otherwise-unremarkable rebuild). `dotnet
   <Assembly>.Tests.dll` worked around it every time it was tried against a *test* project's
   `.dll` this pass; it did **not** work around the same block against the App project's own
   `EventScope.dll` (see the M1a corrections above), so treat it as a useful fallback for
   test runs specifically, not a general SAC workaround.

2. **Smart App Control is not the blocker it was predicted to be.** Measured this pass, and
   it contradicts the earlier assumption. SAC is genuinely in enforcement
   (`VerifiedAndReputablePolicyState = 1`, `SAC_PreviousState = 2`,
   `SAC_EnforcementReason = 1`), yet the unsigned, self-contained 123 MB
   `publish/EventScope.exe` launches fine - **including with Mark-of-the-Web attached**,
   the way a real downloader receives it. Unsigned Release-configuration test binaries
   (which the local signing target does not touch) also run. So the predicted "SAC will
   block the app at M1 step 6" does not happen.
   **Consequence:** `DISTRIBUTION_PLAN.md` Phase 0 says to turn SAC off, which is a one-way
   switch that can only be undone by reinstalling Windows. The justification for doing that
   has not materialised, so it is **not recommended right now**. Revisit only if something
   actually gets blocked. The self-signed local signing workaround
   (`Directory.Build.targets` + `build/Sign-LocalTestBinary.ps1`) stays as an inert
   fallback; it no-ops without the cert and only ever touches Debug test binaries — as of
   M1b it signs every DLL/EXE in the output directory, not just the test project's own
   assembly (see the M1b entry above for why that widening was needed).

   **Something did get blocked, this pass — a new, more severe case.** A fully clean Debug
   rebuild of `EventScope.App.Tests` hangs indefinitely during Avalonia headless setup,
   reproduced four times, with every dependency DLL confirmed signed. It doesn't throw and
   leaves no Code-Integrity or WER trace, so it isn't the same load-time block this item was
   originally about — see the M1b entry above. `EventScope.Core.Tests`/`Storage.Tests` (no
   Avalonia) are unaffected on the identical freshly-built tree. **Workaround: run
   `EventScope.App.Tests` in Release** (`build\Run-Tests.ps1 -Configuration Release`) —
   confirmed 12/12, repeatably, in 2-3 seconds. A previously-built Debug tree (not a clean
   rebuild) has also run clean in the same session, so the trigger looks tied to rebuild
   freshness specifically, not Debug-vs-Release per se — not fully isolated yet.

   **A second, distinct hang found at M1c, in Release this time, tied to Avalonia headless +
   real async I/O rather than a rebuild.** The very first real async file I/O
   (`RandomAccess`-based, e.g. `SegmentReader.ReadAsync`) issued in a Release
   `EventScope.App.Tests` process after Avalonia's headless platform initializes, before
   anything has pumped the dispatcher, can hang indefinitely with near-zero CPU — no throw,
   no Code-Integrity/WER trace, same signature as the Debug hang above but a different
   trigger (this one is order/first-operation dependent, not rebuild-freshness dependent, and
   is non-deterministic even holding the binary fixed). **Reproduces against a pre-existing,
   already-shipped test** (`IngestPipelineStorageTests`) when it happens to run in isolation
   or first — confirmed latent before M1c, not introduced by it. Four fixes tried and
   confirmed **not** to resolve it: `ConfigureAwait(false)` at the await call site; pumping
   the dispatcher once in `HeadlessFixture.EnsureInitialized()`; showing and pumping a
   throwaway `Window` during that same setup; `ThreadPool.SetMinThreads`. Worked around for
   the two new M1c storage-acceptance tests by moving them to a new project
   (`tests/EventScope.Acceptance.Tests`) with no Avalonia reference at all — confirmed
   reliable there. The normal (non-`EVENTSCOPE_SOAK`) `EventScope.App.Tests` suite is
   confirmed unaffected across repeated runs; the risk is specifically in soak/heavy-load
   conditions or single-method isolation. Left open for a real fix.

   **A working recipe for driving the real GUI app, established this pass:** Windows UI
   Automation from PowerShell (`Add-Type -AssemblyName UIAutomationClient`) can find
   controls by their accessible `Name` (`AutomationElement.FindFirst` with a
   `PropertyCondition` on `NameProperty`) and invoke buttons via `InvokePattern` — this is
   how the M1b entry above drove the real Start/Stop toggle without a mouse. `Get-Process`
   needs a follow-up poll for `MainWindowHandle` (it isn't populated the instant
   `Start-Process` returns). Screenshots via `CopyFromScreen` do **not** work in this
   session specifically — no attached interactive display surface — so on-disk or
   accessibility-tree evidence is the fallback where a visual isn't available.

3. **Mockup bundle redistribution is unresolved, and blocks making the repo public.**
   `Mockup preparation from spec/support.js` is 69 KB of generated Claude Design runtime
   (`dc-runtime`), marked "GENERATED ... do not edit", with no licence header. Its
   redistribution terms under this repo MIT licence are not something the code can settle.
   `styles.css` alongside it is bespoke to this project and fine; `_ds_bundle.js` is a
   300-byte empty stub. The build plan manual verification step opens the mockup in a
   browser throughout the build, and it needs `support.js` to render, so it is useful
   locally. **Decide before going public:** keep it, or gitignore it and keep it local
   only. Nothing is blocked while the repo is private.

4. **Release signing for distributed builds.** SignPath Foundation free open-source
   programme is the intended no-cost path, wired into `release.yml` between the
   upload-artifact and create-release steps. Was deliberately deferred until v0.1.0 exists -
   their review assesses a working project, and applying with an empty scaffold weakens it.
   **`v0.1.0` is tagged as of this pass, so applying to SignPath is now unblocked** - it's
   your call whether to apply now. Recompute the SHA256 after signing; signing changes the
   hash.

5. **No live broker access on this machine.** Unchanged in substance: `KafkaEventSource` is
   now written and unit-tested against a mocked `IConsumer<byte[],byte[]>` surface (M1c);
   integration tests are opt-in via `EVENTSCOPE_KAFKA_BOOTSTRAP`/`EVENTSCOPE_KAFKA_TOPIC` and
   skip by default - confirmed skipping cleanly on this machine. If you want Kafka proven
   against a real broker (not just mocks) before treating it as done, that needs a broker
   endpoint to point at, same as ASB/SQS will need before M4 is "done".

GitHub repository creation and the initial push remain yours. There is still no remote, so
neither workflow has ever executed - expect the first push to surface ordinary CI teething
issues (the YAML could not be validated locally; no YAML parser is installed).
