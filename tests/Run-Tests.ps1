<#
.SYNOPSIS
    Builds Stash and runs the integration tests.

.DESCRIPTION
    These are real integration tests, not unit tests: they drive Stash through
    synthesized keystrokes and read results back out of a live WinForms TextBox
    and the on-disk history. That is deliberate — almost everything worth testing
    in a clipboard manager lives in the parts a unit test cannot reach: global
    hotkey registration, focus hand-off between processes, synthetic Ctrl+V, and
    window placement across monitors with different DPI.

    Consequences of that choice:
      * An interactive desktop is required. These cannot run headless or in CI
        without a real session.
      * The tests take over the keyboard for a few seconds. Do not type while
        they run.
      * The local Stash history and settings are reset, so indices and slots are
        deterministic.

.NOTES
    Run:  powershell -ExecutionPolicy Bypass -File tests\Run-Tests.ps1
#>
[CmdletBinding()]
param(
    # Skip the build and test whatever is already in bin.
    [switch] $NoBuild,

    # Keep the existing history and settings instead of resetting them. Handy for
    # reproducing a bug, but Test-Paste and Test-QuickSlots will likely fail
    # because they depend on known card positions.
    [switch] $KeepProfile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Stash\Stash.csproj'
$exe = Join-Path $root 'src\Stash\bin\Debug\net10.0-windows\win-x64\Stash.exe'
$profileDir = Join-Path $env:LOCALAPPDATA 'Stash'

function Resolve-Dotnet {
    # Must have an SDK, not just a runtime: see the same helper in build.ps1.
    $candidates = @()

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }

    $local = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
    if (Test-Path $local) { $candidates += $local }

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $candidate }
    }

    throw "No .NET SDK found. Run build.ps1 for bootstrap instructions."
}

function Stop-Stash {
    Get-Process Stash -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# --- Build -----------------------------------------------------------------
if (-not $NoBuild) {
    $dotnet = Resolve-Dotnet
    Write-Host "Building..." -ForegroundColor Cyan
    Stop-Stash
    & $dotnet build $project -v:m --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

if (-not (Test-Path $exe)) { throw "Stash.exe not found at $exe. Build first." }

# --- Reset profile ----------------------------------------------------------
Stop-Stash

if (-not $KeepProfile) {
    Write-Host "Resetting local history and settings..." -ForegroundColor Cyan
    foreach ($f in 'history.json', 'settings.json', 'stash.log') {
        $p = Join-Path $profileDir $f
        if (Test-Path $p) { Remove-Item $p -Force }
    }
    # Orphaned image files are pruned by Stash itself on the next load.
}

# --- Launch -----------------------------------------------------------------
Write-Host "Starting Stash..." -ForegroundColor Cyan
Start-Process $exe
Start-Sleep -Seconds 4

if (-not (Get-Process Stash -ErrorAction SilentlyContinue)) {
    throw "Stash did not stay running. See $profileDir\stash.log."
}

# --- Run --------------------------------------------------------------------
Write-Host ""
Write-Host "Do not type until the tests finish." -ForegroundColor Yellow

$suites = @(
    'Test-ContentTypes.ps1',
    'Test-Paste.ps1',
    'Test-QuickSlots.ps1',
    'Test-Placement.ps1'
)

$failures = 0
foreach ($suite in $suites) {
    $path = Join-Path $PSScriptRoot $suite

    # -STA is required: the harnesses use the clipboard and WinForms windows.
    & powershell.exe -STA -ExecutionPolicy Bypass -NoProfile -File $path
    $failures += $LASTEXITCODE
}

# --- Report -----------------------------------------------------------------
Write-Host ""
$log = Join-Path $profileDir 'stash.log'
if (Test-Path $log) {
    $errors = Get-Content $log | Select-String -Pattern 'could not|Exception|failed|aborted'
    if ($errors) {
        Write-Host "Errors logged by Stash during the run:" -ForegroundColor Yellow
        $errors | ForEach-Object { Write-Host "  $_" }
        Write-Host ""
    }
}

Stop-Stash

if ($failures -eq 0) {
    Write-Host "All suites passed." -ForegroundColor Green
} else {
    Write-Host "$failures test(s) failed." -ForegroundColor Red
}

exit $failures
