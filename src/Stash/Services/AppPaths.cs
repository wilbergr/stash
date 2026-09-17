namespace Stash.Services;

/// <summary>
/// Everything Stash writes lives under one folder, so uninstalling is
/// "delete this directory" and nothing is scattered across the profile.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Stash");

    public static string Images { get; } = Path.Combine(Root, "images");

    public static string HistoryFile { get; } = Path.Combine(Root, "history.json");

    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

    public static string MacrosFile { get; } = Path.Combine(Root, "macros.json");

    public static string LogFile { get; } = Path.Combine(Root, "stash.log");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Images);
    }

    public static string ImagePath(string fileName) => Path.Combine(Images, fileName);

    /// <summary>
    /// Appends a line to the log. Diagnostics must never be the reason the app
    /// falls over, so all failures here are swallowed.
    /// </summary>
    public static void Log(string message, Exception? error = null)
    {
        try
        {
            var line = error is null
                ? $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}"
                : $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}{error}{Environment.NewLine}";

            File.AppendAllText(LogFile, line);
        }
        catch
        {
            // Ignored deliberately.
        }
    }
}
