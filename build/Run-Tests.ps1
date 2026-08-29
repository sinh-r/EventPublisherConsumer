<#
.SYNOPSIS
  Builds the solution and runs every xUnit v3 test executable directly.

.DESCRIPTION
  This exists because `dotnet test` does not work in this toolchain.

  On the .NET 10 SDK, VSTest is gone - Microsoft.Testing.Platform.MSBuild fails the build
  outright with "Testing with VSTest target is no longer supported by
  Microsoft.Testing.Platform on .NET 10 SDK and later" - so MTP is mandatory and
  global.json opts into it. But `dotnet test` then launches each test assembly in MTP
  server mode:

      --nologo --server dotnettestcli --dotnet-test-pipe testingplatform.pipe.<guid>

  and every assembly reports "Zero tests ran" with exit code 5, including assemblies that
  demonstrably contain passing tests. Verified against xunit.v3 4.0.0 /
  Microsoft.Testing.Platform 2.3.3 / SDK 10.0.400, with and without Microsoft.NET.Test.Sdk
  and xunit.runner.visualstudio, and with OutputType=Exe set explicitly. The documented
  configuration from xunit's own MTP page reproduces it.

  Running the same assemblies directly works correctly. xUnit v3 test projects are
  self-executing console apps - that is the framework's native execution model - so this
  script is not a hack around a misconfiguration, it is the path that actually runs.

  Revisit `dotnet test` after an xunit.v3 or Microsoft.Testing.Platform bump. If it starts
  working, delete this script and put `dotnet test` back in the workflows.

.PARAMETER Configuration
  Build configuration. Defaults to Debug.

.PARAMETER NoBuild
  Skip the build and run whatever is already in bin/.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $NoBuild) {
    Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot 'EventScope.slnx') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

# Discover test projects by the IsTestProject property the csprojs already set, rather
# than by naming convention, so a new test project is picked up automatically.
$testProjects = Get-ChildItem -Path (Join-Path $repoRoot 'tests') -Filter *.csproj -Recurse |
    Where-Object { (Get-Content $_.FullName -Raw) -match '<IsTestProject>\s*true\s*</IsTestProject>' }

if (-not $testProjects) { throw "No test projects found under tests/." }

$failed = @()
$ran = 0

foreach ($proj in $testProjects) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($proj.Name)
    $exe = Join-Path $proj.DirectoryName "bin/$Configuration/net10.0/$name.exe"

    if (-not (Test-Path $exe)) {
        Write-Warning "$name - no executable at $exe, skipping."
        continue
    }

    Write-Host ""
    Write-Host "=== $name ===" -ForegroundColor Cyan
    & $exe
    $code = $LASTEXITCODE
    $ran++

    # xUnit v3 exit codes: 0 = all passed, 8 = no tests found (fine for a project whose
    # tests have not been written yet). Anything else is a real failure.
    if ($code -eq 0) {
        Write-Host "$name passed." -ForegroundColor Green
    } elseif ($code -eq 8) {
        Write-Host "$name contains no tests yet." -ForegroundColor Yellow
    } else {
        Write-Host "$name FAILED (exit $code)." -ForegroundColor Red
        $failed += "$name (exit $code)"
    }
}

Write-Host ""
Write-Host "----------------------------------------"
if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "All $ran test assemblies passed." -ForegroundColor Green
exit 0
