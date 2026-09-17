using System.Text.Encodings.Web;
using System.Text.Json;
using Stash.Models;

namespace Stash.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. A corrupt or hand-broken file is
/// replaced by defaults rather than blocking startup — a clipboard manager that
/// refuses to launch is worse than one that forgets a preference.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,

        // The default encoder rewrites '+' as a unicode escape, which leaves the
        // stored hotkey unreadable to anyone opening the file. Settings are meant
        // to be hand-editable and are never embedded in HTML, so the relaxed
        // encoder is the right trade here.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AppSettings Current { get; private set; } = new();

    /// <summary>
    /// True when no settings file existed at load, i.e. this is a fresh profile.
    /// Used to point a first-time user at the hotkey, which is otherwise entirely
    /// invisible in a tray-only app.
    /// </summary>
    public bool IsFirstRun { get; private set; }

    public event Action<AppSettings>? Changed;

    public AppSettings Load()
    {
        AppPaths.EnsureCreated();

        IsFirstRun = !File.Exists(AppPaths.SettingsFile);

        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    Current = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Settings could not be read; falling back to defaults.", ex);
            Current = new AppSettings();
        }

        return Current;
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureCreated();

            // Write to a sibling file then move, so a crash mid-write cannot
            // leave a half-serialized settings file behind.
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Settings could not be saved.", ex);
        }
    }

    /// <summary>Mutates settings, persists them, and notifies listeners.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Save();
        Changed?.Invoke(Current);
    }
}
