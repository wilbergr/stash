namespace Stash.Models;

/// <summary>
/// User-tunable behaviour, persisted as JSON next to the history so the whole
/// profile is one folder you can delete.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// Chord that opens the stash. Ctrl+Alt+V rather than the more obvious
    /// Win+Shift+V, which Windows 11 already holds, or Ctrl+Shift+V, which many
    /// apps use for "paste as plain text" and which a global hook would steal.
    /// </summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+V";

    /// <summary>Hard cap on retained entries. Oldest unpinned entries are evicted first.</summary>
    public int MaxItems { get; set; } = 400;

    /// <summary>Unpinned entries older than this are dropped at startup. 0 disables age-based eviction.</summary>
    public int RetentionDays { get; set; } = 30;

    public bool CaptureImages { get; set; } = true;

    /// <summary>Skip images whose PNG encoding exceeds this, to keep the store small.</summary>
    public long MaxImageBytes { get; set; } = 12L * 1024 * 1024;

    public bool LaunchAtLogin { get; set; }

    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>"Bottom", "Top", "Left", "Right" or "Floating". See <see cref="DockEdge"/>.</summary>
    public string Edge { get; set; } = "Bottom";

    /// <summary>
    /// Thickness of the panel when docked to the bottom or top edge. Kept
    /// deliberately shallow — a clipboard strip should not eat the screen — which
    /// leaves a tile card about three lines of body text. Drag the panel's inner
    /// edge to change it, or set it here.
    /// </summary>
    public double PanelHeight { get; set; } = 260;

    /// <summary>Thickness of the panel when docked to the left or right edge.</summary>
    public double PanelWidth { get; set; } = 340;

    /// <summary>
    /// Remembered top-left corner for Floating mode, in physical virtual-screen
    /// pixels. Null means "never positioned", which is why these are nullable
    /// rather than NaN-sentinelled: System.Text.Json refuses to write NaN, and
    /// that silently broke every settings save.
    /// </summary>
    public double? FloatingLeft { get; set; }

    public double? FloatingTop { get; set; }

    /// <summary>Dragging the panel within this many pixels of a work-area edge re-docks it there.</summary>
    public double SnapDistance { get; set; } = 72;

    /// <summary>When true, activating an entry types Ctrl+V into the previously focused app.</summary>
    public bool PasteOnSelect { get; set; } = true;

    /// <summary>
    /// Process names (without .exe, case-insensitive) whose clipboard writes are never stored.
    /// Defaults cover the common password managers.
    /// </summary>
    public List<string> IgnoredApps { get; set; } = new()
    {
        "keepass",
        "keepassxc",
        "1password",
        "agilebits",
        "bitwarden",
        "dashlane",
        "lastpass",
        "protonpass",
        "enpass",
        "nordpass",
        "roboform",
    };

    public bool SlideAnimation { get; set; } = true;

    /// <summary>Which view the stash opens on: see <see cref="StashView"/>.</summary>
    public string DefaultView { get; set; } = "Recents";

    /// <summary>
    /// Register global hotkeys that paste favourite quick-slots 1-9 directly,
    /// without showing the stash.
    /// </summary>
    public bool QuickSlotsEnabled { get; set; } = true;

    /// <summary>
    /// Modifier prefix combined with the digits 1-9 for the quick-slot hotkeys.
    /// Ctrl+Alt is the default because Win+digit and Win+Shift+digit are both
    /// already taskbar shortcuts in Windows.
    /// </summary>
    public string QuickSlotModifiers { get; set; } = "Ctrl+Alt";

    /// <summary>Honour the clipboard flags password managers set to opt out of history. Off is not recommended.</summary>
    public bool RespectSensitiveClipboardFlags { get; set; } = true;
}
