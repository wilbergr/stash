using System.Windows;

namespace Stash.Interop;

/// <summary>
/// One monitor, described in real pixels plus its scale factor.
/// </summary>
/// <remarks>
/// Everything here is physical, deliberately. Converting to device-independent
/// units this early is what breaks mixed-DPI setups: a rectangle divided by one
/// monitor's scale is meaningless in another monitor's coordinate space, and WPF
/// gives no single DIP space that spans them all.
/// </remarks>
public readonly record struct MonitorArea(Rect Work, Rect Full, double Scale)
{
    /// <summary>Converts a device-independent length into pixels on this monitor.</summary>
    public double ToPixels(double dip) => dip * Scale;

    /// <summary>Converts a pixel length on this monitor into device-independent units.</summary>
    public double ToDip(double pixels) => pixels / Scale;
}

/// <summary>Monitor lookup for placing the stash.</summary>
public static class ScreenGeometry
{
    private const int MdtEffectiveDpi = 0;

    /// <summary>
    /// The monitor the stash should appear on: the one holding the window the user
    /// was working in, falling back to the one under the mouse.
    /// </summary>
    public static MonitorArea ForWindowOrCursor(IntPtr hwnd)
    {
        var monitor = IntPtr.Zero;

        if (hwnd != IntPtr.Zero && NativeMethods.IsWindow(hwnd))
        {
            monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        }

        if (monitor == IntPtr.Zero)
        {
            NativeMethods.GetCursorPos(out var cursor);
            monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        }

        return Describe(monitor);
    }

    /// <summary>The monitor containing <paramref name="point"/>, in physical pixels.</summary>
    public static MonitorArea ForPoint(Point point)
    {
        var native = new NativeMethods.POINT
        {
            X = (int)Math.Round(point.X),
            Y = (int)Math.Round(point.Y),
        };

        return Describe(NativeMethods.MonitorFromPoint(native, NativeMethods.MONITOR_DEFAULTTONEAREST));
    }

    private static MonitorArea Describe(IntPtr monitor)
    {
        var native = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref native))
        {
            // Last resort: assume a single unscaled primary display.
            var fallback = new Rect(0, 0, 1920, 1080);
            return new MonitorArea(fallback, fallback, 1.0);
        }

        return new MonitorArea(ToRect(native.rcWork), ToRect(native.rcMonitor), ScaleOf(monitor));
    }

    private static double ScaleOf(IntPtr monitor)
    {
        try
        {
            if (NativeMethods.GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch
        {
            // shcore exists on every supported build, but placement must never throw.
        }

        return 1.0;
    }

    private static Rect ToRect(NativeMethods.RECT r)
        => new(r.Left, r.Top, Math.Max(1, r.Width), Math.Max(1, r.Height));

    /// <summary>Reads a window's real pixel bounds.</summary>
    public static Rect WindowRect(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r))
        {
            return ToRect(r);
        }

        return Rect.Empty;
    }
}
