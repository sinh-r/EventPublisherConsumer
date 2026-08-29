# Contributing to EventScope

Thanks for looking. The project is pre-alpha and the architecture is still settling, so
**please open an issue before starting anything substantial** — there is a good chance the
area you are looking at is mid-rewrite or already planned.

## Getting set up

Requires the .NET 10 SDK (exact version pinned in `global.json`).

```
dotnet build EventScope.slnx
./build/Run-Tests.ps1
```

Use `build/Run-Tests.ps1`, not `dotnet test` - see the script header for why the latter
reports zero tests on this toolchain.

The full suite runs with no broker available. Broker integration tests are opt-in and skip
themselves unless the matching environment variable is set (`EVENTSCOPE_KAFKA_BOOTSTRAP`
and friends).

## Before you write code

Read [`Docs/eventscope-build-plan.md`](Docs/eventscope-build-plan.md). It is the
authoritative document — where it and the implementation plan disagree, the build plan wins.
Most of the non-obvious constraints in this codebase are load-bearing and are explained
there. A few worth knowing up front:

- **`EventScope.Core` has no broker or UI dependencies.** A test asserts this against the
  compiled assembly. Do not add a `Confluent.Kafka`, `Azure.Messaging.*`, `AWSSDK.*` or
  `Avalonia.*` reference to it.
- **Never bind the message grid to an `ObservableCollection`.** The grid is backed by
  `MessageRowsView`, a windowed adapter over a ring of structs that materializes row view
  models only for visible rows. This is the single most important correctness detail in the
  UI layer.
- **No broker-type switches in the view layer.** No `if (broker == "kafka")`. Every
  broker-specific control binds its `IsEnabled`/`IsVisible` to a flag on
  `SourceCapabilities`. Adding a broker should require zero changes to `EventScope.App`.
- **Never read a payload on the UI thread.** Segment reads go through the async path.
- **No `DateTime.Now` in Core or Storage.** Inject `TimeProvider` so time-dependent
  behaviour stays testable.

## Style

- Warnings are errors, repo-wide. Keep the build clean rather than suppressing.
- Nullable reference types are enabled. Do not `!` your way past a real nullability issue.
- Package versions are pinned centrally in `Directory.Packages.props`. Add the version
  there, and a bare `<PackageReference Include="..." />` in the project.
- Match the surrounding code. Comments in this codebase explain *why* something is
  counter-intuitive, not what the line does — several of them record measurements that
  justify an unusual choice, so please do not delete them without re-measuring.

## Pull requests

- One logical change per PR, with tests.
- Say what you measured if the change touches ingest, storage or the grid. Those paths have
  explicit throughput and memory acceptance criteria in the build plan.
- Do not commit broker connection strings, SAS tokens or AWS credentials — not as fixtures,
  not as defaults, not in test data.

## Reporting bugs

Use the issue templates. For anything involving throughput or memory, include the broker,
the message rate, and the payload size — those three determine almost everything about
behaviour under load.
