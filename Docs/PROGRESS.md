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
   The suite is **5 tests, all passing** (4 App.Tests, 1 Core.Tests; Storage.Tests is still
   empty). *Revisit after an xunit.v3 or MTP version bump - if it starts working, delete
   the script and put `dotnet test` back.*

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
   fallback; it no-ops without the cert and only ever touches Debug test binaries.

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
