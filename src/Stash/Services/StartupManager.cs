using Microsoft.Win32;

namespace Stash.Services;

/// <summary>
/// Registers Stash to launch at sign-in via the per-user Run key. HKCU only, so
/// this never needs elevation.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Stash";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not read the startup registration.", ex);
            return false;
        }
    }

    /// <summary>Returns true when the requested state was achieved.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    return false;
                }

                // Quoted: the path runs through %LOCALAPPDATA% and may contain spaces.
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Could not {(enabled ? "enable" : "disable")} launch at sign-in.", ex);
            return false;
        }
    }
}
