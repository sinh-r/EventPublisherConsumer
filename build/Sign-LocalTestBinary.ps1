<#
.SYNOPSIS
  Signs a local dev build output with the EventScope local dev signing certificate.

.DESCRIPTION
  Windows Smart App Control blocks freshly-built, unsigned test executables on some
  machines. This signs the build output with a self-signed certificate that has been
  installed into CurrentUser\TrustedPublisher on this machine, which is enough for local
  execution. This is NOT a substitute for real release signing (a CA-issued certificate,
  or a free OSS signing service) — that is a separate task for the release pipeline, not
  local development.

  Silently no-ops if the certificate isn't present, so this is safe to wire into every
  contributor's build even if they haven't run the cert setup.

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
    [string]$Path
)

if (-not (Test-Path $Path)) {
    return
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq "CN=EventScope Local Dev Signing" } |
    Select-Object -First 1

if (-not $cert) {
    Write-Host "Sign-LocalTestBinary: no local dev signing cert found, skipping signature for $Path"
    return
}

$result = Set-AuthenticodeSignature -FilePath $Path -Certificate $cert -ErrorAction SilentlyContinue

$expectedStatuses = "Valid", "UnknownError"
if ($null -eq $result -or $result.Status -notin $expectedStatuses) {
    Write-Warning "Sign-LocalTestBinary: signing failed for $Path (status: $($result.Status))"
}
