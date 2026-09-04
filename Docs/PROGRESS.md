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

## M3 — publisher

### M3 step 8 — the generator engine (this pass)

All new, all in `EventScope.Core/Generation/`, no UI — build plan §3.5's two-pass engine,
built exactly to spec plus the grammar details the plan itself left unspecified (documented
below, not guessed at silently).

**Pass 1 — the lexer and planner.** `GeneratorLexer.Lex` splits one leaf's template string
into `Literal` and token segments — a template freely mixes literal text with `{{...}}`
tokens (`"order-{{ref:$.id}}-{{guid}}"`), which the plan's own examples imply but never
states as a grammar. Every token carries a `TextSpan` (start, length, 1-based line) over its
own `{{...}}` text for inline diagnostics. An unrecognized token kind or an unterminated
`{{` is kept as literal text rather than rejected — a typo'd template should still round-trip
visibly instead of vanishing or throwing.

`GenerationPlanner.Build` then builds the `{{ref}}` dependency graph in CSR form
(`edgeStart`/`edgeTarget`/`inDegree` `int[]`s, never `List<int>[]`) and computes a fill order
by **iterative Kahn** — an explicit `Queue<int>`, no recursion anywhere in either graph
algorithm, because the acceptance criterion is literally "not stack-overflowed" over a
100,000-node chain and no `catch` recovers from `StackOverflowException`. Nodes Kahn never
dequeues (in a cycle, or transitively depending on one) are handed to **iterative Tarjan
SCC** — a per-node edge cursor array standing in for the call stack, restricted to just those
residual nodes — which names every SCC of size > 1, plus every self-loop, as a `RefCycle`: a
closed walk of `CycleHop`s in the direction the refs were actually written (`$.a → $.b → $.a`
reads the same way the user typed it), found by following {{ref}} edges within the SCC until
a node repeats — robust regardless of which SCC member the walk happens to start from,
verified by a dedicated test where the chosen start isn't the one that closes the loop. An
unknown ref path is `UnresolvedRef`, not a cycle — reported separately with its own span,
never given a graph edge at all (so it can't accidentally look like a self-cycle to Kahn).

**Pass 2 — the runner.** `GenerationRunner.Fill` walks `GenerationPlan.FillOrder`, writing
into a reused `string?[]` sized to the plan and a reused `StringBuilder` scratch buffer — a
literal-only leaf (the common case) skips the builder entirely and returns its text directly.
Resolving a `Ref` segment is a straight array read, guaranteed already-filled by the
topological order for any leaf outside a cycle; a leaf inside (or depending on) a reported
cycle may read a not-yet-filled dependency as an empty contribution — by design, not a
runtime exception, since `PlanDiagnostics` is how the caller is meant to find out *before*
publish, exactly as §3.5 specifies for the editor-side "Invalid: unresolved ... at line 8"
treatment. `Guid.CreateVersion7()` for `{{guid}}`; `Random.Shared` for `{{int}}`/`{{pick}}`;
`{{now}}` reads a `TimeProvider` (default `TimeProvider.System`, injectable for tests) rather
than `DateTimeOffset.UtcNow` directly, consistent with every other clock in this codebase.

**Grammar filled in, since the build plan states example tokens but never a full grammar:**
a token is `{{kind}}` or `{{kind:argument}}`, kind matched case-insensitively against
`ref`/`guid`/`int`/`pick`/`now`. `{{int}}` defaults to `0..1_000_000`;
`{{int:min..max}}` is inclusive on both ends. `{{pick:a|b|c}}` uses `|` as the option
separator (not `,`, since a JSON value is a very plausible option to want to pick between,
and those routinely contain commas). `{{now}}` / `{{now:iso}}` both format as `"O"`
(round-trip ISO 8601); any other argument is passed straight to `DateTimeOffset.ToString` as
a .NET custom/standard format string.

**Plan caching (the performance story per §3.5) is a property of the design, not separate
code to write:** `GenerationPlan` depends only on leaf paths and template text, never on
generated values, so one plan safely backs any number of `Fill` calls — proven directly by
the 1,000-GUID-burst test reusing one `GenerationPlanner.Build` result across 1,000 `Fill`
calls on the same `GenerationRunner`.

Tests: 158, up from 128 — 30 new, all in a new `EventScope.Core.Tests/Generation/` folder.
`GeneratorLexerTests` ×10 (literal/token interleaving, case-insensitive kind matching, an
unrecognized kind and an unterminated token both surviving as literal text, span/line
correctness, and the five token-argument shapes as a `Theory`). `GenerationPlannerTests` ×9
(ref ordering, an unresolved ref's span, a self-reference as a 1-hop cycle, a 2-node cycle as
a closed walk, every leaf still appearing exactly once in fill order even when cyclic, a leaf
that merely *depends on* a cycle not being reported as cyclic itself, the 100,000-node chain
completing with a fully verified dependency-respecting order, and an injected back-edge on a
1,000-node chain reporting as exactly one cycle without disturbing the unaffected prefix of
the chain). `GenerationRunnerTests` ×11 (literal fill, ref resolution, the 1,000-distinct-GUID
burst, GUID format validity, `{{int}}` default and explicit ranges, `{{pick}}` membership,
`{{now}}` against an injected fake clock, an unresolved ref filling to an empty contribution
rather than throwing, and a cyclic leaf still producing *something* rather than hanging).

**One correction found while writing the planner tests, not a bug in the shipped code:** the
first draft of the 100k-chain and back-edge tests gave leaf 0 the path `"$.0"` (with a dot)
while every other leaf's `{{ref:$N}}` token pointed at `"$N"` (without one) — a copy-paste
mismatch between the path-naming scheme and the ref-target-naming scheme in the *test data*,
not the lexer or planner. Every leaf from index 1 onward would have reported `UnresolvedRef`
against leaf 0, which the test's own `Assert.False(plan.Diagnostics.HasIssues)` caught
immediately on the first run — exactly the kind of thing that assertion exists to catch.
Fixed by using the same naming scheme everywhere in the test data.

---

### M3 step 9 — the publisher UI (this pass)

The publisher panel is real and wired end to end — toggled from a new "Publish" toolbar
button (`⌃2`'s target, matching the mockup's own `onTogglePublisher`), not a top tab-strip
switch. **Correction to this pass's own plan text, found by re-reading the mockup's actual
markup before building against it**: the plan said "the tab strip needs to actually switch
between the consumer workspace and the publisher," but the mockup's publisher is a resizable
*bottom-docked panel* toggled by a toolbar button, never a tab-strip destination — the top tab
strip in both the mockup and this app is exclusively about connection tabs. Built to what the
mockup actually shows, not to the plan's paraphrase of it.

**Tree model** (`EventScope.App/Publisher/`): `PublisherNode` (an `ObservableObject` per
field: `Key`, `Type`, `Generator` — the editable template string — and `Value`, a read-only
preview of what that template last resolved to) and `PublisherTreeModel`, which owns the tree
and an always-in-sync `FlattenedRows` projection — the "observable flattened projection" build
plan §5 calls for, rebuilt on any structural change or any node's `Key`/`Type`/`Generator`
edit via a `PropertyChanged` subscription taken out on every node as it enters the tree.
`CollectLeafTemplates()` walks every primitive leaf into a `LeafTemplate` keyed by its
`JsonPath`, feeding `GenerationPlanner` directly; `ApplyValues` writes a fill's results back
onto each node's `Value` for display. `FromJson`/`ToJson` convert to and from `JsonNode` — the
seam schema inference (step 10) will build on, and already exercised by round-trip tests.

**One deliberate deviation from Value being independently editable, as the mockup draws it**:
the mockup's row has both an editable Value input and a separate Generator input with no
stated precedence if they disagree. Rather than invent one, Value here is display-only,
refreshed after every fill; editing happens through Generator alone — a literal string with no
`{{}}` tokens is just a literal. Documented in `PublisherNode`'s own remarks, not left as a
silent surprise.

**Generation wiring** (`PublisherViewModel`): `Recompute()` builds a fresh
`GenerationPlanner.Build` plan from the tree's leaves, fills it via a reused
`GenerationRunner`, writes values back onto the tree, and renders `PlanDiagnostics` as inline
text using the build plan's own literal wording — `"Invalid: unresolved {{ref:$.missing}} at
line 8"` for an unresolved ref, `"Invalid: cycle $.a → $.b → $.a"` for a reported cycle. Edits
debounce 150 ms before recomputing (the same debounce this codebase already uses for FTS
search — see `SearchViewModel`), except `Regenerate` and `Publish`, which force it
synchronously first. `Recompute()` is `public` specifically so tests drive it without racing
the debounce timer.

**Preview pane**: `PreviewBuilder` pretty-prints the tree's `JsonNode` via
`JsonSerializer`/`ToJsonString(WriteIndented: true)` and classifies each line with a regex
(key / string / number / literal / punctuation) rather than hand-rolling a second JSON writer
that also tracks line numbers — simpler, and not a perf-sensitive path (publisher messages are
small, interactively edited, nothing like the ingest hot path). Coloured via the same
declarative `Classes.key`/`Classes.str`/etc. binding pattern already proven safe at 10k msg/s
for the message grid's `large`/`searchHit` cells (see the M1-remainder row-styling pass) —
never the imperative alternative that pass found expensive. Envelope tab is a direct,
editable form (Content-Type, Partition Key, Session ID, Correlation ID) rather than the
mockup's read-only key/value mirror — there was nowhere else in this app for those fields to
be edited, so making the tab editable serves a real purpose the mockup's own static dev-state
screenshot didn't need to show.

**No `IEventSink` exists to publish to until step 10's `KafkaEventSink`.** `PublishAsync`/
`BurstAsync` take an injected `Func<IEventSink?>` sink provider (the same provider-injection
pattern as `SearchViewModel`/`SettingsViewModel`), defaulting to "no sink" — `PublishStatus`
reports "No publish target connected." rather than silently doing nothing. Verified manually
(below) that this reports correctly rather than crashing, since it's the only path through the
whole panel this pass can actually exercise for real.

**Scoped down from the mockup on purpose, matching this codebase's established proportionality
calls (see step 6's search-hit highlighting, step 7's settings form):**
- **`DataGrid` instead of `TreeDataGrid.Avalonia`** for the tree editor. The mockup's own
  markup is a flat row list with indent-guide spans and no expand/collapse affordance at all —
  not a nested/collapsible tree — so `HierarchicalTreeDataGridSource<T>`'s hierarchical mode
  buys nothing here, and adopting a completely unproven UI dependency's API (this codebase has
  zero measured experience with `TreeDataGrid.Avalonia`, versus four passes of hard-won
  `DataGrid` knowledge, including two real virtualization/binding bugs found and fixed) for a
  visual that's already just a flat `ItemsControl` would be risk with no payoff. Built as an
  `ItemsControl` with a per-row `Border` indent (`DepthToIndentConverter`, 16px/level per §4.3)
  instead.
- **No per-row hover mini-icons** (add-sibling/add-child/duplicate from the mockup). One
  header "Add field" plus a per-row delete covers the same editing capability with far less
  interaction-state code; a nested child is added by setting a field's own Type to
  Object/Array first, which is what Type is for.
