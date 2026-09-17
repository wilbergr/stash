using System.Windows.Input;

namespace Stash.Interop;

/// <summary>
/// A key plus optional modifiers, e.g. "Tab", "Enter", "Ctrl+A", "Win+Shift+V".
/// </summary>
/// <remarks>
/// Unlike <see cref="HotkeyChord"/> this permits a bare key with no modifiers,
/// because a macro step legitimately wants to press Tab on its own. A global
/// hotkey must not, which is the single rule <see cref="HotkeyChord"/> adds on
/// top.
/// </remarks>
public readonly record struct KeySpec(uint Modifiers, uint VirtualKey, string Display)
{
    public bool HasModifiers => Modifiers != 0;

    /// <summary>
    /// Parses a key specification. Returns false rather than throwing, so a
    /// hand-edited file degrades to a reported warning instead of taking the app
    /// down.
    /// </summary>
    public static bool TryParse(string? text, out KeySpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        uint mods = 0;
        string? keyToken = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "win":
                case "meta":
                case "super":
                    mods |= NativeMethods.MOD_WIN;
                    break;
                case "ctrl":
                case "control":
                    mods |= NativeMethods.MOD_CONTROL;
                    break;
                case "shift":
                    mods |= NativeMethods.MOD_SHIFT;
                    break;
                case "alt":
                    mods |= NativeMethods.MOD_ALT;
                    break;
                default:
                    // The last non-modifier token wins as the trigger key.
                    keyToken = raw;
                    break;
            }
        }

        if (keyToken is null || !TryResolveKey(keyToken, out var vk))
        {
            return false;
        }

        spec = new KeySpec(mods, vk, Format(mods, keyToken));
        return true;
    }

    private static bool TryResolveKey(string token, out uint vk)
    {
        vk = 0;

        // Single letters and digits map straight to their ASCII virtual-key codes.
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c))
            {
                vk = c;
                return true;
            }
        }

        // Friendlier spellings for the keys macros reach for most.
        var alias = token.ToLowerInvariant() switch
        {
            "enter" or "return" => Key.Return,
            "esc" or "escape" => Key.Escape,
            "del" or "delete" => Key.Delete,
            "ins" or "insert" => Key.Insert,
            "pgup" or "pageup" => Key.PageUp,
            "pgdn" or "pagedown" => Key.PageDown,
            "space" or "spacebar" => Key.Space,
            "backspace" or "bksp" => Key.Back,
            _ => Key.None,
        };

        if (alias != Key.None)
        {
            vk = (uint)KeyInterop.VirtualKeyFromKey(alias);
            return vk != 0;
        }

        // Anything else WPF names: Tab, F1..F24, Home, End, Left, Up, ...
        if (Enum.TryParse<Key>(token, ignoreCase: true, out var key) && key != Key.None)
        {
            var mapped = KeyInterop.VirtualKeyFromKey(key);
            if (mapped != 0)
            {
                vk = (uint)mapped;
                return true;
            }
        }

        return false;
    }

    /// <summary>Normalises display order so chords read the way Windows writes them.</summary>
    private static string Format(uint mods, string keyToken)
    {
        var parts = new List<string>(5);
        if ((mods & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        if ((mods & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        parts.Add(keyToken.Length == 1 ? keyToken.ToUpperInvariant() : keyToken);
        return string.Join("+", parts);
    }
}
