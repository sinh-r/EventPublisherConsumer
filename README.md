# EventScope

A cloud-agnostic event publisher and subscriber for Windows. One desktop tool for
inspecting and producing messages across **Apache/Confluent Kafka**, **Azure Service Bus**
and **AWS SQS**, without switching between three vendor consoles.

> **Status: alpha, under active development.** Kafka works end to end — consume, persist,
> search, and publish back, all through a real UI with no environment variables required.
> Azure Service Bus and AWS SQS are not implemented yet (both are stubbed projects, tracked
> as milestone M4). There is no tagged release yet. See
> [`Docs/PROGRESS.md`](Docs/PROGRESS.md) for the full, honestly-kept status — including what
> currently falls short of its own acceptance criteria.
>
> Try it with no setup at all: `dotnet run --project src/EventScope.App`, close the
> connection dialog, and click **Start** to stream a synthetic feed. Or open that same
> dialog and add a real Kafka connection.

## What it does today

- **Real Kafka, no config files or env vars.** Add a broker from the connection manager,
  test it (a real `AdminClient` metadata probe, not a ping), save it, and connect — the app
  remembers it for next time. A saved connection's password is DPAPI-protected at rest.
- **Non-destructive by default.** Every Kafka connection here uses a fresh, throwaway
  consumer group with auto-commit disabled, so running this tool never disturbs a real
  consumer group's offsets or lag. SQS (once M4 lands) cannot peek without consuming, and
  the UI will say so permanently rather than hiding it.
- **Everything is kept.** Messages stream to a local append-only log (LZ4-compressed
  segments) with a SQLite index, day-file rolling, and capped retention with eviction — so
  you can scroll back through a saturated run instead of watching lines disappear.
- **Search that works on volume.** Full-text search over message bodies (FTS5), trigram
  infix search on message and correlation IDs, and a streaming deep scan for anything the
  index doesn't cover yet.
- **Bounded by design.** A byte-budgeted ingest path, a configurable on-disk cap with
  eviction, and a message grid that never materializes more rows than are on screen — this
  is the actual point of the project, not an afterthought.
- **A publisher, not just a viewer.** Take a consumed message, turn it into a template with
  generated fields (`{{guid}}`, `{{now:iso}}`, `{{int:1..100}}`, `{{ref:$.path}}` — with
  cycle detection), and publish or burst it back to the same broker.

### Broker support

| Broker | Status |
|---|---|
| Apache / Confluent Kafka | Done — consume, publish, partition targeting, connection testing |
| Azure Service Bus | Not started (M4) |
| AWS SQS | Not started (M4) |

## Screenshots

<table>
<tr><td width="50%">

**Connection manager** — add, test, and connect to a real Kafka broker with no config files.

<img src="ScreenShots/connection-manager.png" alt="Connection manager showing the Fake source and buttons to add Kafka, Azure Service Bus, or AWS SQS connections">

</td><td width="50%">

**Consumer view** — a selected row's full body in the detail pane, streaming at 10k msg/s.

<img src="ScreenShots/consumer-view.png" alt="Message grid streaming synthetic orders, with a selected row's JSON body shown in the detail pane">

</td></tr>
<tr><td colspan="2">

**Publisher** — "Use as template" schema-infers generators from a consumed message
(`{{int:0..2016936}}`, `{{guid}}`, …), live JSON preview, publish or burst.

<img src="ScreenShots/publisher-view.png" alt="Publisher panel with schema-inferred generator tokens and a coloured JSON preview">

</td></tr>
</table>

## Install

Nothing to install yet — no release has been cut. This section will carry the download and
a Scoop bucket once the first tagged release ships.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (the exact version is
pinned in `global.json`).

```
git clone https://github.com/rsrishabh007/EventScope.git
cd EventScope
dotnet build EventScope.slnx
./build/Run-Tests.ps1
```

Tests are run through `build/Run-Tests.ps1`, not `dotnet test`. On the current toolchain
(xunit.v3 4.0.0 / Microsoft.Testing.Platform 2.3.3 / .NET SDK 10.0.400) `dotnet test`
reports "Zero tests ran" for every assembly even though the tests pass; the script runs the
xUnit v3 test executables directly, which is the framework's native execution model. Its
header documents the problem in full.

To produce the single-file executable:

```
dotnet publish src/EventScope.App/EventScope.App.csproj -c Release ^
  -r win-x64 --self-contained ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish
```

That writes a self-contained `publish/EventScope.exe`. It is large (~130 MB) because it
bundles the .NET runtime, Avalonia, and the native Kafka and SQLite libraries. Trimming and
AOT are deliberately disabled — the broker SDKs are reflection-heavy and break under both.

Broker integration tests are opt-in and skipped unless the matching environment variable is
set (`EVENTSCOPE_KAFKA_BOOTSTRAP` and friends), so the suite runs with no broker available.

## Why does Windows warn about this?

> **Windows SmartScreen warning**
>
> Releases are not yet code-signed, so Windows may show an "Unknown publisher" warning. To
> run anyway, click **More info** → **Run anyway**, or right-click the file →
> **Properties** → **Unblock**. From PowerShell: `Unblock-File .\EventScope.exe`
>
> Every release is built by GitHub Actions from this repository and published with a SHA256
> hash and a build provenance attestation, both verifiable against the release page.
> Installing via Scoop avoids the warning entirely.

Code signing through the [SignPath Foundation](https://signpath.org/) open-source program is
planned once a first release ships. See [`Docs/DISTRIBUTION_PLAN.md`](Docs/DISTRIBUTION_PLAN.md).

## Documentation

| Document | What it covers |
|---|---|
| [`Docs/PROGRESS.md`](Docs/PROGRESS.md) | Live status — what is done, what is next, what is blocked, and every measurement and correction found while building it |
| [`Docs/eventscope-build-plan.md`](Docs/eventscope-build-plan.md) | Authoritative architecture and build order |
| [`Docs/eventscope-implementation-plan.md`](Docs/eventscope-implementation-plan.md) | Milestones, schema, acceptance criteria |
| [`Docs/eventscope-ui-spec.md`](Docs/eventscope-ui-spec.md) | Screen inventory and interaction behaviour |
| [`Docs/DISTRIBUTION_PLAN.md`](Docs/DISTRIBUTION_PLAN.md) | Release, packaging and code-signing plan |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). The project is early and the architecture is still
settling, so please open an issue before starting anything substantial.

## License

[MIT](LICENSE).
