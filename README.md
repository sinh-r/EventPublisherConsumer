# EventScope

A cloud-agnostic event publisher and subscriber for Windows. One desktop tool for
inspecting and producing messages across **Apache/Confluent Kafka**, **Azure Service Bus**
and **AWS SQS**, without switching between three vendor consoles.

> **Status: pre-alpha, under active development.** The shell runs against a synthetic
> in-memory source — streaming, follow/pin, row states, a detail pane with JSON preview —
> but nothing is written to disk yet (milestone M1b: SQLite, segments, the real Kafka
> source). The publisher and the Service Bus / SQS sources come after. There is no release
> yet. See [`Docs/PROGRESS.md`](Docs/PROGRESS.md) for the live status.
>
> Try it without a broker: `dotnet run --project src/EventScope.App`, then click **Start**.

## What it is for

Consuming from a busy topic and actually being able to read what came through. EventScope
is built around sustained high-volume ingest rather than a scrolling log window:

- **Non-destructive by default.** Peek where the broker supports it; destructive receive has
  to be explicitly armed. SQS cannot peek without consuming, and the UI says so permanently
  rather than hiding it.
- **Everything is kept.** Messages stream to a local append-only log with a SQLite index, so
  you can scroll back through a saturated run instead of watching lines disappear.
- **Search that works on volume.** Full-text search over message bodies, plus trigram infix
  search on message and correlation IDs, with a streaming deep scan for anything the index
  does not cover.
- **Bounded by design.** A byte-budgeted ingest path, a configurable on-disk cap with
  eviction, and a message grid that never materializes more rows than are on screen.
- **A publisher, not just a viewer.** Take a consumed message, turn it into a template with
  generated fields (`{{guid}}`, `{{now:iso}}`, `{{int:1..100}}`, `{{ref:$.path}}`), and burst
  publish it back.

## Install

Nothing to install yet — no release has been cut. This section will carry the download and
a Scoop bucket once v0.1.0 ships.

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
xUnit v3 test executables directly, which is the framework native model. Its header
documents the problem in full.

To produce the single-file executable:

```
dotnet publish src/EventScope.App/EventScope.App.csproj -c Release ^
  -r win-x64 --self-contained ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish
```

That writes a self-contained `publish/EventScope.exe`. It is large (~120 MB) because it
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
planned once the first release ships. See [`Docs/DISTRIBUTION_PLAN.md`](Docs/DISTRIBUTION_PLAN.md).

## Documentation

| Document | What it covers |
|---|---|
| [`Docs/PROGRESS.md`](Docs/PROGRESS.md) | Live status — what is done, what is next, what is blocked |
| [`Docs/eventscope-build-plan.md`](Docs/eventscope-build-plan.md) | Authoritative architecture and build order |
| [`Docs/eventscope-implementation-plan.md`](Docs/eventscope-implementation-plan.md) | Milestones, schema, acceptance criteria |
| [`Docs/eventscope-ui-spec.md`](Docs/eventscope-ui-spec.md) | Screen inventory and interaction behaviour |
| [`Docs/DISTRIBUTION_PLAN.md`](Docs/DISTRIBUTION_PLAN.md) | Release, packaging and code-signing plan |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). The project is early and the architecture is still
settling, so please open an issue before starting anything substantial.

## License

[MIT](LICENSE).
