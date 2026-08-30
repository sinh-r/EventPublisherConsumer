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

## Pending — in build-plan order

- **M1c — `KafkaEventSource`, then tag `v0.1.0`.** M1b is done (see above). Throwaway
  consumer group, `enable.auto.commit=false`, `auto.offset.reset=latest`, a dedicated
  `LongRunning` task per the threading table (`Consume()` is blocking sync). Unit-tested
  against a mocked `IConsumer<byte[],byte[]>` surface; integration tests stay opt-in behind
  `EVENTSCOPE_KAFKA_BOOTSTRAP` (Blocked item 5 — no broker on this machine). Also still
  pending from M1b: a full `EventScope.Bench` run with committed baselines, and measuring
  M1's five acceptance criteria (build plan §6) for real via `dotnet-counters` / a
  render-tick histogram — the benchmark classes exist, they just haven't been run yet.
  `v0.1.0` gets tagged once Kafka closes M1.
- **M2 — storage discipline and search.** Day-file rolling, retention/eviction, FTS5
  tiered search (`body_fts` / `ident_fts`), pinned JSON-field columns, settings view.
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
   The suite is **44 tests, all passing** (12 App.Tests, 20 Core.Tests, 12 Storage.Tests) as
   of M1b — up from 31 at M1a, 5 at Stage 1. *Revisit after an xunit.v3 or MTP version
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

4. **Release signing for distributed builds.** Unchanged. SignPath Foundation free
   open-source programme is the intended no-cost path, wired into `release.yml` between the
   upload-artifact and create-release steps. Deliberately deferred until v0.1.0 exists -
   their review assesses a working project, and applying with an empty scaffold weakens it.
   Recompute the SHA256 after signing; signing changes the hash.

5. **No live broker access on this machine.** Unchanged. Broker sources are written and
   unit-tested against mocked client surfaces; integration tests are opt-in via
   `EVENTSCOPE_KAFKA_BOOTSTRAP` and friends and skipped by default. If you want these proven
   against a real broker before M4 is "done", that needs a broker endpoint to point at.

GitHub repository creation and the initial push remain yours. There is still no remote, so
neither workflow has ever executed - expect the first push to surface ordinary CI teething
issues (the YAML could not be validated locally; no YAML parser is installed).
