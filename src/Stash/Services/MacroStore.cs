using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stash.Interop;
using Stash.Models;

namespace Stash.Services;

/// <summary>The file's top-level shape, so the JSON has a named root.</summary>
public sealed class MacroFile
{
    public List<Macro> Macros { get; set; } = new();
}

/// <summary>
/// One entry as it appears in the file, whether or not it is usable.
/// </summary>
/// <param name="Macro">The definition.</param>
/// <param name="Active">True when it is enabled, valid, and has a hotkey registered.</param>
/// <param name="Problem">Why it is not active, or null if it is.</param>
public sealed record MacroEntry(Macro Macro, bool Active, string? Problem);

/// <summary>
/// Loads, validates and edits macros.json.
/// </summary>
/// <remarks>
/// Two views on purpose. <see cref="Macros"/> is what gets hotkeys: enabled and
/// valid. <see cref="All"/> is everything in the file including the disabled and
/// the broken, because Settings has to be able to show you a macro in order to
/// let you re-enable, fix or delete it. An earlier version only exposed the
/// active ones, which made a disabled macro invisible and therefore unfixable
/// from the UI.
/// </remarks>
public sealed class MacroStore
{
    /// <summary>Guards against a hand-edited file turning into a runaway.</summary>
    private const int MaxMacros = 64;
    private const int MaxSteps = 200;
    private const int MaxTextLength = 4000;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Keeps "Ctrl+Alt+H" readable rather than escaping the plus signs.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<Macro> _macros = new();
    private readonly List<MacroEntry> _all = new();
    private readonly List<string> _problems = new();

    /// <summary>Macros that parsed cleanly and are enabled; these get hotkeys.</summary>
    public IReadOnlyList<Macro> Macros => _macros;

    /// <summary>Every entry in the file, for management in Settings.</summary>
    public IReadOnlyList<MacroEntry> All => _all;

