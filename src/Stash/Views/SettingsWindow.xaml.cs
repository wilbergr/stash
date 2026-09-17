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

    /// <summary>Raised with a copy of the macro the user wants to edit.</summary>
    public event Action<Models.Macro>? EditMacroRequested;

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

    /// <summary>Row shape for the macro list.</summary>
    private sealed record MacroRow(
        string Id,
        string Name,
        string Chord,
        string Summary,
        bool Enabled,
        string? Problem,
        bool HasProblem,
        double DimWhenOff);

    /// <summary>
    /// Re-reads the macro list into the UI, after a record, edit or reload.
    /// </summary>
    /// <remarks>
    /// Lists every entry in the file, not just the active ones, so a disabled or
    /// broken macro can still be re-enabled, edited or deleted from here.
    /// </remarks>
    public void ShowMacros()
    {
        // Suppress the checkbox handlers while the list is rebuilt, or assigning
        // ItemsSource fires Checked/Unchecked and writes the file back.
        _populating = true;

        try
        {
            var rows = _macros.All
                .Select(e => new MacroRow(
                    e.Macro.Id,
                    e.Macro.Name,
                    e.Macro.Hotkey,
                    Summarise(e.Macro),
                    e.Macro.Enabled,
                    // "Disabled" is self-evident from the unticked box.
                    e.Problem == "Disabled" ? null : e.Problem,
                    e.Problem is not null && e.Problem != "Disabled",
                    e.Active ? 1.0 : 0.45))
                .ToList();

            MacroList.ItemsSource = rows;

            var active = rows.Count(r => r.Enabled && !r.HasProblem);

            MacroCountText.Text = rows.Count switch
            {
                0 => "No macros defined",
                1 => active == 1 ? "1 macro" : "1 macro, inactive",
                _ => $"{rows.Count} macros, {active} active",
            };
        }
        finally
        {
            _populating = false;
        }
    }

    private bool _populating;

    private static string? IdOf(object sender)
        => sender is FrameworkElement { } element
            ? (element as System.Windows.Controls.Primitives.ButtonBase)?.CommandParameter as string ?? element.Tag as string
            : null;

    private void OnMacroEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_populating || sender is not System.Windows.Controls.CheckBox box || box.Tag is not string id)
        {
            return;
        }

        if (!_macros.SetEnabled(id, box.IsChecked == true, out var error))
        {
            StatusText.Text = error ?? "That macro could not be changed.";

            // Put the tick back where it was, since the change did not take.
            ShowMacros();
            return;
        }

        StatusText.Text = box.IsChecked == true ? "Macro enabled." : "Macro disabled.";
        ReloadMacrosRequested?.Invoke();
        ShowMacros();
    }

    private void OnEditMacro(object sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is not { } id)
        {
            return;
        }

        var macro = _macros.All.FirstOrDefault(m => m.Macro.Id == id)?.Macro;
        if (macro is null)
        {
            return;
        }

        EditMacroRequested?.Invoke(macro.Clone());
    }

    private void OnDeleteMacro(object sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is not { } id)
        {
            return;
        }

        var macro = _macros.All.FirstOrDefault(m => m.Macro.Id == id)?.Macro;
        if (macro is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Delete the macro '{macro.Name}' on {macro.Hotkey}?\n\nThis cannot be undone.",
            "Delete macro",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        if (!_macros.Delete(id, out var error))
        {
            StatusText.Text = error ?? "That macro could not be deleted.";
            return;
        }

        StatusText.Text = $"Deleted '{macro.Name}'.";
        ReloadMacrosRequested?.Invoke();
        ShowMacros();
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
