namespace Stash.Models;

/// <summary>
/// A named sequence of keystrokes bound to a global hotkey.
/// </summary>
/// <remarks>
/// Macros type rather than paste. That matters for fields that reject pasted
/// text, for remote sessions where the clipboard is not shared, and for anything
/// that needs real key presses such as Tab between form fields. It also leaves
/// the clipboard untouched.
/// </remarks>
public sealed class Macro
{
    /// <summary>
    /// Stable identity, so editing and deleting cannot act on the wrong entry.
    /// </summary>
    /// <remarks>
    /// Position in the file would be fragile: the user can reorder or hand-edit
    /// macros.json at any time, and name and hotkey are both things an edit is
    /// likely to be changing. Entries written before this existed are assigned an
    /// id the first time the file is loaded.
    /// </remarks>
    public string Id { get; set; } = "";

    /// <summary>Shown in Settings and in the confirmation toast.</summary>
    public string Name { get; set; } = "";

    /// <summary>Global chord that runs it, e.g. "Ctrl+Alt+H".</summary>
    public string Hotkey { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public List<MacroStep> Steps { get; set; } = new();

    /// <summary>Total characters this macro would type, for the size guard.</summary>
    public int TextLength => Steps.Sum(s => s.Text?.Length ?? 0);

    /// <summary>A copy, so an edit in progress cannot mutate the loaded macro.</summary>
    public Macro Clone() => new()
    {
        Id = Id,
        Name = Name,
        Hotkey = Hotkey,
        Enabled = Enabled,
        Steps = Steps.Select(s => new MacroStep { Text = s.Text, Key = s.Key, DelayMs = s.DelayMs }).ToList(),
    };
}

/// <summary>
/// One action in a macro. Exactly one of the three should be set; if more than
/// one is, they run in the order text, key, delay.
/// </summary>
public sealed class MacroStep
{
    /// <summary>Literal text to type.</summary>
    public string? Text { get; set; }

    /// <summary>A key or chord to press, e.g. "Tab", "Enter", "Ctrl+A".</summary>
    public string? Key { get; set; }

    /// <summary>Pause before continuing, in milliseconds.</summary>
    public int? DelayMs { get; set; }

    public bool IsEmpty => string.IsNullOrEmpty(Text) && string.IsNullOrWhiteSpace(Key) && DelayMs is null;
}
