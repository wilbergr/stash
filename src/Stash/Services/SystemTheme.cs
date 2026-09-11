using System.Windows.Media;
using Microsoft.Win32;

namespace Stash.Services;

/// <summary>
/// Reads the two things that let Stash look native: whether apps are in dark mode,
/// and the user's accent colour.
/// </summary>
public static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    /// <summary>Used when the registry is unreadable — matches the Windows 11 default accent.</summary>
    private static readonly Color FallbackAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    public static bool IsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue("AppsUseLightTheme") is int lightTheme)
            {
                return lightTheme == 0;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not read the system theme; assuming dark.", ex);
        }

        return true;
    }

    /// <summary>
    /// The accent colour, read from DWM.
    /// </summary>
    /// <remarks>
    /// AccentColor is stored as a DWORD in 0xAABBGGRR order — blue and red are
    /// swapped relative to the usual ARGB layout, which is the classic trap here.
    /// </remarks>
    public static Color AccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
            if (key?.GetValue("AccentColor") is int packed)
            {
                var bytes = BitConverter.GetBytes(packed);
                return Color.FromRgb(bytes[0], bytes[1], bytes[2]);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not read the accent colour; using the default.", ex);
        }

        return FallbackAccent;
    }

    /// <summary>Resolves the "System" theme preference into a concrete choice.</summary>
    public static bool ResolveDark(string preference) => preference?.ToLowerInvariant() switch
    {
        "light" => false,
        "dark" => true,
        _ => IsDarkMode(),
    };
}
