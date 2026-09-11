using System.Diagnostics;
using System.Text;

namespace Stash.Interop;

/// <summary>Identity of the window that was in front when a clip was captured.</summary>
public readonly record struct AppIdentity(IntPtr Handle, string? ProcessName, string? FriendlyName)
{
    public static readonly AppIdentity None = new(IntPtr.Zero, null, null);
}

/// <summary>
/// Reads and restores the foreground window. Stash needs both: it attributes each
/// clip to the app it came from, and it must hand focus back before pasting.
/// </summary>
public static class ForegroundApp
{
    private static readonly Dictionary<uint, AppIdentity> Cache = new();

    /// <summary>Identifies the current foreground window, caching per process id.</summary>
    public static AppIdentity Current()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return AppIdentity.None;
        }

        if (NativeMethods.GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
        {
            return new AppIdentity(hwnd, null, null);
        }

        if (Cache.TryGetValue(pid, out var cached))
        {
            return cached with { Handle = hwnd };
        }

        var identity = new AppIdentity(hwnd, ProcessNameOf(pid), FriendlyNameOf(pid));

        // Bound the cache; process ids get recycled and this is only a display nicety.
        if (Cache.Count > 64)
        {
            Cache.Clear();
        }
        Cache[pid] = identity;

        return identity;
    }

    private static string? ProcessNameOf(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                return null;
            }

            return Path.GetFileNameWithoutExtension(buffer.ToString(0, size));
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Prefers the executable's FileDescription ("Google Chrome") over its module
    /// name ("chrome"), matching how Windows itself labels apps.
    /// </summary>
    private static string? FriendlyNameOf(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description.Trim();
            }

            return process.ProcessName;
        }
        catch
        {
            // Access is denied for elevated or protected processes; the module name will do.
            return ProcessNameOf(pid);
        }
    }

    /// <summary>
    /// Brings <paramref name="hwnd"/> back to the foreground.
    /// </summary>
    /// <remarks>
    /// SetForegroundWindow is rate-limited by Windows: a process that does not own
    /// the foreground is normally refused. Briefly attaching our input queue to the
    /// target's thread makes the call legitimate, which is the documented way to
    /// hand focus back to a window we took it from.
    /// </remarks>
    public static bool Restore(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (targetThread == 0)
        {
            return NativeMethods.SetForegroundWindow(hwnd);
        }

        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = targetThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, targetThread, true);

        try
        {
            return NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }
}
