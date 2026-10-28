namespace Stash.Models;

/// <summary>
/// A named sequence of keystrokes, and optionally programs to open, bound to a
/// global hotkey.
/// </summary>
/// <remarks>
/// Macros type rather than paste. That matters for fields that reject pasted
/// text, for remote sessions where the clipboard is not shared, and for anything
/// that needs real key presses such as Tab between form fields. It also leaves
/// the clipboard untouched. A launch step lets one chord open an application,
/// on its own or before typing into it.
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
        Steps = Steps.Select(s => s.Clone()).ToList(),
    };
}

/// <summary>
/// One action in a macro. Exactly one of the four should be set; if more than
/// one is, they run in the order launch, text, key, delay.
/// </summary>
public sealed class MacroStep
{
    /// <summary>
    /// A program, shortcut or document to open, e.g. "notepad.exe" or
    /// "%USERPROFILE%\Desktop\Notepad++.lnk".
    /// </summary>
    /// <remarks>
    /// Opened through the shell, so a .lnk keeps whatever target, arguments and
    /// start folder the shortcut carries, a document opens in its associated
    /// application, and a bare name resolves the way the Run dialog would.
    /// Environment variables are expanded.
    /// </remarks>
    public string? Launch { get; set; }

    /// <summary>Command-line arguments for <see cref="Launch"/>.</summary>
    public string? Arguments { get; set; }

    /// <summary>Literal text to type.</summary>
    public string? Text { get; set; }

    /// <summary>A key or chord to press, e.g. "Tab", "Enter", "Ctrl+A".</summary>
    public string? Key { get; set; }

    /// <summary>Pause before continuing, in milliseconds.</summary>
    public int? DelayMs { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Launch)
                           && string.IsNullOrEmpty(Text)
                           && string.IsNullOrWhiteSpace(Key)
                           && DelayMs is null;

    public MacroStep Clone() => new()
    {
        Launch = Launch,
        Arguments = Arguments,
        Text = Text,
        Key = Key,
        DelayMs = DelayMs,
    };

    /// <summary>
    /// <see cref="Launch"/> with environment variables expanded and any
    /// surrounding quotes removed, or null if it is not set.
    /// </summary>
    /// <remarks>
    /// A method rather than a property so it is not written into macros.json.
    /// </remarks>
    public string? ResolveLaunchPath() => string.IsNullOrWhiteSpace(Launch)
        ? null
        : Environment.ExpandEnvironmentVariables(Launch.Trim().Trim('"'));
}
