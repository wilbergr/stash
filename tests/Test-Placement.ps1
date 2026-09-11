# Verifies panel placement for every dock edge on every connected monitor,
# including monitors with different DPI scaling.
#
# Nothing here is read back out of settings.json. The expected geometry is
# derived from the measured window rectangle and the monitor work area, so
# changing a default size cannot silently invalidate the test.
#
# Must run -STA (WinForms anchor window) and PerMonitorV2, or Windows feeds this
# process virtualised coordinates and the mixed-DPI cases pass for the wrong
# reason.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class T {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
  [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonProc cb, IntPtr d);
  [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(IntPtr m, ref MONITORINFO mi);
  [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr m, int t, out uint x, out uint y);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public delegate bool MonProc(IntPtr m, IntPtr hdc, IntPtr r, IntPtr d);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT mon; public RECT work; public uint flags; }

  public static bool MakeAware() { return SetProcessDpiAwarenessContext(new IntPtr(-4)); }

  public class Mon { public RECT Full; public RECT Work; public double Scale; public bool Primary; }

  public static List<Mon> Monitors() {
    var list = new List<Mon>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m,hdc,r,d) => {
      var mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
      GetMonitorInfoW(m, ref mi);
      uint dx, dy; GetDpiForMonitor(m, 0, out dx, out dy);
      list.Add(new Mon { Full = mi.mon, Work = mi.work, Scale = dx/96.0, Primary = (mi.flags & 1) != 0 });
      return true;
    }, IntPtr.Zero);
    return list;
  }

  const uint KEYUP = 2;
  static void Tap(byte vk) {
    keybd_event(vk, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(35);
    keybd_event(vk, 0, KEYUP, IntPtr.Zero);
  }
  public static void SendCtrlAltV() {
    keybd_event(0x11, 0, 0, IntPtr.Zero);
    keybd_event(0x12, 0, 0, IntPtr.Zero);
    Tap(0x56);
    keybd_event(0x12, 0, KEYUP, IntPtr.Zero);
    keybd_event(0x11, 0, KEYUP, IntPtr.Zero);
  }
  // Ctrl + arrow re-docks the panel while it is open.
  public static void SendCtrlArrow(byte vk) {
    keybd_event(0x11, 0, 0, IntPtr.Zero);
    Tap(vk);
    keybd_event(0x11, 0, KEYUP, IntPtr.Zero);
  }
  public static void SendEscape() { Tap(0x1B); }

  public static RECT FindStash(uint target) {
    RECT best = new RECT();
    EnumWindows((h,p) => {
      uint wp; GetWindowThreadProcessId(h, out wp);
      if (wp == target && IsWindowVisible(h)) {
        var sb = new StringBuilder(256); GetClassNameW(h, sb, 256);
        if (sb.ToString().StartsWith("HwndWrapper[Stash")) {
          RECT r; GetWindowRect(h, out r);
          if (r.R - r.L > 300) best = r;
        }
      }
      return true;
    }, IntPtr.Zero);
    return best;
  }
}
'@

[void][T]::MakeAware()
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$stashId = [uint32](Get-Process Stash).Id
$mons = [T]::Monitors()

# Geometry constants mirrored from StashPlacement.
$EdgeMargin      = 12.0
$ShadowPad       = 30.0
$TravelOvershoot = 24.0

# vk codes for Ctrl+arrow.
$edges = @(
    , @('Bottom', [byte]0x28)
    , @('Top',    [byte]0x26)
    , @('Left',   [byte]0x25)
    , @('Right',  [byte]0x27)
)

Write-Host ""
Write-Host "=== Stash placement: $($edges.Count) edges x $($mons.Count) monitors ==="

$failures = 0
$checks = 0
$i = 0

