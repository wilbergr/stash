using System.Windows.Threading;
using Stash.Interop;
using Stash.Models;

namespace Stash.Services;

/// <summary>
/// Watches the system clipboard and files every change into the history.
/// </summary>
/// <remarks>
/// Uses AddClipboardFormatListener rather than the legacy SetClipboardViewer
/// chain: the chain breaks permanently if any participant misbehaves, whereas the
/// listener is managed by the OS.
/// </remarks>
public sealed class ClipboardMonitor : IDisposable
{
    /// <summary>
    /// Apps commonly publish formats in several passes, producing a burst of
    /// WM_CLIPBOARDUPDATE for one logical copy. Waiting briefly means we read the
    /// finished clipboard once instead of a half-populated one repeatedly.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(140);

    private readonly MessageWindow _window;
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly DispatcherTimer _debounceTimer;

    private bool _listening;
    private AppIdentity _pendingSource = AppIdentity.None;

    /// <summary>Raised for every stored entry, so the UI can flash a subtle confirmation.</summary>
    public event Action<ClipItem>? Captured;

    /// <summary>Set true while Stash itself writes to the clipboard, to ignore its own echo.</summary>
    public bool Suspended { get; set; }

    public ClipboardMonitor(MessageWindow window, SettingsStore settings, HistoryStore history)
    {
        _window = window;
        _settings = settings;
        _history = history;

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = Debounce };
        _debounceTimer.Tick += OnDebounceElapsed;
    }

    public void Start()
    {
        if (_listening)
        {
            return;
        }

        _window.Message += OnMessage;

        if (NativeMethods.AddClipboardFormatListener(_window.Handle))
        {
            _listening = true;
        }
        else
        {
            _window.Message -= OnMessage;
            AppPaths.Log("AddClipboardFormatListener failed; clipboard changes will not be captured.");
        }
    }

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeMethods.WM_CLIPBOARDUPDATE)
        {
            return false;
        }

        if (Suspended)
        {
            return true;
        }

        // Identify the source app now: by the time the debounce elapses the
        // foreground window may well have changed.
        _pendingSource = ForegroundApp.Current();

        _debounceTimer.Stop();
        _debounceTimer.Start();
        return true;
    }

    private void OnDebounceElapsed(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();

        if (Suspended)
        {
            return;
        }

        try
        {
            if (ClipboardCapture.TryCapture(_settings.Current, _pendingSource, out var captured, out var skipReason))
            {
                var item = _history.Add(captured);
                Captured?.Invoke(item);
            }
            else if (skipReason is not null)
            {
                AppPaths.Log($"Clipboard change not stored: {skipReason}.");
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Clipboard capture failed.", ex);
        }
        finally
        {
            _pendingSource = AppIdentity.None;
        }
    }

    public void Dispose()
    {
        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceElapsed;

        if (_listening)
        {
            NativeMethods.RemoveClipboardFormatListener(_window.Handle);
            _window.Message -= OnMessage;
            _listening = false;
        }
    }
}
