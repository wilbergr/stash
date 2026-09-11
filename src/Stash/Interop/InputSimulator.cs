namespace Stash.Interop;

/// <summary>
/// Synthesizes the Ctrl+V that actually delivers a clip into the target app.
/// </summary>
public static class InputSimulator
{
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
    /// chord like Win+Shift+V, and those keys are often still physically down when
    /// they pick an item. If we injected Ctrl+V while the OS still believed Win and
    /// Shift were held, the target app would receive Win+Shift+Ctrl+V and do
    /// something unrelated. So we first inject key-up for every modifier that reads
    /// as down, then send a clean Ctrl+V.
    /// </remarks>
    public static void SendPaste()
    {
        var inputs = new List<NativeMethods.INPUT>(12);

        foreach (var key in ModifierKeys)
        {
            if (IsDown(key))
            {
                inputs.Add(KeyUp(key));
            }
        }

        inputs.Add(KeyDown(NativeMethods.VK_CONTROL));
        inputs.Add(KeyDown(NativeMethods.VK_V));
        inputs.Add(KeyUp(NativeMethods.VK_V));
        inputs.Add(KeyUp(NativeMethods.VK_CONTROL));

        var array = inputs.ToArray();
        NativeMethods.SendInput((uint)array.Length, array, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>());
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
}
