using Stash.Interop;
using Stash.Models;

namespace Stash.Services;

/// <summary>
/// Executes a macro's steps as real keystrokes into whatever window has focus.
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

            foreach (var step in macro.Steps)
            {
                // Abort rather than scatter keystrokes across two applications.
                if (NativeMethods.GetForegroundWindow() != target)
                {
                    AppPaths.Log($"Macro '{macro.Name}' stopped part-way: focus moved.");
                    Reported?.Invoke($"'{macro.Name}' stopped — the focused window changed");
                    return false;
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

            AppPaths.Log($"Macro '{macro.Name}' typed {macro.TextLength} characters into 0x{target:X}.");
            Reported?.Invoke($"Typed '{macro.Name}'");
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
