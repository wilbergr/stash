using System.Windows;
using Stash.Interop;
using Stash.Services;

namespace Stash.Views;

/// <summary>
/// A plain-language guide to the app. Reachable from the panel header, F1, and
/// the tray menu.
/// </summary>
/// <remarks>
/// The shortcut tables are built in code rather than hardcoded in XAML because
/// two of the chords are configurable: the chord that opens Stash may be a
/// fallback if the preferred one was already taken, and the quick-slot prefix is
/// a setting. Printing a chord the user does not actually have would be worse
/// than no help at all.
/// </remarks>
public partial class HelpWindow : Window
{
    private readonly SettingsStore _settings;

    /// <summary>Raised when the user asks for Settings from the footer.</summary>
    public event Action? SettingsRequested;

    /// <summary>The chord that actually opens Stash, shown in the summary box.</summary>
    public string OpenChord { get; }

    public HelpWindow(SettingsStore settings, string? openChord)
    {
        _settings = settings;
        OpenChord = string.IsNullOrWhiteSpace(openChord) ? "the tray icon" : openChord;

        InitializeComponent();
        DataContext = this;

        CloseButton.Click += (_, _) => Close();
        SettingsButton.Click += (_, _) =>
        {
            SettingsRequested?.Invoke();
            Close();
        };

        Populate();
    }

    /// <summary>One row in a shortcut table.</summary>
    private sealed record Shortcut(string Keys, string Description);

    private void Populate()
    {
        var open = OpenChord;
        var slotPrefix = string.IsNullOrWhiteSpace(_settings.Current.QuickSlotModifiers)
            ? "Ctrl+Alt"
            : _settings.Current.QuickSlotModifiers;

        BasicsList.ItemsSource = new[]
        {
            new Shortcut(open, "Open Stash. Press it again, or Esc, to send it away."),
            new Shortcut("Any letter", "Filter as you type, across clip contents, the app it came from, and link text."),
            new Shortcut("Left / Right", "Move between cards. Up and Down work too."),
            new Shortcut("Enter", "Paste the selected clip into the app you were just in."),
            new Shortcut("Ctrl+1 to 9", "Paste the first to ninth card immediately, without selecting it first."),
            new Shortcut("Esc", "Close without pasting anything."),
        };

        ClipActionsList.ItemsSource = new[]
        {
            new Shortcut("Click", "Clicking a card pastes it, the same as pressing Enter."),
            new Shortcut("Alt+C", "Put the clip on the clipboard but do not paste it."),
            new Shortcut("Alt+O", "Open a link in the browser, show files in Explorer, or open an image."),
            new Shortcut("Alt+Delete", "Forget this clip."),
        };

        FavoritesList.ItemsSource = new[]
        {
            new Shortcut("Ctrl+D", "Star or unstar the selected clip."),
            new Shortcut("Alt+1 to 9", "Give the selected clip that quick slot. Press the same one again to take it back."),
            new Shortcut($"{slotPrefix}+1 to 9", "Paste that quick slot from anywhere, without opening Stash at all."),
        };

        MacroList.ItemsSource = new[]
        {
            new Shortcut("Your chord", "Whatever hotkey you gave the macro types it into the focused app."),
            new Shortcut("Ctrl+Alt+Shift+R", "Stops a recording that is in progress."),
        };

        LayoutList.ItemsSource = new[]
        {
            new Shortcut("Ctrl+Arrow", "Dock the panel to that edge of the screen."),
            new Shortcut("Tab", "Cycle through the views. Shift+Tab goes back."),
            new Shortcut("Ctrl+,", "Open Settings."),
            new Shortcut("F1", "Show this help."),
        };

        DataPathText.Text = $"Your clips live in {AppPaths.Root}. Deleting that folder removes them all.";

        var version = typeof(HelpWindow).Assembly.GetName().Version;
        VersionText.Text = version is null ? "Stash" : $"Stash {version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>Matches the title bar to the app theme, as the settings window does.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        DwmEffects.SetDarkMode(this, SystemTheme.ResolveDark(_settings.Current.Theme));
        DwmEffects.SetRoundedCorners(this);
    }
}
