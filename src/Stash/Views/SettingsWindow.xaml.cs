using System.Diagnostics;
using System.Windows;
using Stash.Interop;
using Stash.Models;
using Stash.Services;

namespace Stash.Views;

/// <summary>
/// The settings dialog. Reads the current <see cref="AppSettings"/> into the
/// controls, validates on save, and reports what it rejected rather than silently
/// discarding it.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly ThumbnailCache _thumbnails;
    private readonly MacroStore _macros;

    /// <summary>Raised after settings are saved, so hotkeys and theme can be reapplied.</summary>
    public event Action? Saved;

    /// <summary>Raised when the user asks to record a macro.</summary>
    public event Action? RecordMacroRequested;

    /// <summary>Raised when macros should be re-read and re-registered.</summary>
    public event Action? ReloadMacrosRequested;

    public SettingsWindow(
        SettingsStore settings,
        HistoryStore history,
        ThumbnailCache thumbnails,
        MacroStore macros,
        IReadOnlyList<string> hotkeyWarnings)
    {
        _settings = settings;
        _history = history;
        _thumbnails = thumbnails;
        _macros = macros;

        InitializeComponent();

        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
        OpenFolderButton.Click += OnOpenFolder;
        ClearHistoryButton.Click += (_, _) => OnClear(includeFavorites: false);
        ClearAllButton.Click += (_, _) => OnClear(includeFavorites: true);

        RecordMacroButton.Click += (_, _) => RecordMacroRequested?.Invoke();
        OpenMacrosButton.Click += (_, _) => OnOpenMacros();
        ReloadMacrosButton.Click += (_, _) =>
        {
            ReloadMacrosRequested?.Invoke();
            ShowMacros();
        };

        Load(hotkeyWarnings);
    }

    /// <summary>Re-reads the macro list into the UI, after a record or reload.</summary>
    public void ShowMacros()
    {
        var rows = _macros.Macros
            .Select(m => new
            {
                m.Name,
                Chord = m.Hotkey,
                Summary = Summarise(m),
            })
            .ToList();

        MacroList.ItemsSource = rows;

        MacroCountText.Text = rows.Count switch
        {
            0 => "No macros defined",
            1 => "1 macro",
            _ => $"{rows.Count} macros",
        };

        if (_macros.Problems.Count > 0)
        {
            MacroCountText.Text += $" · {_macros.Problems.Count} rejected";
        }
    }

    /// <summary>A one-line description of what a macro does.</summary>
    private static string Summarise(Models.Macro macro)
    {
        var parts = macro.Steps.Take(4).Select(s =>
        {
            if (!string.IsNullOrEmpty(s.Text))
            {
                var t = s.Text.Replace("\r", "").Replace("\n", "\\n");
                return t.Length > 24 ? $"“{t[..24]}…”" : $"“{t}”";
            }

            if (!string.IsNullOrWhiteSpace(s.Key))
            {
                return s.Key;
            }

            return $"{s.DelayMs}ms";
        });

        var text = string.Join(" · ", parts);
        return macro.Steps.Count > 4 ? $"{text} · +{macro.Steps.Count - 4} more" : text;
    }

    private void OnOpenMacros()
    {
        try
        {
            AppPaths.EnsureCreated();

            // UseShellExecute so whatever the user has associated with .json opens.
            Process.Start(new ProcessStartInfo(AppPaths.MacrosFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not open macros.json.", ex);
            StatusText.Text = "Windows could not open macros.json. Use 'Open data folder'.";
        }
    }

    /// <summary>
    /// Matches the title bar to the app theme. This is a normal framed window, so
    /// without it a dark dialog would sit under a light caption bar.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        DwmEffects.SetDarkMode(this, SystemTheme.ResolveDark(_settings.Current.Theme));
        DwmEffects.SetRoundedCorners(this);
    }

    // ---- Load ---------------------------------------------------------------

    private void Load(IReadOnlyList<string> hotkeyWarnings)
    {
        var s = _settings.Current;

        HotkeyBox.Text = s.Hotkey;
        QuickSlotsCheck.IsChecked = s.QuickSlotsEnabled;
        QuickSlotPrefixBox.Text = s.QuickSlotModifiers;

        var edge = DockEdgeExtensions.Parse(s.Edge);
        EdgeBottom.IsChecked = edge == DockEdge.Bottom;
        EdgeTop.IsChecked = edge == DockEdge.Top;
        EdgeLeft.IsChecked = edge == DockEdge.Left;
        EdgeRight.IsChecked = edge == DockEdge.Right;
        EdgeFloating.IsChecked = edge == DockEdge.Floating;

        SlideCheck.IsChecked = s.SlideAnimation;
        PanelHeightBox.Text = ((int)s.PanelHeight).ToString();
        PanelWidthBox.Text = ((int)s.PanelWidth).ToString();

        ThemeLight.IsChecked = string.Equals(s.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        ThemeDark.IsChecked = string.Equals(s.Theme, "Dark", StringComparison.OrdinalIgnoreCase);
        ThemeSystem.IsChecked = !ThemeLight.IsChecked!.Value && !ThemeDark.IsChecked!.Value;

        PasteOnSelectCheck.IsChecked = s.PasteOnSelect;
        CaptureImagesCheck.IsChecked = s.CaptureImages;
        MaxItemsBox.Text = s.MaxItems.ToString();
        RetentionBox.Text = s.RetentionDays.ToString();

        // Read the live registry state rather than the stored flag: the user may
        // have removed the entry outside Stash.
        LaunchAtLoginCheck.IsChecked = StartupManager.IsEnabled();

        SensitiveFlagsCheck.IsChecked = s.RespectSensitiveClipboardFlags;
        IgnoredAppsBox.Text = string.Join(Environment.NewLine, s.IgnoredApps);

        MacrosEnabledCheck.IsChecked = s.MacrosEnabled;
        MacroDelayBox.Text = s.MacroTypingDelayMs.ToString();
        ShowMacros();

        if (hotkeyWarnings.Count > 0)
        {
            WarningList.ItemsSource = hotkeyWarnings;
            WarningBox.Visibility = Visibility.Visible;
        }

        StatusText.Text = $"{_history.Items.Count} clips stored";
    }

    // ---- Save ---------------------------------------------------------------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var problems = new List<string>();

        var hotkey = HotkeyBox.Text.Trim();
        if (!HotkeyChord.TryParse(hotkey, out _))
        {
            problems.Add("the stash hotkey needs at least one modifier plus a key");
        }

        var prefix = QuickSlotPrefixBox.Text.Trim();
        if (QuickSlotsCheck.IsChecked == true && !HotkeyChord.TryParse(prefix + "+1", out _))
        {
            problems.Add("the quick-slot prefix must be modifiers only, e.g. Ctrl+Alt");
        }

        var macroDelay = ParseInt(MacroDelayBox.Text, 0, 0, 100, "typing pace", problems);
        var maxItems = ParseInt(MaxItemsBox.Text, 400, 10, 5000, "keep at most", problems);
        var retention = ParseInt(RetentionBox.Text, 30, 0, 3650, "forget after", problems);

        var (minThickness, maxThickness) = StashPlacement.ThicknessRange;
        var height = ParseInt(PanelHeightBox.Text, 272, (int)minThickness, (int)maxThickness, "height", problems);
        var width = ParseInt(PanelWidthBox.Text, 340, (int)minThickness, (int)maxThickness, "width", problems);

        if (problems.Count > 0)
        {
            StatusText.Text = "Not saved — " + string.Join("; ", problems) + ".";
            return;
        }

        var edge = EdgeTop.IsChecked == true ? DockEdge.Top
            : EdgeLeft.IsChecked == true ? DockEdge.Left
            : EdgeRight.IsChecked == true ? DockEdge.Right
            : EdgeFloating.IsChecked == true ? DockEdge.Floating
            : DockEdge.Bottom;

        var theme = ThemeLight.IsChecked == true ? "Light"
            : ThemeDark.IsChecked == true ? "Dark"
            : "System";

        var ignored = IgnoredAppsBox.Text
            .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).Trim())
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var wantsStartup = LaunchAtLoginCheck.IsChecked == true;
        var startupChanged = wantsStartup != StartupManager.IsEnabled();
        var startupOk = !startupChanged || StartupManager.SetEnabled(wantsStartup);

        _settings.Update(s =>
        {
            s.Hotkey = hotkey;
            s.QuickSlotsEnabled = QuickSlotsCheck.IsChecked == true;
            s.QuickSlotModifiers = prefix;

            s.Edge = edge.ToString();
            s.SlideAnimation = SlideCheck.IsChecked == true;
            s.PanelHeight = height;
            s.PanelWidth = width;

            s.Theme = theme;

            s.PasteOnSelect = PasteOnSelectCheck.IsChecked == true;
            s.CaptureImages = CaptureImagesCheck.IsChecked == true;
            s.MaxItems = maxItems;
            s.RetentionDays = retention;
            s.LaunchAtLogin = wantsStartup && startupOk;

            s.RespectSensitiveClipboardFlags = SensitiveFlagsCheck.IsChecked == true;
            s.IgnoredApps = ignored;

            s.MacrosEnabled = MacrosEnabledCheck.IsChecked == true;
            s.MacroTypingDelayMs = macroDelay;
        });

        Saved?.Invoke();

        if (!startupOk)
        {
            // Saving the rest succeeded, so report the one part that did not
            // rather than failing the whole dialog.
            StatusText.Text = "Saved, but the sign-in entry could not be written. See stash.log.";
            return;
        }

        Close();
    }

    private static int ParseInt(
        string text,
        int fallback,
        int min,
        int max,
        string label,
        List<string> problems)
    {
        if (!int.TryParse(text.Trim(), out var value))
        {
            problems.Add($"{label} must be a whole number");
            return fallback;
        }

        if (value < min || value > max)
        {
            problems.Add($"{label} must be between {min} and {max}");
            return Math.Clamp(value, min, max);
        }

        return value;
    }

    // ---- Data actions -------------------------------------------------------

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not open the data folder.", ex);
            StatusText.Text = "Windows could not open that folder.";
        }
    }

    private void OnClear(bool includeFavorites)
    {
        var message = includeFavorites
            ? "Delete every clip, including favorites and their quick slots?\n\nThis cannot be undone."
            : "Delete the clip history?\n\nFavorites and their quick slots are kept.";

        var answer = MessageBox.Show(
            this,
            message,
            includeFavorites ? "Clear everything" : "Clear history",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _history.Clear(includeFavorites);
        _history.FlushIfDirty();
        _thumbnails.Clear();

        StatusText.Text = $"{_history.Items.Count} clips stored";
    }
}
