using System.Windows;
using Stash.Interop;
using Stash.Models;

namespace Stash.Services;

/// <summary>
/// Where the stash window goes and how the panel sits inside it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WindowPixels"/> is in real screen pixels because that is the only
/// coordinate space that means the same thing on every monitor. Everything else
/// is in device-independent units, because that is the space WPF lays out in
/// once the window is on its target monitor.
/// </para>
/// </remarks>
public readonly record struct StashLayout(
    Rect WindowPixels,
    Thickness PanelMargin,
    Size PanelSize,
    Rect MonitorClip,
    double Scale);

/// <summary>
/// Works out the stash's geometry for a given dock edge, and decides which edge a
/// dragged panel should snap back to.
/// </summary>
public static class StashPlacement
{
    /// <summary>Gap between the panel and the edge of the work area, in DIPs.</summary>
    private const double EdgeMargin = 12;

    /// <summary>Room around the panel for its drop shadow, in DIPs.</summary>
    private const double ShadowPad = 30;

    /// <summary>Slide distance beyond the panel's own size, so it clears the edge.</summary>
    private const double TravelOvershoot = 24;

    /// <summary>The panel never spans a whole monitor; it stops reading as a stash.</summary>
    private const double MaxHorizontalSpan = 1180;
    private const double MaxVerticalSpan = 900;

    private const double MinSpan = 360;
    private const double MinThickness = 180;
    private const double MaxThickness = 620;

    public static (double Min, double Max) ThicknessRange => (MinThickness, MaxThickness);

    /// <summary>
    /// Computes the layout for <paramref name="edge"/> on the monitor belonging to
    /// <paramref name="targetWindow"/>.
    /// </summary>
    public static StashLayout Compute(DockEdge edge, AppSettings settings, IntPtr targetWindow)
        => Compute(edge, settings, ScreenGeometry.ForWindowOrCursor(targetWindow));

    public static StashLayout Compute(DockEdge edge, AppSettings settings, MonitorArea monitor)
    {
        var s = monitor.Scale;

        // The work area in this monitor's own device-independent units.
        var workWidth = monitor.ToDip(monitor.Work.Width);
        var workHeight = monitor.ToDip(monitor.Work.Height);

        var thicknessH = Clamp(settings.PanelHeight, MinThickness, MaxThickness);
        var thicknessV = Clamp(settings.PanelWidth, MinThickness, MaxThickness);

        // Panel position relative to the work-area's top-left corner, in DIPs.
        double px, py;
        Size size;

        switch (edge)
        {
            case DockEdge.Bottom:
            case DockEdge.Top:
            {
                var span = Math.Max(Math.Min(workWidth - (EdgeMargin * 2), MaxHorizontalSpan), MinSpan);
                size = new Size(span, thicknessH);
                px = (workWidth - span) / 2;
                py = edge == DockEdge.Bottom
                    ? workHeight - EdgeMargin - thicknessH
                    : EdgeMargin;
                break;
            }

            case DockEdge.Left:
            case DockEdge.Right:
            {
                var span = Math.Max(Math.Min(workHeight - (EdgeMargin * 2), MaxVerticalSpan), MinSpan);
                size = new Size(thicknessV, span);
                py = (workHeight - span) / 2;
                px = edge == DockEdge.Left
                    ? EdgeMargin
                    : workWidth - EdgeMargin - thicknessV;
                break;
            }

            default:
            {
                // Floating positions are stored as virtual-screen pixels, which
                // stay meaningful regardless of which monitor they land on.
                var span = Math.Max(Math.Min(workWidth - (EdgeMargin * 2), MaxHorizontalSpan), MinSpan);
                size = new Size(span, thicknessH);

                px = settings.FloatingLeft is { } storedLeft
                    ? monitor.ToDip(storedLeft - monitor.Work.Left)
                    : (workWidth - span) / 2;

                py = settings.FloatingTop is { } storedTop
                    ? monitor.ToDip(storedTop - monitor.Work.Top)
                    : (workHeight - thicknessH) / 2;

                // Keep it on screen even if the display layout changed since.
                px = Clamp(px, 0, Math.Max(0, workWidth - size.Width));
                py = Clamp(py, 0, Math.Max(0, workHeight - size.Height));
                break;
            }
        }

        // Travel is the slide distance, reserved on the docked side only.
        var travel = edge == DockEdge.Floating
            ? 0
            : (edge.IsVertical() ? size.Width : size.Height) + TravelOvershoot;

        var extraLeft = edge == DockEdge.Left ? travel : 0;
        var extraRight = edge == DockEdge.Right ? travel : 0;
        var extraTop = edge == DockEdge.Top ? travel : 0;
        var extraBottom = edge == DockEdge.Bottom ? travel : 0;

        var margin = new Thickness(
            ShadowPad + extraLeft,
            ShadowPad + extraTop,
            ShadowPad + extraRight,
            ShadowPad + extraBottom);

        var windowXDip = px - margin.Left;
        var windowYDip = py - margin.Top;
        var windowWDip = size.Width + margin.Left + margin.Right;
        var windowHDip = size.Height + margin.Top + margin.Bottom;

        var windowPixels = new Rect(
            monitor.Work.Left + monitor.ToPixels(windowXDip),
            monitor.Work.Top + monitor.ToPixels(windowYDip),
            Math.Max(1, monitor.ToPixels(windowWDip)),
            Math.Max(1, monitor.ToPixels(windowHDip)));

        // The travel space hangs off the edge of the monitor, and on a multi-monitor
        // desktop that overhang can land on a neighbour. Clipping the window to its
        // own monitor stops the sliding panel appearing on the display next door.
        var clip = new Rect(
            (monitor.Full.Left - windowPixels.Left) / s,
            (monitor.Full.Top - windowPixels.Top) / s,
            monitor.ToDip(monitor.Full.Width),
            monitor.ToDip(monitor.Full.Height));

        return new StashLayout(windowPixels, margin, size, clip, s);
    }

