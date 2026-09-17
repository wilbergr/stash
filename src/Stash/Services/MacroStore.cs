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
/// Loads and validates macros from macros.json.
/// </summary>
/// <remarks>
/// Definitions live in a hand-editable file rather than a dialog. A macro is a
/// small program — text, keys and pauses in order — and a text file expresses
/// that far better than a first attempt at an editor UI would. Settings links to
/// the file and reports whatever failed to parse.
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
    private readonly List<string> _problems = new();

    /// <summary>Macros that parsed cleanly and are enabled.</summary>
    public IReadOnlyList<Macro> Macros => _macros;

    /// <summary>Human-readable notes about entries that were rejected.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// Reads the file, creating it with a worked example the first time.
    /// </summary>
    public void Load()
    {
        AppPaths.EnsureCreated();

        _macros.Clear();
        _problems.Clear();

        if (!File.Exists(AppPaths.MacrosFile))
        {
            WriteStarterFile();
        }

        MacroFile? file = null;

        try
        {
            file = JsonSerializer.Deserialize<MacroFile>(File.ReadAllText(AppPaths.MacrosFile), Options);
        }
        catch (JsonException ex)
        {
            // Point at the line, since the user edits this by hand.
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

        var seenChords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var macro in file.Macros)
        {
            if (_macros.Count >= MaxMacros)
            {
                _problems.Add($"Only the first {MaxMacros} macros are loaded; the rest were ignored.");
                break;
            }

            var label = string.IsNullOrWhiteSpace(macro.Name) ? "(unnamed)" : macro.Name.Trim();
            macro.Name = label;

            if (!macro.Enabled)
            {
                continue;
            }

            if (!HotkeyChord.TryParse(macro.Hotkey, out var chord))
            {
                _problems.Add($"'{label}': '{macro.Hotkey}' is not a usable hotkey. It needs at least one modifier plus a key, e.g. Ctrl+Alt+H.");
                continue;
            }

            // Normalise so the display and the duplicate check agree.
            macro.Hotkey = chord.Display;

            if (seenChords.TryGetValue(chord.Display, out var owner))
            {
                _problems.Add($"'{label}' wants {chord.Display}, which '{owner}' already uses. Only the first one runs.");
                continue;
            }

            macro.Steps.RemoveAll(s => s is null || s.IsEmpty);

            if (macro.Steps.Count == 0)
            {
                _problems.Add($"'{label}' has no steps, so it would do nothing.");
                continue;
            }

            if (macro.Steps.Count > MaxSteps)
            {
                _problems.Add($"'{label}' has {macro.Steps.Count} steps; the limit is {MaxSteps}.");
                continue;
            }

            if (macro.TextLength > MaxTextLength)
            {
                _problems.Add($"'{label}' would type {macro.TextLength} characters; the limit is {MaxTextLength}.");
                continue;
            }

            var badStep = macro.Steps.FirstOrDefault(s =>
                !string.IsNullOrWhiteSpace(s.Key) && !KeySpec.TryParse(s.Key, out _));

            if (badStep is not null)
            {
                _problems.Add($"'{label}': '{badStep.Key}' is not a key Stash recognises. Try Tab, Enter, Esc, F1, Home, Left, or a chord such as Ctrl+A.");
                continue;
            }

            seenChords[chord.Display] = label;
            _macros.Add(macro);
        }
    }

    /// <summary>
    /// Appends a macro to the file and reloads.
    /// </summary>
    /// <remarks>
    /// Re-reads from disk first rather than writing back the in-memory list,
    /// because that list holds only the enabled, valid entries. Serialising it
    /// would quietly delete every disabled macro and anything the user was
    /// midway through hand-editing.
    /// </remarks>
    public bool Append(Macro macro, out string? error)
    {
        error = null;

        try
        {
            AppPaths.EnsureCreated();

            var file = File.Exists(AppPaths.MacrosFile)
                ? JsonSerializer.Deserialize<MacroFile>(File.ReadAllText(AppPaths.MacrosFile), Options) ?? new MacroFile()
                : new MacroFile();

            if (file.Macros.Count >= MaxMacros)
            {
                error = $"macros.json already holds the maximum of {MaxMacros} macros.";
                return false;
            }

            if (file.Macros.Any(m => string.Equals(m.Hotkey, macro.Hotkey, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"{macro.Hotkey} is already used by another macro in the file.";
                return false;
            }

            file.Macros.Add(macro);

            // Write beside and move, so a failure cannot truncate the file.
            var temp = AppPaths.MacrosFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
            File.Move(temp, AppPaths.MacrosFile, overwrite: true);

            AppPaths.Log($"Saved macro '{macro.Name}' on {macro.Hotkey}.");
            Load();
            return true;
        }
        catch (JsonException ex)
        {
            error = $"macros.json is not valid JSON, so the new macro was not added: {ex.Message}";
            AppPaths.Log("Could not append a macro; the file is malformed.", ex);
            return false;
        }
        catch (Exception ex)
        {
            error = $"The macro could not be saved: {ex.Message}";
            AppPaths.Log("Could not append a macro.", ex);
            return false;
        }
    }

    /// <summary>True if any macro in the file already claims this chord.</summary>
    public bool IsChordTaken(string chord)
        => _macros.Any(m => string.Equals(m.Hotkey, chord, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes a starter file containing the worked example from the feature
    /// request, so the format is self-documenting on first run.
    /// </summary>
    private void WriteStarterFile()
    {
        var starter = new MacroFile
        {
            Macros =
            {
                new Macro
                {
                    Name = "Example",
                    Hotkey = "Ctrl+Alt+H",
                    Enabled = true,
                    Steps = { new MacroStep { Text = "EXAMPLE" } },
                },
                new Macro
                {
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

        try
        {
            File.WriteAllText(AppPaths.MacrosFile, JsonSerializer.Serialize(starter, Options));
            AppPaths.Log("Wrote a starter macros.json.");
        }
        catch (Exception ex)
        {
            AppPaths.Log("Starter macros.json could not be written.", ex);
        }
    }
}
