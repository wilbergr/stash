<#
.SYNOPSIS
    Builds Stash and publishes a single self-contained-ish executable to dist\.

.DESCRIPTION
    Publishes framework-dependent and single-file: one Stash.exe that needs the
    .NET 10 desktop runtime present, which keeps the output a few megabytes
    rather than ~150 MB. Pass -SelfContained if you want an exe that runs on a
    machine with no .NET installed.

.NOTES
    Run:  powershell -ExecutionPolicy Bypass -File build.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    # Bundle the runtime. Much larger, but runs anywhere.
    [switch] $SelfContained,

    # Regenerate Assets\Stash.ico from tools\New-StashIcon.ps1 first.
    [switch] $Icon,

    # Also build dist\Stash-<version>.msi. Requires the WiX 5 dotnet tool; the
    # script explains how to get it if it is missing.
    [switch] $Installer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'src\Stash\Stash.csproj'
$outDir = Join-Path $root 'dist'

function Resolve-Dotnet {
    <#
        Returns a dotnet that actually has an SDK. Finding dotnet on PATH is not
        enough: a machine can have the runtime installed system-wide with no SDK,
        while the SDK sits in the user profile because installing it needed no
        admin rights. Probing --list-sdks is what distinguishes them.
    #>
    $candidates = @()

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }

    $local = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
    if (Test-Path $local) { $candidates += $local }

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $candidate }
    }

    $checked = if ($candidates) { $candidates -join ', ' } else { '(none found)' }
    throw "No .NET SDK found. Checked: $checked. Install the .NET 10 SDK, or bootstrap it with: & ([scriptblock]::Create((irm https://dot.net/v1/dotnet-install.ps1))) -Channel 10.0 -InstallDir `"`$env:LOCALAPPDATA\dotnet`""
}

$dotnet = Resolve-Dotnet
Write-Host "Using $dotnet" -ForegroundColor DarkGray

if ($Icon) {
    Write-Host "Regenerating the icon..." -ForegroundColor Cyan
    & powershell.exe -ExecutionPolicy Bypass -NoProfile -File (Join-Path $root 'tools\New-StashIcon.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Icon generation failed." }
}

# Stash cannot be replaced while it is running.
Get-Process Stash -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

Write-Host "Publishing ($Configuration, self-contained: $($SelfContained.IsPresent))..." -ForegroundColor Cyan

& $dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained $($SelfContained.IsPresent.ToString().ToLowerInvariant()) `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -o $outDir `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$exe = Join-Path $outDir 'Stash.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe but it was not produced." }

$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "Built $exe ($sizeMb MB)" -ForegroundColor Green

# --- Installer --------------------------------------------------------------
if ($Installer) {
    Write-Host ""
    Write-Host "Building the installer..." -ForegroundColor Cyan

    $wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
    if (-not (Test-Path $wix)) {
        $onPath = Get-Command wix -ErrorAction SilentlyContinue
        if ($onPath) { $wix = $onPath.Source }
    }

    if (-not (Test-Path $wix)) {
        throw @"
WiX was not found. Install it per-user (no admin required) with:

    dotnet tool install --global wix --version 5.0.2
    wix extension add --global WixToolset.UI.wixext/5.0.2
    wix extension add --global WixToolset.Util.wixext/5.0.2

Note: WiX 6 and 7 require accepting the Open Source Maintenance Fee licence.
Version 5 is pinned here deliberately to stay on the freely licensed release.
"@
    }

    # Take the version from the project so the MSI and the exe never disagree.
    $csproj = [xml](Get-Content (Join-Path $root 'src\Stash\Stash.csproj'))
    $version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) -as [string]
    if (-not $version) { $version = '1.0.0' }

    $msi = Join-Path $outDir "Stash-$version.msi"

    $wixArgs = @(
        'build'
        (Join-Path $root 'installer\Stash.wxs')
        '-arch', 'x64'
        '-ext', 'WixToolset.UI.wixext'
        '-ext', 'WixToolset.Util.wixext'
        '-d', "ProductVersion=$version"
        '-d', "Manufacturer=$env:USERNAME"
        '-d', "SourceExe=$exe"
        '-d', "IconFile=$(Join-Path $root 'src\Stash\Assets\Stash.ico')"
        '-d', "NoticeFile=$(Join-Path $root 'installer\Notice.rtf')"
        '-o', $msi
    )

    & $wix @wixArgs
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
    if (-not (Test-Path $msi)) { throw "Expected $msi but it was not produced." }

    $msiMb = [Math]::Round((Get-Item $msi).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Built $msi ($msiMb MB)" -ForegroundColor Green
    Write-Host "Per-user install, no admin needed. Double-click it, or:" -ForegroundColor DarkGray
    Write-Host "  msiexec /i `"$msi`" /qn      (silent install)" -ForegroundColor DarkGray
    Write-Host "  msiexec /x `"$msi`" /qn      (silent uninstall)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "Run Stash, then press Ctrl+Alt+V." -ForegroundColor Green
Write-Host "To start it at sign-in, turn on 'Start Stash when I sign in' in Settings." -ForegroundColor DarkGray
