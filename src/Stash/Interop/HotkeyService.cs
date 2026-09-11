namespace Stash.Interop;

/// <summary>Why a hotkey fired.</summary>
public enum HotkeyKind
{
    /// <summary>The main chord: show the stash.</summary>
    ShowPanel,

    /// <summary>A quick-slot chord: paste favourite slot <see cref="HotkeyPressed.Slot"/> immediately.</summary>
    QuickSlot,
}

public readonly record struct HotkeyPressed(HotkeyKind Kind, int Slot);

/// <summary>
/// Owns every system-wide hotkey Stash registers: the chord that opens the stash,
/// plus one chord per favourite quick slot.
/// </summary>
/// <remarks>
/// Registration is genuinely unreliable on a real desktop — Win+V belongs to
/// Windows, and other utilities squat on the obvious chords. So the main chord
/// walks a fallback list, per-slot failures are recorded individually instead of
/// aborting the batch, and <see cref="Warnings"/> is surfaced in Settings so the
/// user can see what did not take rather than wondering why nothing happens.
/// </remarks>
public sealed class HotkeyService : IDisposable
{
    private const int MainHotkeyId = 0xB0_00;
    private const int SlotHotkeyIdBase = 0xB0_10;

    /// <summary>
    /// Tried in order when the user's chord will not register. Ctrl+Alt+V leads
    /// because it is the least likely to be claimed; Ctrl+Shift+V is late in the
    /// list because taking it globally breaks "paste as plain text" everywhere.
    /// </summary>
    private static readonly string[] MainFallbacks =
    {
        "Ctrl+Alt+V",
        "Win+Shift+V",
        "Ctrl+Shift+Alt+V",
        "Ctrl+Shift+V",
    };

    private readonly MessageWindow _window;
    private readonly List<int> _registeredIds = new();
    private readonly List<string> _warnings = new();

    /// <summary>The main chord that took effect, which may be a fallback.</summary>
    public string? StashChord { get; private set; }

    /// <summary>Quick-slot number to the chord that took effect for it.</summary>
    public IReadOnlyDictionary<int, string> SlotChords => _slotChords;
    private readonly Dictionary<int, string> _slotChords = new();

    /// <summary>Human-readable notes about chords that could not be registered.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public event Action<HotkeyPressed>? Pressed;

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _window.Message += OnMessage;
    }

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeMethods.WM_HOTKEY)
        {
            return false;
        }

        var id = wParam.ToInt32();

        if (id == MainHotkeyId)
        {
            Pressed?.Invoke(new HotkeyPressed(HotkeyKind.ShowPanel, 0));
            return true;
        }

        if (id >= SlotHotkeyIdBase && id < SlotHotkeyIdBase + 9)
        {
            Pressed?.Invoke(new HotkeyPressed(HotkeyKind.QuickSlot, id - SlotHotkeyIdBase + 1));
            return true;
        }

        return false;
    }

    /// <summary>
    /// Re-registers everything from scratch. Safe to call whenever settings change.
    /// </summary>
    public void Apply(string? preferredChord, bool quickSlotsEnabled, string quickSlotModifiers)
    {
        UnregisterAll();
        _warnings.Clear();
        _slotChords.Clear();

        StashChord = RegisterMain(preferredChord);
        if (StashChord is null)
        {
            _warnings.Add("The stash hotkey could not be registered — every candidate chord is already claimed by another app. Open the stash from the tray icon, or pick a different chord.");
        }
        else if (!string.IsNullOrWhiteSpace(preferredChord)
                 && !string.Equals(StashChord, preferredChord, StringComparison.OrdinalIgnoreCase))
        {
            _warnings.Add($"'{preferredChord}' was unavailable, so the stash opens with {StashChord} instead.");
        }

        if (quickSlotsEnabled)
        {
            RegisterQuickSlots(quickSlotModifiers);
        }
    }

    private string? RegisterMain(string? preferred)
    {
        var candidates = new List<string>(MainFallbacks.Length + 1);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            candidates.Add(preferred);
        }
        candidates.AddRange(MainFallbacks.Where(f => !string.Equals(f, preferred, StringComparison.OrdinalIgnoreCase)));

        foreach (var candidate in candidates)
        {
            if (TryRegister(MainHotkeyId, candidate, out var display))
            {
                return display;
            }
        }

        return null;
    }

    private void RegisterQuickSlots(string modifiers)
    {
        if (string.IsNullOrWhiteSpace(modifiers))
        {
            modifiers = "Ctrl+Alt";
        }

        var failed = new List<int>();

        for (var slot = 1; slot <= 9; slot++)
        {
            var chord = $"{modifiers}+{slot}";
            if (TryRegister(SlotHotkeyIdBase + slot - 1, chord, out var display))
            {
                _slotChords[slot] = display;
            }
            else
            {
                failed.Add(slot);
            }
        }

        if (failed.Count > 0)
        {
            _warnings.Add(
                $"Quick-slot hotkeys for slot(s) {string.Join(", ", failed)} could not be registered with the {modifiers} prefix — something else is using them. Try a different prefix in Settings.");
        }
    }

    private bool TryRegister(int id, string chordText, out string display)
    {
        display = "";

        if (!HotkeyChord.TryParse(chordText, out var chord))
        {
            return false;
        }

        // MOD_NOREPEAT stops auto-repeat re-firing while the chord is held down.
        if (!NativeMethods.RegisterHotKey(_window.Handle, id, chord.Modifiers | NativeMethods.MOD_NOREPEAT, chord.VirtualKey))
        {
            return false;
        }

        _registeredIds.Add(id);
        display = chord.Display;
        return true;
    }

    private void UnregisterAll()
    {
        foreach (var id in _registeredIds)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
        }

        _registeredIds.Clear();
        StashChord = null;
    }

    public void Dispose()
    {
        _window.Message -= OnMessage;
        UnregisterAll();
    }
}
