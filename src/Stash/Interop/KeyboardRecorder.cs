using System.Text;
using Stash.Models;
using Stash.Services;

namespace Stash.Interop;

/// <summary>
/// Captures real key presses and turns them into macro steps.
/// </summary>
/// <remarks>
/// <para>
/// This installs a low-level keyboard hook, which is the same mechanism a
/// keylogger uses, so its lifetime is deliberately tight:
/// </para>
/// <list type="bullet">
///   <item>The hook exists only between <see cref="Start"/> and
///   <see cref="Stop"/>, both driven by an explicit user action. Nothing is
///   hooked while Stash merely sits in the tray.</item>
///   <item>Injected events are ignored, so a running macro cannot be recorded by
///   another and Stash never captures its own output.</item>
///   <item>Captured keys exist only as steps in memory. Nothing is written
///   anywhere unless the user saves the macro.</item>
/// </list>
/// <para>
/// The recorder window states plainly that everything typed is captured, because
/// during a recording that is exactly what happens.
/// </para>
/// </remarks>
public sealed class KeyboardRecorder : IDisposable
{
    /// <summary>
    /// A gap longer than this becomes a recorded pause. Shorter gaps are just
    /// human typing rhythm and would bloat every macro with noise.
    /// </summary>
    private const int PauseThresholdMs = 400;

    private const int MaxSteps = 200;

    /// <summary>Keys that are modifiers in their own right and never a step.</summary>
    private static readonly HashSet<uint> ModifierVks = new()
    {
        0x10, 0x11, 0x12,       // Shift, Control, Alt
        0xA0, 0xA1,             // L/R Shift
        0xA2, 0xA3,             // L/R Control
        0xA4, 0xA5,             // L/R Alt
        0x5B, 0x5C,             // L/R Win
        0x14,                   // Caps Lock
    };

    private readonly List<MacroStep> _steps = new();
    private readonly StringBuilder _pendingText = new();

    private NativeMethods.LowLevelKeyboardProc? _callback;
    private IntPtr _hook = IntPtr.Zero;
    private uint _lastEventTime;

    /// <summary>Raised whenever the captured step list changes.</summary>
    public event Action? Changed;

    /// <summary>Raised when the stop chord is pressed during a recording.</summary>
    public event Action? StopRequested;

    public bool IsRecording => _hook != IntPtr.Zero;

    /// <summary>Whether long gaps become explicit pause steps.</summary>
    public bool RecordPauses { get; set; } = true;

    /// <summary>
    /// The chord that ends a recording. It is swallowed rather than recorded, and
    /// exists because the recorder window cannot hold focus while the user types
    /// into another application.
    /// </summary>
    public static string StopChordDisplay => "Ctrl+Alt+Shift+R";

    /// <summary>Steps captured so far.</summary>
    public IReadOnlyList<MacroStep> Steps
    {
        get
        {
            // Surface the in-progress text run without committing it, so the UI
            // shows what is being typed as it happens.
            if (_pendingText.Length == 0)
            {
                return _steps;
            }

            var preview = new List<MacroStep>(_steps)
            {
                new() { Text = _pendingText.ToString() },
            };
            return preview;
        }
    }

    public void Start()
    {
        if (IsRecording)
        {
            return;
        }

        _steps.Clear();
        _pendingText.Clear();
        _lastEventTime = 0;

        // Keep the delegate alive for the hook's lifetime; letting it be
        // collected would crash the moment a key is pressed.
        _callback = HookCallback;

        // A low-level hook needs no module handle and is global for the session.
        _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _callback, IntPtr.Zero, 0);

        if (_hook == IntPtr.Zero)
        {
            AppPaths.Log("The keyboard hook could not be installed; macro recording is unavailable.");
            _callback = null;
            return;
        }

