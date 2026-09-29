using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Stash.Interop;

namespace Stash.Views;

/// <summary>
/// A box you set a hotkey in by pressing it, rather than typing its name.
/// </summary>
/// <remarks>
/// <para>
/// Typing "Ctrl+Alt+Shift+2" is error-prone and gives no feedback about whether
/// the chord is usable. This captures the real key press, shows the modifiers as
/// they are held, and only accepts a chord that <see cref="HotkeyChord"/> can
/// register.
/// </para>
/// <para>
/// Two rules beyond the parser's: at least one of Ctrl, Alt or Win is required,
/// because a global Shift+letter would stop that capital letter being typable
/// anywhere in Windows; and in <see cref="PrefixMode"/> only the modifiers are
/// kept, for the quick-slot prefix that gets combined with the digits 1 to 9.
/// </para>
/// </remarks>
public sealed class HotkeyCaptureBox : TextBox
{
    public static readonly DependencyProperty ChordProperty = DependencyProperty.Register(
        nameof(Chord),
        typeof(string),
        typeof(HotkeyCaptureBox),
        new FrameworkPropertyMetadata(
            "",
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) =>
            {
                var box = (HotkeyCaptureBox)d;
                box.ShowChord();
                box.ChordChanged?.Invoke(box, EventArgs.Empty);
            }));

    /// <summary>Raised whenever <see cref="Chord"/> changes, from a key press or from code.</summary>
    public event EventHandler? ChordChanged;

    public static readonly DependencyProperty PrefixModeProperty = DependencyProperty.Register(
        nameof(PrefixMode),
        typeof(bool),
        typeof(HotkeyCaptureBox),
        new PropertyMetadata(false));

    /// <summary>The captured chord, e.g. "Ctrl+Alt+H"; or modifiers only in prefix mode.</summary>
    public string Chord
    {
        get => (string)GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    /// <summary>Keep only the modifiers, for a prefix such as the quick-slot one.</summary>
    public bool PrefixMode
    {
        get => (bool)GetValue(PrefixModeProperty);
        set => SetValue(PrefixModeProperty, value);
    }

    public HotkeyCaptureBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        ToolTip = "Click, then press the shortcut. Backspace clears it, Esc keeps the old one.";

        GotKeyboardFocus += (_, _) => Text = PrefixMode ? "Hold modifiers, press any key…" : "Press a shortcut…";
        LostKeyboardFocus += (_, _) => ShowChord();
        Loaded += (_, _) => ShowChord();
    }

    private void ShowChord()
    {
        if (!IsKeyboardFocused)
        {
            Text = string.IsNullOrWhiteSpace(Chord) ? "(none)" : Chord;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Alt combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);

        // Let Tab move between boxes as usual.
        if (key == Key.Tab && mods == ModifierKeys.None && !win)
        {
            return;
        }

        e.Handled = true;

        if (IsModifier(key))
        {
            var held = Modifiers(mods, win);
            Text = held.Length == 0 ? "Press a shortcut…" : held + "+…";
            return;
        }

        if (mods == ModifierKeys.None && !win)
        {
            switch (key)
            {
                case Key.Escape:
                    // Keep what was there.
                    Keyboard.ClearFocus();
                    ShowChord();
                    return;

                case Key.Back:
                case Key.Delete:
                    Chord = "";
                    Text = "(none)";
                    return;

                default:
                    Text = "Needs Ctrl, Alt or Win as well";
                    return;
            }
        }

        var hasStrongModifier = (mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 || win;
        if (!hasStrongModifier)
        {
            // Shift alone: a global Shift+A would make capital A untypable.
            Text = "Needs Ctrl, Alt or Win as well";
            return;
        }

        var prefix = Modifiers(mods, win);

        if (PrefixMode)
        {
            if (HotkeyChord.TryParse(prefix + "+1", out _))
            {
                Chord = prefix;
                Text = prefix;
            }

            return;
        }

        if (KeyName(key) is not { } name)
        {
            Text = "That key cannot be used";
            return;
        }

        if (HotkeyChord.TryParse($"{prefix}+{name}", out var chord))
        {
            Chord = chord.Display;
            Text = chord.Display;
        }
        else
        {
            Text = "That combination cannot be used";
        }
    }

    private static bool IsModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    /// <summary>Modifiers in the order Windows writes them.</summary>
    private static string Modifiers(ModifierKeys mods, bool win)
    {
        var parts = new List<string>(4);
        if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if (win) parts.Add("Win");
        if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        return string.Join("+", parts);
    }

    /// <summary>
    /// The name KeySpec understands for a key, or null if it is not one Stash
    /// will bind. Punctuation keys are left out: their virtual keys vary by
    /// keyboard layout, so a chord on one would mean different keys to different
    /// people.
    /// </summary>
    private static string? KeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return key.ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((int)(key - Key.D0)).ToString();
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return key.ToString();
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return key.ToString();
        }

        return key switch
        {
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Space => "Space",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            _ => null,
        };
    }
}
