using System.Windows.Interop;

namespace Stash.Interop;

/// <summary>
/// A message-only window (child of HWND_MESSAGE). It never appears on screen, in
/// Alt+Tab or on the taskbar, but it owns an HWND — which is what
/// AddClipboardFormatListener and RegisterHotKey both require.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private const int HWND_MESSAGE = -3;

    private readonly HwndSource _source;
    private bool _disposed;

    /// <summary>
    /// Raised for every message. Return true to mark the message handled and stop
    /// further processing.
    /// </summary>
    public event Func<int, IntPtr, IntPtr, bool>? Message;

    public IntPtr Handle => _source.Handle;

    public MessageWindow(string name)
    {
        var parameters = new HwndSourceParameters(name)
        {
            ParentWindow = new IntPtr(HWND_MESSAGE),
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var subscribers = Message;
        if (subscribers is null)
        {
            return IntPtr.Zero;
        }

        // Invoke each handler; any one of them claiming the message wins.
        foreach (var handler in subscribers.GetInvocationList().Cast<Func<int, IntPtr, IntPtr, bool>>())
        {
            try
            {
                if (handler(msg, wParam, lParam))
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }
            catch
            {
                // A faulting listener must never take down the message pump.
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}