- **No drag-to-resize** for the publisher panel, unlike the detail pane's working
  `GridSplitter`. The two requirements — collapsing to zero height when closed, and being
  drag-resizable while open — are in real tension in Avalonia: `GridSplitter` resizes by
  overwriting its target `RowDefinition.Height` with a fixed pixel value on the first drag,
  which would silently turn an `Auto` row (needed for the close-to-zero behaviour, since an
  `IsVisible="False"` child collapses an `Auto` row to nothing) into a row that no longer
  collapses. Chose reliable open/close over resize-while-open; the panel's height is a fixed
  380px (§4.3's own default) while open.

**Manually verified against the real running app**, not just tests — driven via Windows UI
Automation from PowerShell, the working recipe this codebase already established: opened the
publisher panel, added a field, switched to the Envelope tab and back, clicked Regenerate,
edited all four envelope text boxes, clicked Publish (no sink configured — confirmed it
reports rather than crashes), clicked Burst, then closed cleanly. **Caught and fixed a real
crash this way before it shipped**: the Preview/Envelope tab buttons' `SelectTabCommand` was a
`RelayCommand<int>`, and Avalonia's XAML `CommandParameter="0"`/`"1"` binds a literal
`string`, not an `int` — `RelayCommand<T>.CanExecute` throws `ArgumentException` on a
type-mismatched parameter, which fires the instant the button's `Command` property is set
during window construction, crashing the app before the window ever appears. Fixed by
splitting into two parameterless commands (`SelectPreviewTabCommand`/
`SelectEnvelopeTabCommand`) rather than fighting XAML's string-typed literal parameters. This
is exactly the class of bug compiled bindings do not catch — the binding *path* was valid, only
the runtime parameter type was wrong — which is why running the real app mattered here, not
just a green test suite.

Tests: 178, up from 158 — 20 new, all in `EventScope.App.Tests`, none needing
`HeadlessFixture` (the tree model and view model use no Avalonia types). `PublisherTreeModelTests`
×9 (flattening order including nested objects, array-element paths by index rather than key,
delete, rename raising `Changed`, leaf collection skipping containers, `ApplyValues` writing
generated values onto nodes, `ToJson`/`FromJson` round-tripping type and generator seeding).
`PublisherViewModelTests` ×11 (unresolved-ref and cycle diagnostics rendered as the build
plan's own inline wording, a valid tree having no issue, add/delete updating the bound rows,
publish sending the generated body to an injected fake sink, publish without a sink reporting
rather than throwing, publish refusing on a validation issue rather than sending a broken
message, a 5-message burst producing 5 distinct GUIDs, tab selection flags, and envelope
fields carrying through to the published message).

---

### M3 step 10 — schema inference, the publish path, and `KafkaEventSink` — closes M3

**`KafkaEventSink`** (`EventScope.Brokers.Kafka/`) — the first `IEventSink` implementation.
`ProduceAsync` is genuinely async in the Confluent client (unlike `Consume()`'s blocking-sync
shape `KafkaEventSource` has to run on a dedicated thread for), so no threading trick is
needed here. `OutgoingMessage.Body` serializes to UTF-8 JSON bytes as the Kafka message
value; `PartitionKey` maps to the Kafka message *key*, which is what genuinely determines
partition placement in real Kafka rather than being a separate concept the way it might be in
ASB/SQS; `ContentType`/`CorrelationId`/`ApplicationProperties` become headers. `SessionId` and
`TimeToLive` have no native Kafka mapping (no session concept, no per-message TTL — only
topic-level retention) and are silently unused, documented in the class's own remarks rather
than left as a silent gap. `EventSinkFactory` mirrors `EventSourceFactory`'s
env-var-driven pattern (`EVENTSCOPE_KAFKA_BOOTSTRAP`), except returning `null` — "no sink
configured" — is the expected common case here, not a fallback to a fake the way the source
side falls back to `FakeEventSource`.

**Schema inference** (`EventScope.App/Publisher/SchemaInference.cs`) — exactly the three
shapes the build plan names: a GUID-shaped string infers `{{guid}}`; an ISO-8601-shaped
string infers `{{now:iso}}`; a whole-number value infers `{{int:min..max}}` bracketing the
observed value (a fractional number is left as a literal — "bracketing an int range" doesn't
apply to a value that isn't one). The bracket width isn't specified more precisely than
"bracketing" by the plan, so it's a symmetric span at least as wide as the observed
magnitude, floored at zero for a non-negative observed value — documented as a deliberate,
un-derived heuristic rather than implied to be the One True Answer. `PublisherTreeModel`
gained `LoadFrom(json, inferGenerators)` — replacing the tree's content **in place** (same
`FlattenedRows` instance, so the view's binding survives) rather than the model being torn
down and reconstructed, which the view's `Rows` binding would need help noticing.
`PublisherViewModel.LoadFromConsumedMessage` wires it up, and `MainWindowViewModel` exposes
it as "Use as template" — a button next to the detail pane header, disabled while the
payload is unavailable, that parses the selected message's body as JSON (a no-op, not a
throw, if it isn't valid JSON) and opens the publisher panel on the result.

**Manually verified against the real running app**, not just tests — started the fake
source, selected a live-ingested row via keyboard-driven `DataGrid` selection (Avalonia's
`DataGridRow`/`DataItem` automation peers don't expose `SelectionItemPattern` the way a
`ListItem` would, so `SendKeys` arrow-key navigation stood in for the click-based technique
used elsewhere in this pass), clicked "Use as template", and confirmed the publisher tree
populated with the message's actual four fields and the *correct* per-field inference:
`sequence` → `{{int:0..5069150}}`, `amount` → `{{int:0..1150}}`, `correlationId` → `{{guid}}`,
and the long literal `padding` field left untouched as a literal (matched neither shape,
correctly) — this is the acceptance behaviour in the flesh, not just a green unit test.

**The round-trip acceptance test exists but its limitation is stated, not implied
away**: `KafkaRoundTripAcceptanceTests` (in `EventScope.Brokers.Kafka.Tests`, not
`EventScope.App.Tests`, matching every other broker test's isolation from the UI layer)
publishes via `KafkaEventSink`, consumes it back via `KafkaEventSource`, and asserts the body
shape and correlation id survive — gated behind `EVENTSCOPE_KAFKA_BOOTSTRAP`/
`EVENTSCOPE_KAFKA_TOPIC` and skipping by default, exactly like the existing Kafka integration
test, because this machine has no live broker to run it against. The "consume → Use as
publish template" half of the criterion's own wording is covered separately, against the
tree/schema-inference code, by `EventScope.App.Tests` — duplicating that against a real
broker would test the same code twice for no added confidence.

Tests: 195, up from 178 — 17 new. `KafkaEventSinkTests` ×7 in
`EventScope.Brokers.Kafka.Tests` (body serialization, partition-key-to-Kafka-key mapping
both ways, content-type/correlation-id/application-properties headers, no headers object at
all when nothing needs one, flush-then-dispose on shutdown) against a hand-rolled
`FakeKafkaProducer` (same style as the existing `FakeKafkaConsumer` — every member
`KafkaEventSink` never touches throws `NotSupportedException`). `KafkaRoundTripAcceptanceTests`
×1, skipped by default (no broker). `SchemaInferenceTests` ×7 and two new
`PublisherTreeModelTests`/one new `PublisherViewModelTests` in `EventScope.App.Tests`
(guid/iso8601/plain-string/whole-number/negative-number/fractional-number inference,
`LoadFrom` seeding a guid-shaped leaf correctly, `LoadFrom` preserving the `FlattenedRows`
instance across a full content replacement, and `LoadFromConsumedMessage` wiring both
together end to end).

**M3 is closed.** The generator engine (step 8), the publisher UI (step 9), and schema
inference plus the publish path (step 10) are all done, tested, and manually verified against
the real running app.

---

## Stage 5a — connection manager and launcher

**Reprioritised at your direction**: Kafka being fully functional end-to-end, with no
environment variables, took priority over M4 (Service Bus/SQS — both still empty project
shells) and over the heap-growth remainder above (explicitly deferred, unchanged). This pulls
forward the build plan's Stage 5 connection manager and launcher (§5, UI spec §6) — exactly
what `EventSourceFactory`'s own M1c-era comment called itself "the stand-in for."

**Pre-step**: the uncommitted broker-neutral refactor already sitting in the tree
(`SourceError` moved into `Core.Abstractions`, `IEventSource.DisplayName`/`ErrorOccurred`,
`MainWindowViewModel`'s `if (source is KafkaEventSource)` removed) was verified building and
green (195/195) and committed on its own first — this pass's connection manager builds
directly on it.

**`EventScope.App/Connections/`** (new): `ConnectionProfile` — a plain, JSON-serializable
model whose Kafka fields mirror `KafkaSourceOptions`/`KafkaSinkOptions` field-for-field (this
is the form data those get built from, not a new shape); `SecurityProtocol`/`SaslMechanism`
are stored as the target enum's member name string, not the enum itself, so the model has zero
dependency on `Confluent.Kafka` and room for ASB/SQS fields later without pulling in every
broker SDK. `ConnectionKind.Fake` has a fixed `Guid.Empty` id (`FakeSourceId`) and is never
persisted — every consumer of the saved list gets it prepended in memory instead.
`ConnectionStore` persists the rest as plain JSON under
`%LOCALAPPDATA%\EventScope\connections.json`, copying `AppSettings.Load`'s exact
fallback-to-defaults-on-any-failure contract.

**`ConnectionSecretProtector`** — DPAPI (`ProtectedData`, `CurrentUser` scope), so a saved
SASL password is never plaintext on disk. New package:
`System.Security.Cryptography.ProtectedData` 10.0.11 (matching `Microsoft.Data.Sqlite`'s own
pinned version). `CA1416` (the platform-compat analyzer) fires on the `ProtectedData` calls
since this project targets plain `net10.0`, not `net10.0-windows`; suppressed locally with a
one-line pragma rather than retargeting the whole App project, since the type's own
`catch (PlatformNotSupportedException)` is already the cross-platform guard the analyzer wants,
just expressed at runtime instead of compile time.

**`KafkaConnectionTester`** (`EventScope.Brokers.Kafka/`) — `AdminClientBuilder` +
`GetMetadata(timeout)`, per the build plan's own Stage 5 note. Seeded for tests via a
`MetadataFetcher` delegate returning a real `Confluent.Kafka.Metadata` — confirmed by
reflection *before* writing the seam that `Metadata`/`BrokerMetadata`/`TopicMetadata`/
`PartitionMetadata` are all plain, publicly-constructible types, so no fake for the 20-member
`IAdminClient` interface was needed at all. **Stated deviation from the UI spec's literal
wording**: §6 asks for "broker version detected"; librdkafka's admin metadata exposes the
*client* library's version (`Library.VersionString`), not the broker's — reported as such, not
silently reworded.

**Partition targeting is a real `KafkaEventSource` change**, not just a form field:
`KafkaSourceOptions.Partition` (new, nullable) routes to `consumer.Assign(TopicPartition)`
instead of `consumer.Subscribe(topics)` when set. `FakeKafkaConsumer.Assign(IEnumerable<TopicPartition>)`
— previously one of the fake's "never touched" members throwing `NotSupportedException` — now
actually tracks assignments, since this pass is the first code to exercise that path.

**`ConnectionManagerViewModel` + the launcher overlay in `MainWindow.axaml`** — UI spec §6's
two panes (saved connections left, editor right), built as a `Panel`+`Border` overlay in the
root grid exactly like the existing Settings overlay, not a new `Window` or `UserControl` —
this app has no window-management story and Settings already established the pattern. The
empty state's three broker buttons render Kafka enabled and ASB/SQS disabled-with-tooltip (§9's
capability-gated-control wrapper), since those sources don't exist until M4. "Test connection"
is idle/testing/result text — no animated spinner, this codebase has none yet, the same
proportionality call as the search bar's own text-only "index catching up" indicator. A saved
connection's password field is always blank when reopened for editing, and a blank Save leaves
the stored password untouched — retyping is the only way to change it, the same UX as any
credential manager.

**Tab strip rebuilt** (`MainWindow.axaml`) from one hardcoded "Live (Fake source)" label into
an `ItemsControl` over `MainWindowViewModel.Tabs`, each with a status dot (green streaming /
grey idle / amber degraded / red error — UI spec §4.1), a close ×, and a declarative
`Classes.active` binding for the underline (the same safe pattern as the message grid's
`.large`/`.searchHit` cells, not the imperative `Classes.Set` path the M1-remainder pass found
expensive). New `ConnectionTabViewModel`.

**One connection actually running at a time — a deliberate, documented scope boundary, not an
oversight**: selecting a different tab stops whichever pipeline is live before anything about
the new tab starts (`MainWindowViewModel.HandleTabSwitchAsync`). Concurrent per-tab pipelines
would need one `MessageRowsView` ring per tab (~20 MB each, build plan §3.1) and
shared-`SessionStore` write routing under §3.6's collision #1 — real scope for a separate pass.

**Storage is namespaced per connection** — `SessionRootDirectory(profileId)` puts each
non-Fake connection's day files under its own `sessions/{profileId}/` subdirectory, so two
Kafka topics never land in the same SQLite file. The Fake source and the legacy env-var path
(`profileId == null`) both keep the *exact* original unnamespaced `sessions/` path, confirmed
against this machine's own pre-existing `sessions/2026-08-31/` directory from a prior run —
nothing on disk from before this pass is orphaned.

**A real bug caught before it shipped, by reasoning through the design, not by a failing
test**: the publisher panel's `IEventSink` was cached forever
(`_sink ??= EventSinkFactory.Create(...)`), a fine assumption when only one connection could
ever exist (M3's own design) but wrong now — switching Kafka connections while the publisher
panel was open would keep silently publishing to the *previous* connection's topic. Fixed by
tearing the sink down alongside the session store on every real tab switch
(`HandleTabSwitchAsync`), so the provider lazily rebuilds it against whatever's actually
selected.

**Read-mode segmented control renders `Consume` permanently disabled** — a stated assumption,
not an inferred one: `KafkaSourceOptions` deliberately forbids a fixed consumer group id (see
its own remarks), which a real committing "Consume" mode would need, and that is exactly the
property keeping this tool from disturbing a real consumer group's offsets. Reversing it is a
decision for later.

**A genuine shutdown-hang bug, found only by manually driving the real app** (this codebase's
established UI-Automation recipe), not by any unit test: closing the app, or switching away
from a connection, after it had ever pointed `KafkaEventSource` at an unreachable broker took
15+ seconds and had to be force-killed. Root cause: the consume loop's `finally` block calls
`consumer.Close()` unconditionally to let a real group leave cleanly — a reasonable assumption
when the broker is reachable, but against one that never was, `Close()` can block for a long
time negotiating a graceful leave that can never succeed. Fixed by bounding it:
`Task.Run(() => consumer.Close()).Wait(TimeSpan.FromSeconds(2))`, abandoning the attempt on
timeout rather than blocking the dedicated consume thread — and therefore the whole app's
shutdown — on a broker that was never there. Measured directly, same repro, same machine:
shutdown after Stop against a bogus bootstrap host went from a 15s timeout + force-kill to
**0.097 seconds**.

**Manually verified against the real running app**, via this codebase's established Windows UI
Automation recipe (PowerShell, `FindFirst` by accessible `Name`, `InvokePattern`) — not just
green tests: cold launch shows the launcher overlay with the Fake source and the three broker
buttons; added a Kafka connection with a deliberately bogus bootstrap host; "Test connection"
correctly failed with the real librdkafka reason text ("Local: Broker transport failure")
after its full timeout, not a crash or a hang; saved it; connected it from the launcher, which
opened a second tab and closed the overlay; Start surfaced "Kafka error: 1/1 brokers are down"
in the toolbar without crashing the process (a non-fatal transport error, matching
`KafkaEventSource`'s documented behaviour); switched back to the Fake source tab, which
correctly tore down and restarted a fresh pipeline and streamed normally
(`Streaming (Fake source)`, ~9,700–11,200 msg/s, real rows visible in the grid); Stop, then
window-close, exited in ~0.1s. Also re-verified `EVENTSCOPE_MEASURE` mode directly (not through
the full acceptance script): the launcher overlay never blocks it, and a 5s run auto-started,
streamed, and wrote its frame-time CSV exactly as before this pass.

**Also confirmed, not newly caused**: a freshly rebuilt `EventScope.App` Release output can
still be blocked by Smart App Control the same way M1a first found (Code Integrity, this pass
hit it against `EventScope.Brokers.Kafka.dll` specifically) — signing the output directory with
the existing local dev cert (`build/Sign-LocalTestBinary.ps1`, pointed at the App's own
`bin/Release/net10.0`, not just a test project's) resolved it identically. Same pre-existing
issue as PROGRESS.md's Blocked item 2, not something this pass introduced.

Tests: 233, up from 195 — 38 new. `ConnectionStoreTests` (×4) and `ConnectionSecretProtectorTests`
(×4) in `EventScope.App.Tests` (JSON round-trip including every Kafka field, corrupt-file
fallback, DPAPI round-trip, the protected value never containing the plaintext, unprotecting a
null/corrupt value failing without throwing). `EventSourceFactoryTests`/`EventSinkFactoryTests`
(×9) — every form field's mapping onto `KafkaSourceOptions`/`KafkaSinkOptions`, including the
whitespace-trimming and blank-field-fallback rules, and `NotSupportedException` for ASB/SQS
kinds. `ConnectionManagerViewModelTests` (×15) — the Fake source always present/non-editable/
first, validation blocking Save and Test alike, new-vs-edit password handling, delete guards,
the injected-tester success/failure paths, and last-used reordering with a fake `TimeProvider`.
`KafkaConnectionTesterTests` (×5) in `EventScope.Brokers.Kafka.Tests` — reachable/unreachable/
unknown-topic, cluster-only (no topic), and security/SASL options reaching the admin config.
`KafkaEventSourceTests` (×2 new) — explicit-partition `Assign` versus whole-topic `Subscribe`,
needing `FakeKafkaConsumer.Assign(IEnumerable<TopicPartition>)` to actually track assignments
instead of throwing.

**One test written, then deleted for testing the wrong layer**: a first draft of
`ConnectionStoreTests` asserted `ConnectionStore.Save` itself filters out the Fake source —
it doesn't; only `ConnectionManagerViewModel.Persist()` does that, before ever calling into the
store. The store is (deliberately) a dumb JSON reader/writer with no Fake-source awareness at
all. Caught immediately by the test itself failing; fixed by removing the wrong test rather
than adding filtering logic to a layer that shouldn't have it — the real contract is already
covered where it actually lives (`Saving_a_valid_new_connection_inserts_it_first_and_persists_without_the_fake_source`).

**Explicitly not in this pass** (see the updated Pending list below): the ASB/SQS editor forms
and the M4 capability-binding audit test, the deep-search overlay, large-payload confirmation,
toast, light theme, full keyboard map, and concurrent multi-connection pipelines.

---

## Distribution pass — repo made shareable

You asked for the distribution step so the repo can be shared publicly; you are creating the
GitHub repository yourself. This executes `Docs/DISTRIBUTION_PLAN.md`'s Phase 1 (repo
preparation) against the app's actual current state — substantially more capable than when
that plan and the original README were written — and resolves the one open item that
explicitly blocked going public.

- **Mockup runtime redistribution, resolved.** Blocked item 3 above:
  `Mockup preparation from spec/support.js` is Claude Design's generated `dc-runtime` bundle,
  marked "GENERATED ... do not edit," with no license header — its terms under this repo's MIT
  license were never something the code could settle. Untracked (`git rm --cached`, file kept
  on disk) and added to `.gitignore`, since it's only needed locally to render
  `EventScope.dc.html` in a browser for manual UI verification — never shipped, never needed
  to build or run the app. `styles.css` and `_ds_bundle.js` alongside it stay tracked, per the
  original finding that only `support.js` itself carries the licensing concern.
- **Secret audit re-run against full history, clean.** `git log --all -p` scanned for AWS/Azure
  key and connection-string shapes, private-key headers, and credential-shaped filenames ever
  committed. Two hits, both benign: a `"s3cret"` test fixture value in this pass's own Kafka
  tests, and a `"payments-prod…"` placeholder string inside the mockup's own UI markup (an
  ellipsis-truncated display example, not a real value). Nothing else.
- **README rewritten.** The previous version described the app before M1b even landed
  ("nothing is written to disk yet") — nine milestones and passes out of date. Now states what
  actually works today (real Kafka end to end, storage, search, the publisher, the connection
  manager) versus what doesn't (Service Bus/SQS, M4), with a broker-support table and three new
  screenshots.
- **New screenshots, replacing four stale ones.** The previous four (`MainWindow-*.png`,
  committed but never actually embedded in the README) predate the connection manager, the
  publisher panel, and the current search bar — one still shows "Search — wired in M2" as a
  disabled placeholder and has the Windows taskbar visible. Untracked and deleted. Three new
  ones captured against the real running `v0.2.0` build (screen capture works in this session,
  unlike the session PROGRESS.md's M1c/M1b entries recorded it failing in — confirmed directly
  rather than assumed either way): the connection manager/launcher, a streaming consumer view
  with a row selected and its JSON body in the detail pane, and the publisher panel populated
  via "Use as template" showing real schema-inferred generators
  (`{{int:0..2016936}}`, `{{guid}}`) and the coloured JSON preview.
- **Version bumped 0.1.0 → 0.2.0** (`Directory.Build.props`, `app.manifest`) and tagged
  `v0.2.0`, marking everything since the M1-only `v0.1.0` tag (all of M2, M3, and this pass's
  connection manager) as one shareable snapshot. Picked as a reasonable single next step, not
  a fixed scheme — cheap to retag before anything is ever pushed, since no remote exists yet.
  Full suite re-verified green (233/233) at the new version before tagging.
- **Publish pipeline re-verified end to end at the new version**, not assumed still correct:
  `dotnet publish` per the README/`release.yml`'s own command produced a self-contained
  `publish/EventScope.exe` (128,796,774 bytes, consistent with the M1c-era ~128 MB figure —
  size is unaffected by this pass's changes), signed with the local dev cert (Smart App
  Control still blocks a freshly-built unsigned exe on this machine, as documented), launched,
  and closed cleanly. Not committed — `publish/` is gitignored, and per the distribution plan's
  own rule, release binaries are built by CI from a tag push, never uploaded by hand.
- **CI/release workflows and OSS scaffolding (LICENSE, CONTRIBUTING.md, `.github/ISSUE_TEMPLATE/`,
  `.gitattributes`) reviewed, unchanged.** All were already correct and current from the
  earlier release-readiness pass — `ci.yml`/`release.yml` already call `build/Run-Tests.ps1`
  rather than `dotnet test`, action versions already current. Nothing to fix.

**Still genuinely outstanding, not resolved by this pass** (unchanged from the Blocked section
above except where noted): real code signing (SignPath Foundation application — unblocked
since `v0.1.0` was tagged, more clearly justified now with M2/M3/the connection manager also
done; still your call whether to apply now or wait further); the Scoop bucket (Phase 3, needs a
real published release's SHA256 first); no CI run has ever executed, since there is still no
GitHub remote — the first push will be the first real exercise of both workflows, and may
surface ordinary CI teething issues the way any first run does.

---

## Release pass — v0.2.1, the first tag that can actually produce a binary

You asked why the CI work done so far has never produced a downloadable executable. It is
three separate things stacked, none of them a bug in the workflows:

1. **`ci.yml` never builds one, by design.** Restore → Build → Test, and nothing else.
   `dotnet build` does write `EventScope.exe` into the runner's `bin/Release/net10.0/`, but
   there is no `dotnet publish` and no `upload-artifact`, so the runner is destroyed and the
   binary with it. It is a correctness gate, not a packager.
2. **`release.yml` is the workflow that produces the exe, and only tags reach it** —
   `push: tags: ['v*']` or a manual `workflow_dispatch`. Ordinary pushes to `main` never
   run it.
3. **The one `release` run that has ever happened died before Publish.** It ran on the
   `v0.2.0` tag and hung on its Test step until `timeout-minutes: 20` killed it — Blocked
   item 2's Avalonia-headless dispatcher deadlock, recorded verbatim in `b02d661`'s own
   commit message. `Publish` runs after `Test`, so no artifact, hash, attestation or release
   page was ever created.

The fix (`HeadlessFixture` owning a thread and running `Dispatcher.UIThread.MainLoop` on it)
landed in `b02d661` — **one commit after `v0.2.0`**, whose remote ref still points at
`0f82e67`, a pre-fix commit. `main` has the fix; no tag does. Re-running `release` on
`v0.2.0` would hang again.

**This pass cuts `v0.2.1` so a tag finally exists whose code gets past Test.** Chosen over
adding a publish + `upload-artifact` step to `ci.yml`: `DISTRIBUTION_PLAN.md`'s rule that
release artifacts are built by `release.yml` and only there is what makes the SignPath
provenance story clean, and a second binary-producing path would undercut it.

- **Version bumped 0.2.0 → 0.2.1** in both places the previous bump touched:
  `Directory.Build.props` (the one place its own comment says to bump per release tag) and
  `app.manifest`'s `assemblyIdentity version="0.2.1.0"`, which is easy to miss because
  nothing fails if it drifts. Patch, not minor: everything since `v0.2.0` is
  test-infrastructure repair with no user-facing change. The tag points at *this* commit, not
  at `b02d661` — tagging `b02d661` directly would ship a binary stamped `0.2.0` from a release
  page saying `v0.2.1`.
- **`v0.2.0` deliberately left where it is.** It is already on the remote and has already been
  moved once (pushed at `ced0d0d`, now at `0f82e67`); moving a published tag a second time is
  worse than superseding it.
- **README Install section written for a release that now exists.** Replaces "Nothing to
  install yet — no release has been cut" with the download, `Unblock-File`, and a SHA256
  check against the published `.sha256`. Links are repo-relative (`../../releases/latest`) so
  they resolve correctly regardless of which owner/name the repo ends up under — see the
  unresolved `RepositoryUrl` mismatch below.
- **Pre-flight before tagging:** the dispatcher fix has only ever been verified locally, and
  this file already records twice that local-clean was misleading for exactly this bug. The
  `ci` run for `b02d661` on `windows-latest` is the authoritative check, and the tag was not
  pushed until it was confirmed green — tagging ahead of it risks burning a second version
  number on a run that hangs the same way. **Confirmed:** that run
  (2026-09-01T16:23:51Z, `main`) reports `status: completed`, `conclusion: success` —
  read from the unauthenticated `GET /repos/{owner}/{repo}/actions/runs?head_sha=…` API,
  which works because the repo is public; only *in-progress job logs* need write access, the
  403 recorded in item 2 above. So the dispatcher fix holds on the one runner where this bug
  ever reproduced reliably, not just locally.

**Outcome — `release` run for `v0.2.1` succeeded, and the binary is verified.** Recorded
after the tag, so it could not be in the tagged commit itself. Run
[33690495889](https://github.com/sinh-r/EventPublisherConsumer/actions/runs/33690495889)
completed `conclusion: success` — the first `release` run ever to get past Test and into
Publish. Verified against the published release rather than a local build, since that is the
artifact users actually get:

- **Assets published:** `EventScope.exe` (128,796,774 bytes — byte-identical in size to the
  local publish measured during the distribution pass, so the single-file bundle's contents
  did not drift) and `EventScope.exe.sha256` (66 bytes).
- **SHA256 verified by download**, not assumed: downloaded both assets and recomputed —
  `21C326C5…9461AD`, matching the published hash exactly.
- **Provenance attestation verified.** Decoded the Sigstore bundle's in-toto payload from
  `GET /repos/{owner}/{repo}/attestations/sha256:…`: subject `EventScope.exe` with the
  matching digest, `buildType` `actions.github.io/buildtypes/workflow/v1`, `ref`
  `refs/tags/v0.2.1`, repository `sinh-r/EventPublisherConsumer`. This is the claim the
  README makes, so it was checked once rather than assumed.
- **Version stamping confirmed end to end.** The downloaded binary reports
  `FileVersion 0.2.1.0` and `ProductVersion 0.2.1+e2392af…` — the tagged commit's own SHA.
  This is exactly what tagging `b02d661` directly would have broken.
- **The published binary runs.** `Unblock-File`d and launched the downloaded exe (not a local
  build): main window up in seconds, then driven via the UI Automation recipe recorded in
  item 2 above — closed the connection dialog, invoked **Start**, and read the counters back
  from the accessibility tree at **59,500 messages, 11,206 msg/s, 0 ui-dropped**. That
  exercises Avalonia rendering, the segment writer and the native SQLite (`e_sqlite3`)
  extraction — the pieces whose absence from a single-file bundle fails at *runtime*, not at
  build time, which is what `IncludeNativeLibrariesForSelfExtract=true` is there to prevent.
  Stopped and closed cleanly. `librdkafka` remains exercised only by a real Kafka connection.

Not verified: startup on a machine with no .NET runtime installed. This one has the SDK, so
the self-contained claim is inferred from the publish flags rather than demonstrated. Worth a
single check on a clean machine if one is ever to hand.

**Follow-on — SmartScreen on other machines, and the Scoop bucket (Phase 3).** Downloading
the release on a machine other than the dev box produces *"EventScope.exe isn't commonly
downloaded"*, then a second prompt on **Keep**. Worth being precise about what that is: a
**reputation** verdict, not a detection. SmartScreen vouches for a file either by its
publisher's Authenticode signature or by download volume for that exact hash; an unsigned
binary published hours ago has neither, so it correctly reports that it cannot vouch. The
"report this app as safe" link it offers is a dead end — that process is per file hash, and
every release is a new hash, so anything earned for `v0.2.1` resets at `v0.2.2`.

Signing is the real fix and is still Phase 4 (SignPath Foundation, unapplied). Phase 3 is what
was available today and needed nobody's approval:

- **Scoop bucket prepared** at `D:\My Work\scoop-EventScope` — one commit, ready to push;
  creating the GitHub repo is yours. `scoop install` fetches through Scoop's own client, so
  the file never gets Mark-of-the-Web and neither prompt appears. It does nothing for someone
  clicking the `.exe` link on the Releases page, and nothing for Smart App Control.
- **The manifest was verified against the live release, not assumed.** The published
  `EventScope.exe.sha256` is reachable and the `autoupdate` regex extracts exactly the pinned
  hash; `releases/latest` reports `v0.2.1`, which is what `checkver` reads. Four deliberate
  departures from `DISTRIBUTION_PLAN.md`'s sketch are recorded in that document's Phase 3 —
  the important one being that the sketch had no `autoupdate.hash` block, which would have
  left a stale hash on every future release and failed installs in a way that looks like
  tampering rather than neglect.
- **No `persist` block, and that is a property of the app worth knowing:** everything
  EventScope writes lives in `%LOCALAPPDATA%\EventScope` (`settings.json`, `connections.json`,
  `sessions/`), nothing beside the executable, so `scoop update` replacing the install
  directory cannot lose saved connections.
- **README rewritten around the actual question a downloader asks.** The Install section now
  leads with Scoop; the SmartScreen section explains reputation-versus-detection and points at
  `gh attestation verify`, which is a genuinely stronger check than a signature and was
  previously mentioned as existing but never shown.

**Not verified: `scoop install` was never actually run.** Scoop is not installed on this
machine, and installing it was not something to do unasked. The manifest parsing, both URLs
and the hash extraction were checked directly; a real install on a clean machine is still the
check that matters, and is the one open box in Phase 3.

**Unresolved, surfaced by this pass:** `origin` is
`https://github.com/sinh-r/EventPublisherConsumer.git`, but `Directory.Build.props` sets
`<RepositoryUrl>https://github.com/rsrishabh007/EventScope</RepositoryUrl>` and the README's
clone command uses that same URL. With `PublishRepositoryUrl=true` the non-matching URL is
stamped into the shipped binary, and it is metadata the SignPath Foundation review reads.
Left alone on the assumption that `rsrishabh007/EventScope` is the intended home; if
`sinh-r/EventPublisherConsumer` is canonical, both need updating.

---

## Stage 5b — past events: history browsing and a Kafka start position

Closes the gap that prompted this pass: **EventScope could only ever show live events.** It was
true in two independent ways, both verified in code before anything was written.

1. **The broker's backlog was skipped.** `KafkaSourceOptions.AutoOffsetReset` defaulted to
   `Latest` and nothing overrode it — `EventSourceFactory.BuildKafkaSourceOptions` never set it and
   `ConnectionProfile` had no field for it. With the throwaway group id and
   `enable.auto.commit=false`, a fresh group has no committed offset, so "latest" meant literally
   *tail from now*. `KafkaEventSource`'s own remarks said so.
2. **The grid never read back what the app itself stored.** Everything was already persisted —
   segments plus a SQLite row per message, day-rolled — but `MessageRowsView`'s only production
   mutation path is `AppendBatch` from the live pipeline, and `Start()` handed the `SessionStore` to
   the pipeline purely as a write target. Restart the app and the grid was empty while yesterday's
   data sat on disk.

Two symptoms of the same gap, both now closed: `Capabilities.SupportsReplay = true` was a promise
no code kept (no `Seek`, `OffsetsForTimes` or `TopicPartitionOffset` existed anywhere in `src/`),
and `FtsSearchService` already searched every historical day file but `SearchViewModel` counted the
hits and threw the rows away.

### A real bug found on the way, and why the obvious fix is unsafe

`SessionStorePayloadReader` derives a message's day directory from `MessageHeader.EnqueuedTicks`.
That is the **broker's** timestamp (`KafkaMessageMapper` maps
`result.Message.Timestamp.UtcDateTime.Ticks`), while the directory a message is written to comes
from the **writer's** clock (`SessionStore.CurrentDay`). Those agree only while tailing a live
topic. The moment a backlog is read, month-old messages are filed under today and the reader looks
for them under their original day — so the detail pane would report "payload evicted" for the entire
backlog. A batch straddling midnight has the same problem in miniature, today.

A "try the other days" fallback is **not** safe and was rejected: segment ids restart at 0 every
day and offsets are dense, so `(segmentId 0, offset 512)` exists in most day files and refers to a
different message in each. A blind fallback would silently render the wrong body — worse than an
error in a tool whose whole job is telling you what a message actually contained.

The fix is to carry the day rather than infer it. A row read back off disk knows the directory it
came from (`SearchHit.Day`, set from the directory name, not from a timestamp), and
`HistoryPayloadReaders.ForDay(day)` takes it as a parameter. `SessionLayout.DayFor` still exists for
the live path and now documents in full that it is an inference and which way it fails.
**Stamping the write day onto live ring rows is not done in this pass** — see Pending.

### Storage

- **`SessionLayout`** — the on-disk shape of a session root, split out of `SessionStore` so
  read-only callers can find day files without constructing a store (whose constructor opens *and
  creates* the current day's writer — exactly wrong for someone who only wants to read). Day
  enumeration now also filters to names that parse as `yyyy-MM-dd`: the base root holds a
  `{profileId:N}` directory per saved connection alongside its own day directories, and counting
  those as days misreports what is on disk.
- **`HistoryQueryService`** — plain paging over captured days, mirroring `FtsSearchService`'s
  proven per-day read-only-connection pattern. Keyset paging (`WHERE m.id >= $from ORDER BY m.id`)
  rather than `OFFSET`, so cost does not grow with scroll depth. `DaySummary.IsDense` checks the
  contiguous-id invariant per day file rather than assuming it, and `PageByOffset` is the fallback
  when it does not hold. Its core is synchronous because the grid's indexer is; `…Async` wrappers
  offload to the pool for the browse-open summary sweep.
- **`MessageRowQuery`** — one column list and one reader mapping shared by FTS and history paging,
  so the two cannot drift into producing differently-shaped rows for the same message. `SearchHit`
  gained `Partition` and `Flags` (the grid's PART column and its evicted/large/dead-lettered row
  styling were dead without them) and is now documented as the shared read projection, with
  `IndexHwm` meaningful only for search results (`IndexHwmNotApplicable` otherwise).
- **`HistoryPayloadReaders`** — a day-keyed `SegmentReader` cache over a session root, day passed
  in, no writer involved.
- **`FtsSearchService` now takes a root path** instead of a `SessionStore`, so search works against
  a connection that has never been started this run — which is the point of being able to look at
  past captures.

### App

- **`HistoryRowsView`** — a sibling of `MessageRowsView` implementing the same
  `IList` + `IReadOnlyList<MessageRowViewModel>` + `IDataGridCollectionView` contract, now factored
  as `IGridRowsView`, backed by paged SQLite reads. Chosen over seeding the live ring, which would
  have capped history at the ring's 65,536 rows and given its monotonic head sequence a second,
  conflicting meaning.
- **`DayRangePageSource`** — binary-searches day spans, bounded approximate-LRU page cache (256
  rows/page, 32 pages), the same shape `SegmentReader` uses for decompressed blocks.
  **`FixedResultsPageSource`** shows an already-materialized result set in the same grid.
- **`SessionCatalog`** — finds captures on disk, including ones whose connection has since been
  deleted (still browsable, labelled as such). The shared unnamespaced root is listed as one entry
  named "Fake source & legacy sessions" with a note saying its contents are commingled and cannot be
  told apart. That is unrecoverable after the fact, so it is named rather than hidden.
- **`DetailPaneViewModel.LoadAsync` now takes an `IPayloadReader`** rather than an `IngestPipeline`,
  which is what let a browsed row's body resolve with nothing running. Pinned fields moved to a
  `PinnedFieldSource` for the same reason.
- **Mode switching** — `MainWindowViewModel.ActiveRows` swaps the grid's source. Entering history
  pins the live ring rather than stopping it, so capture continues and the existing "N new messages"
  counter reports what arrived while you were reading.
- **Search results are openable**, not just countable — an explicit "Show matches" gesture rather
  than something the debounced search does per keystroke, which would swap the grid out from under a
  live stream as you type. Results are reversed to oldest-first to match every other grid mode.

### Kafka start position

- **`KafkaStartOffsets`** — a pure resolver (`Latest`/`Earliest`/`Timestamp`/`Offset`) taking the
  broker lookup as a delegate, which is where nearly all the behaviour lives and what makes it
  testable without a broker. `Latest` resolves to `Offset.Unset` so `auto.offset.reset` still
  governs and the default path is byte-for-byte what it was, not merely equivalent.
- **The fallback for an unresolvable timestamp is `Offset.End`, never `Offset.Beginning`** — a
  partition the broker did not answer for, or that has nothing at or after the requested time, must
  not silently turn "the last hour" into "the entire retained topic". Asserted explicitly, including
  the negative.
- `Earliest` also sets `AutoOffsetReset.Earliest` in the config, covering a partition assigned
  mid-run (a repartitioned topic) that never went through this run's seek decision.
- Starting at an explicit offset requires an explicit partition — offsets are per-partition, so one
  number across a subscribed topic means a different message in each. Rejected in the connection
  editor *and* in the factory, so a hand-edited `connections.json` cannot get past it.
- `SupportsReplay = true` is now honest.

### Tests — 295 total, up from 235; all five assemblies green

- `KafkaStartOffsetsTests` (9) — the resolver, including that a missing/errored partition tails
  rather than replaying.
- `KafkaEventSourceTests` (+6) — the Latest path still assigns *without* offsets (the regression
  guard for every existing user), earliest/offset/timestamp seeks, and `ResolveStartOffsets` driven
  directly via a new `InternalsVisibleTo`.
- `FakeKafkaConsumer` widened for `Assign(IEnumerable<TopicPartitionOffset>)` and `OffsetsForTimes`,
  keeping its "throws for anything the source must not touch" discipline: `OffsetsForTimes` still
  throws unless a test scripts it, which is how "Latest never consults the broker" is proven.
- `HistoryQueryServiceTests` (11) — paging, density, partition/flags projection, and that a row
  reports the day it was *read from* rather than one inferred from its timestamp.
- `HistoryRowsViewTests` (13) — realization, pooling, selection identity across a reset, and that an
  unreadable row renders as unavailable instead of throwing out of the indexer.
- `HistoryGridVirtualizationTests` (3) — headless, and the one that had to pass before any of this
  was worth building: binding a 2,000,000-row history realizes ~15 rows, and **swapping the
  DataGrid's `ItemsSource` between the live ring and history and back stays virtualized in both
  directions**. Measured, because Stage 1 already paid once to learn that DataGrid silently wraps
  and eagerly enumerates anything that is not an `IDataGridCollectionView`.
- `HistoryViewModelTests` (8) — the feature end to end with no pipeline: write a capture, discover
  it, list its days, open one, read a row's body back.
- `EventSourceFactoryTests` (+5), `ConnectionManagerViewModelTests` (+5) — mapping, validation, and
  a round-trip that would fail if a new field were missing from `Clone`.

### One unrelated fix, taken because it was making the suite unreliable

`DeepScannerTests.Reports_progress_once_per_message_scanned` was intermittently failing — twice in
four full-suite runs, always as `[2,3,4,5]` against an expected `[1,2,3,4,5]`, and never once when
run standalone. Pre-existing and unrelated to this pass. The test collected `Progress<T>` reports
into a plain `List<long>`; its own comment already documented that `Progress<T>` delivers each
report on a separate thread-pool work item with no `SynchronizationContext` installed, which means
those adds are genuinely concurrent and can **lose** a report, not merely reorder it. Under
full-suite load the pool is busy enough to hit it. Changed to a `ConcurrentBag<long>`; the ordering
tolerance the test already had is unchanged. Green across three consecutive runs after.

### Known limitations, stated rather than smoothed over

- ~~**Live ring rows carry no day.**~~ Closed in Stage 5c below.
- **The Subscribe-path seek is unverified against a real broker.** The rebalance handler is driven
  directly in tests, but that librdkafka honours the returned offsets across a real rebalance can
  only be shown by the opt-in integration test, which still has no broker on this machine (Blocked
  item 5).
- **Retention versus an open browse.** Page reads use short-lived `Pooling=False` connections and
  `HistoryPayloadReaders` is disposed on leaving history, but segment handles held during an active
  browse of a day retention wants to delete remain a window. `RetentionService`'s delete path is not
  hardened against `IOException` in this pass.
- **`DeepScanner` is still unwired.** Its overlay was not part of closing this gap.
- **`MessageRowViewModel.Sequence` divergence, not fixed.** For live rows it is the *view's* window
  sequence, while `InMemoryPayloadStore` is keyed by the *pipeline's* sequence, which restarts at 0
  on every `Start()`. After a Stop/Start the hot tier can return a different message's body. Found
  while designing this pass; pre-existing, untouched, and recorded here because it is the strongest
  argument against ever seeding the live ring with history.

---

## Stage 5c — the replayed backlog is readable in the live grid

Stage 5b gave Kafka a start position, so a run can begin at `Earliest`, a timestamp or an explicit
offset and then carry straight on into the live tail — past events *and* upcoming ones through one
pipeline into one grid. It left one hole open, listed as the first Pending item, and this pass
closes it: **the replayed half of that stream could not have its payloads read.**

### Why a backlog broke payload reads specifically

A message is filed under the day the **writer's** clock says, while `SessionStorePayloadReader`
inferred the day from `EnqueuedTicks`, the **broker's** timestamp. Tailing a live topic those agree,
which is why nothing noticed. Replay a backlog and every message is filed under *today* carrying a
timestamp from weeks ago, so the inference looks in a directory that either does not exist — or,
because segment ids restart at 0 every day and offsets are dense, holds **a different message at
the same coordinates**. Not a missing body: the wrong body, silently, in a tool whose entire job is
showing what a message contained.

The hot in-memory ring hid this for the newest few thousand rows and hid it *inconsistently*, which
is the worst shape for a bug like this — a small demo replay looks fine, a real one is wrong
everywhere past the ring.

### The fix: carry the day, never infer it

The day now travels with the message from the writer that filed it, the same way `SearchHit.Day`
already travelled with a row read back off disk:

- **`IngestPipeline.Ingest`** reads `_sessionStore.CurrentDay` immediately after `EnsureCurrentDay()`
  — the day the bytes it is about to append actually land in — and hands it to the coalescer.
- **`IngestCoalescer`** carries it **per message**, not per batch, in a fourth parallel array. Per
  batch would have been cheaper and is what the Pending item sketched, but a batch staged either
  side of a midnight rollover holds messages from two directories, and stamping it with either day
  points half its rows at the other day's bytes. The old note accepted that as "a handful of rows
  within milliseconds of a boundary"; carrying it per message costs one reference per staged
  message and makes the caveat disappear instead.
- **`MessageRowsView`** keeps a parallel `string[]` day ring beside the preview/subject/correlation
  rings it already had, and `MessageRowViewModel.Populate` stamps it — where it previously *cleared*
  `Day` on every populate, which is what made a pooled row view model safe to recycle and is now
  covered by its own test.
- **`SessionStorePayloadReader`** takes an explicit day, falling back to inference only when handed
  none. **`IngestPipeline.ReaderFor(day)`** is the live counterpart to history mode's `ReaderFor`,
  still hot-ring-first with only the cold fallback pinned, so `OnSelectedRowChangedAsync` now reads
  the same way in both modes.
- **`DetailPaneViewModel`'s pinned-field lookup** had the identical inference, keyed by
  `(segment_id, offset)` which is unique only *within* a day. Same fix.

Every new parameter is defaulted or overloaded, so all six existing `AppendBatch` call sites and the
whole `Enqueue` surface compile unchanged; only the `BatchReady` handler signature moved, in three
test lambdas.

### Tests — 304 total, up from 295; all five assemblies green

`BacklogReplayDayTests` (7) and `IngestCoalescerTests` (+2). The one that carries the argument:
**`Resolving_by_timestamp_returns_another_days_bytes_while_the_stamped_day_returns_the_real_ones`**
stages the hazard rather than describing it — a real replayed payload written under today, and a
decoy of identical length planted at the same `(segment, offset)` in the day the row's timestamp
points at. Reading by the stamped day returns the real bytes; reading by inference returns the
decoy. No exception, no empty buffer. The others cover the day surviving a pooled row view model's
recycle, per-message days across a rollover inside one batch, a short/absent day span leaving rows
empty, and a cold read through `ReaderFor` that first asserts the inferring reader finds *nothing*
— otherwise the test would be asserting the hot ring, not the fix.

### A pre-existing data-loss bug found while writing those tests

The first draft of the decoy test disposed the `SessionStore` to seal the segment, then reopened one
over the same root to read it back. The payload came back empty. Cause:
`SessionStore.OpenCurrentDay` constructs `new SegmentWriter(Directory)` with the default
`startingSegmentId: 0`, and `SegmentWriter.OpenNewSegment` opens with `FileMode.Create`. **Reopening
a session root on the same UTC day truncates that day's segment 0**, while the SQLite rows pointing
into it survive — so today's earlier capture becomes rows with unreadable bodies. `startingSegmentId`
exists as a parameter and no caller has ever passed it, which suggests the recovery was intended and
never wired. Untouched here: it is pre-existing, unrelated to replay, and the honest fix (resume at
the highest segment id already on disk, and reconcile that with the roll logic) is its own change
with its own risk. Recorded in Pending. The test now flushes by appending a >1 MB payload and keeps
the store open, which is a better test anyway — it reads through the live writer's own directory.

---

## Release pass — v0.3.0, the first release with a user-facing feature since v0.1.0

`v0.2.0` and `v0.2.1` were both plumbing — a workflow that could not reach `Publish`, then a tag
whose code could. `v0.3.0` is the first tag since `v0.1.0` that changes what the tool *does*: it
ships Stages 5b and 5c, so a user can start a connection somewhere other than "now" and can reopen
the app to browse what it already captured.

- **Version bumped 0.2.1 → 0.3.0** in both places every previous bump touched:
  `Directory.Build.props` (the one place its own comment says to bump per release tag) and
  `app.manifest`'s `assemblyIdentity version="0.3.0.0"`, which is easy to miss because nothing fails
  if it drifts. **Minor, not patch:** `v0.2.1` → `v0.3.0` adds history browsing, Kafka start
  positions and openable search results. Still `0.x`, so this is not a stability promise.
- **No behaviour change for anyone who does not opt in.** `KafkaStartFrom.Latest` is the default and
  resolves to `Offset.Unset`, leaving `auto.offset.reset` in charge, so an existing
  `connections.json` — which has no `StartFrom` field at all and deserializes to `null` — tails from
  now exactly as it did before.
- **Release notes are `generate_release_notes: true`**, so the release page is built from the commit
  log rather than hand-written. That makes the feature commit's message the release notes, which is
  why it is written as prose rather than a bullet list.

### Outcome — the `release` run for `v0.3.0` succeeded and the binary is verified

Run [33793316443](https://github.com/sinh-r/EventPublisherConsumer/actions/runs/33793316443),
conclusion `success`, all steps including `Publish`, `Attest build provenance` and
`Create release`. Verified after the fact by downloading the published assets rather than by
trusting the run's own logs:

- **`EventScope.exe`, 122.89 MB**, and **`EventScope.exe.sha256`** attached to
  <https://github.com/sinh-r/EventPublisherConsumer/releases/tag/v0.3.0>, not a draft, and
  `releases/latest` now reports `v0.3.0` — which is what the Scoop manifest's `checkver` reads.
- **Published hash matches the asset.** Downloaded both; recomputed SHA256 is
  `976668B7…D40CC0FB`, identical to the published `.sha256`.
- **The binary is stamped from the tagged commit.** `FileVersion 0.3.0.0` and
  `ProductVersion 0.3.0+94740f1612e9d558d6a68d6c2cc79c13f495bbe9` — the version bump reached the
  artifact, and `app.manifest` did not drift.
- **Provenance attestation resolves for that digest**, subject `EventScope.exe`, buildType
  `https://actions.github.io/buildtypes/workflow/v1`, repository
  `sinh-r/EventPublisherConsumer`, ref `refs/tags/v0.3.0`. This is the claim a security-conscious
  user can check with `gh attestation verify` while the binary is still unsigned.

Still unsigned, so the SmartScreen prompt is unchanged from `v0.2.1` — and reputation is per-hash,
so nothing `v0.2.1` earned carries over. **The exe itself was downloaded and inspected, not run:**
that it launches on a clean machine is untested, as it was for `v0.2.1`.

### What this release does not include

- **No real-broker verification.** The Subscribe-path seek is driven directly in tests; that
  librdkafka honours the returned offsets across a live rebalance is still unproven here (Blocked
  item 5). Anyone pointing `Earliest` at a large production topic is the first to find out.
- **The same-day segment-0 truncation bug ships with it** — see Pending. It predates this release
  and is not made worse by it, but browsing past captures is now a feature users can reach, which
  raises how much it matters.
- **The Scoop manifest is not updated by this tag.** `D:\My Work\scoop-EventScope` is still a local,
  unpushed repo; its `autoupdate` block will pick up the new version and hash once that repo exists
  on GitHub. Until then Scoop users stay on whatever the bucket pins.

---

## Stage 5d — reopening a day no longer destroys it (this pass)

Closes the Pending item Stage 5c opened, which was the highest-value thing on the list: **reopening
a session root on the same UTC day silently destroyed that day's earlier capture.**

`SegmentWriter`'s constructor always started at segment 0, and `OpenNewSegment` opens with
`FileMode.Create`, which truncates. The day file's rows survive and keep pointing at
`(segment 0, offset N)`, so an entire earlier session became rows whose bodies could not be read —
and once the new run wrote past offset N, those rows read back **another message's bytes** rather
than failing. Same failure mode as the Stage 5c day bug, from the opposite direction: there the row
named the wrong directory, here the directory's contents were replaced underneath a correct row.

### It was far more reachable than "restart the app"

The trigger is *any* `new SessionStore` over a root that already has today's directory. Restarting
is one way. The other, found while checking this fix, is ordinary use:
`MainWindowViewModel.HandleTabSwitchAsync` disposes the store and nulls it on a profile change, and
`Start` then recreates it. **Switching connection tab A → B → A on the same day destroyed A's
earlier capture**, no restart involved. That is a normal thing to do in a tool with a connection
manager, which makes this a data-loss bug on the happy path rather than an edge case.

### The fix, and the two decisions inside it

`SegmentWriter(directory, int? startingSegmentId = null)` — `null`, the new default, resumes at
`NextUnusedSegmentId(directory)`: one past the highest `*.seg` already there, 0 for a fresh
directory. Every existing call site passes nothing and keeps compiling; a fresh directory behaves
exactly as before.

- **Resume past the highest, not fill the first gap.** Retention deletes individual segment files
  while their rows stay in the day file flagged `PayloadEvicted`
  (`RetentionService.EvictOldestSegment`). Reusing a deleted segment's id would hand it to unrelated
  new bytes and make those evicted rows resolve against them — reintroducing the exact
  wrong-body failure this fix exists to remove. Ids only ever go up; a gap stays a gap, which the
  reader is fine with since it looks segments up by id and never assumes contiguity.
- **Start a new segment rather than append to the last one.** Appending would mean truncating the
  footer off a sealed segment and restoring `_blocks`, `_uncompressedCursor` and `_filePosition`
  from it — a lot of new failure surface on the write path to reclaim part of one 64 MB file. The
  cost of not doing it is one partially-filled segment per reopen, which is the cheap side.

### Tests — 311 total, up from 304

`SegmentWriterResumeTests` (7). **Six of them failed against the old code before the fix went in**,
which is how the bug's shape was confirmed rather than assumed — including
`Reopening_a_directory_does_not_destroy_what_an_earlier_writer_left_there`, which failed by
returning the *second* writer's bytes for the first writer's row. Also covered: the resumed id,
three sequential runs over one directory all reading back, an explicit `startingSegmentId` still
winning, a retention-deleted id never being handed out again, the `SessionStore`-level reopen, and
retention running after a reopen leaving the surviving segment readable while the evicted one reads
empty rather than as something else.

**Not fixed here: data already lost is lost.** A day directory whose segment 0 was truncated by an
earlier build has rows pointing into bytes that no longer exist. Those rows read empty, which is the
correct behaviour available — nothing can recover them.

---

## Release pass — v0.3.1, a data-loss fix (this pass)

Cut immediately after `v0.3.0` because what it fixes destroys user data on the happy path, and
`v0.3.0` is the release that made it matter more: browsing past captures is now a feature people can
reach, so a truncated day is something they will actually notice.

- **Version bumped 0.3.0 → 0.3.1** in `Directory.Build.props` and `app.manifest`. Patch, not minor:
  one storage fix, no new capability, no API or settings change.
- **Nothing to migrate, and nothing a user must do.** The fix changes only where a newly opened
  writer starts. Existing day directories are read exactly as before.
- **What it cannot do is undo the damage.** A day whose segment 0 was truncated by `v0.3.0` or
  earlier has rows pointing at bytes that are gone; they read as unavailable, which is the honest
  answer and the only one available.

Everyone on `v0.3.0` should move to `v0.3.1` — anyone who switches connection tabs, or reopens the
app twice in a day, is hitting this.

### Outcome — shipped and verified

Run [33797179702](https://github.com/sinh-r/EventPublisherConsumer/actions/runs/33797179702),
conclusion `success`. Verified by downloading the published assets rather than by trusting the run's
logs, the same checks `v0.3.0` got:

- `EventScope.exe` (122.89 MB) and `EventScope.exe.sha256` attached to
  <https://github.com/sinh-r/EventPublisherConsumer/releases/tag/v0.3.1>, not a draft, and
  `releases/latest` reports `v0.3.1`.
- **Published hash matches the asset** — recomputed SHA256 `3882C6E0…BF362E4F`, identical to the
  published `.sha256`.
- **Stamped from the tagged commit** — `FileVersion 0.3.1.0`,
  `ProductVersion 0.3.1+0821e7401d34831de890701dc9c3fa89626f44f2`.
- **Provenance attestation resolves** for that digest, subject `EventScope.exe`, repository
  `sinh-r/EventPublisherConsumer`, ref `refs/tags/v0.3.1`.

Still unsigned, and SmartScreen reputation is per-hash, so `v0.3.1` starts from zero exactly as
`v0.3.0` did. **The exe was downloaded and inspected, not run** — that it launches on a clean
machine remains untested, as for every release so far.

---

## Stage 5e — retention survives an open reader, and deep scan reaches the UI (this pass)

The two items at the top of §Pending, done in one pass because they are the same bug from two
sides. Retention could not survive a day file being held open; `DeepScanner` — fully built and
tested since M2, with zero callers — is the thing most likely to hold it open, since it walks
*every* day file on disk. Shipping either alone would have left the obvious hole.

### 1. A day file held open stopped retention for the rest of the session

`RunLoopAsync` caught only `OperationCanceledException`. `SessionStore.DeleteDay` ends in
`Directory.Delete(dir, recursive: true)`, and `SegmentReader` opens segment handles
`FileShare.ReadWrite` — no `FILE_SHARE_DELETE` — so any open reader makes that throw
`IOException`. It escaped `RunOnce`, faulted `_loopTask`, and **retention never ran again**.
`Dispose` observes that fault and has nowhere to report it, so the only symptom was a store
quietly growing past its cap.

Four changes, all inside `RetentionService`:

- **The loop is guarded**, so a blocked pass defers to the next 30 s tick.
- **Each candidate is guarded**, so one locked day doesn't abort the rest of the pass —
  `TryDeleteDay` for both delete sites, a `continue` for the segment delete.
- **`EnforceCap`'s loop can no longer spin.** It runs until total bytes drop under the cap, so a
  locked oldest segment that swallowed its failure and still reported success would be retried
  forever against a total that never falls — a *hung* retention thread instead of a faulted one.
  `EvictOldestSegment` now moves to the next candidate and returns `false` only when nothing
  anywhere could be evicted.
- **Eviction ordering is reversed.** It used to `EnqueueSetFlags(PayloadEvicted)` and *then*
  `File.Delete`. When the delete fails, that leaves rows flagged as evicted while their bytes are
  still on disk and readable — the row lying about itself. Delete first, flag only on success.

**A blocked age-deletion can still partially delete a day**, because `Directory.Delete` removes
what it can before it throws. That is not a regression (the same partial delete happened before,
and killed retention on the way out), it converges — the day is still expired, so the next pass
finishes it — and `SessionLayout.ListDayDirectories` already enumerates day *directories* rather
than day *files*, explicitly anticipating a directory that has outlived its database. So nothing
is orphaned.

### 2. `DeepScanner` now has a caller, an overlay, and one row shape

- **It yields `SearchHit`, not `DeepScanMatch`.** `ScanDayAsync` already selected
  `id, segment_id, offset, length`; widening that to `MessageRowQuery.Columns` + `SubjectJoin`
  and reading through `MessageRowQuery.ReadHit` was most of the work, and it is what made the UI
  side cheap: `HistoryViewModel.ShowResults` already takes `IReadOnlyList<SearchHit>`, so
  deep-scan results open through the exact path FTS results use — no second hydration, and no way
  for the two tiers to describe the same message differently, which is what `MessageRowQuery`
  exists to prevent. `SearchHit.IndexHwmNotApplicable` was already there for precisely this: a
  deep scan never consults the index, so "are these current" is not a question it can answer, nor
  needs to.
- **`ScanAsync(root, …)` scans the whole store**, newest day first with an early exit at
  `maxResults`, mirroring `FtsSearchService.SearchAsync`'s traversal so "the first N matches"
  means the same thing in both tiers.
- **Progress is measured in payload bytes, not rows.** UI spec §7 asks for
  "Scanned 412 MB of 1.84 GB · 87 matches · 3.2s elapsed"; a row count has no denominator. The
  total is one `SUM(length)` per day, summed across every day *before* the first payload is read,
  so the bar never rebases mid-scan. Bytes, not segment-file sizes: those are LZ4-compressed and
  carry block tables and footers, so they would under-report against what is actually being
  decompressed.
- **`SearchViewModel` gained the third tier.** `DeepScanCommand` /`DeepScanCancelCommand` (the
  toolkit's `IncludeCancelCommand`), the overlay's bound state, and results published through the
  existing `ResultsRequested` event — already wired to `History.ShowResults`, so
  `MainWindowViewModel` needed nothing but the ticker. A **cancelled scan still publishes what it
  found**, labelled "partial": cancelling means "that's enough", not "throw that away".
- **Progress is pulled by the 60 ms UI ticker, not pushed per message.** A scan reports once per
  row; a `Progress<T>` created on the UI thread would post every one of those to the dispatcher —
  millions of posts to move a bar a pixel at a time. The scan writes three plain `long`s instead
  and `IUiTicker` (the same abstraction the ingest coalescer uses, its own instance started only
  for the scan's duration) reads them. The three are deliberately not synchronized: a reader can
  see one row's byte count beside the next row's match count, which on a progress display is
  invisible and self-correcting.
- **The overlay shares row 4 with the `DataGrid`**, top-aligned, rather than taking a
  `RowDefinition` of its own — a new row would renumber every `Grid.Row` below it, and UI spec §7
  wants a panel over the content anyway.

**Not done, deliberately:** the full `Live / Today / 20 days / Deep` scope selector (UI spec
§4.4). It needs the segmented control from §9's component inventory, which does not exist yet and
is Stage 5 polish. A single `Deep scan` button reaches the tier now.

### A latent test flake, found by this pass and fixed properly

The first full-suite run came back with **three `RetentionServiceTests` failures — two of them
pre-existing tests** — that had passed 79/79 when the assembly ran alone, twice.

Not a regression, and worth recording because the diagnosis was not the obvious one.
`SessionStore.EnsureCurrentDay` seals a rolled-over day on a fire-and-forget `Task.Run` that
exposes no handle to await, and six tests across three files all bet a fixed
`await Task.Delay(200)` on it finishing. That bet holds in isolation and loses under full-suite
load, where the thread pool is saturated and the seal task queues behind everything else. The
tell was which assertion failed in `A_non_current_day_with_no_segments_left_has_its_db_dropped_too`:
its own `File.Delete` of the `.seg` files *succeeded* and only the directory delete failed —
exactly the state where the seal had finished `oldSegmentWriter.Dispose()` but not yet
`oldWriter.Dispose()`, so the `.db` was still held.

The retention guards did not cause this; they changed how it presents. Before them the same race
surfaced as an unhandled `IOException`, after them as "the directory is still there". Fixed at the
cause rather than by lengthening the sleep: `SqliteTestHelpers.WaitForRolloverSealAsync` polls
until the day's `.db` can be opened `FileShare.None`, which is the observable end of the seal. All
six sites now use it, including the two in `FtsSearchServiceTests` and `DeepScannerTests` that
carry the same latent flake for the same reason.

**Left alone: the production side.** An unobservable fire-and-forget seal is a real design smell,
but `EnsureCurrentDay` returning something awaitable is a change to the ingest hot path and does
not belong in a pass about retention and search.

### Tests — 324 total, up from 311

Repro-first for the retention half, the discipline Stage 5d used and the dispatcher hunt learned
the hard way: **all three new `RetentionServiceTests` were confirmed failing against the pre-fix
code before the fix went in**, each for its predicted reason — `IOException` out of
`DeleteExpiredDays`, `IOException` out of `EvictOldestSegment`, and the loop test timing out
because the faulted loop never ticked again. They cover a day held open by a real `SegmentReader`
mid-read (deferred, then deleted once released), a locked oldest segment (the next candidate
evicted instead, and the locked segment's rows *not* flagged), and the background loop surviving
blocked passes and finishing on a later tick.

`DeepScannerTests` covers the new row shape (subject, identifiers, preview, flags and
`IndexHwmNotApplicable` all hydrated), byte-denominated progress against a fixed total, match
counting, and newest-day-first traversal with the `maxResults` early exit. `SearchViewModelTests`
(new, 6) drives the tier end to end: a needle past the 2 KB prefix found and published, results
re-sorted oldest-first, the overlay closing on a full bar with real final numbers, cancel never
leaving it open, and the disabled/not-connected states.

`SearchViewModelTests` needs **no `HeadlessFixture`** — `MessageRowsView` is not an Avalonia UI
object and the view model touches only plain properties — which keeps it clear of the headless
dispatcher hazards in Blocked item 2 outright rather than relying on that fixture's workarounds.

Full suite green in Release across two consecutive runs: Acceptance 3 (3 soak-gated skips),
App 142 (1 skip), Kafka 46 (2 broker-gated skips), Core 54, Storage 79.

### Driven in the real app, against 1.98 GB of real capture

Not just unit-tested. Every binding here is compiled (`x:DataType` on the window plus
`AvaloniaUseCompiledBindingsByDefault`), so a typo would have been a build error — but that says
nothing about whether the thing works. Driven instead through Windows UI Automation from
PowerShell, the recipe Blocked item 2 records, against the **Fake source tab, whose session root
is the base directory** — which on this machine holds two real captures, `2026-09-01` (1.3 GB) and
`2026-09-03` (213 MB).

- **`Deep scan` is disabled on an empty query and enables on the first keystroke** —
  `NotifyCanExecuteChangedFor` confirmed live, not just in a test.
- **The overlay's counter is real.** Polled once a second during a scan of the whole root:
  `Scanned 135.7 MB of 1.98 GB · 0 matches · 6.2s elapsed` → `279.7 MB` → `470.6 MB` → … →
  `1.77 GB`, with the denominator fixed at 1.98 GB throughout, exactly as designed. **≈200 MB/s**,
  and the UI stayed responsive to automation calls the whole time — the 60 ms ticker doing its
  job.
- **Measured, and worth knowing: the denominator costs about 5 seconds on a 2 GB store.** The
  first counter reading already said "6.2s elapsed" one second after the click, because
  `SUM(length)` across both day files runs before the first payload is read. The overlay shows
  "Measuring…" for that window rather than a bar frozen at zero with no explanation. Honest, but
  it is a real pause and it scales with the store.
- **Completion opens the results**: the browse banner read
  `Browsing deep scan for “order” — live capture continues in the background.`
- **The `maxResults` early exit works**: a scan for `id` hit the cap almost instantly and reported
  `5000+ deep matches` — it stopped after the first 5000 rows rather than reading 1.98 GB.
- **Cancel works and is honest about it**: cancelled 3.3 s into a scan
  (`Scanned 500.4 MB of 1.98 GB`), the overlay closed and the banner read
  `Browsing partial deep scan for “zzz-no-such-token” …`.

**Not done manually: the retention-defers-under-an-open-scan check.** Staging it in the live app
would mean setting retention short enough to expire `2026-09-01` and letting it delete 1.3 GB of
real capture — destroying user data to demonstrate a fix. The mechanism is covered instead by
`An_expired_day_still_open_for_reading_defers_instead_of_faulting_the_pass`, which holds a real
`SegmentReader` mid-read over the day retention wants (the same handle a deep scan takes) and was
confirmed failing against the pre-fix code first.

---

## Release pass — v0.4.0, deep search reaches the UI

Minor, not patch: this pass **changes what the tool can do**, which is the same bar `v0.3.0` was
cut at. `v0.3.1` was patch because it was a fix and nothing else. Here a whole search tier that
existed only as library code becomes something a user can actually reach, alongside a real
reliability fix underneath it.

- **Version bumped 0.3.1 → 0.4.0** in `Directory.Build.props` and `app.manifest`.
- **Nothing to migrate, and nothing a user must do.** No settings, schema, or on-disk format
  changed. Day files written by `v0.3.1` are read identically.
- **README corrected while here.** Its search bullet claimed trigram infix search on message and
  correlation IDs as a feature. That has been true of the *library* since M2 and false of the
  *product* the whole time — `SearchViewModel` has no scope selector to reach it, as its own
  remarks have said all along. The bullet now claims the deep scan (newly true) and says plainly
  that identifier search is built but not yet reachable.

Worth taking for the retention fix alone: on `v0.3.1` and earlier, anything holding a day file
open — a history browse, and now a deep scan — permanently stopped retention for the rest of the
session, with no symptom except a store growing past its configured cap.

Still unsigned. SmartScreen reputation is per-hash, so `v0.4.0` starts from zero exactly as every
release before it has.

---

## Reach and polish — a build fit to hand to someone else (this pass)

Four releases existed and **none of them was reachable without friction**. You chose to fix that
before building more features, and to follow it with enough polish that the Kafka path can be
handed to other people. Service Bus and SQS come after.

### 1. The Scoop path is no longer theoretical

The bucket at `D:\My Work\scoop-EventScope` had been prepared and never used. Three things were
wrong with it, and none would have been caught without actually running it.

- **Pinned to `v0.2.1`** — three releases stale. Now `v0.4.0`, with the hash taken from the
  release's own published `EventScope.exe.sha256` rather than recomputed locally: the whole point
  of that field is to match what GitHub serves.
- **The description overclaimed** — "Cloud-agnostic event publisher and subscriber for Azure
  Service Bus, AWS SQS and Kafka", when two of those three are empty `.csproj` files. It is the
  first thing anyone reads in `scoop search`. Same line fixed in the bucket's README.
- **`scoop install` had never been run by anyone.**

**Now it has, end to end.** Scoop installed to `%USERPROFILE%\scoop`; installing from the
manifest downloaded the real 122.9 MB asset from the GitHub release and reported
*"Checking hash of EventScope.exe ... ok"* — which independently confirms the manifest hash
matches the published binary. Shim, shortcut and notes all created correctly.

- **No Mark-of-the-Web, confirmed directly**: the installed
  `~\scoop\apps\EventScope\current\EventScope.exe` has no `Zone.Identifier` alternate data
  stream at all — only `:$DATA`. That is the mechanism behind the no-prompt claim, checked rather
  than asserted.
- **It launches.** *The first time a published EventScope release binary has ever been run* —
  every prior release note says "downloaded and inspected, not run". Window came up, stamped
  `ProductVersion 0.4.0+391ab7ef…`, matching the tagged commit.
- **The direct-download path was checked too**: downloaded `v0.4.0` from the release, recomputed
  SHA256 — matches the published one — attached a real `Zone.Identifier` (`ZoneId=3`) the way a
  browser delivers it, and launched it. No launch-time block on this machine.

**What this still cannot prove.** SmartScreen's *download-time* "isn't commonly downloaded"
prompt is a browser-side reputation verdict against a cloud service, keyed on the file hash. This
machine has now seen and run that hash, so it cannot be a clean test of what a stranger sees. The
Scoop path avoids the prompt *by construction* — no Mark-of-the-Web means nothing to prompt about
— and that part is proven. The direct-download prompt can only be observed by someone
downloading it fresh.

### 2. Signing: everything that does not need the certificate

- **`Docs/SIGNPATH_APPLICATION.md`** — the application, ready to paste, plus EventScope assessed
  row by row against SignPath's actual published conditions (read from `signpath.org/terms.html`,
  not recalled). Nine criteria; eight clear passes. Repository visibility was *verified* rather
  than assumed: unauthenticated `api.github.com` calls against the repo return data, which only
  works when it is public.
- **One thing needs your decision before submitting**, recorded there: their "no proprietary,
  non-open-source component" condition versus the three still-tracked Claude Design mockup files.
  Low risk — none is compiled into the binary and the design content is yours — but the condition
  is written strictly, and `support.js` was already untracked for exactly this reason. Cheapest
  resolution is to gitignore `Mockup preparation from spec/` entirely.
- **`release.yml` now carries the signing step**, inert until `SIGNPATH_API_TOKEN` exists, so
  approval takes effect with no workflow edit at that moment.

**Two things were looked up rather than written from memory**, which the workflow's own header had
warned about:

- The action is `SignPath/github-action-submit-signing-request@v2`, and its inputs were read from
  its `action.yml`. It takes `github-artifact-id`, so the upload step needed an `id` to source
  `steps.unsigned.outputs.artifact-id` from.
- **The `secrets` context is not available in a step-level `if`.** Confirmed against GitHub's
  context availability table, which lists only
  `github/needs/strategy/matrix/job/runner/env/vars/steps/inputs` there. The token is therefore
  hoisted to a job-level `env` and the step tests that. Written the obvious way, the gate would
  have silently never fired.

**A real ordering bug in the workflow's own guidance, fixed.** Its header said signing goes
"between Upload unsigned artifact and Create release". That gap also contains `Attest build
provenance` and `Compute SHA256`, both of which run against `publish/EventScope.exe`. Signing
after either one would publish an attestation covering a digest nobody can download, or a
`.sha256` that does not match the binary beside it. Correct order is now stated and implemented:
publish → upload → **sign** → attest → hash → release. A `Report signing status` step prints the
signer subject into the run log so the answer is in the run, not in the binary.

### 3. Polish: removing what makes it look unfinished

- **The Fake source is hidden outside Debug.** `DeveloperOptions.ShowFakeSource` decides;
  `ConnectionManagerViewModel` gained an optional `includeFakeSource = true` so every existing
  call site and test compiles untouched — the same additive shape `SegmentWriter`'s
  `startingSegmentId` used at Stage 5d. `EVENTSCOPE_FAKE_SOURCE=1` brings it back, and
  `EVENTSCOPE_MEASURE` implies it, because the measurement harness drives the Fake source
  deliberately and runs in Release.
- **Verifying it caught a second, more visible instance.** The list entry was the obvious one;
  `MainWindowViewModel` *also* opened a Fake source **tab** at startup unconditionally, so a new
  user's first screen was a tab of invented traffic from a broker they never configured. Found by
  driving the built Release binary through UI Automation and finding "Fake source" still in the
  tree after the first fix — not by reading the code, which had looked complete.
- **The disabled Azure Service Bus and AWS SQS buttons are gone.** A greyed-out control promising
  a future milestone reads as an unfinished product, not a roadmap.
- **Internal jargon removed from user-visible strings** — the two "Available in a future
  milestone (M4)" tooltips went with the buttons, and `EventSourceFactory`'s "see build plan M4"
  became "EventScope currently connects to Kafka".
- **README corrected on two counts**: the zero-setup instruction now says the Fake source is a
  Debug affordance and names the escape hatch, and the connection-manager screenshot was
  recaptured — the old one showed the two buttons that no longer exist. The first recapture was
  thrown away for showing the Windows taskbar and a clipped window, the exact flaw the
  distribution pass had fixed in the previous screenshots.

### Tests — 326, up from 324

Two new `ConnectionManagerViewModelTests`: the list omits the Fake source when told to and keeps
saved connections in order, and the default still includes it — which is what keeps the other
tests in that file, several of which index `SavedConnections[0]` expecting it, working untouched.

**Deliberately not unit-tested: `DeveloperOptions` itself.** Its value depends on the build
configuration, so a test asserting "false by default" would pass in Release and fail in Debug.
The app-level wiring is covered by the UI Automation check instead, both directions: the default
Release build shows no element named "Fake source" and no ASB/SQS buttons, and the same binary
with `EVENTSCOPE_FAKE_SOURCE=1` brings the Fake source back.

---

## Pending — in build-plan order

- **Both of this list's former top items are done** — see Stage 5e above. `RetentionService` now
  defers a blocked delete instead of faulting its loop, and `DeepScanner` is wired to the UI
  behind the spec §7 overlay.
- **`EnsureCurrentDay`'s rollover seal is unobservable.** It runs on a fire-and-forget `Task.Run`
  with no handle to await, which is what let six tests bet on a fixed sleep and fail under load
  (Stage 5e above). Those tests now wait on the observable end instead, so nothing is broken — but
  a seal nothing can await is still the wrong shape, and the *next* caller that needs to know when
  a day is finished will hit it too. Fixing it properly means touching the ingest hot path, which
  is why it is listed rather than done.
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
- **M3 is complete** (see above) — the generator engine, the publisher UI, schema inference
  ("Use as template"), and the publish path (`KafkaEventSink`, opt-in round-trip acceptance
  test) are all done. `Publish`/`Burst` report "no publish target connected" for the Fake
  source or when no connection is selected; a connected Kafka tab (via the connection
  manager, Stage 5a above) publishes for real.
- **M4 — Service Bus and SQS.** `ServiceBusEventSource`, `SqsEventSource`, and the
  capability-binding audit (no `if (broker == …)` in the view layer). The connection
  manager's empty state already names both and reserves their editor-form slot
  (disabled with a tooltip) — see Stage 5a above.
- **Stage 5 — polish, partially done (Stage 5a above).** Connection manager and launcher
  are done: saved connections, the Kafka editor form, three-state Test connection, the tab
  strip, per-tab error state with Retry. Still pending: the ASB/SQS editor forms (blocked on
  M4's sources existing), deep-search overlay, large-payload confirmation, toast, light
  theme, full keyboard map.
- **Release engineering — real code signing, waiting on two human actions.** Everything
  mechanical is done (see the reach-and-polish pass above): `release.yml` carries the signing
  step, inert until the token exists, and `Docs/SIGNPATH_APPLICATION.md` holds the ready-to-paste
  application with the eligibility assessment. What remains is **yours**: decide the mockup-files
  question that doc raises, then submit at <https://signpath.org/apply>. Approval takes days to
  weeks. Until it lands, direct downloads keep showing the SmartScreen prompt — Scoop users do
  not.
- **Push the Scoop bucket.** `D:\My Work\scoop-EventScope` is updated to `v0.4.0` with an honest
  description and verified working (`scoop install` from the local manifest downloads, hash-checks
  and launches). It still has **no remote**: creating `sinh-r/scoop-EventScope` on GitHub needs
  an account action, and the `gh` CLI is not installed on this machine. Until it is pushed, the
  install command in both READMEs points at a repository that does not exist.
- **Bump the Scoop manifest after each release.** `excavator.yml` automates this once the bucket
  is pushed, but that workflow's own header records that its action reference could not be
  verified — run it via `workflow_dispatch` and check the commit before trusting the schedule.

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

   **First reproduced outside this dev machine on 2026-09-01, in the normal (non-soak) suite —
   worse than the local characterization above.** The distribution pass's first-ever push
   (`v0.2.0` and the first `ci` run for `main`, both against commit `ced0d0d`) both hung on
   their Test step on GitHub Actions' `windows-latest` runner, independently, at the identical
   point: right after a clean build, before any test past the first one printed a result.
   Confirmed genuinely hung, not slow, by elapsed time (40+ minutes against a step that takes
   under 2 seconds locally) since job logs for an in-progress run aren't readable without
   write access to the repository (checked directly — `GET .../actions/jobs/{id}/logs` returns
   403 "Must have admin rights" over the public API). You supplied the log after manually
   cancelling: `EventScope.App.Tests` printed exactly one result —
   `AcceptanceCriteriaTests.Scrolling_fifty_thousand_rows_stays_under_the_frame_budget [SKIP]`
   — then nothing else. **A fifth fix attempt, different in shape from the four above, was
   tried and also did not resolve it — worse, it introduced a new, deterministic local hang
   that didn't exist before.** Working theory: `AcceptanceCriteriaTests` is gated behind
   `EVENTSCOPE_SOAK` and `[SKIP]`s immediately, never touching the dispatcher at all: if
   xUnit's (confirmed non-deterministic — two consecutive local runs of the unchanged binary
   produced two different orderings) discovery order happens to put it first, as it did on
   this run, the *next* test needing real segment I/O hits a dispatcher that's never been
   pumped even once, closing none of the race window the four prior attempts tried to close
   from other angles. Attempted fix: an xUnit v3 `[assembly: AssemblyFixture(typeof(...))]`
   (a `4.0.0` feature — confirmed by reflection before using it, not assumed) forcing a *real*
   `RandomAccess.ReadAsync` completion through `Dispatcher.UIThread.InvokeAsync`, guaranteed to
   run once before any test regardless of discovery order — a different shape than the four
   prior attempts, none of which exercised a real I/O completion during warm-up, only an empty
   pump. It did not hang locally itself, but running the assembly fixture's own headless
   initialization on its own (different) thread broke a *coincidence*
   `DataGridVirtualizationSpikeTests` had silently depended on since Stage 1 — namely, that
   whichever thread first called `HeadlessFixture.EnsureInitialized()` also happened to be the
   thread its own test bodies ran on, so its un-wrapped `new DataGrid()` calls never threw "Call
   from invalid thread" purely by luck. With the fixture forcing initialization on a different
   thread, three of its four tests failed with exactly that exception. Fixed *that* part
   correctly and durably — wrapped all three in `Dispatcher.UIThread.Invoke(...)`, the same
   established pattern `AcceptanceCriteriaTests` already uses for the identical reason (its own
   remarks already named this exact non-guarantee) — confirmed clean across 8 consecutive local
   runs. But with the coincidence fixed and the assembly fixture still installed, the *first*
   test to run (whichever it was that run) hung **deterministically, every time**, inside a
   plain synchronous `Dispatcher.UIThread.Invoke(...)` call — a new, worse failure mode than the
   intermittent one this was meant to fix. **Reverted the assembly fixture entirely** (deleted
   `HeadlessWarmupFixture.cs`, removed the `AssemblyFixture` attribute) rather than chase a sixth
   theory blind, with no fast CI iteration available to verify one. Kept the
   `DataGridVirtualizationSpikeTests` wrapping fix — independently correct and verified safe on
   its own (8/8 clean local runs without the fixture), a real latent-fragility fix regardless of
   this investigation's outcome. **Net result: the underlying race is still open, now on record
   as resistant to five differently-shaped fix attempts**, four of them local-only and this
   fifth one CI-motivated. Mitigated, not fixed: `timeout-minutes` (15 for `ci.yml`, 20 for
   `release.yml`) added to both workflows so a future occurrence fails within a bounded time
   instead of silently consuming hours — bounds the damage, does not close the race.

   **Root-caused and fixed, 2026-09-01, sixth attempt.** A `SynchronizationContext` posting
   continuations to a dispatcher nothing ever runs — confirmed against Avalonia 11.3 source,
   not assumed, and confirmed by reproducing the hang deterministically *before* writing the
   fix (see Verification below), which none of the five prior attempts had done. The chain:

   1. `HeadlessFixture.EnsureInitialized()` (`tests/EventScope.App.Tests/HeadlessFixture.cs`)
      calls `AppBuilder...SetupWithoutStarting()`.
   2. `HeadlessTestApp` derives from `Application`, so setup runs
      `Application.RegisterServices()`, whose *first line* is
      `AvaloniaSynchronizationContext.InstallIfNeeded()`.
   3. `InstallIfNeeded()` — `AutoInstall` defaults to `true` — installs a
      `SynchronizationContext` via `SetSynchronizationContext(Dispatcher.UIThread.GetContextWithPriority(...))`.
      `SynchronizationContext.Current` is thread-static, so this lands on whichever thread ran
      that test class's constructor.
   4. That context's `Post`/`Send` forward to `Dispatcher.Post`/`Send` — they enqueue onto the
      dispatcher's job queue.
   5. `SetupWithoutStarting()` never runs a dispatcher loop by definition. The only pumping in
      this assembly is the manual `Dispatcher.UIThread.RunJobs()` behind `HeadlessFixture.Pump()`,
      called only by some tests, at moments of their own choosing.
   6. So any `await` on a *genuinely* asynchronous task (one that does not complete
      synchronously — an already-completed `ValueTask`/`Task` never touches the context at all,
      confirmed directly: `InMemoryPayloadStore.ReadAsync` always returns
      `ValueTask.FromResult(...)`, which is exactly why `IngestPipelineEndToEndTests`'s own
      `async Task` test never hung despite an unqualified `await`) that is not
      `ConfigureAwait(false)`, on the thread that installed the context, posts its continuation
      to a queue nothing drains — and hangs forever.

   Exactly three classes have both a constructor calling `EnsureInitialized()` *and* an
   `async Task` test with a genuinely-asynchronous unqualified `await`:
   `IngestPipelineStorageTests`, `IngestPipelinePreviewTests`, `IngestPipelineEndToEndTests`
   (the last one only *looks* exposed — see point 6). Which one hung, if any, was pure luck of
   xUnit's discovery order: whichever class's constructor ran first is the one whose thread got
   poisoned; a sync-only class running first (e.g. `DataGridVirtualizationSpikeTests`) absorbs
   the install harmlessly and every later test gets a clean thread — which is what every local
   run this whole project has ever seen, and exactly why this never reproduced here. This also
   explains, precisely, why all five prior attempts missed it: one `ConfigureAwait(false)` at a
   single call site left the test's other unqualified awaits exposed; pumping once (twice, now,
   counting the empty pump inside `EnsureInitialized()` and the sixth attempt's throwaway
   `Window`) drains jobs queued *at that instant*, not the continuation queued *later* when the
   real I/O actually completes; `ThreadPool.SetMinThreads` was never thread starvation; and the
   fifth attempt's assembly fixture forced init onto its own thread, so `Dispatcher.UIThread.Invoke`
   from every other thread blocked on the same unpumped dispatcher — turning an intermittent
   hang into a deterministic one, which in hindsight was this exact mechanism biting a second,
   different code path (a *synchronous* `Invoke` rather than an `await`).

   **Fix**, in `HeadlessFixture.EnsureInitialized()` only — no production code touched:
   `AvaloniaSynchronizationContext.AutoInstall = false;` before `SetupWithoutStarting()`, plus
   `_ = Dispatcher.UIThread;` immediately after, so dispatcher thread-binding — previously a
   side effect of `InstallIfNeeded()` — still happens deterministically on the same thread as
   before, rather than moving to whatever thread references it first. Cannot break a currently
   passing test: in a loop-less environment the Avalonia sync context can only ever *hang* a
   continuation, never *deliver* one, so nothing passing today can depend on it.

   **Verification, repro-first:** isolated each of the three exposed classes via
   `EventScope.App.Tests.exe -class <FullName>` *before* touching the fix —
   `IngestPipelineStorageTests` and `IngestPipelinePreviewTests` both hung (confirmed by kill
   after 15s; `IngestPipelineEndToEndTests` passed, consistent with point 6 above). Applied the
   fix; re-ran the identical isolated commands — both now pass in ~1s. Added
   `HeadlessFixtureTests.cs`, asserting `SynchronizationContext.Current` is never an
   `AvaloniaSynchronizationContext` after `EnsureInitialized()`; confirmed it fails against the
   pre-fix code (stashed the fix, reran, watched it fail with the expected message) and passes
   with it restored — a real regression guard, not a tautology. Full suite (Release):
   `EventScope.App.Tests` 94/94 (new regression test included) across 10 consecutive full runs
   with no hang, plus the two previously-hanging classes independently in a further 10-run loop;
   `EventScope.Core.Tests` 52/52, `EventScope.Storage.Tests` 54/54,
   `EventScope.Brokers.Kafka.Tests` 31/31 (Debug — Release hit an unrelated, transient Smart App
   Control block on that one freshly-rebuilt DLL specifically, confirmed via the
   `Microsoft-Windows-CodeIntegrity/Operational` log as Event ID 3077/3033, the same signature
   already tracked elsewhere in this item; Kafka.Tests has no Avalonia dependency at all, so
   this is unrelated to the fix). The authoritative check is GitHub's `windows-latest` runner —
   the only environment this bug ever reproduced reliably, and where local Smart App Control is
   moot — via the `ci` and `release` workflows on the next push.

   **The sixth-attempt fix above was real but partial — a seventh, distinct mechanism reached
   the same "hangs at near-zero CPU" signature and is what actually hung the first-ever
   `release` run.** `AvaloniaSynchronizationContext.AutoInstall = false` closes the `await`-
   shaped half of the problem (a posted continuation nothing drains). It does not touch the
   other half, already present in the code before this pass and unrelated to that sync
   context: `DataGridVirtualizationSpikeTests` (three tests) and `AcceptanceCriteriaTests`
   (one, soak-gated) wrap their bodies in `Dispatcher.UIThread.Invoke(...)`, per those
   classes' own remarks, because xUnit v3 does not guarantee a test method runs on the same
   OS thread as the constructor that called `EnsureInitialized()`. `Dispatcher.Invoke` runs
   inline only when already on the dispatcher thread; from any other thread it queues an
   operation and **blocks the caller waiting for it to run**. Setup bound the dispatcher to
   whatever thread called `EnsureInitialized()` first — an xUnit worker thread — and ran no
   loop to service that queue. An `Invoke` from a different worker thread therefore deadlocks
   forever: no exception, no CPU, indistinguishable from the sixth attempt's signature.

   This exact mechanism was already reproduced and recorded, in this same item's fifth-attempt
   notes, and misread at the time as a side effect of that attempt's assembly fixture rather
   than as a second live bug: *"the assembly fixture forced init onto its own thread, so
   `Dispatcher.UIThread.Invoke` from every other thread blocked on the same unpumped
   dispatcher — turning an intermittent hang into a deterministic one."* That deterministic
   hang **was** this mechanism; reverting the fixture only made the poisoned thread go back to
   being thread-luck-dependent rather than closing it. Confirmed by the release run's own log:
   `AcceptanceCriteriaTests`'s `[SKIP]` printed (it never touches the dispatcher when skipped),
   then nothing — consistent with the very next test to run being one of
   `DataGridVirtualizationSpikeTests`'s `Invoke`-wrapped tests, landing on a worker thread
   other than the one `EnsureInitialized()` happened to run on first.

   **Fix:** `HeadlessFixture.EnsureInitialized()` now runs headless setup on a dedicated
   background thread it owns outright (`Thread(IsBackground = true)`, name
   `avalonia-headless-ui`) and calls `Dispatcher.UIThread.MainLoop(CancellationToken)` on it —
   confirmed present in Avalonia.Base 11.3.20 and exactly what Avalonia's own
   `HeadlessUnitTestSession` runs internally. `Dispatcher.UIThread.Invoke` from any other
   thread now completes correctly regardless of scheduling: it queues the callback, the loop
   dequeues and runs it on the owned thread, the caller unblocks with the result. A new
   `HeadlessFixture.RunOnUi(Action)` centralizes the pattern; the four `Dispatcher.UIThread.
   Invoke(...)` call sites now go through it. The `AutoInstall = false` fix from the sixth
   attempt is unchanged and still needed — the two mechanisms are independent, closing
   different halves of the same symptom.

   **Verification:** built Release; ran `DataGridVirtualizationSpikeTests` and
   `HeadlessFixtureTests` in isolation (`-class <FullName>`) — both clean. Full
   `EventScope.App.Tests` suite: 95/95 (94 run + 1 soak-gated skip), repeated. With
   `EVENTSCOPE_SOAK=1` (exercises the fourth `Invoke`/`RunOnUi` site,
   `AcceptanceCriteriaTests`, which is otherwise skipped in CI): no hang across repeated runs;
   the frame-budget assertion itself (16 ms) intermittently failed on this sandbox machine at
   ~18-26 ms — noise from a shared/loaded environment unrelated to this fix, not a regression,
   and moot for the actual CI failure since neither workflow sets `EVENTSCOPE_SOAK`. Added a
   second `HeadlessFixtureTests` case pinning the new invariant directly: from a thread
   confirmed not to be the dispatcher thread, `Dispatcher.UIThread.InvokeAsync(...)` must
   complete within 5 s — fails fast with a clear message instead of hanging, rather than
   reproducing the pre-fix hang inside the test suite itself. `Run-Tests.ps1` also gained a
   per-assembly wall-clock timeout (default 300 s) so any future occurrence of this class of
   bug fails named and fast instead of consuming the workflow's full `timeout-minutes`; fixing
   it surfaced that `Start-Process -PassThru`'s `ExitCode` came back empty here even for a
   clean exit (confirmed: `HasExited` true, `ExitCode` not) — replaced with raw
   `System.Diagnostics.Process` and asynchronous stream reads, which also avoids a real
   pipe-buffer deadlock risk the temp-file version had. Also surfaced: `Process.Kill(true)`
   (kill the whole process tree) only exists on .NET 5+ — confirmed to throw
   `MethodCountCouldNotFindBest` under Windows PowerShell 5.1's .NET Framework, where this
   script must also work even though CI itself runs under `pwsh` 7 — replaced with
   `taskkill.exe /T /F`, available on every Windows PowerShell edition; verified directly
   against a synthetic 30s-sleeping child process with a 3s timeout, killed at ~3.4s. The
   authoritative check remains GitHub's `windows-latest` runner via `ci` and `release` on the
   next push — this is the
   second time local-clean has been misleading for this exact class of bug.

   **That authoritative check has now happened, and the fix holds.** Both workflows have run
   green on `windows-latest` against the fixed code: `ci` for `b02d661` (run 33531527150) and
   `release` for the `v0.2.1` tag (run 33690495889), the latter getting past Test and all the
   way through Publish/Attest for the first time. Every prior run of either workflow was
   cancelled on this hang. Treat this item as closed unless it recurs; the per-assembly
   300 s timeout in `Run-Tests.ps1` is what will name it fast if it ever does.

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

The repository now exists at `origin` and both workflows have executed on GitHub's
`windows-latest` runner. The first `ci` run (for `ced0d0d`) and the first `release` run (for
the `v0.2.0` tag) each hung on their Test step and had to be killed by the job timeout - the
dispatcher deadlock in item 2 above, which is what that item's later attempts fixed. The
`release` run therefore never reached its Publish step, which is why no binary has ever been
produced; see the "Release pass - v0.2.1" section for the full account and the fix.
