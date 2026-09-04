# SignPath Foundation application — ready to submit

Execution of `DISTRIBUTION_PLAN.md` Phase 4. Submitting is a manual action on
<https://signpath.org/apply>; everything that can be prepared in advance is below.

**Why this matters:** SmartScreen trusts a Windows binary either by its publisher's code
signature or by download volume for that exact hash. EventScope has neither, so every direct
download shows *"EventScope.exe isn't commonly downloaded"*. Reputation is **per hash**, so it
resets with every release — volume will never accumulate. A consistent signing identity is the
only thing that ends this permanently. (The Scoop bucket sidesteps it today by never attaching
Mark-of-the-Web; that is a real fix for Scoop users and no help to anyone else.)

---

## Eligibility, checked against their published conditions

From <https://signpath.org/terms.html>. Assessed against the repository as it stands, not from
memory.

| Their condition | EventScope | Evidence |
|---|---|---|
| No malware or unwanted programs | Pass | A read-only event viewer plus a publisher, all user-initiated |
| OSI-approved licence, no commercial dual-licensing | Pass | MIT, `LICENSE` at the repo root |
| No proprietary, non-open-source component | **See "Decide before submitting"** | Dependencies are all OSS (below); one design-mockup file needs a call |
| Actively maintained | Pass | Five releases; `v0.1.0` → `v0.4.0` inside a fortnight, with `Docs/PROGRESS.md` as the trail |
| Already released in the form to be signed | Pass | `v0.4.0` publishes exactly the `EventScope.exe` that would be signed |
| Functionality described on the download page | Pass | `README.md` with a broker-support table and screenshots; every release carries notes |
| Built from source verifiably | **Strong** | GitHub Actions from the public repo, plus a build provenance attestation per release |
| Every release manually approved | Pass | Releases only ever come from a hand-pushed tag |
| Signing team owns the source | Pass | Single maintainer, sole owner of repo and workflows |

**Repository visibility confirmed, not assumed:** unauthenticated `api.github.com` requests
against `sinh-r/EventPublisherConsumer` return releases and workflow runs, which only works for
a public repo.

### Dependencies, all open source

Avalonia and its packages (MIT), CommunityToolkit.Mvvm (MIT), Microsoft.Data.Sqlite (MIT),
K4os.Compression.LZ4 (MIT), Confluent.Kafka (Apache-2.0), Azure.Messaging.ServiceBus (MIT),
AWSSDK.SQS (Apache-2.0), xunit.v3 (Apache-2.0), BenchmarkDotNet (MIT). Pinned centrally in
`Directory.Packages.props`.

---

## Decide before submitting

**The Claude Design mockup files.** `Mockup preparation from spec/support.js` — 69 KB of
generated `dc-runtime` marked "GENERATED … do not edit" with no licence header — was already
untracked and gitignored during the distribution pass for exactly this reason. Three files
alongside it are still tracked:

- `EventScope.dc.html` (106 KB) — your own design content in the tool's `<x-dc>` file format,
  carrying no generated-code marker and no licence header
- `_ds/…/styles.css` — bespoke to this project
- `_ds/…/_ds_bundle.js` — a 300-byte empty stub

The risk is low: none of it is compiled into the signed binary, and the substance is yours. But
SignPath's condition is written strictly ("may not contain any proprietary, non open-source
component"), and a reviewer who opens the repo will see a file format they do not recognise with
no licence on it. **Cheapest resolution: gitignore the `Mockup preparation from spec/` directory
entirely**, the way `support.js` already is. It is a local design aid — the build does not need
it and the app does not ship it.

---

## What to submit

**Project name:** EventScope

**Repository:** <https://github.com/sinh-r/EventPublisherConsumer>

**Licence:** MIT

**Description** (their form asks what the project does):

> EventScope is a Windows desktop client for working with event streams. It connects to Apache
> Kafka, streams live messages into a virtualized grid, captures them to local disk with a
> configurable size cap, and provides tiered search over message bodies — an instant filter, a
> full-text index, and a deep scan that reads every stored body. It can also turn a captured
> message into a template with generated fields and publish it back to the broker. Azure Service
> Bus and AWS SQS support is planned.
>
> It is a diagnostic tool for developers working with message-based systems: the kind of thing
> you reach for when you need to see what is actually on a topic.

**How binaries are built:**

> Every release is built by GitHub Actions from the public repository, triggered by a signed
> maintainer pushing a version tag — never built locally and uploaded by hand. The workflow is
> `.github/workflows/release.yml`. It restores, runs the full test suite, publishes a
> self-contained single-file win-x64 executable, and produces a GitHub build provenance
> attestation (`actions/attest-build-provenance`) binding the artifact digest to the workflow,
> commit and tag. The published SHA256 accompanies every release as `EventScope.exe.sha256`.

Keep it in English, short and factual — that is their stated preference.

---

## After approval

The workflow is **already wired**; none of this needs a code change under time pressure.

1. Create the project and signing policy in SignPath; note the organization ID, project slug and
   signing policy slug.
2. Add repository **secret** `SIGNPATH_API_TOKEN`.
3. Add repository **variables** `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`,
   `SIGNPATH_SIGNING_POLICY_SLUG`. Variables rather than secrets deliberately: they are not
   sensitive, and a missing variable fails loudly instead of silently expanding to an empty
   string.
4. Push a tag. The `Sign with SignPath` step activates on the token being present, and the
   `Report signing status` step prints the signer subject into the run log.
5. Verify on the downloaded asset: `Get-AuthenticodeSignature .\EventScope.exe`.
6. Update `README.md` to say the binary is signed, and refresh the Scoop bucket's note that says
   releases are not yet code-signed.

**The hash changes when the binary is signed.** The workflow already handles this — signing runs
before both the attestation and the SHA256 computation, so both describe the signed file. The
Scoop manifest picks the new hash up from the published `.sha256` via its `autoupdate` block.

**Never rotate the signing identity casually.** SmartScreen reputation accrues to a consistent
publisher across releases; changing identity resets it.

---

## If SignPath declines

Per `DISTRIBUTION_PLAN.md`: an OV certificate from Sectigo, DigiCert or GlobalSign runs roughly
**$200–400/year**, and since June 2023 the CA/Browser Forum requires the private key on an HSM or
hardware token — so expect a shipped USB token or a cloud HSM subscription. EV certificates earn
SmartScreen reputation faster but are issued to organisations only. **Azure Artifact Signing is
not available in India**, so it is not an option here.
