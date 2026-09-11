# End-to-end test of the core flow: hotkey -> navigate -> paste into the app
# that had focus. Uses a real TextBox as the paste target so the result can be
# read back exactly.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class P {
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
    keybd_event(0x11, 0, 0, IntPtr.Zero);
    keybd_event(0x12, 0, 0, IntPtr.Zero);
    Tap(0x56);
    keybd_event(0x12, 0, KEYUP, IntPtr.Zero);
    keybd_event(0x11, 0, KEYUP, IntPtr.Zero);
  }
  public static void Right() { Tap(0x27); }
  public static void Enter() { Tap(0x0D); }
  public static void Esc()   { Tap(0x1B); }
  public static void CtrlDigit(int d) {
    keybd_event(0x11, 0, 0, IntPtr.Zero);
    Tap((byte)(0x30 + d));
    keybd_event(0x11, 0, KEYUP, IntPtr.Zero);
  }
}
'@

[void][P]::MakeAware()
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
        [void][P]::SetForegroundWindow($form.Handle)
        $form.Activate()
        $control.Focus() | Out-Null
        Pump 120
        if ([P]::GetForegroundWindow() -eq $form.Handle) { return $true }
    }
    return $false
}

$id = (Get-Random -Maximum 999999)
$alpha = "ALPHA-$id"
$beta  = "BETA-$id"

# Seed two clips so BETA is newest and ALPHA is second.
[System.Windows.Forms.Clipboard]::SetText($alpha); Pump 700
[System.Windows.Forms.Clipboard]::SetText($beta);  Pump 700

# Paste target.
$form = New-Object System.Windows.Forms.Form
$form.Text = 'paste target'
$form.StartPosition = 'Manual'
$form.Left = 300; $form.Top = 300; $form.Width = 520; $form.Height = 180
$form.TopMost = $true
$box = New-Object System.Windows.Forms.TextBox
$box.Multiline = $true
$box.Dock = 'Fill'
$box.Font = New-Object System.Drawing.Font('Consolas', 11)
$form.Controls.Add($box)
$form.Show()
Pump 300
if (-not (Activate $form $box)) { Write-Host "  [SKIP] could not focus the paste target"; exit 0 }
Pump 250

$results = @()

# --- Test 1: arrow-navigate to the second card, then Enter -----------------
$box.Clear(); Pump 150
[P]::CtrlAltV(); Pump 1200      # stash slides in, card 0 = BETA
[P]::Right();    Pump 350       # select card 1 = ALPHA
[P]::Enter();    Pump 1800      # paste into the TextBox

$got = $box.Text
$results += [pscustomobject]@{
    Test     = 'hotkey + Right + Enter pastes the 2nd clip'
    Expected = $alpha
    Actual   = $got
    Pass     = ($got.Trim() -eq $alpha)
}

# --- Test 2: Ctrl+1 quick-picks the first card ------------------------------
[void](Activate $form $box)
$box.Clear(); Pump 200
[P]::CtrlAltV();   Pump 1200
[P]::CtrlDigit(1); Pump 1800

$got2 = $box.Text
$results += [pscustomobject]@{
    Test     = 'Ctrl+1 pastes the 1st clip'
    Expected = $beta
    Actual   = $got2
    Pass     = ($got2.Trim() -eq $beta)
}

# --- Test 3: Esc dismisses without pasting ----------------------------------
[void](Activate $form $box)
$box.Clear(); Pump 200
[P]::CtrlAltV(); Pump 1100
[P]::Esc();      Pump 900

$got3 = $box.Text
$results += [pscustomobject]@{
    Test     = 'Esc closes without pasting'
    Expected = '(empty)'
    Actual   = if ($got3.Length -eq 0) { '(empty)' } else { $got3 }
    Pass     = ($got3.Length -eq 0)
}

$form.Close(); $form.Dispose()

Write-Host ""
Write-Host "=== Paste flow ==="
$fail = 0
foreach ($r in $results) {
    $tag = if ($r.Pass) { '[PASS]' } else { '[FAIL]'; }
    if (-not $r.Pass) { $fail++ }
    Write-Host "  $tag $($r.Test)"
    if (-not $r.Pass) {
        Write-Host "         expected: '$($r.Expected)'"
        Write-Host "         actual  : '$($r.Actual)'"
    }
}
Write-Host ""
Write-Host "failures: $fail"
exit $fail
