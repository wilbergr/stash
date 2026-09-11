using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Stash.Interop;

/// <summary>
/// Applies the Windows 11 compositor effects that make a window look like it
/// shipped with the OS: acrylic backdrop, rounded corners and dark-mode framing.
/// Every call is best-effort — on an older build the attribute is simply ignored
/// and the window falls back to its own painted background.
/// </summary>
public static class DwmEffects
{
    /// <summary>
    /// Turns on the translucent acrylic backdrop. Requires the window to have a
    /// transparent WPF background so the composited material shows through.
    /// </summary>
    public static bool TryApplyAcrylic(Window window, bool dark)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        SetDarkMode(hwnd, dark);

        // Extending the frame into the client area is what lets the backdrop
        // render behind our content instead of being clipped away.
        var margins = new NativeMethods.MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1,
        };
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        var backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        var applied = NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;

        if (applied && PresentationSource.FromVisual(window) is HwndSource source)
        {
            // The HWND background must be transparent too, or Win32 paints over the material.
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        return applied;
    }

    public static void SetRoundedCorners(Window window, bool small = false)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var preference = small ? NativeMethods.DWMWCP_ROUNDSMALL : NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    public static void SetDarkMode(Window window, bool dark)
    {
        var hwnd = GetHandle(window);
        if (hwnd != IntPtr.Zero)
        {
            SetDarkMode(hwnd, dark);
        }
    }

    private static void SetDarkMode(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(
            hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    private static IntPtr GetHandle(Window window)
        => PresentationSource.FromVisual(window) is HwndSource source ? source.Handle : IntPtr.Zero;
}
