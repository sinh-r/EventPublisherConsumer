# Distribution & Code Signing Plan

Implementation spec for making this Windows desktop app runnable locally and
distributable to others via GitHub, without SmartScreen / Smart App Control
blocking it.

---

## Resolved values

These were placeholders when this document was written. They are now settled, and every
code block below has been substituted accordingly.

| Was | Value |
|---|---|
| `<APP_NAME>` | `EventScope` |
| `<REPO_OWNER>` | `rsrishabh007` |
| `<REPO_NAME>` | `EventScope` |
| `<CSPROJ_PATH>` | `src/EventScope.App/EventScope.App.csproj` |
| `<DOTNET_VERSION>` | `10.0.x` (SDK pinned to 10.0.400 in `global.json`) |
| License | MIT |

The product name is **EventScope** throughout — assemblies, namespaces, the solution file
and the `EVENTSCOPE_*` environment variables. The working directory is still called
`EventPublisherConsumer`; that is historical and not the product name.

The app project is `EventScope.App`, so the default assembly name is `EventScope.App` and
the published binary would be `EventScope.App.exe`. Every path below says `EventScope.exe`,
so the csproj must set `<AssemblyName>EventScope</AssemblyName>` explicitly.

---

## Context

The app is a cloud-agnostic event publisher/subscriber desktop tool for Windows.
It talks to Azure Service Bus, AWS SQS, and Kafka (Apache / Confluent / Oracle),
so it carries several reflection-heavy cloud SDKs.

Two goals:

1. Run it locally for self-testing.
2. Distribute it publicly — source on GitHub, prebuilt `.exe` on Releases.

The blocker is that unsigned binaries trigger SmartScreen "Unknown publisher"
warnings for everyone, and are outright blocked on machines with Smart App
Control enabled.

### Constraints that shape this plan

- Azure Artifact Signing (formerly Trusted Signing) is **not usable here**.
  Its public trust is limited to organizations in the US, Canada, EU and UK,
  and individual developers in the US and Canada only. The maintainer is an
  individual developer in India.
- Self-signed certificates do **not** satisfy Smart App Control. They are
  useful only to verify a signing pipeline works.
- SignPath Foundation issues free OV-level certificates to open-source
  projects, verified against the public repository rather than personal
  identity. This requires builds to come from CI, not a local machine.
- Signing does not grant instant SmartScreen trust. Reputation accrues to a
  consistent publisher identity across releases, so signing identity must
  never be rotated casually once established.

---

## Phase 0 — Local development machine

**Manual, not automatable. Do this by hand.**

- [ ] Windows Security → App & browser control → Smart App Control settings → **Off**

This is a one-way switch: once off, it can only be re-enabled by reinstalling
Windows. That is the normal state for a development machine.

- [ ] Keep a second machine or Windows VM available for testing release
      artifacts as a real user would receive them (downloaded, with
      Mark-of-the-Web attached), rather than testing `bin/Release` output
      directly.

**Do not** attempt to work around SAC with a self-signed certificate.

### Reconciliation — a self-signed workaround already exists

This rule was written after the fact and conflicts with what is already in the repo.
Before this document existed, SAC was blocking freshly-built, unsigned **test** binaries
on the dev machine (a Code Integrity block confirmed in the
`Microsoft-Windows-CodeIntegrity/Operational` log, not a malware detection). The fix was a
self-signed `CN=EventScope Local Dev Signing` certificate in `CurrentUser\TrustedPublisher`
plus an automatic post-build signing step: `Directory.Build.targets` and
`build/Sign-LocalTestBinary.ps1`.

Two things about it matter here:

- It covers **test projects only** (`Condition="$(IsTestProject) == true"`, Windows, Debug).
  It does **not** cover `EventScope.App`, so it would not have helped the moment M1 builds a
  runnable app anyway. Turning SAC off is the actual fix, not an optional alternative.
