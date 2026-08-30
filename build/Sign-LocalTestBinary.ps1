<#
.SYNOPSIS
  Signs every unsigned DLL/EXE in a local dev build output directory with the EventScope
  local dev signing certificate.

.DESCRIPTION
  Windows Smart App Control blocks freshly-built, unsigned binaries on some machines —
  not only a test project's own output assembly, but its copied dependencies too
  (Avalonia.*.dll, SQLitePCLRaw.provider.e_sqlite3.dll, and similar have all been observed
  blocked independently of the test assembly itself). Signing the whole output directory,
  not just $(TargetPath), is what actually makes a Debug test run reliable — see
  PROGRESS.md's M1b entry for the measurements that motivated widening this from a
  single-file signer.

  Signs with a self-signed certificate installed into CurrentUser\TrustedPublisher, which
  is enough for local execution. This is NOT a substitute for real release signing (a
  CA-issued certificate, or a free OSS signing service) — that is a separate task for the
  release pipeline, not local development.

  Silently no-ops if the certificate isn't present, so this is safe to wire into every
  contributor's build even if they haven't run the cert setup. Also silently skips files
  that are already signed (whether by this cert or another), so re-running after an
  incremental build only touches what actually needs it.

  Expected status: Set-AuthenticodeSignature reports "UnknownError" / "terminated in a
  root certificate which is not trusted" for this signature, because the cert is
  self-signed and only installed into CurrentUser\TrustedPublisher, not CurrentUser\Root.
  That's fine — Windows Code Integrity (what Smart App Control enforces on) honors
  TrustedPublisher directly and does not require a full chain to a trusted root, which is
  the whole point of using that store here rather than Root. This script only warns if
  signing itself failed outright (e.g., the private key wasn't marked exportable, or the
  file is in use), not on that expected chain-status mismatch.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Directory
)

if (-not (Test-Path $Directory)) {
    return
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq "CN=EventScope Local Dev Signing" } |
    Select-Object -First 1

if (-not $cert) {
    Write-Host "Sign-LocalTestBinary: no local dev signing cert found, skipping signature for $Directory"
    return
}

$files = Get-ChildItem $Directory -Include "*.dll", "*.exe" -Recurse -ErrorAction SilentlyContinue
$expectedStatuses = "Valid", "UnknownError"

foreach ($file in $files) {
    $existing = Get-AuthenticodeSignature -FilePath $file.FullName -ErrorAction SilentlyContinue
    if ($existing -and $existing.Status -ne "NotSigned") {
        continue
    }

    $result = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -ErrorAction SilentlyContinue
    if ($null -eq $result -or $result.Status -notin $expectedStatuses) {
        Write-Warning "Sign-LocalTestBinary: signing failed for $($file.FullName) (status: $($result.Status))"
    }
}