        AppPaths.Log("Macro recording started.");
    }

    public void Stop()
    {
        if (!IsRecording)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _callback = null;

        FlushText();
        AppPaths.Log($"Macro recording stopped with {_steps.Count} step(s).");
        Changed?.Invoke();
    }

    /// <summary>Discards everything captured so far.</summary>
    public void Clear()
    {
        _steps.Clear();
        _pendingText.Clear();
        Changed?.Invoke();
    }

    /// <summary>Removes the last captured step, for undoing a stray key.</summary>
    public void RemoveLast()
    {
        if (_pendingText.Length > 0)
        {
            _pendingText.Length--;
        }
        else if (_steps.Count > 0)
        {
            _steps.RemoveAt(_steps.Count - 1);
        }

        Changed?.Invoke();
    }

    /// <summary>The captured steps, with any trailing text run committed.</summary>
    public List<MacroStep> Build()
    {
        FlushText();
        return new List<MacroStep>(_steps);
    }

    // ---- Hook ---------------------------------------------------------------

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HC_ACTION)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;

        if (!isDown)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        // Never record synthesized input: otherwise running one macro while
        // recording another would capture its output as if it were typed.
        if ((data.flags & NativeMethods.LLKHF_INJECTED) != 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        try
        {
            if (HandleKey(data))
            {
                // Swallow the stop chord so it does not reach the app underneath.
                return new IntPtr(1);
            }
        }
        catch (Exception ex)
        {
            // A throwing hook would be removed by Windows and take the recording
            // with it, so never let one escape.
            AppPaths.Log("Macro recorder hook faulted.", ex);
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Returns true if the key should be swallowed.</summary>
    private bool HandleKey(NativeMethods.KBDLLHOOKSTRUCT data)
    {
        var vk = data.vkCode;

        var ctrl = IsDown(0x11);
        var alt = IsDown(0x12);
        var shift = IsDown(0x10);
        var win = IsDown(0x5B) || IsDown(0x5C);

        // Stop chord: Ctrl+Alt+Shift+R.
        if (ctrl && alt && shift && vk == 0x52)
        {
            StopRequested?.Invoke();
            return true;
        }

        if (ModifierVks.Contains(vk))
        {
            return false;
        }

        RecordPauseIfNeeded(data.time);

        // Ctrl or Alt held means this is a command, not text. AltGr arrives as
        // Ctrl+Alt together and does produce characters, so it is excluded.
        var isAltGr = ctrl && alt;
        if ((ctrl || alt || win) && !isAltGr)
        {
            FlushText();
            AddStep(new MacroStep { Key = DescribeChord(vk, ctrl, alt, shift, win) });
            return false;
        }

        // A named key that has no character, or whose character we would rather
        // replay as a real key press.
        if (NamedKey(vk) is { } named)
        {
            FlushText();
            AddStep(new MacroStep { Key = shift ? $"Shift+{named}" : named });
            return false;
        }

        var text = Translate(vk, data.scanCode);
        if (!string.IsNullOrEmpty(text))
        {
            _pendingText.Append(text);
            Changed?.Invoke();
        }

        return false;
    }

    private void RecordPauseIfNeeded(uint time)
    {
        if (_lastEventTime != 0 && RecordPauses)
        {
            var gap = (int)(time - _lastEventTime);
            if (gap >= PauseThresholdMs && (_steps.Count > 0 || _pendingText.Length > 0))
            {
                FlushText();

                // Round to a tenth of a second: replaying a human's exact 734ms
                // hesitation is false precision.
                AddStep(new MacroStep { DelayMs = (int)Math.Round(gap / 100.0) * 100 });
            }
        }

        _lastEventTime = time;
    }

    private void AddStep(MacroStep step)
    {
        if (_steps.Count >= MaxSteps)
        {
            return;
        }

        _steps.Add(step);
        Changed?.Invoke();
    }

    private void FlushText()
    {
        if (_pendingText.Length == 0)
        {
            return;
        }

        var text = _pendingText.ToString();
        _pendingText.Clear();

        if (_steps.Count < MaxSteps)
        {
            _steps.Add(new MacroStep { Text = text });
        }
    }

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static string DescribeChord(uint vk, bool ctrl, bool alt, bool shift, bool win)
    {
        var parts = new List<string>(4);
        if (ctrl) parts.Add("Ctrl");
        if (win) parts.Add("Win");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(NamedKey(vk) ?? KeyName(vk));
        return string.Join("+", parts);
    }

    private static string KeyName(uint vk)
    {
        if (vk is >= 0x41 and <= 0x5A || vk is >= 0x30 and <= 0x39)
        {
            return ((char)vk).ToString();
        }

        var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)vk);
        return key == System.Windows.Input.Key.None ? $"0x{vk:X2}" : key.ToString();
    }

    /// <summary>
    /// Keys that should replay as key presses rather than characters.
    /// </summary>
    private static string? NamedKey(uint vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x20 => null,          // space is text
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => null,
    };

    /// <summary>
    /// Asks Windows what character this key produces, honouring the active
    /// layout and the current shift state. Doing it this way means a recording
    /// made on any layout replays as the characters the user actually saw.
    /// </summary>
    private static string Translate(uint vk, uint scanCode)
    {
        var state = new byte[256];
        if (!NativeMethods.GetKeyboardState(state))
        {
            return "";
        }

        // GetKeyboardState lags behind a low-level hook, so set the live state
        // for the keys that change what a keypress produces.
        state[0x10] = (byte)(IsDown(0x10) ? 0x80 : 0);
        state[0x11] = (byte)(IsDown(0x11) ? 0x80 : 0);
        state[0x12] = (byte)(IsDown(0x12) ? 0x80 : 0);

        var buffer = new StringBuilder(8);
        var layout = NativeMethods.GetKeyboardLayout(0);

        var count = NativeMethods.ToUnicodeEx(
            vk, scanCode, state, buffer, buffer.Capacity,
            NativeMethods.TOUNICODE_NOCHANGEKEYSTATE, layout);

        if (count <= 0)
        {
            // Negative means a dead key; nothing to record yet.
            return "";
        }

        var text = buffer.ToString(0, Math.Min(count, buffer.Length));

        // Drop control characters; the named-key path handles those.
        return text.Any(char.IsControl) ? "" : text;
    }

    public void Dispose() => Stop();
}
