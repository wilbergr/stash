using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Stash.Interop;
using Stash.Models;
using Stash.Services;
using Stash.ViewModels;
using Stash.Views;

namespace Stash;

/// <summary>
/// Composition root. Stash has no DI container: the object graph is small, fixed,
/// and easier to follow written out.
/// </summary>
public partial class App : Application
{
    /// <summary>Guards against a second copy running and fighting over the hotkey.</summary>
    private const string InstanceMutexName = @"Local\Stash.SingleInstance";

    /// <summary>A second launch signals this instead of starting up.</summary>
    private const string ShowSignalName = @"Local\Stash.ShowPanel";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showSignalRegistration;

    private SettingsStore _settings = null!;
    private HistoryStore _history = null!;
    private ThumbnailCache _thumbnails = null!;
    private MessageWindow _messageWindow = null!;
    private HotkeyService _hotkeys = null!;
    private ClipboardMonitor _monitor = null!;
    private PasteService _paste = null!;
    private StashViewModel _stashViewModel = null!;
    private StashWindow _stashWindow = null!;
    private TrayIcon _tray = null!;
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// False until OnStartup finishes. Decides whether an unhandled exception is
    /// treated as fatal or merely logged.
    /// </summary>
    private bool _startupComplete;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ClaimSingleInstance())
        {
            // Another copy owns the hotkey; ask it to show itself and step aside.
            SignalExistingInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        AppPaths.EnsureCreated();
        AppPaths.Log("Stash starting.");

        _settings = new SettingsStore();
        _settings.Load();

        ApplyTheme();

        _thumbnails = new ThumbnailCache();
        _history = new HistoryStore(_settings);
        _history.Load();

        _messageWindow = new MessageWindow("Stash.Messages");

        _monitor = new ClipboardMonitor(_messageWindow, _settings, _history);
        _paste = new PasteService(_history, _monitor);

        _stashViewModel = new StashViewModel(_history, _paste, _thumbnails, _settings);
        _stashWindow = new StashWindow(_stashViewModel, _settings);
        _stashWindow.SettingsRequested += ShowSettings;

        _hotkeys = new HotkeyService(_messageWindow);
        _hotkeys.Pressed += OnHotkeyPressed;
        ApplyHotkeys();

        _tray = new TrayIcon(_settings);
        _tray.OpenRequested += () => ShowPanel();
        _tray.SettingsRequested += ShowSettings;
        _tray.DockRequested += edge => _stashWindow.DockTo(edge);
        _tray.ClearRequested += ClearHistory;
        _tray.QuitRequested += Shutdown;
        _tray.SetHotkeyHint(_hotkeys.StashChord);

        _monitor.Start();

        // Rebuild the stash whenever history changes, so it is already correct
        // the moment the hotkey is pressed.
        _history.Changed += () => _stashViewModel.Rebuild();
        _stashViewModel.Rebuild();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Build the visual tree now rather than on first show.
        _stashWindow.UpdateLayout();

        if (_hotkeys.StashChord is null)
        {
            _tray.ShowMessage(
                "Stash is running, but has no hotkey",
                "Every candidate chord is already in use. Open Stash from this tray icon, or pick a different hotkey in Settings.");
        }

        _startupComplete = true;
        AppPaths.Log($"Stash ready. Hotkey: {_hotkeys.StashChord ?? "none"}.");
    }

    // ---- Single instance ----------------------------------------------------

    private bool ClaimSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);

            if (!createdNew)
            {
                return false;
            }

            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);

            // Wake up and show the stash when a second launch signals us.
            _showSignalRegistration = ThreadPool.RegisterWaitForSingleObject(
                _showSignal,
                (_, _) => Dispatcher.BeginInvoke(() => ShowPanel()),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);

            return true;
        }
        catch (Exception ex)
        {
            // If the mutex cannot be created, running is better than not running.
            AppPaths.Log("Single-instance check failed; continuing anyway.", ex);
            return true;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not signal the running instance.", ex);
        }
    }

    // ---- Hotkeys ------------------------------------------------------------

    private void ApplyHotkeys()
    {
        var s = _settings.Current;
        _hotkeys.Apply(s.Hotkey, s.QuickSlotsEnabled, s.QuickSlotModifiers);

        _stashViewModel.HotkeyHint = _hotkeys.StashChord ?? "no hotkey";
        _tray?.SetHotkeyHint(_hotkeys.StashChord);

        foreach (var warning in _hotkeys.Warnings)
        {
            AppPaths.Log("Hotkey: " + warning);
        }
    }

    private void OnHotkeyPressed(HotkeyPressed pressed)
    {
        switch (pressed.Kind)
        {
            case HotkeyKind.ShowPanel:
                ShowPanel();
                break;

            case HotkeyKind.QuickSlot:
                _ = PasteQuickSlot(pressed.Slot);
                break;
        }
    }

    /// <summary>
    /// Pastes a quick-slot favourite straight into the focused app, without the
    /// stash appearing at all.
    /// </summary>
    private async Task PasteQuickSlot(int slot)
    {
        var item = _history.BySlot(slot);

        if (item is null)
        {
            _tray.ShowMessage(
                $"Quick slot {slot} is empty",
                "Open the stash, select a clip and press Alt+" + slot + " to assign it.");
            return;
        }

        // The stash is not involved, so the focused window is already the target.
        var target = NativeMethods.GetForegroundWindow();
        await _paste.PasteInto(item, target, _settings.Current.PasteOnSelect);
    }

    // ---- Windows ------------------------------------------------------------

    private void ShowPanel()
    {
        // Captured before the stash takes focus: this is where a clip gets pasted.
        var target = NativeMethods.GetForegroundWindow();
        _stashWindow.ShowPanel(target);
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _stashWindow.HidePanel();

        _settingsWindow = new SettingsWindow(_settings, _history, _thumbnails, _hotkeys.Warnings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Saved += () =>
        {
            ApplyTheme();
            ApplyHotkeys();
            _stashViewModel.Rebuild();
        };

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ClearHistory(bool includeFavorites)
    {
        if (includeFavorites)
        {
            var answer = MessageBox.Show(
                "Delete every clip, including favorites and their quick slots?\n\nThis cannot be undone.",
                "Clear everything",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (answer != MessageBoxResult.OK)
            {
                return;
            }
        }

        _history.Clear(includeFavorites);
        _history.FlushIfDirty();
        _thumbnails.Clear();
        _stashViewModel.Rebuild();
    }

    // ---- Theme --------------------------------------------------------------

    /// <summary>
    /// Swaps the palette and republishes the accent brushes.
    /// </summary>
    private void ApplyTheme()
    {
        var dark = SystemTheme.ResolveDark(_settings.Current.Theme);

        try
        {
            var palette = new ResourceDictionary
            {
                Source = new Uri(
                    dark ? "Theme/Palette.Dark.xaml" : "Theme/Palette.Light.xaml",
                    UriKind.Relative),
            };

            Resources.MergedDictionaries[0] = palette;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Palette could not be swapped.", ex);
        }

        var accent = SystemTheme.AccentColor();

        // Entries set directly on this dictionary win over the merged palette,
        // which is how the system accent overrides the placeholder.
        Resources["Brush.Accent"] = Frozen(accent);
        Resources["Brush.AccentSoft"] = Frozen(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        Resources["Brush.OnAccent"] = Frozen(ReadableOn(accent));

        if (_stashWindow is not null)
        {
            DwmEffects.SetDarkMode(_stashWindow, dark);
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Picks black or white text for an accent background using relative
    /// luminance, so a pale accent does not end up with unreadable white text.
    /// </summary>
    private static Color ReadableOn(Color background)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        var luminance =
            (0.2126 * Channel(background.R)) +
            (0.7152 * Channel(background.G)) +
            (0.0722 * Channel(background.B));

        return luminance > 0.45 ? Colors.Black : Colors.White;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
            && string.Equals(_settings.Current.Theme, "System", StringComparison.OrdinalIgnoreCase))
        {
            ApplyTheme();
        }
        else if (e.Category == UserPreferenceCategory.Color)
        {
            // Accent can change independently of light/dark.
            ApplyTheme();
        }
    }

    // ---- Shutdown -----------------------------------------------------------

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.Log("Unhandled exception on the UI thread.", e.Exception);

        // Swallowing everything would leave a half-initialised app running as a
        // zombie with no tray icon and no hotkey, which is far worse than a clean
        // failure. So a fault during startup is reported and fatal; only faults
        // after startup are absorbed, because a clipboard manager should not die
        // over a single malformed clip.
        if (!_startupComplete)
        {
            MessageBox.Show(
                $"Stash could not start.\n\n{e.Exception.Message}\n\nDetails were written to:\n{AppPaths.LogFile}",
                "Stash",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
            Shutdown(1);
            return;
        }

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppPaths.Log("Stash exiting.");

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        _history?.Shutdown();
        _monitor?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _messageWindow?.Dispose();

        _showSignalRegistration?.Unregister(null);
        _showSignal?.Dispose();

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owner, e.g. the single-instance claim failed earlier.
            }

            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
