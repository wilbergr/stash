# Verifies that a macro hotkey types real keystrokes into the focused app.
#
# Writes its own macros.json, restarts Stash so the hotkeys register, then fires
# each chord at a live TextBox and reads back what was typed. That is the only
# way to prove the whole chain: RegisterHotKey, the held-modifier release, the
# Unicode injection path, and named keys such as Tab and Enter.
#
# Requires -STA. Backs up and restores any existing macros.json.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class M {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte s, uint f, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public static bool Aware(){ return SetProcessDpiAwarenessContext(new IntPtr(-4)); }
  const uint UP = 2;
  static void Tap(byte v){keybd_event(v,0,0,IntPtr.Zero);System.Threading.Thread.Sleep(35);keybd_event(v,0,UP,IntPtr.Zero);}
  /// Presses Ctrl+Alt+<letter>, holding the modifiers across the tap exactly as
  /// a user would. The macro runner has to release them before typing.
  public static void CtrlAltKey(char letter) {
    keybd_event(0x11,0,0,IntPtr.Zero); keybd_event(0x12,0,0,IntPtr.Zero);
    Tap((byte)char.ToUpperInvariant(letter));
    keybd_event(0x12,0,UP,IntPtr.Zero); keybd_event(0x11,0,UP,IntPtr.Zero);
  }
}
'@

[void][M]::Aware()
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Pump([int]$ms) {
    $end = (Get-Date).AddMilliseconds($ms)
    while ((Get-Date) -lt $end) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 25 }
}

function Activate($form, $control) {
    for ($i = 0; $i -lt 12; $i++) {
        [void][M]::SetForegroundWindow($form.Handle)
        $form.Activate()
        $control.Focus() | Out-Null
        Pump 120
        if ([M]::GetForegroundWindow() -eq $form.Handle) { return $true }
    }
    return $false
}

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Stash\bin\Debug\net10.0-windows\win-x64\Stash.exe'
if (-not (Test-Path $exe)) { $exe = Join-Path $env:LOCALAPPDATA 'Programs\Stash\Stash.exe' }

$prof = Join-Path $env:LOCALAPPDATA 'Stash'
$macrosPath = Join-Path $prof 'macros.json'
$backup = "$macrosPath.testbackup"

$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$t,[bool]$p,[string]$n=''){ $results.Add([pscustomobject]@{ Test=$t; Pass=$p; Note=$n }) }

# --- Install a known macro set --------------------------------------------
if (Test-Path $macrosPath) { Copy-Item $macrosPath $backup -Force }

$fixture = @'
{
  "Macros": [
    {
      "Name": "Plain text",
      "Hotkey": "Ctrl+Alt+H",
      "Enabled": true,
      "Steps": [ { "Text": "EXAMPLE" } ]
    },
    {
      "Name": "Punctuation and case",
      "Hotkey": "Ctrl+Alt+K",
      "Enabled": true,
      "Steps": [ { "Text": "a-B_c 1+2 (x@y) 50%" } ]
    },
    {
      "Name": "Named keys",
      "Hotkey": "Ctrl+Alt+L",
      "Enabled": true,
      "Steps": [
        { "Text": "one" },
        { "Key": "Enter" },
        { "Text": "two" },
        { "Key": "Tab" },
        { "Text": "three" }
      ]
    },
    {
      "Name": "Backspace correction",
      "Hotkey": "Ctrl+Alt+M",
      "Enabled": true,
      "Steps": [
        { "Text": "abcX" },
        { "Key": "Backspace" },
        { "Text": "d" }
      ]
    },
    {
      "Name": "Disabled one",
      "Hotkey": "Ctrl+Alt+N",
      "Enabled": false,
      "Steps": [ { "Text": "SHOULD NOT TYPE" } ]
    }
  ]
}
'@
Set-Content -Path $macrosPath -Value $fixture -Encoding UTF8

# Everything from here runs inside try/finally so the user's macros.json is put
# back even when the run bails out early. An earlier version returned straight
# from the focus check and left its fixture installed.
$fail = 0
try {

# Restart so the macro hotkeys are registered from the fixture.
Get-Process Stash -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
Start-Process $exe
Start-Sleep -Seconds 5

# --- Target window --------------------------------------------------------
$form = New-Object System.Windows.Forms.Form
$form.Text = 'macro target'; $form.StartPosition = 'Manual'
$form.Left = 300; $form.Top = 300; $form.Width = 560; $form.Height = 220
$form.TopMost = $true
$box = New-Object System.Windows.Forms.TextBox
$box.Multiline = $true; $box.Dock = 'Fill'
$box.AcceptsTab = $true
$box.Font = New-Object System.Drawing.Font('Consolas', 10)
$form.Controls.Add($box); $form.Show()
Pump 300
$focused = Activate $form $box
Pump 250

if (-not $focused) {
    Write-Host "  [SKIP] could not focus the paste target; another window would not yield foreground"
}
else {

function Run-Macro([char]$letter, [int]$waitMs = 1400) {
    [void](Activate $form $box)
    $box.Clear(); Pump 200
    [M]::CtrlAltKey($letter)
    Pump $waitMs
    return $box.Text
}

Write-Host ""
Write-Host "=== Macros ==="

$got = Run-Macro 'H'
Add-Result 'Ctrl+Alt+H types plain text' ($got -eq 'EXAMPLE') "got '$got'"

$got = Run-Macro 'K'
$want = 'a-B_c 1+2 (x@y) 50%'
Add-Result 'punctuation, mixed case and symbols survive' ($got -eq $want) "got '$got' expected '$want'"

$got = Run-Macro 'L'
$norm = $got -replace "`r`n", "`n"
$want = "one`ntwo`tthree"
Add-Result 'Enter and Tab replay as real key presses' ($norm -eq $want) "got '$($norm -replace "`n",'\n' -replace "`t",'\t')'"

$got = Run-Macro 'M'
Add-Result 'Backspace edits while typing' ($got -eq 'abcd') "got '$got'"

$got = Run-Macro 'N'
Add-Result 'a disabled macro does not run' ($got.Length -eq 0) "got '$got'"

# The trigger chord's modifiers must not leak into the typed output. If the
# release failed, Ctrl+Alt+E would fire a shortcut instead of typing 'E' and the
# text would be wrong or empty -- covered by the assertions above -- but check
# the clipboard was left alone too, since macros must not disturb it.
[System.Windows.Forms.Clipboard]::SetText('CLIPBOARD-SENTINEL'); Pump 500
[void](Run-Macro 'H')
$clip = [System.Windows.Forms.Clipboard]::GetText()
Add-Result 'typing a macro leaves the clipboard untouched' ($clip -eq 'CLIPBOARD-SENTINEL') "clipboard now '$clip'"

}   # end of the focused branch

$form.Close(); $form.Dispose()

foreach ($r in $results) {
    if ($r.Pass) { Write-Host "  [PASS] $($r.Test)" }
    else { Write-Host "  [FAIL] $($r.Test)"; Write-Host "         $($r.Note)"; $fail++ }
}

}
finally {
    # --- Restore, whatever happened above --------------------------------
    Get-Process Stash -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400

    if (Test-Path $backup) {
        Move-Item $backup $macrosPath -Force
    } else {
        # There was no macros.json before this run; leave none, and Stash will
        # write its starter file on the next launch.
        Remove-Item $macrosPath -Force -ErrorAction SilentlyContinue
    }

    # Leave Stash running on the restored macros, so this suite can appear
    # anywhere in the runner's order without stranding the ones after it.
    Start-Process $exe
    Start-Sleep -Seconds 4
}

Write-Host ""
Write-Host "failures: $fail"
exit $fail