    /// <summary>
    /// After a drag, returns the edge to dock to — or <see cref="DockEdge.Floating"/>
    /// if the panel was dropped away from every edge. Distances are compared in
    /// pixels, so the snap feels the same on every monitor.
    /// </summary>
    public static DockEdge SnapTarget(Rect panelPixels, MonitorArea monitor, double snapDistanceDip)
    {
        var work = monitor.Work;
        var threshold = monitor.ToPixels(snapDistanceDip);

        var distances = new (DockEdge Edge, double Distance)[]
        {
            (DockEdge.Left, Math.Abs(panelPixels.Left - work.Left)),
            (DockEdge.Right, Math.Abs(work.Right - panelPixels.Right)),
            (DockEdge.Top, Math.Abs(panelPixels.Top - work.Top)),
            (DockEdge.Bottom, Math.Abs(work.Bottom - panelPixels.Bottom)),
        };

        var closest = distances.OrderBy(d => d.Distance).First();
        return closest.Distance <= threshold ? closest.Edge : DockEdge.Floating;
    }

    /// <summary>Maps an arrow key to the edge it should dock to.</summary>
    public static DockEdge? EdgeForArrow(System.Windows.Input.Key key) => key switch
    {
        System.Windows.Input.Key.Left => DockEdge.Left,
        System.Windows.Input.Key.Right => DockEdge.Right,
        System.Windows.Input.Key.Up => DockEdge.Top,
        System.Windows.Input.Key.Down => DockEdge.Bottom,
        _ => null,
    };

    /// <summary>Records a resize the user performed by dragging the panel's inner edge.</summary>
    public static void StoreThickness(AppSettings settings, DockEdge edge, double thicknessDip)
    {
        var clamped = Clamp(thicknessDip, MinThickness, MaxThickness);

        if (edge.IsVertical())
        {
            settings.PanelWidth = clamped;
        }
        else
        {
            settings.PanelHeight = clamped;
        }
    }

    private static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return min;
        }

        return value < min ? min : value > max ? max : value;
    }
}
