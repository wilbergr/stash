using System.Diagnostics;
using Stash.Interop;
using Stash.Models;

namespace Stash.Services;

/// <summary>
/// Executes a macro's steps as real keystrokes into whatever window has focus,
/// opening any programs it names along the way.
/// </summary>
public sealed class MacroRunner
{
    /// <summary>
    /// Pause after releasing the trigger chord, before typing anything.
    /// </summary>
    /// <remarks>
    /// The user is still physically holding the hotkey when this starts. The
    /// synthetic key-ups update the OS's view immediately, but the target
    /// application needs a moment to process them; typing in the same breath
    /// occasionally delivered the first character with a stale modifier attached.
    /// </remarks>
    private static readonly TimeSpan SettleAfterRelease = TimeSpan.FromMilliseconds(45);

    /// <summary>
    /// How long to wait for a launched program's window before giving up on the
    /// steps that would type into it.
    /// </summary>
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Pause after a launched program's window comes forward, before typing.
    /// </summary>
    /// <remarks>
    /// A window can take the foreground before its edit control has focus, and
    /// keystrokes sent in that gap are lost. A longer wait, for a slow program,
    /// is a pause step after the launch.
    /// </remarks>
    private static readonly TimeSpan SettleAfterLaunch = TimeSpan.FromMilliseconds(400);

    /// <summary>A single step may not pause longer than this.</summary>
    private const int MaxStepDelayMs = 5000;

    private readonly SettingsStore _settings;
    private bool _running;

    public MacroRunner(SettingsStore settings)
    {
        _settings = settings;
    }

    /// <summary>Raised with a short status line for the toast.</summary>
    public event Action<string>? Reported;

    /// <summary>
    /// Runs <paramref name="macro"/>. Returns false if it was declined, which
    /// happens when another macro is mid-flight or the foreground window moved.
    /// </summary>
    public async Task<bool> RunAsync(Macro macro)
    {
        // Re-entrancy would interleave two macros' keystrokes into nonsense.
        if (_running)
        {
            AppPaths.Log($"Macro '{macro.Name}' ignored: another macro is still running.");
            return false;
        }

        _running = true;

        try
        {
            var target = NativeMethods.GetForegroundWindow();

            // The chord that triggered this is still held down; typing over it
            // would deliver Ctrl+Alt+letter instead of letters.
            InputSimulator.ReleaseHeldModifiers();
            await Task.Delay(SettleAfterRelease);

            if (NativeMethods.GetForegroundWindow() != target)
            {
                AppPaths.Log($"Macro '{macro.Name}' aborted: focus moved before it could type.");
                Reported?.Invoke($"'{macro.Name}' stopped — the focused window changed");
                return false;
            }

            var typingDelay = Math.Clamp(_settings.Current.MacroTypingDelayMs, 0, 100);
            var steps = macro.Steps;

            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];

                // Abort rather than scatter keystrokes across two applications.
                if (NativeMethods.GetForegroundWindow() != target)
                {
                    AppPaths.Log($"Macro '{macro.Name}' stopped part-way: focus moved.");
                    Reported?.Invoke($"'{macro.Name}' stopped — the focused window changed");
                    return false;
                }

                if (step.ResolveLaunchPath() is { } path)
                {
                    if (!Launch(macro, step, path))
                    {
                        return false;
                    }

                    // Opening a program is meant to move focus, so the guard above
                    // would stop the macro at the next step. If anything is left to
                    // type, adopt the new window as the target once it appears.
                    if (TypesFrom(steps, i))
                    {
                        var opened = await WaitForNewForegroundAsync(target);
                        if (opened == IntPtr.Zero)
                        {
                            AppPaths.Log($"Macro '{macro.Name}' stopped: no window came forward after opening '{path}'.");
                            Reported?.Invoke($"'{macro.Name}' stopped — the program did not come to the front");
                            return false;
                        }

                        target = opened;
                        await Task.Delay(SettleAfterLaunch);
                    }
                }

                if (!string.IsNullOrEmpty(step.Text))
                {
                    await TypeAsync(step.Text, typingDelay);
                }

                if (!string.IsNullOrWhiteSpace(step.Key) && KeySpec.TryParse(step.Key, out var spec))
                {
                    InputSimulator.SendKey(spec);

                    if (typingDelay > 0)
                    {
                        await Task.Delay(typingDelay);
                    }
                }

                if (step.DelayMs is { } delay && delay > 0)
                {
                    await Task.Delay(Math.Min(delay, MaxStepDelayMs));
                }
            }

            var typed = steps.Any(s => !string.IsNullOrEmpty(s.Text) || !string.IsNullOrWhiteSpace(s.Key));

            AppPaths.Log($"Macro '{macro.Name}' typed {macro.TextLength} characters into 0x{target:X}.");
            Reported?.Invoke(typed ? $"Typed '{macro.Name}'" : $"Ran '{macro.Name}'");
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Macro '{macro.Name}' failed.", ex);
            Reported?.Invoke($"'{macro.Name}' failed — see stash.log");
            return false;
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// Opens a launch step's program, shortcut or document through the shell.
    /// </summary>
    /// <remarks>
    /// Shell execution rather than starting the file directly, so that a .lnk is
    /// followed with its own arguments and start folder, and a document opens in
    /// whatever application is associated with it. The working folder is the
    /// target's own, which is what most programs expect when launched from
    /// Explorer.
    /// </remarks>
    private bool Launch(Macro macro, MacroStep step, string path)
    {
        try
        {
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Arguments = step.Arguments ?? "",
            };

            if (Path.IsPathRooted(path) && Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                info.WorkingDirectory = folder;
            }

            Process.Start(info)?.Dispose();
            AppPaths.Log($"Macro '{macro.Name}' opened '{path}'.");
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Macro '{macro.Name}' could not open '{path}'.", ex);
            Reported?.Invoke($"'{macro.Name}' could not open {Path.GetFileName(path)}");
            return false;
        }
    }

    /// <summary>True if this step or any after it presses keys.</summary>
    private static bool TypesFrom(List<MacroStep> steps, int index)
        => steps.Skip(index).Any(s => !string.IsNullOrEmpty(s.Text) || !string.IsNullOrWhiteSpace(s.Key));

    /// <summary>
    /// Waits for some window other than <paramref name="previous"/> to take the
    /// foreground, and returns it, or zero if none did in time.
    /// </summary>
    /// <remarks>
    /// There is no reliable way to map a launched process to the window it will
    /// show: a shortcut may start a different executable, a single-instance
    /// application hands off to the copy already running and exits, and a document
    /// opens in some other process altogether. Watching the foreground covers all
    /// of those.
    /// </remarks>
    private static async Task<IntPtr> WaitForNewForegroundAsync(IntPtr previous)
    {
        var deadline = DateTime.UtcNow + LaunchTimeout;

        while (DateTime.UtcNow < deadline)
        {
            var current = NativeMethods.GetForegroundWindow();
            if (current != IntPtr.Zero && current != previous)
            {
                return current;
            }

            await Task.Delay(50);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Types text, either in one batch or paced character by character.
    /// </summary>
    /// <remarks>
    /// The batched path is right almost everywhere and is effectively instant.
    /// Some targets — remote desktop and virtual-app clients especially — drop
    /// input delivered that fast, which is what the pacing setting exists for.
    /// </remarks>
    private static async Task TypeAsync(string text, int delayMs)
    {
        if (delayMs <= 0)
        {
            InputSimulator.SendText(text);
            return;
        }

        foreach (var unit in text)
        {
            InputSimulator.SendChar(unit);
            await Task.Delay(delayMs);
        }
    }
}
