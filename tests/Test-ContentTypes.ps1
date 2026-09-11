# Verifies every content type Stash handles, in both directions:
#   capture  - the clip is stored with the right kind and metadata
#   round-trip - selecting it puts the original payload back on the clipboard
#
# Round-trip uses Alt+C (copy without pasting) rather than Enter, because
# "paste into a text box" is meaningless for images and file lists. Reading the
# clipboard back afterwards checks the real payload for every type uniformly.
#
# Also covers the privacy promise: clips that an app marks as sensitive must
# never be stored.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class C {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  public static bool MakeAware() { return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
  const uint KEYUP = 2;
  static void Tap(byte vk) { keybd_event(vk,0,0,IntPtr.Zero); System.Threading.Thread.Sleep(30); keybd_event(vk,0,KEYUP,IntPtr.Zero); }
  public static void CtrlAltV() {
    keybd_event(0x11,0,0,IntPtr.Zero); keybd_event(0x12,0,0,IntPtr.Zero);
    Tap(0x56);
    keybd_event(0x12,0,KEYUP,IntPtr.Zero); keybd_event(0x11,0,KEYUP,IntPtr.Zero);
  }
  public static void AltC() {
    keybd_event(0x12,0,0,IntPtr.Zero);
    Tap(0x43);
    keybd_event(0x12,0,KEYUP,IntPtr.Zero);
  }
  public static void Right() { Tap(0x27); }
  public static void Esc()   { Tap(0x1B); }
}
'@

[void][C]::MakeAware()
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Pump([int]$ms) {
    $end = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 25 }
}

function Set-ClipboardRetry([scriptblock] $action) {
    # The clipboard is singly-locked; another app may hold it briefly.
    for ($i = 0; $i -lt 6; $i++) {
        try { & $action; return $true } catch { Start-Sleep -Milliseconds 120 }
    }
    return $false
}

$id = Get-Random -Maximum 999999
$results = New-Object System.Collections.Generic.List[object]

function Add-Result([string] $test, [bool] $pass, [string] $note = '') {
    $results.Add([pscustomobject]@{ Test = $test; Pass = $pass; Note = $note })
}

# ---------------------------------------------------------------------------
# Test payloads. One per kind Stash recognises.
# ---------------------------------------------------------------------------
$plain     = "Rebalance memo $id - confirm tracking error before Thursday."
$multiline = "line one $id`r`nline two`r`nline three`r`nline four"
$link      = "https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic?x=$id"
$color     = "#2F9BFF"

# A deterministic test image.
$img = New-Object System.Drawing.Bitmap 420, 240
$gfx = [System.Drawing.Graphics]::FromImage($img)
$gfx.Clear([System.Drawing.Color]::FromArgb(255, 47, 155, 255))
$gfx.FillRectangle([System.Drawing.Brushes]::White, 40, 40, 120, 80)
$gfx.Dispose()

# Real files, so the file-drop list survives a round trip.
$repoRoot = Split-Path -Parent $PSScriptRoot
$fileA = Join-Path $repoRoot 'README.md'
$fileB = Join-Path $repoRoot 'build.ps1'
$fileList = New-Object System.Collections.Specialized.StringCollection
[void]$fileList.Add($fileA)
[void]$fileList.Add($fileB)

# ---------------------------------------------------------------------------
# Round-trip each kind.
#
# Sequence per case: seed the target, then seed a unique sentinel so the target
# is pushed to card index 1. Open the stash, press Right to select it, Alt+C to
# copy it, then read the clipboard back. Without the sentinel the clipboard would
# already hold the target and the test would pass without Stash doing anything.
# ---------------------------------------------------------------------------
function Test-RoundTrip {
    param(
        [string] $Name,
        [scriptblock] $Seed,
        [scriptblock] $Check   # receives nothing, returns @{ Pass; Note }
    )

    if (-not (Set-ClipboardRetry $Seed)) {
        Add-Result "$Name round-trips through the stash" $false 'could not seed the clipboard'
        return
    }
    Pump 750

    $sentinel = "SENTINEL-$Name-$id"
    if (-not (Set-ClipboardRetry { [System.Windows.Forms.Clipboard]::SetText($sentinel) })) {
        Add-Result "$Name round-trips through the stash" $false 'could not seed the sentinel'
        return
    }
    Pump 750

    [C]::CtrlAltV(); Pump 1200   # stash opens on Recents, card 0 = sentinel
    [C]::Right();    Pump 350    # card 1 = the target
    [C]::AltC();     Pump 700    # copy without pasting
    [C]::Esc();      Pump 600

    $outcome = & $Check
    Add-Result "$Name round-trips through the stash" $outcome.Pass $outcome.Note
}