foreach ($m in $mons) {
    $i++
    $s = $m.Scale
    $monLabel = "monitor $i ($(($m.Full.R - $m.Full.L))x$(($m.Full.B - $m.Full.T)) @ $([int]($s*100))%)"
    Write-Host ""
    Write-Host "  $monLabel  work=($($m.Work.L),$($m.Work.T))-($($m.Work.R),$($m.Work.B))"

    # Anchor focus to this monitor so the panel opens here.
    $form = New-Object System.Windows.Forms.Form
    $form.StartPosition = 'Manual'
    $form.FormBorderStyle = 'FixedSingle'
    $form.Text = 'anchor'
    $form.Width = 300; $form.Height = 160
    $form.Left = [int]($m.Work.L + 60)
    $form.Top  = [int]($m.Work.T + 60)
    $form.TopMost = $true
    $form.Show()
    [System.Windows.Forms.Application]::DoEvents()
    [void][T]::SetForegroundWindow($form.Handle)
    Start-Sleep -Milliseconds 350
    [System.Windows.Forms.Application]::DoEvents()

    [T]::SendCtrlAltV()
    Start-Sleep -Milliseconds 1100

    foreach ($edge in $edges) {
        $name = $edge[0]
        $checks++

        [T]::SendCtrlArrow($edge[1])
        Start-Sleep -Milliseconds 700

        $r = [T]::FindStash($stashId)
        if ($r.R -le $r.L) {
            Write-Host "    [FAIL] $name -> panel not visible"
            $failures++
            continue
        }

        $windowWDip = ($r.R - $r.L) / $s
        $windowHDip = ($r.B - $r.T) / $s
        $workWDip   = ($m.Work.R - $m.Work.L) / $s
        $workHDip   = ($m.Work.B - $m.Work.T) / $s

        # The window reserves travel equal to the panel's own thickness plus the
        # overshoot, on the docked side only. Inverting that recovers both the
        # panel's span and its thickness from the measured window.
        if ($name -eq 'Bottom' -or $name -eq 'Top') {
            $spanDip      = $windowWDip - (2 * $ShadowPad)
            $thicknessDip = ($windowHDip - (2 * $ShadowPad) - $TravelOvershoot) / 2
            $panelWDip    = $spanDip
            $panelHDip    = $thicknessDip
        } else {
            $spanDip      = $windowHDip - (2 * $ShadowPad)
            $thicknessDip = ($windowWDip - (2 * $ShadowPad) - $TravelOvershoot) / 2
            $panelWDip    = $thicknessDip
            $panelHDip    = $spanDip
        }

        $travelDip = $thicknessDip + $TravelOvershoot

        # Offset of the panel inside the window depends on which side holds the
        # travel space.
        switch ($name) {
            'Bottom' { $offXDip = $ShadowPad;               $offYDip = $ShadowPad }
            'Top'    { $offXDip = $ShadowPad;               $offYDip = $ShadowPad + $travelDip }
            'Left'   { $offXDip = $ShadowPad + $travelDip;  $offYDip = $ShadowPad }
            'Right'  { $offXDip = $ShadowPad;               $offYDip = $ShadowPad }
        }

        $panelL = $r.L + ($offXDip * $s)
        $panelT = $r.T + ($offYDip * $s)
        $panelR = $panelL + ($panelWDip * $s)
        $panelB = $panelT + ($panelHDip * $s)

        # What the docked edge must line up with, and the centring on the other axis.
        switch ($name) {
            'Bottom' {
                $primary   = [Math]::Abs($panelB - ($m.Work.B - ($EdgeMargin * $s)))
                $secondary = [Math]::Abs($panelL - ($m.Work.L + ((($workWDip - $spanDip) / 2) * $s)))
                $axis = 'bottom gap'
            }
            'Top' {
                $primary   = [Math]::Abs($panelT - ($m.Work.T + ($EdgeMargin * $s)))
                $secondary = [Math]::Abs($panelL - ($m.Work.L + ((($workWDip - $spanDip) / 2) * $s)))
                $axis = 'top gap'
            }
            'Left' {
                $primary   = [Math]::Abs($panelL - ($m.Work.L + ($EdgeMargin * $s)))
                $secondary = [Math]::Abs($panelT - ($m.Work.T + ((($workHDip - $spanDip) / 2) * $s)))
                $axis = 'left gap'
            }
            'Right' {
                $primary   = [Math]::Abs($panelR - ($m.Work.R - ($EdgeMargin * $s)))
                $secondary = [Math]::Abs($panelT - ($m.Work.T + ((($workHDip - $spanDip) / 2) * $s)))
                $axis = 'right gap'
            }
        }

        $inside = ($panelL -ge $m.Work.L - 2) -and ($panelR -le $m.Work.R + 2) -and
                  ($panelT -ge $m.Work.T - 2) -and ($panelB -le $m.Work.B + 2)

        $ok = ($primary -le 2) -and ($secondary -le 2) -and $inside

        if ($ok) {
            Write-Host ("    [PASS] {0,-6} panel=({1:0},{2:0})-({3:0},{4:0})  {5:0}x{6:0}" -f `
                $name, $panelL, $panelT, $panelR, $panelB, ($panelWDip*$s), ($panelHDip*$s))
        } else {
            Write-Host "    [FAIL] $name"
            Write-Host ("           panel=({0:0},{1:0})-({2:0},{3:0})  {4:0}x{5:0}" -f `
                $panelL, $panelT, $panelR, $panelB, ($panelWDip*$s), ($panelHDip*$s))
            Write-Host ("           $axis off by {0:0.0}px, centring off by {1:0.0}px, inside work area: {2}" -f `
                $primary, $secondary, $inside)
            $failures++
        }
    }

    [T]::SendEscape()
    Start-Sleep -Milliseconds 450
    $form.Close(); $form.Dispose()
    [System.Windows.Forms.Application]::DoEvents()
}

# Leave the dock where it started so later runs and the user's own session are
# not surprised by a side dock.
[T]::SendCtrlAltV()
Start-Sleep -Milliseconds 900
[T]::SendCtrlArrow([byte]0x28)
Start-Sleep -Milliseconds 500
[T]::SendEscape()
Start-Sleep -Milliseconds 400

Write-Host ""
Write-Host "checks: $checks, failures: $failures"
exit $failures
