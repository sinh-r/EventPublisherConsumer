<#
.SYNOPSIS
  Measures the two M1 acceptance criteria (build plan §6) that need a real windowed
  process: "10,000 msg/s for 60s, no frame over 100 ms" and "heap growth under 50 MB
  across that run". The other three criteria are measured by xunit tests — see
  tests/EventScope.App.Tests/AcceptanceCriteriaTests.cs and
  tests/EventScope.Acceptance.Tests/StorageAcceptanceCriteriaTests.cs.

.DESCRIPTION
  Launches EventScope.exe with EVENTSCOPE_MEASURE=<DurationSeconds>, which auto-starts
  streaming (FakeEventSource by default, real Kafka if EVENTSCOPE_KAFKA_BOOTSTRAP is set),
  runs a DispatcherPriority.Render frame-time probe (MainWindow.Measurement.cs), and
  auto-closes when done — no UI Automation click-driving needed for this measurement, unlike
  the Start/Stop smoke-drive PROGRESS.md's M1b entry describes for manual verification.

  In parallel, dotnet-counters attaches to the same process and records System.Runtime
  counters (gc-heap-size, in MB) for the same duration, so this script produces both halves
  of the acceptance measurement from one run.

  Requires the `dotnet-counters` global tool (`dotnet tool install --global dotnet-counters`).

.PARAMETER DurationSeconds
  How long the app streams before auto-closing. Defaults to 60, matching the acceptance
  criterion's own duration.

.PARAMETER Configuration
  Build configuration for EventScope.exe. Defaults to Release.
#>
[CmdletBinding()]
param(
    [int]$DurationSeconds = 60,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $repoRoot "src/EventScope.App/bin/$Configuration/net10.0/EventScope.exe"
$outputDir = Join-Path $repoRoot 'tests/EventScope.Bench/baselines/acceptance'
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$frameCsv = Join-Path $outputDir 'gui-frame-time.csv'
$countersCsv = Join-Path $outputDir 'gui-heap-growth.csv'

if (-not (Test-Path $exePath)) {
    throw "$exePath not found. Build first: dotnet build src/EventScope.App/EventScope.App.csproj -c $Configuration"
}

$dotnetCounters = Get-Command dotnet-counters -ErrorAction SilentlyContinue
if (-not $dotnetCounters) {
    throw "dotnet-counters not found. Install with: dotnet tool install --global dotnet-counters"
}

Write-Host "Launching EventScope.exe for a ${DurationSeconds}s measurement run..." -ForegroundColor Cyan
$env:EVENTSCOPE_MEASURE = "$DurationSeconds"
$env:EVENTSCOPE_MEASURE_OUTPUT = $frameCsv
Remove-Item $frameCsv -ErrorAction SilentlyContinue
Remove-Item $countersCsv -ErrorAction SilentlyContinue

$proc = Start-Process -FilePath $exePath -PassThru

try {
    # Give the process a moment to start before attaching diagnostics.
    Start-Sleep -Seconds 2

    $durationSpan = [TimeSpan]::FromSeconds($DurationSeconds).ToString('dd\:hh\:mm\:ss')
    Write-Host "Attaching dotnet-counters to PID $($proc.Id) for $durationSpan..." -ForegroundColor Cyan
    & dotnet-counters collect -p $proc.Id --counters System.Runtime --duration $durationSpan -o $countersCsv --format csv

    $exited = $proc.WaitForExit(30000)
    if (-not $exited) {
        Write-Warning "EventScope.exe did not exit on its own within 30s of the measurement window closing; stopping it."
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Item Env:\EVENTSCOPE_MEASURE -ErrorAction SilentlyContinue
    Remove-Item Env:\EVENTSCOPE_MEASURE_OUTPUT -ErrorAction SilentlyContinue
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host '--- Frame time (build plan: no frame over 100 ms) ---' -ForegroundColor Cyan
if (Test-Path $frameCsv) { Get-Content $frameCsv } else { Write-Warning "$frameCsv was not written." }

Write-Host ''
Write-Host '--- Heap size samples (build plan: growth under 50 MB) ---' -ForegroundColor Cyan
if (Test-Path $countersCsv) {
    # dotnet-counters (System.Runtime meter naming) has no single "total heap size" counter;
    # it reports one per generation. Sum gen0+gen1+gen2+poh+loh at each collection timestamp
    # to reconstruct the total, then compare the first and last totals for the delta the
    # acceptance criterion asks about.
    $rows = Import-Csv $countersCsv | Where-Object { $_.'Counter Name' -like 'dotnet.gc.last_collection.heap.size*' }
    $byTimestamp = $rows | Group-Object Timestamp | Sort-Object { [datetime]$_.Name }

    if ($byTimestamp.Count -ge 1) {
        $totals = $byTimestamp | ForEach-Object {
            ($_.Group | Measure-Object -Property 'Mean/Increment' -Sum).Sum
        }
        $firstMb = [math]::Round($totals[0] / 1MB, 2)
        $lastMb = [math]::Round($totals[-1] / 1MB, 2)
        $peakMb = [math]::Round(($totals | Measure-Object -Maximum).Maximum / 1MB, 2)
        Write-Host "First collection: $firstMb MB total heap"
        Write-Host "Last collection:  $lastMb MB total heap"
        Write-Host "Peak:             $peakMb MB total heap"
        Write-Host "Delta (last - first): $([math]::Round($lastMb - $firstMb, 2)) MB"
    } else {
        Write-Warning "No GC collections observed during the run - nothing to compute a delta from."
    }
} else {
    Write-Warning "$countersCsv was not written."
}