- It is local-only and never ships. Nothing signs a release artifact, no certificate is
  committed, and the script silently no-ops on any machine without the cert, on non-Windows,
  on CI, and in Release. So it does not violate the *intent* of the rule above, which is
  about distributed binaries.

**Resolution:** turn SAC off as this phase says. Leave the two files in place as an inert
fallback until M1 is complete and a full app run has confirmed SAC-off removed the need,
then delete `Directory.Build.targets`, delete `build/Sign-LocalTestBinary.ps1`, and remove
the certificate from `CurrentUser\TrustedPublisher`. Do **not** widen the target to cover
`EventScope.App` in the meantime.

---

## Phase 1 — Repository preparation

Everything downstream depends on this, and SignPath's review will check it.

- [ ] Make the repository public.
- [ ] Add an OSI-approved license at `LICENSE`. Use **MIT** for minimum
      friction, or **Apache-2.0** if the patent grant is wanted. Pick one and
      reference it consistently in the csproj and README.
- [ ] Write `README.md` containing:
  - What the tool does and which brokers it supports
  - Screenshots of the publisher and consumer screens
  - An **Install** section (populated in Phase 3)
  - A short "Why does Windows warn about this?" section explaining the
    signing status honestly
- [ ] Add `.github/ISSUE_TEMPLATE/` and a brief `CONTRIBUTING.md`. Low effort,
      and it makes the project read as a maintained OSS project during review.

### Assembly metadata

Set these in `src/EventScope.App/EventScope.App.csproj`. The publisher name users eventually see comes
from the certificate, but consistent metadata matters for reputation and for
the SignPath review.

```xml
<PropertyGroup>
  <Product>EventScope</Product>
  <Company>rsrishabh007</Company>
  <Authors>rsrishabh007</Authors>
  <AssemblyTitle>EventScope</AssemblyTitle>
  <Description>Cloud-agnostic event publisher and subscriber</Description>
  <Version>0.1.0</Version>
  <RepositoryUrl>https://github.com/rsrishabh007/EventScope</RepositoryUrl>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
</PropertyGroup>
```

### Publish configuration

**Amended.** The original version of this section put all five properties in the csproj.
Do not do that. Setting `RuntimeIdentifier` in the csproj makes *every* build RID-specific,
which slows restore and complicates the test projects that reference the app. Only the
metadata belongs in the project file; the publish switches belong on the command line.

In `src/EventScope.App/EventScope.App.csproj`:

```xml
<PropertyGroup>
  <AssemblyName>EventScope</AssemblyName>
  <PublishTrimmed>false</PublishTrimmed>
  <PublishAot>false</PublishAot>
</PropertyGroup>
```

On the command line, in `release.yml` and in the README:

```
dotnet publish src/EventScope.App/EventScope.App.csproj -c Release ^
  -r win-x64 --self-contained ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish
```

`IncludeNativeLibrariesForSelfExtract` is load-bearing, not optional: `Microsoft.Data.Sqlite`
bundles the native `e_sqlite3` and `Confluent.Kafka` bundles `librdkafka`. Without it the
single-file exe fails at runtime rather than at build time.

**Do not enable trimming.** The Azure Service Bus, AWS SDK and Kafka client
libraries are reflection-heavy and will break in non-obvious ways at runtime. The same
applies to `PublishAot`.

Watch for `TreatWarningsAsErrors=true` (set repo-wide in `Directory.Build.props`) colliding
with the single-file analyzer warnings IL3000/IL3002. That combination can fail the Release
publish even though the Debug build is clean, and may need a targeted suppression.

- [ ] Verify the publish command above produces a single working `publish/EventScope.exe`.
- [ ] Launch that exe and confirm all three broker types can still connect.
      **Deferred:** not possible until M4. There is no UI before M1 and no Service Bus or
      SQS source before M4. Check "the app launches and consumes from a fake source" at the
      end of M1, and revisit this line for real at M4.

---

## Phase 2 — GitHub Actions release workflow

