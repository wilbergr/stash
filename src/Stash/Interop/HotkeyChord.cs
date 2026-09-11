using System.Windows.Input;

namespace Stash.Interop;

/// <summary>A parsed hotkey chord, e.g. "Win+Shift+V" or "Ctrl+Alt+3".</summary>
public readonly record struct HotkeyChord(uint Modifiers, uint VirtualKey, string Display)
{
    /// <summary>
    /// Parses chords like "Win+Shift+V", "Ctrl+Alt+3", "Ctrl+Shift+F12".
    /// Returns false rather than throwing, so a hand-edited settings file degrades
    /// to the default instead of taking startup down with it.
    /// </summary>
    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;
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

        // Refuse modifier-less chords: they would swallow a bare keystroke system-wide.
        if (mods == 0 || keyToken is null)
        {
            return false;
        }

        if (!TryResolveKey(keyToken, out var vk))
        {
            return false;
        }

        chord = new HotkeyChord(mods, vk, Format(mods, keyToken));
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

        // Named keys (F1, Insert, Space, ...) go through WPF's own table.
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
