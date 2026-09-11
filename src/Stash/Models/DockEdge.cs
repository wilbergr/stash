using System.Windows;
using System.Windows.Controls;

namespace Stash.Models;

/// <summary>Where the stash lives, which also decides the direction it slides in from.</summary>
public enum DockEdge
{
    Bottom,
    Top,
    Left,
    Right,

    /// <summary>Detached: the user has dragged it somewhere and the position is remembered.</summary>
    Floating,
}

public static class DockEdgeExtensions
{
    /// <summary>
    /// Docked left/right turns the stash into a tall column, so the card strip
    /// stacks vertically instead of running across.
    /// </summary>
    public static bool IsVertical(this DockEdge edge)
        => edge is DockEdge.Left or DockEdge.Right;

    public static Orientation CardOrientation(this DockEdge edge)
        => edge.IsVertical() ? Orientation.Vertical : Orientation.Horizontal;

    /// <summary>
    /// The offset the panel animates in from, expressed as a fraction of its own
    /// size along the travel axis. Floating gets a small vertical pop instead of a
    /// full slide, because it has no edge to emerge from.
    /// </summary>
    public static Vector SlideFrom(this DockEdge edge, Size panel) => edge switch
    {
        DockEdge.Bottom => new Vector(0, panel.Height),
        DockEdge.Top => new Vector(0, -panel.Height),
        DockEdge.Left => new Vector(-panel.Width, 0),
        DockEdge.Right => new Vector(panel.Width, 0),
        _ => new Vector(0, 18),
    };

    /// <summary>Which side of the panel faces into the screen, i.e. the resize grip edge.</summary>
    public static Dock InnerEdge(this DockEdge edge) => edge switch
    {
        DockEdge.Bottom => Dock.Top,
        DockEdge.Top => Dock.Bottom,
        DockEdge.Left => Dock.Right,
        _ => Dock.Left,
    };

    public static DockEdge Parse(string? text)
        => Enum.TryParse<DockEdge>(text, ignoreCase: true, out var edge) ? edge : DockEdge.Bottom;
}