Needed regardless of signing, and it is the hook SignPath attaches to later.

Create `.github/workflows/release.yml`:

```yaml
name: release

on:
  push:
    tags: ['v*']
  workflow_dispatch:

permissions:
  contents: write
  id-token: write
  attestations: write

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore
        run: dotnet restore src/EventScope.App/EventScope.App.csproj

      - name: Publish
        run: dotnet publish src/EventScope.App/EventScope.App.csproj -c Release -o publish --no-restore

      - name: Upload unsigned artifact
        uses: actions/upload-artifact@v4
        with:
          name: unsigned
          path: publish/EventScope.exe

      - name: Attest build provenance
        uses: actions/attest-build-provenance@v1
        with:
          subject-path: publish/EventScope.exe

      - name: Compute SHA256
        shell: pwsh
        run: |
          Get-FileHash publish/EventScope.exe -Algorithm SHA256 |
            Select-Object -ExpandProperty Hash |
            Out-File -Encoding ascii publish/EventScope.exe.sha256

      - name: Create release
        uses: softprops/action-gh-release@v2
        with:
          files: |
            publish/EventScope.exe
            publish/EventScope.exe.sha256
          generate_release_notes: true
```

The provenance attestation and published hash are not cosmetic. They give
security-conscious users something verifiable while the binary is still
unsigned, and they demonstrate a clean repo-to-binary path during SignPath
review.

- [ ] Commit the workflow.
- [ ] Exercise it via its `workflow_dispatch` trigger. Confirm it publishes, attests and
      uploads a runnable artifact.
- [ ] **Do not tag `v0.1.0` yet.** `EventScope.App` is still the unmodified Avalonia
      template - an empty window. Tagging now would burn the version on an artifact that
      does nothing and produce a release page that misrepresents the project. Tag at the
      end of M1, when the app actually consumes and displays messages.