Test-RoundTrip 'plain text' `
    { [System.Windows.Forms.Clipboard]::SetText($plain) } `
    {
        $got = [System.Windows.Forms.Clipboard]::GetText()
        @{ Pass = ($got -eq $plain); Note = "got '$got'" }
    }

Test-RoundTrip 'multi-line text' `
    { [System.Windows.Forms.Clipboard]::SetText($multiline) } `
    {
        $got = [System.Windows.Forms.Clipboard]::GetText()
        # Normalise line endings: the clipboard round trip may not preserve CRLF exactly.
        $a = $got -replace "`r`n", "`n"
        $b = $multiline -replace "`r`n", "`n"
        @{ Pass = ($a -eq $b); Note = "got $($a.Split("`n").Count) lines" }
    }

Test-RoundTrip 'link' `
    { [System.Windows.Forms.Clipboard]::SetText($link) } `
    {
        $got = [System.Windows.Forms.Clipboard]::GetText()
        @{ Pass = ($got -eq $link); Note = "got '$got'" }
    }

Test-RoundTrip 'color' `
    { [System.Windows.Forms.Clipboard]::SetText($color) } `
    {
        $got = [System.Windows.Forms.Clipboard]::GetText()
        @{ Pass = ($got -eq $color); Note = "got '$got'" }
    }

Test-RoundTrip 'image' `
    { [System.Windows.Forms.Clipboard]::SetImage($img) } `
    {
        if (-not [System.Windows.Forms.Clipboard]::ContainsImage()) {
            return @{ Pass = $false; Note = 'clipboard holds no image' }
        }
        $got = [System.Windows.Forms.Clipboard]::GetImage()
        $ok = ($got.Width -eq 420 -and $got.Height -eq 240)
        $note = "got $($got.Width)x$($got.Height), expected 420x240"
        $got.Dispose()
        @{ Pass = $ok; Note = $note }
    }

Test-RoundTrip 'file list' `
    { [System.Windows.Forms.Clipboard]::SetFileDropList($fileList) } `
    {
        if (-not [System.Windows.Forms.Clipboard]::ContainsFileDropList()) {
            return @{ Pass = $false; Note = 'clipboard holds no file drop list' }
        }
        $got = @([System.Windows.Forms.Clipboard]::GetFileDropList())
        $ok = ($got.Count -eq 2 -and $got -contains $fileA -and $got -contains $fileB)
        @{ Pass = $ok; Note = "got $($got.Count) file(s): $($got -join ', ')" }
    }

# ---------------------------------------------------------------------------
# Sensitive clips must never be stored.
# ---------------------------------------------------------------------------
$secretExcluded = "SECRET-EXCLUDED-$id"
$secretNoHistory = "SECRET-NOHISTORY-$id"

# Presence of this format alone means "do not record me".
[void](Set-ClipboardRetry {
    $d = New-Object System.Windows.Forms.DataObject
    $d.SetText($secretExcluded)
    $d.SetData('ExcludeClipboardContentFromMonitorProcessing', (New-Object System.IO.MemoryStream(,[byte[]](0,0,0,0))))
    [System.Windows.Forms.Clipboard]::SetDataObject($d, $true)
})
Pump 800

# A DWORD 0 here is an explicit opt-out.
[void](Set-ClipboardRetry {
    $d = New-Object System.Windows.Forms.DataObject
    $d.SetText($secretNoHistory)
    $d.SetData('CanIncludeInClipboardHistory', (New-Object System.IO.MemoryStream(,[byte[]](0,0,0,0))))
    [System.Windows.Forms.Clipboard]::SetDataObject($d, $true)
})
Pump 800

# Leave the clipboard clean so the secrets are not the current content.
[void](Set-ClipboardRetry { [System.Windows.Forms.Clipboard]::SetText("done-$id") })
Pump 600

# ---------------------------------------------------------------------------
# Capture assertions, read once from the persisted index.
# ---------------------------------------------------------------------------
Start-Sleep -Seconds 3   # the index is written on a debounced timer

$historyPath = Join-Path $env:LOCALAPPDATA 'Stash\history.json'
if (-not (Test-Path $historyPath)) {
    Write-Host "history.json missing at $historyPath"
    exit 1
}
# Do NOT wrap this in @(). Windows PowerShell 5.1 emits a deserialised JSON array
# as a single pipeline object rather than enumerating it, so @() produces a
# one-element array containing the array, and every count and field lookup below
# silently reads the wrong thing.
$hist = ConvertFrom-Json (Get-Content $historyPath -Raw)
if ($null -eq $hist) { $hist = @() }
elseif ($hist -isnot [System.Array]) { $hist = , $hist }

Write-Host "  (history entries: $($hist.Count))"

# Kind is serialised as its numeric enum value.
$KindText = 0; $KindLink = 1; $KindImage = 2; $KindFiles = 3; $KindColor = 4

function Find-Clip([int] $kind, [string] $contains) {
    $found = @($hist | Where-Object { $_.Kind -eq $kind -and $_.Preview -like "*$contains*" })

    # -NoEnumerate keeps a single match as a one-element array; PowerShell would
    # otherwise unroll it on return and .Count would not exist under StrictMode.
    Write-Output -NoEnumerate $found
}

$e = Find-Clip $KindText 'Rebalance memo'
Add-Result 'plain text is captured as Text' ($e.Count -ge 1) "matches=$($e.Count)"
if ($e.Count -ge 1) {
    Add-Result 'plain text records its length' ($e[0].TextLength -eq $plain.Length) "TextLength=$($e[0].TextLength) expected=$($plain.Length)"
}

$e = Find-Clip $KindText 'line one'
Add-Result 'multi-line text is captured as Text' ($e.Count -ge 1) "matches=$($e.Count)"
if ($e.Count -ge 1) {
    Add-Result 'multi-line text records 4 lines' ($e[0].LineCount -eq 4) "LineCount=$($e[0].LineCount)"
}

$e = Find-Clip $KindLink 'learn.microsoft.com'
Add-Result 'a URL is classified as Link' ($e.Count -ge 1) "matches=$($e.Count)"

$e = Find-Clip $KindColor '#2F9BFF'
Add-Result 'a hex color is classified as Color' ($e.Count -ge 1) "matches=$($e.Count)"

$e = @($hist | Where-Object { $_.Kind -eq $KindImage })
Add-Result 'an image is captured as Image' ($e.Count -ge 1) "matches=$($e.Count)"
if ($e.Count -ge 1) {
    $img0 = $e[0]
    Add-Result 'the image records its pixel size' (($img0.ImageWidth -eq 420) -and ($img0.ImageHeight -eq 240)) "$($img0.ImageWidth)x$($img0.ImageHeight)"

    $thumb = Join-Path $env:LOCALAPPDATA "Stash\images\$($img0.ThumbFile)"
    Add-Result 'the image has a thumbnail on disk' ($null -ne $img0.ThumbFile -and (Test-Path $thumb)) "ThumbFile=$($img0.ThumbFile)"
}

$e = @($hist | Where-Object { $_.Kind -eq $KindFiles })
Add-Result 'a file drop list is captured as Files' ($e.Count -ge 1) "matches=$($e.Count)"
if ($e.Count -ge 1) {
    Add-Result 'the file clip records both paths' (@($e[0].Files).Count -eq 2) "paths=$(@($e[0].Files).Count)"
}

# Privacy: neither secret may appear anywhere in the store.
$raw = Get-Content $historyPath -Raw
Add-Result 'a clip flagged ExcludeClipboardContentFromMonitorProcessing is not stored' `
    (-not $raw.Contains($secretExcluded)) 'found in history.json'
Add-Result 'a clip flagged CanIncludeInClipboardHistory=0 is not stored' `
    (-not $raw.Contains($secretNoHistory)) 'found in history.json'

# ---------------------------------------------------------------------------
$img.Dispose()

Write-Host ""
Write-Host "=== Content types ==="
$fail = 0
foreach ($r in $results) {
    if ($r.Pass) {
        Write-Host "  [PASS] $($r.Test)"
    } else {
        Write-Host "  [FAIL] $($r.Test)"
        if ($r.Note) { Write-Host "         $($r.Note)" }
        $fail++
    }
}
Write-Host ""
Write-Host "failures: $fail"
exit $fail