    /// <summary>Human-readable notes about entries that were rejected.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// Reads the file, creating it with worked examples the first time.
    /// </summary>
    public void Load()
    {
        AppPaths.EnsureCreated();

        _macros.Clear();
        _all.Clear();
        _problems.Clear();

        if (!File.Exists(AppPaths.MacrosFile))
        {
            WriteStarterFile();
        }

        MacroFile? file;

        try
        {
            file = JsonSerializer.Deserialize<MacroFile>(File.ReadAllText(AppPaths.MacrosFile), Options);
        }
        catch (JsonException ex)
        {
            _problems.Add($"macros.json could not be read: {ex.Message}");
            AppPaths.Log("macros.json is not valid JSON.", ex);
            return;
        }
        catch (Exception ex)
        {
            _problems.Add($"macros.json could not be opened: {ex.Message}");
            AppPaths.Log("macros.json could not be opened.", ex);
            return;
        }

        if (file?.Macros is null)
        {
            return;
        }

        // One-time migration: give anything written before ids had them one, so
        // editing and deleting have something stable to match on.
        var assigned = false;
        foreach (var macro in file.Macros.Where(m => string.IsNullOrWhiteSpace(m.Id)))
        {
            macro.Id = Guid.NewGuid().ToString("n");
            assigned = true;
        }

        if (assigned)
        {
            TryWrite(file, out _);
        }

        var seenChords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var macro in file.Macros)
        {
            if (_all.Count >= MaxMacros)
            {
                _problems.Add($"Only the first {MaxMacros} macros are loaded; the rest were ignored.");
                break;
            }

            macro.Name = string.IsNullOrWhiteSpace(macro.Name) ? "(unnamed)" : macro.Name.Trim();
            macro.Steps.RemoveAll(s => s is null || s.IsEmpty);

            var problem = Validate(macro, seenChords);

            if (problem is null && macro.Enabled)
            {
                seenChords[macro.Hotkey] = macro.Name;
                _macros.Add(macro);
                _all.Add(new MacroEntry(macro, true, null));
            }
            else
            {
                if (problem is not null)
                {
                    _problems.Add($"'{macro.Name}': {problem}");
                }

                _all.Add(new MacroEntry(macro, false, problem ?? "Disabled"));
            }
        }
    }

    /// <summary>Returns why a macro is unusable, or null if it is fine.</summary>
    private static string? Validate(Macro macro, Dictionary<string, string> seenChords)
    {
        if (!HotkeyChord.TryParse(macro.Hotkey, out var chord))
        {
            return $"'{macro.Hotkey}' is not a usable hotkey. It needs at least one modifier plus a key, e.g. Ctrl+Alt+H.";
        }

        // Normalise so the display and the duplicate check agree.
        macro.Hotkey = chord.Display;

        if (macro.Enabled && seenChords.TryGetValue(chord.Display, out var owner))
        {
            return $"{chord.Display} is already used by '{owner}'.";
        }

        if (macro.Steps.Count == 0)
        {
            return "it has no steps, so it would do nothing.";
        }

        if (macro.Steps.Count > MaxSteps)
        {
            return $"it has {macro.Steps.Count} steps; the limit is {MaxSteps}.";
        }

        if (macro.TextLength > MaxTextLength)
        {
            return $"it would type {macro.TextLength} characters; the limit is {MaxTextLength}.";
        }

        var badStep = macro.Steps.FirstOrDefault(s =>
            !string.IsNullOrWhiteSpace(s.Key) && !KeySpec.TryParse(s.Key, out _));

        if (badStep is not null)
        {
            return $"'{badStep.Key}' is not a key Stash recognises. Try Tab, Enter, Esc, F1, Home, Left, or a chord such as Ctrl+A.";
        }

        return null;
    }

    // ---- Editing ------------------------------------------------------------

    /// <summary>Adds a macro to the file.</summary>
    public bool Append(Macro macro, out string? error)
    {
        if (string.IsNullOrWhiteSpace(macro.Id))
        {
            macro.Id = Guid.NewGuid().ToString("n");
        }

        return Mutate(list =>
        {
            if (list.Count >= MaxMacros)
            {
                return $"macros.json already holds the maximum of {MaxMacros} macros.";
            }

            if (macro.Enabled && list.Any(m => m.Enabled && SameChord(m.Hotkey, macro.Hotkey)))
            {
                return $"{macro.Hotkey} is already used by another enabled macro.";
            }

            list.Add(macro);
            return null;
        }, out error);
    }

    /// <summary>Replaces an existing macro, matched on its id.</summary>
    public bool Update(Macro macro, out string? error)
    {
        return Mutate(list =>
        {
            var index = list.FindIndex(m => m.Id == macro.Id);
            if (index < 0)
            {
                return "That macro is no longer in the file; it may have been edited elsewhere.";
            }

            if (macro.Enabled && list.Any(m => m.Id != macro.Id && m.Enabled && SameChord(m.Hotkey, macro.Hotkey)))
            {
                return $"{macro.Hotkey} is already used by another enabled macro.";
            }

            list[index] = macro;
            return null;
        }, out error);
    }

    /// <summary>Removes a macro.</summary>
    public bool Delete(string id, out string? error)
    {
        return Mutate(list =>
        {
            var removed = list.RemoveAll(m => m.Id == id);
            return removed == 0 ? "That macro is no longer in the file." : null;
        }, out error);
    }

    /// <summary>Enables or disables a macro without otherwise touching it.</summary>
    public bool SetEnabled(string id, bool enabled, out string? error)
    {
        return Mutate(list =>
        {
            var macro = list.FirstOrDefault(m => m.Id == id);
            if (macro is null)
            {
                return "That macro is no longer in the file.";
            }

            if (enabled && list.Any(m => m.Id != id && m.Enabled && SameChord(m.Hotkey, macro.Hotkey)))
            {
                return $"{macro.Hotkey} is already used by another enabled macro.";
            }

            macro.Enabled = enabled;
            return null;
        }, out error);
    }

    /// <summary>True if any enabled macro other than <paramref name="exceptId"/> claims this chord.</summary>
    public bool IsChordTaken(string chord, string? exceptId = null)
        => _all.Any(e => e.Macro.Enabled
                         && e.Macro.Id != exceptId
                         && SameChord(e.Macro.Hotkey, chord));

    private static bool SameChord(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies a change to the file's macro list and saves.
    /// </summary>
    /// <remarks>
    /// Always re-reads from disk first rather than writing back the in-memory
    /// list, so a hand edit made since the last load is not silently discarded,
    /// and so the disabled entries that <see cref="Macros"/> omits survive.
    /// </remarks>
    private bool Mutate(Func<List<Macro>, string?> change, out string? error)
    {
        error = null;

        try
        {
            AppPaths.EnsureCreated();

            var file = File.Exists(AppPaths.MacrosFile)
                ? JsonSerializer.Deserialize<MacroFile>(File.ReadAllText(AppPaths.MacrosFile), Options) ?? new MacroFile()
                : new MacroFile();

            var problem = change(file.Macros);
            if (problem is not null)
            {
                error = problem;
                return false;
            }

            if (!TryWrite(file, out error))
            {
                return false;
            }

            Load();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"macros.json is not valid JSON, so it was left alone: {ex.Message}";
            AppPaths.Log("Could not edit macros.json; it is malformed.", ex);
            return false;
        }
        catch (Exception ex)
        {
            error = $"macros.json could not be saved: {ex.Message}";
            AppPaths.Log("Could not edit macros.json.", ex);
            return false;
        }
    }

    private static bool TryWrite(MacroFile file, out string? error)
    {
        error = null;

        try
        {
            // Write beside and move, so a failure cannot truncate the file.
            var temp = AppPaths.MacrosFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
            File.Move(temp, AppPaths.MacrosFile, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            error = $"macros.json could not be written: {ex.Message}";
            AppPaths.Log("macros.json could not be written.", ex);
            return false;
        }
    }

    /// <summary>
    /// Writes a starter file containing worked examples, so the format is
    /// self-documenting on first run.
    /// </summary>
    private void WriteStarterFile()
    {
        var starter = new MacroFile
        {
            Macros =
            {
                new Macro
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Name = "Example",
                    Hotkey = "Ctrl+Alt+H",
                    Enabled = true,
                    Steps = { new MacroStep { Text = "EXAMPLE" } },
                },
                new Macro
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Name = "Sign-off",
                    Hotkey = "Ctrl+Alt+G",
                    Enabled = false,
                    Steps =
                    {
                        new MacroStep { Text = "Kind regards," },
                        new MacroStep { Key = "Enter" },
                        new MacroStep { Text = "Greg" },
                    },
                },
                new Macro
                {
                    Id = Guid.NewGuid().ToString("n"),
                    Name = "Fill two fields",
                    Hotkey = "Ctrl+Alt+J",
                    Enabled = false,
                    Steps =
                    {
                        new MacroStep { Text = "first field" },
                        new MacroStep { Key = "Tab" },
                        new MacroStep { DelayMs = 100 },
                        new MacroStep { Text = "second field" },
                    },
                },
            },
        };

        if (TryWrite(starter, out _))
        {
            AppPaths.Log("Wrote a starter macros.json.");
        }
    }
}