Also add a `build` job to this workflow that runs `dotnet test` on push and pull request.
`eventscope-implementation-plan.md` section 7 assumes a CI exists ("fail CI on >20%
regression" for the `EventScope.Bench` baselines) but no CI is specified anywhere. This is
that CI.

---

## Phase 3 — Scoop bucket

A convenience channel, not a fix for signing. Worth doing immediately because
`scoop install` does not attach Mark-of-the-Web, so it sidesteps the
SmartScreen prompt even before signing is in place — and the target audience is
developers who already have Scoop.

It does nothing for a user who clicks the `.exe` link on the Releases page, and
nothing for Smart App Control.

- [ ] Create a second repository named `scoop-EventScope`.
- [ ] Add `bucket/EventScope.json`:

```json
{
  "version": "0.1.0",
  "description": "Cloud-agnostic event publisher and subscriber for Azure Service Bus, AWS SQS and Kafka",
  "homepage": "https://github.com/rsrishabh007/EventScope",
  "license": "MIT",
  "architecture": {
    "64bit": {
      "url": "https://github.com/rsrishabh007/EventScope/releases/download/v0.1.0/EventScope.exe",
      "hash": "PASTE_SHA256_FROM_RELEASE"
    }
  },
  "bin": "EventScope.exe",
  "shortcuts": [["EventScope.exe", "EventScope"]],
  "checkver": "github",
  "autoupdate": {
    "architecture": {
      "64bit": {
        "url": "https://github.com/rsrishabh007/EventScope/releases/download/v$version/EventScope.exe"
      }
    }
  }
}
```

- [ ] Add to the README install section:

```
scoop bucket add EventScope https://github.com/rsrishabh007/scoop-EventScope
scoop install EventScope
```

- [ ] Test end to end on a clean machine or VM.

---

## Phase 4 — SignPath Foundation signing

Apply as soon as Phases 1 and 2 are complete. Approval takes anywhere from a
few days to a few weeks, and releases can continue unsigned in the meantime.

### Application

- [ ] Apply via the SignPath open-source community page.
- [ ] Have ready: project description, license, public repo URL, and how
      binaries are built (GitHub Actions from the public repo, published to
      GitHub Releases).
- [ ] Write it in English. Keep it short and factual.

### After approval

- [ ] Create the project and signing policy in SignPath.
- [ ] Store the API token as repo secret `SIGNPATH_API_TOKEN`.
- [ ] Note the organization ID, project slug, and signing policy slug.
- [ ] Insert a signing step into `release.yml` between the upload-artifact and
      create-release steps. The flow is: upload the unsigned artifact, submit a
      signing request referencing that artifact, wait for completion, download
      the signed binary back, and attach **that** to the release.

**Check SignPath's current documentation for the exact action name and input
parameters — these have changed across versions. Do not assume the shape of the
action from memory.**

- [ ] Update the workflow so the release attaches the *signed* exe.
- [ ] Recompute the SHA256 **after** signing — the hash changes — and update
      both the release asset and the Scoop manifest.
- [ ] Verify the signature on the downloaded artifact:
      `Get-AuthenticodeSignature .\EventScope.exe`
- [ ] Update the README to state the binary is signed by SignPath Foundation.

### Ongoing

- Sign every release from this point on, with the same identity.
- Never rotate signing identity casually. SmartScreen reputation accrues to a
  consistent publisher across releases; changing identity resets it.

---

## Fallback if SignPath declines

An OV certificate from Sectigo, DigiCert or GlobalSign runs roughly
$200–400/year. Since June 2023 the CA/Browser Forum requires the private key to
be held on an HSM or hardware token, so expect either a shipped USB token or a
cloud HSM subscription. Available in India, unlike Azure Artifact Signing.

EV certificates give faster SmartScreen reputation but are issued to
organizations only.

---

## README section to add while unsigned

Include this until Phase 4 completes:

> **Windows SmartScreen warning**
>
> Releases are not yet code-signed, so Windows may show an "Unknown publisher"
> warning. To run anyway, click **More info** → **Run anyway**, or right-click
> the file → **Properties** → **Unblock**. From PowerShell:
> `Unblock-File .\EventScope.exe`
>
> Every release is built by GitHub Actions from this repository and published
> with a SHA256 hash and a build provenance attestation, both verifiable
> against the release page. Installing via Scoop avoids the warning entirely.

---

## Sequencing

**Revised.** The original table assumed the app already worked. It does not yet - see
`PROGRESS.md`. Distribution work is interleaved with the build milestones instead:

| When | Work |
|---|---|
| Now | Phase 0 (SAC off), Phase 1 (repo prep, metadata, publish config) |
| Now | Phase 2 workflow, exercised via `workflow_dispatch` only - no tag |
| Then | **M1 - Kafka consumer end to end** (see `eventscope-build-plan.md` section 5) |
| End of M1 | Remove the local signing workaround; README screenshots; tag `v0.1.0` |
| After v0.1.0 | Phase 3 (Scoop bucket, hash from the real release), README install section |
| After v0.1.0 | Submit SignPath application |
| On approval | Phase 4 |

The SignPath application is deliberately held until v0.1.0 exists. Their review assesses a
working open-source project, and applying with an empty scaffold weakens it. Approval takes
days to weeks; releases continue unsigned in the meantime, which is what the README warning
section below is for.

---

## Out of scope — do not do these

- Do not create or ship a self-signed certificate as a workaround. (One already exists
  for local test binaries and predates this document - see "Reconciliation" under Phase 0
  for why it is not a violation and when it gets deleted.)
- Do not enable `PublishTrimmed` or `PublishAot`.
- Do not attempt Azure Artifact Signing setup; eligibility is geographic and
  does not apply here.
- Do not build release artifacts locally and upload them by hand. SignPath
  verifies binaries came from CI.
- Do not commit any broker connection strings, SAS tokens, or AWS credentials
  as fixtures or defaults when making the repo public. Audit history before
  going public.
