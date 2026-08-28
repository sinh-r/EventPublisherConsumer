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

## Pending — in build-plan order

- **M1 — Kafka consumer, end to end.** Not started beyond the abstractions above:
  `FakeEventSource` (built first — every throughput/memory acceptance criterion is
  measured against it), the byte-bounded ingest channel (`ByteBudget`), segment writer
  over `RandomAccess` with LZ4 block framing, SQLite schema + `SqliteBatchWriter`, the
  header ring with per-file string interning, `KafkaEventSource`, the `IngestCoalescer`,
  the Avalonia shell (tab strip, toolbar, grid, detail pane, status bar) built on
  `MessageRowsView`, and the async segment reader.
- **M2 — storage discipline and search.** Day-file rolling, retention/eviction, FTS5
  tiered search (`body_fts` / `ident_fts`), pinned JSON-field columns, settings view.
- **M3 — publisher.** Generator token parser + two-pass engine (Kahn + Tarjan SCC for
  cycle detection), JSON tree editor, preview pane, schema inference, burst publish.
- **M4 — Service Bus and SQS.** `ServiceBusEventSource`, `SqsEventSource`, and the
  capability-binding audit (no `if (broker == …)` in the view layer).
- **Stage 5 — polish.** Connection manager + per-broker forms, deep-search overlay,
  large-payload confirmation, toast, light theme, full keyboard map.
- **Release engineering — real code signing.** Not started; deliberately deferred (see
  below).

---

## Blocked / needs a decision from you

Nothing is blocking the next unit of implementation work (M1) right now. Two things need
your input later, not immediately:

1. **Release signing for distributed builds.** The local dev signing workaround above
   only helps this machine — it does nothing for someone downloading a built `.exe` from
   GitHub. For that, Smart App Control and SmartScreen need a real CA-chained signature.
   The practical no-cost path for an open-source project is **SignPath.io**'s free
   code-signing program, wired into the release CI pipeline. This is a release-pipeline
   task, not something to set up now — flagging it here so it isn't forgotten before the
   first public release.
2. **No live broker access on this machine.** Kafka/ASB/SQS source implementations will
   be written and unit-tested against mocked client surfaces per the build plan; the
   integration tests that hit a real broker are opt-in via environment variables
   (`EVENTSCOPE_KAFKA_BOOTSTRAP`, etc.) and skipped by default. If you want these proven
   against a real broker before M4 is considered "done," that needs a broker endpoint to
   point at.

GitHub repository creation and the initial push are intentionally left to you, per your
instruction — this pass stops at a clean local commit.
