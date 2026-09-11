# Tests favorites and the quick-slot global hotkeys:
#   Alt+1 in the stash assigns the selected clip to slot 1
#   Ctrl+Alt+1 anywhere pastes it without the stash appearing
# Also exercises modifier release: Ctrl and Alt are physically held when the
# synthetic Ctrl+V is injected.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class S {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public static bool MakeAware() { return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
  const uint KEYUP = 2;
  static void Tap(byte vk) {
    keybd_event(vk, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(30);
    keybd_event(vk, 0, KEYUP, IntPtr.Zero);
  }
  public static void CtrlAltV() {
    keybd_event(0x11,0,0,IntPtr.Zero); keybd_event(0x12,0,0,IntPtr.Zero);
    Tap(0x56);
    keybd_event(0x12,0,KEYUP,IntPtr.Zero); keybd_event(0x11,0,KEYUP,IntPtr.Zero);
  }
  public static void AltDigit(int d) {
    keybd_event(0x12,0,0,IntPtr.Zero);
    Tap((byte)(0x30 + d));
    keybd_event(0x12,0,KEYUP,IntPtr.Zero);
  }
  public static void CtrlAltDigit(int d) {
    keybd_event(0x11,0,0,IntPtr.Zero); keybd_event(0x12,0,0,IntPtr.Zero);
    Tap((byte)(0x30 + d));
    keybd_event(0x12,0,KEYUP,IntPtr.Zero); keybd_event(0x11,0,KEYUP,IntPtr.Zero);
  }
  public static void CtrlD() {
    keybd_event(0x11,0,0,IntPtr.Zero);
    Tap(0x44);
    keybd_event(0x11,0,KEYUP,IntPtr.Zero);
  }
  public static void Esc() { Tap(0x1B); }
}
'@

[void][S]::MakeAware()
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Pump([int]$ms) {
    $end = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $end) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 25
    }
}

function Activate($form, $control) {
    <#
        SetForegroundWindow is routinely refused for a process that does not own
        the foreground, and a WinForms control cannot take focus until its form is
        active. Without confirming both, a synthetic Ctrl+V lands in a window with
        no focused text control and silently does nothing.
    #>
    for ($i = 0; $i -lt 12; $i++) {
        [void][S]::SetForegroundWindow($form.Handle)
        $form.Activate()
        $control.Focus() | Out-Null
        Pump 120
        if ([S]::GetForegroundWindow() -eq $form.Handle) { return $true }
    }
    return $false
}

$id = Get-Random -Maximum 999999
$signature = "SIGNATURE-$id"
$noise     = "NOISE-$id"

[System.Windows.Forms.Clipboard]::SetText($signature); Pump 700

$form = New-Object System.Windows.Forms.Form
$form.Text = 'slot target'; $form.StartPosition = 'Manual'
$form.Left = 320; $form.Top = 320; $form.Width = 520; $form.Height = 180
$form.TopMost = $true
$box = New-Object System.Windows.Forms.TextBox
$box.Multiline = $true; $box.Dock = 'Fill'
$form.Controls.Add($box); $form.Show()
Pump 300
if (-not (Activate $form $box)) { Write-Host "  [SKIP] could not focus the paste target"; exit 0 }
Pump 250

$results = @()

# --- Assign the newest clip to quick slot 1, and favorite it ---------------
[S]::CtrlAltV();  Pump 1200
[S]::CtrlD();     Pump 300      # favorite
[S]::AltDigit(1); Pump 400      # assign slot 1
[S]::Esc();       Pump 700

Start-Sleep -Seconds 3   # let the debounced save land
$hist = Get-Content "$env:LOCALAPPDATA\Stash\history.json" -Raw | ConvertFrom-Json
$slotted = @($hist | Where-Object { $_.FavoriteSlot -eq 1 })

$results += [pscustomobject]@{
    Test = 'Alt+1 assigns the clip to quick slot 1'
    Pass = ($slotted.Count -eq 1 -and $slotted[0].Preview -eq $signature)
    Note = if ($slotted.Count -eq 1) { "slot1='$($slotted[0].Preview)'" } else { "slotted count=$($slotted.Count)" }
}
$results += [pscustomobject]@{
    Test = 'a slotted clip is implicitly a favorite'
    Pass = ($slotted.Count -eq 1 -and $slotted[0].IsFavorite -eq $true)
    Note = if ($slotted.Count -eq 1) { "IsFavorite=$($slotted[0].IsFavorite)" } else { 'n/a' }
}

# --- Push a different clip so the clipboard no longer holds the signature ---
[System.Windows.Forms.Clipboard]::SetText($noise); Pump 800

# --- Ctrl+Alt+1 should paste the slotted clip, stash never opening ----------
$focused = Activate $form $box
$box.Clear(); Pump 250
[S]::CtrlAltDigit(1); Pump 1900

$got = $box.Text
$results += [pscustomobject]@{
    Test = 'Ctrl+Alt+1 pastes the slotted clip directly'
    Pass = ($got.Trim() -eq $signature)
    Note = "pasted='$($got.Trim())' expected='$signature' (target focused: $focused)"
}

$form.Close(); $form.Dispose()

Write-Host ""
Write-Host "=== Favorites and quick slots ==="
$fail = 0
foreach ($r in $results) {
    if ($r.Pass) { Write-Host "  [PASS] $($r.Test)" }
    else { Write-Host "  [FAIL] $($r.Test)"; Write-Host "         $($r.Note)"; $fail++ }
}
Write-Host ""
Write-Host "failures: $fail"
exit $fail
