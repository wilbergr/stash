namespace Stash.Interop;

/// <summary>A parsed global hotkey chord, e.g. "Win+Shift+V" or "Ctrl+Alt+3".</summary>
public readonly record struct HotkeyChord(uint Modifiers, uint VirtualKey, string Display)
{
    /// <summary>
    /// Parses chords like "Win+Shift+V", "Ctrl+Alt+3", "Ctrl+Shift+F12".
    /// </summary>
    /// <remarks>
    /// Delegates the parsing to <see cref="KeySpec"/> and adds the one rule a
    /// system-wide hotkey needs: it must carry at least one modifier. A bare key
    /// would swallow that keystroke everywhere in Windows.
    /// </remarks>
    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;

        if (!KeySpec.TryParse(text, out var spec) || !spec.HasModifiers)
        {
            return false;
        }

        chord = new HotkeyChord(spec.Modifiers, spec.VirtualKey, spec.Display);
        return true;
    }
}
