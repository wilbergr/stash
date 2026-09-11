using System.Windows;
using Stash.Models;
using Stash.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Stash.Views;

/// <summary>
/// The notification-area icon and its menu — the way Stash stays reachable when
/// the hotkey is unavailable, and the only visible sign it is running.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly SettingsStore _settings;
    private Drawing.Icon? _loadedIcon;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action<DockEdge>? DockRequested;
    public event Action<bool>? ClearRequested;
    public event Action? QuitRequested;

    public TrayIcon(SettingsStore settings)
    {
        _settings = settings;

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "Stash — clipboard manager",
        };

        _icon.MouseClick += OnMouseClick;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.ContextMenuStrip = BuildMenu();
    }

    /// <summary>Updates the hover tooltip with the chord actually in effect.</summary>
    public void SetHotkeyHint(string? chord)
    {
        // NotifyIcon.Text is capped at 63 characters by the shell.
        var text = string.IsNullOrEmpty(chord)
            ? "Stash — clipboard manager (no hotkey registered)"
            : $"Stash — press {chord}";

        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowMessage(string title, string body)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = body;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Tray notification failed.", ex);
        }
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            OpenRequested?.Invoke();
        }
    }

    /// <summary>
    /// Loads the icon at the shell's small-icon size so it stays crisp on
    /// high-DPI displays instead of being downscaled from 32px.
    /// </summary>
    private Drawing.Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("Assets/Stash.ico", UriKind.Relative))?.Stream;
            if (stream is not null)
            {
                using (stream)
                {
                    var size = Forms.SystemInformation.SmallIconSize;
                    _loadedIcon = new Drawing.Icon(stream, size.Width, size.Height);
                    return _loadedIcon;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Tray icon could not be loaded from resources.", ex);
        }

        return Drawing.SystemIcons.Application;
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var dark = SystemTheme.ResolveDark(_settings.Current.Theme);

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new ThemedRenderer(dark),
            ShowImageMargin = false,
            BackColor = dark ? Drawing.Color.FromArgb(43, 43, 47) : Drawing.Color.FromArgb(249, 249, 250),
            ForeColor = dark ? Drawing.Color.FromArgb(240, 240, 242) : Drawing.Color.FromArgb(26, 26, 28),
        };

        var open = new Forms.ToolStripMenuItem("Open stash", null, (_, _) => OpenRequested?.Invoke())
        {
            Font = new Drawing.Font(Forms.Control.DefaultFont, Drawing.FontStyle.Bold),
        };
        menu.Items.Add(open);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var dock = new Forms.ToolStripMenuItem("Dock to");
        foreach (var edge in new[] { DockEdge.Bottom, DockEdge.Top, DockEdge.Left, DockEdge.Right, DockEdge.Floating })
        {
            var captured = edge;
            var label = edge == DockEdge.Floating ? "Floating" : edge.ToString();

            dock.DropDownItems.Add(new Forms.ToolStripMenuItem(label, null, (_, _) => DockRequested?.Invoke(captured)));
        }
        menu.Items.Add(dock);

        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add(new Forms.ToolStripMenuItem("Settings…", null, (_, _) => SettingsRequested?.Invoke()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Clear history (keep favorites)", null, (_, _) => ClearRequested?.Invoke(false)));
        menu.Items.Add(new Forms.ToolStripMenuItem("Clear everything…", null, (_, _) => ClearRequested?.Invoke(true)));

        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add(new Forms.ToolStripMenuItem("Quit Stash", null, (_, _) => QuitRequested?.Invoke()));

        // Reflect the current dock choice each time the menu opens.
        menu.Opening += (_, _) =>
        {
            var current = DockEdgeExtensions.Parse(_settings.Current.Edge);

            foreach (var item in dock.DropDownItems)
            {
                if (item is Forms.ToolStripMenuItem entry)
                {
                    entry.Checked = string.Equals(entry.Text, current.ToString(), StringComparison.OrdinalIgnoreCase);
                }
            }
        };

        return menu;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _loadedIcon?.Dispose();
    }

    /// <summary>
    /// Paints the tray menu to match the system theme; the stock WinForms colours
    /// look wrong next to a dark Windows 11 shell.
    /// </summary>
    private sealed class ThemedRenderer : Forms.ToolStripProfessionalRenderer
    {
        private readonly bool _dark;

        public ThemedRenderer(bool dark)
            : base(new ThemedColors(dark))
        {
            _dark = dark;
            RoundedEdges = true;
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item?.Selected == true
                ? (_dark ? Drawing.Color.White : Drawing.Color.Black)
                : (_dark ? Drawing.Color.FromArgb(238, 238, 240) : Drawing.Color.FromArgb(26, 26, 28));

            base.OnRenderItemText(e);
        }
    }

    private sealed class ThemedColors : Forms.ProfessionalColorTable
    {
        private readonly bool _dark;

        public ThemedColors(bool dark)
        {
            _dark = dark;
            UseSystemColors = false;
        }

        private Drawing.Color Surface => _dark
            ? Drawing.Color.FromArgb(43, 43, 47)
            : Drawing.Color.FromArgb(249, 249, 250);

        private Drawing.Color Highlight => _dark
            ? Drawing.Color.FromArgb(62, 62, 68)
            : Drawing.Color.FromArgb(232, 232, 236);

        public override Drawing.Color ToolStripDropDownBackground => Surface;

        public override Drawing.Color MenuBorder => _dark
            ? Drawing.Color.FromArgb(64, 64, 70)
            : Drawing.Color.FromArgb(214, 214, 218);

        public override Drawing.Color MenuItemBorder => Highlight;

        public override Drawing.Color MenuItemSelected => Highlight;

        public override Drawing.Color MenuItemSelectedGradientBegin => Highlight;

        public override Drawing.Color MenuItemSelectedGradientEnd => Highlight;

        public override Drawing.Color MenuItemPressedGradientBegin => Surface;

        public override Drawing.Color MenuItemPressedGradientEnd => Surface;

        public override Drawing.Color ImageMarginGradientBegin => Surface;

        public override Drawing.Color ImageMarginGradientMiddle => Surface;

        public override Drawing.Color ImageMarginGradientEnd => Surface;

        public override Drawing.Color SeparatorDark => _dark
            ? Drawing.Color.FromArgb(70, 70, 76)
            : Drawing.Color.FromArgb(222, 222, 226);

        public override Drawing.Color SeparatorLight => SeparatorDark;
    }
}
