using System.Runtime.InteropServices;

namespace Stash.Interop;

/// <summary>
/// Synthesizes keyboard input: the Ctrl+V that delivers a clip, and the
/// keystrokes that a macro types.
/// </summary>
public static class InputSimulator
{
    /// <summary>
    /// SendInput takes an array in one call; this caps how large those arrays get
    /// so a long macro does not allocate one enormous block.
    /// </summary>
    private const int MaxBatch = 512;

    private static readonly ushort[] ModifierKeys =
    {
        NativeMethods.VK_CONTROL,
        NativeMethods.VK_SHIFT,
        NativeMethods.VK_MENU,
        NativeMethods.VK_LWIN,
        NativeMethods.VK_RWIN,
    };

    /// <summary>
    /// Sends Ctrl+V to whatever window currently has focus.
    /// </summary>
    /// <remarks>
    /// The subtle part is the first step. The user reached the stash by holding a
    /// chord like Ctrl+Alt+V, and those keys are often still physically down when
    /// they pick an item. If we injected Ctrl+V while the OS still believed Alt was
    /// held, the target app would receive Ctrl+Alt+V and do something unrelated. So
    /// we first inject key-up for every modifier that reads as down.
    /// </remarks>
    public static void SendPaste()
    {
        var inputs = new List<NativeMethods.INPUT>(12);
        AddHeldModifierReleases(inputs);

        inputs.Add(KeyDown(NativeMethods.VK_CONTROL));
        inputs.Add(KeyDown(NativeMethods.VK_V));
        inputs.Add(KeyUp(NativeMethods.VK_V));
        inputs.Add(KeyUp(NativeMethods.VK_CONTROL));

        Send(inputs);
    }

    /// <summary>
    /// Releases every modifier that currently reads as physically down.
    /// </summary>
    /// <remarks>
    /// Macros need this for the same reason paste does: the chord that triggered
    /// the macro is still held, so typing "EXAMPLE" while Ctrl and Alt are down
    /// would deliver Ctrl+Alt+E and friends rather than letters.
    /// </remarks>
    public static void ReleaseHeldModifiers()
    {
        var inputs = new List<NativeMethods.INPUT>(ModifierKeys.Length);
        AddHeldModifierReleases(inputs);

        if (inputs.Count > 0)
        {
            Send(inputs);
        }
    }

    /// <summary>
    /// Types literal text into the focused window.
    /// </summary>
    /// <remarks>
    /// Uses KEYEVENTF_UNICODE rather than virtual-key codes. A VK describes a key
    /// position, so VK_E only yields "E" on a layout where that key is E, and
    /// characters such as an em dash or an accented vowel have no VK at all.
    /// Injecting the code unit sidesteps both problems. Surrogate pairs work
    /// because each UTF-16 unit is sent in order, which is exactly what the API
    /// expects.
    /// </remarks>
    public static void SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var inputs = new List<NativeMethods.INPUT>(Math.Min(text.Length * 2, MaxBatch));

        foreach (var unit in text)
        {
            // A newline in a macro should behave like pressing Enter; injecting
            // U+000A as a character is ignored by most edit controls.
            if (unit == '\n')
            {
                inputs.Add(KeyDown(NativeMethods.VK_RETURN));
                inputs.Add(KeyUp(NativeMethods.VK_RETURN));
            }
            else if (unit == '\r')
            {
                continue;
            }
            else if (unit == '\t')
            {
                inputs.Add(KeyDown(NativeMethods.VK_TAB));
                inputs.Add(KeyUp(NativeMethods.VK_TAB));
            }
            else
            {
                inputs.Add(Unicode(unit, up: false));
                inputs.Add(Unicode(unit, up: true));
            }

            if (inputs.Count >= MaxBatch)
            {
                Send(inputs);
                inputs.Clear();
            }
        }

        Send(inputs);
    }

    /// <summary>
    /// Types one character, for the paced path where a target application drops
    /// input delivered in bulk.
    /// </summary>
    public static void SendChar(char unit) => SendText(unit.ToString());

    /// <summary>Presses a key with its modifiers, then releases everything.</summary>
    public static void SendKey(KeySpec spec)
    {
        var inputs = new List<NativeMethods.INPUT>(10);

        var down = new List<ushort>(4);
        if ((spec.Modifiers & NativeMethods.MOD_CONTROL) != 0) down.Add(NativeMethods.VK_CONTROL);
        if ((spec.Modifiers & NativeMethods.MOD_SHIFT) != 0) down.Add(NativeMethods.VK_SHIFT);
        if ((spec.Modifiers & NativeMethods.MOD_ALT) != 0) down.Add(NativeMethods.VK_MENU);
        if ((spec.Modifiers & NativeMethods.MOD_WIN) != 0) down.Add(NativeMethods.VK_LWIN);

        foreach (var mod in down)
        {
            inputs.Add(KeyDown(mod));
        }

        inputs.Add(KeyDown((ushort)spec.VirtualKey));
        inputs.Add(KeyUp((ushort)spec.VirtualKey));

        // Release in reverse, the way a human's hand would come off the keys.
        for (var i = down.Count - 1; i >= 0; i--)
        {
            inputs.Add(KeyUp(down[i]));
        }

        Send(inputs);
    }

    // ---- Plumbing -----------------------------------------------------------

    private static void AddHeldModifierReleases(List<NativeMethods.INPUT> inputs)
    {
        foreach (var key in ModifierKeys)
        {
            if (IsDown(key))
            {
                inputs.Add(KeyUp(key));
            }
        }
    }

    private static void Send(List<NativeMethods.INPUT> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        var array = inputs.ToArray();
        NativeMethods.SendInput((uint)array.Length, array, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static bool IsDown(ushort virtualKey)
        => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static NativeMethods.INPUT KeyDown(ushort vk) => Key(vk, up: false);

    private static NativeMethods.INPUT KeyUp(ushort vk) => Key(vk, up: true);

    private static NativeMethods.INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static NativeMethods.INPUT Unicode(char unit, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = unit,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (up ? NativeMethods.KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };
}
