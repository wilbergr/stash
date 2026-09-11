using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using Stash.Interop;
using Stash.Models;

namespace Stash.Services;

/// <summary>
/// Puts an entry back on the clipboard and, optionally, types Ctrl+V into the app
/// the user came from.
/// </summary>
public sealed class PasteService
{
    /// <summary>
    /// How long to let the target window settle after focus is handed back, before
    /// injecting the keystroke. Too short and the paste lands nowhere because the
    /// app has not finished activating.
    /// </summary>
    private static readonly TimeSpan FocusSettle = TimeSpan.FromMilliseconds(70);

    private readonly HistoryStore _history;
    private readonly ClipboardMonitor _monitor;

    public PasteService(HistoryStore history, ClipboardMonitor monitor)
    {
        _history = history;
        _monitor = monitor;
    }

    /// <summary>
    /// Places <paramref name="item"/> on the clipboard. Returns false if the
    /// clipboard could not be written.
    /// </summary>
    public bool CopyToClipboard(ClipItem item)
    {
        var data = BuildDataObject(item);
        if (data is null)
        {
            return false;
        }

        // Our own write would otherwise come straight back as a capture.
        _monitor.Suspended = true;

        try
        {
            return TrySetClipboard(data);
        }
        finally
        {
            // Release the suspend after the resulting WM_CLIPBOARDUPDATE has been
            // delivered and discarded.
            ReleaseSuspendSoon();
        }
    }

    /// <summary>
    /// Copies the entry and pastes it into <paramref name="target"/>.
    /// </summary>
    public async Task<bool> PasteInto(ClipItem item, IntPtr target, bool sendKeystroke)
    {
        if (!CopyToClipboard(item))
        {
            AppPaths.Log($"Paste aborted: could not put the {item.Kind} clip on the clipboard.");
            return false;
        }

        _history.RecordUse(item);

        if (!sendKeystroke || target == IntPtr.Zero)
        {
            AppPaths.Log($"Copied {item.Kind} clip to the clipboard without pasting (target=0x{target:X}, sendKeystroke={sendKeystroke}).");
            return true;
        }

        var restored = ForegroundApp.Restore(target);

        // Poll rather than wait a fixed interval: how long a window takes to come
        // back to the foreground varies with what else the shell is doing, and a
        // single fixed delay is either too short or wasteful.
        var focused = await WaitForForeground(target, TimeSpan.FromMilliseconds(600));

        if (!focused)
        {
            AppPaths.Log(
                $"Paste skipped: target 0x{target:X} never took focus " +
                $"(SetForegroundWindow={restored}, foreground=0x{NativeMethods.GetForegroundWindow():X}). " +
                "The clip is on the clipboard, so Ctrl+V still works.");
            return false;
        }

        // Let the newly focused app settle its caret before injecting keys.
        await Task.Delay(FocusSettle);

        InputSimulator.SendPaste();
        AppPaths.Log($"Pasted {item.Kind} clip into 0x{target:X}.");
        return true;
    }

    /// <summary>
    /// Waits for <paramref name="target"/> to become the foreground window,
    /// re-asserting the request once part-way through in case the first attempt
    /// was refused while our own window was still closing.
    /// </summary>
    private static async Task<bool> WaitForForeground(IntPtr target, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        var reasserted = false;

        while (DateTime.UtcNow < deadline)
        {
            if (NativeMethods.GetForegroundWindow() == target)
            {
                return true;
            }

            await Task.Delay(25);

            if (!reasserted && DateTime.UtcNow > deadline - (budget / 2))
            {
                reasserted = true;
                ForegroundApp.Restore(target);
            }
        }

        return NativeMethods.GetForegroundWindow() == target;
    }

    private static IDataObject? BuildDataObject(ClipItem item)
    {
        try
        {
            var data = new DataObject();

            switch (item.Kind)
            {
                case ClipKind.Image:
                {
                    if (item.ImageFile is null)
                    {
                        return null;
                    }

                    var path = AppPaths.ImagePath(item.ImageFile);
                    if (!File.Exists(path))
                    {
                        return null;
                    }

                    var bytes = File.ReadAllBytes(path);

                    // Publish both: "PNG" keeps transparency for apps that prefer it,
                    // Bitmap is the format every legacy target understands.
                    data.SetData("PNG", new MemoryStream(bytes), autoConvert: false);

                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = new MemoryStream(bytes);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    data.SetImage(bitmap);
                    return data;
                }

                case ClipKind.Files:
                {
                    if (item.Files is null || item.Files.Count == 0)
                    {
                        return null;
                    }

                    var existing = item.Files.Where(File.Exists).ToArray();
                    var paths = new StringCollection();
                    paths.AddRange(existing.Length > 0 ? existing : item.Files.ToArray());
                    data.SetFileDropList(paths);

                    // Text fallback so the paths paste into an editor too.
                    data.SetText(string.Join(Environment.NewLine, item.Files));
                    return data;
                }

                default:
                {
                    if (item.Text is null)
                    {
                        return null;
                    }

                    data.SetText(item.Text, TextDataFormat.UnicodeText);
                    return data;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Clipboard payload could not be built.", ex);
            return null;
        }
    }

    /// <summary>
    /// Writes to the clipboard with retries, since another process may hold it open.
    /// </summary>
    private static bool TrySetClipboard(IDataObject data)
    {
        const int attempts = 6;

        for (var i = 0; i < attempts; i++)
        {
            try
            {
                // copy: true flushes the data so it outlives our process.
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(25 + (i * 15));
            }
            catch (Exception ex)
            {
                AppPaths.Log("Clipboard write failed.", ex);
                return false;
            }
        }

        AppPaths.Log("Clipboard remained locked by another process; write abandoned.");
        return false;
    }

    private void ReleaseSuspendSoon()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _monitor.Suspended = false;
        };

        timer.Start();
    }
}
