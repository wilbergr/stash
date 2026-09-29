using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Stash.Interop;
using Stash.Models;
using Stash.Services;
using Stash.ViewModels;

namespace Stash.Views;

/// <summary>
/// Every hotkey Stash uses on one screen, with the configurable ones editable.
/// </summary>
/// <remarks>
/// <para>
/// Settings and the recorder each edit their own chords, which is fine until two
/// of them collide: a macro on Ctrl+Alt+2 silently loses to quick slot 2, and
/// nothing on either screen shows the other. This window puts the open-the-panel
/// chord, the nine quick-slot chords and every macro chord side by side, checks
/// them against each other as you edit, and shows what Windows actually granted
/// after saving.
/// </para>
/// <para>
/// Chords are set by pressing them. While a capture box has focus the owner
/// suspends Stash's registered hotkeys (see <see cref="CaptureStarted"/>),
/// because Windows consumes a registered chord before any window sees it.
/// </para>
/// </remarks>
public partial class HotkeysWindow : Window
{
    private static readonly Brush ProblemBrush = Frozen(Color.FromRgb(0xE0, 0x8A, 0x8A));

    /// <summary>The recorder's stop chord, caught by its keyboard hook.</summary>
    private const string RecorderStopChord = "Ctrl+Alt+Shift+R";

    private readonly SettingsStore _settings;
    private readonly HotkeyService _hotkeys;
    private readonly MacroStore _macros;
    private readonly HistoryStore _history;

    private readonly List<SlotRow> _slotRows = new();
    private List<MacroRow> _macroRows = new();

    private string _savedOpen = "";
    private string _savedOpenStatus = "";
    private bool _savedOpenIsProblem;
    private string _savedPrefix = "";
    private bool _savedSlotsEnabled;

    private bool _loading;
    private bool _capturing;

    /// <summary>Raised after changes are written, so the owner can re-register.</summary>
    public event Action? Saved;

    /// <summary>A capture box took focus: release the global hotkeys.</summary>
    public event Action? CaptureStarted;

    /// <summary>No capture box has focus any more: register the hotkeys again.</summary>
    public event Action? CaptureEnded;

    /// <summary>The user wants to record, edit or delete macros.</summary>
    public event Action? MacroSettingsRequested;

    public HotkeysWindow(SettingsStore settings, HotkeyService hotkeys, MacroStore macros, HistoryStore history)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _macros = macros;
        _history = history;

        InitializeComponent();

        for (var slot = 1; slot <= 9; slot++)
        {
            _slotRows.Add(new SlotRow(slot));
        }

        SlotList.ItemsSource = _slotRows;

        PanelList.ItemsSource = new[]
        {
            new Fixed("Enter", "Paste the selected clip."),
            new Fixed("Arrow keys", "Move between cards."),
            new Fixed("Ctrl+1 to 9", "Paste the first to ninth card."),
            new Fixed("Alt+1 to 9", "Put the selected clip in that quick slot."),
            new Fixed("Ctrl+D", "Star or unstar the selected clip."),
            new Fixed("Alt+C", "Copy without pasting."),
            new Fixed("Alt+O", "Open the link, file or image."),
            new Fixed("Alt+Delete", "Forget the selected clip."),
            new Fixed("Ctrl+Arrow", "Dock the panel to that edge."),
            new Fixed("Tab", "Next view. Shift+Tab goes back."),
            new Fixed("Ctrl+,", "Open Settings."),
            new Fixed("F1", "Open help."),
            new Fixed("Esc", "Close the panel."),
        };

        RecorderList.ItemsSource = new[]
        {
            new Fixed(RecorderStopChord, "Stop recording. Only listened for while a recording is running."),
        };

        OpenBox.ChordChanged += (_, _) => Recheck();
        PrefixBox.ChordChanged += (_, _) => Recheck();
        SlotsCheck.Checked += (_, _) => Recheck();
        SlotsCheck.Unchecked += (_, _) => Recheck();

        SaveButton.Click += (_, _) => Save();
        CloseButton.Click += (_, _) => Close();
        MacroSettingsButton.Click += (_, _) => MacroSettingsRequested?.Invoke();

        // Focus bubbles, so one pair of handlers covers every capture box,
        // including the ones generated inside the macro list.
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotFocus), true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostFocus), true);

        Closed += (_, _) => EndCapture();

        Reload();
    }

    // ---- Rows ---------------------------------------------------------------

    private sealed record Fixed(string Keys, string Description);

    /// <summary>One quick slot. Its chord follows the prefix, so it is not edited directly.</summary>
    private sealed class SlotRow : ObservableObject
    {
        private string _chord = "";
        private string _contents = "";
        private string _status = "";
        private bool _isProblem;
        private double _dim = 1.0;

        public SlotRow(int slot) => Slot = slot;

        public int Slot { get; }

        public string SavedStatus { get; set; } = "";
        public bool SavedIsProblem { get; set; }

        public string Chord { get => _chord; set => Set(ref _chord, value); }
        public string Contents { get => _contents; set => Set(ref _contents, value); }
        public string Status { get => _status; set => Set(ref _status, value); }
        public bool IsProblem { get => _isProblem; set => Set(ref _isProblem, value); }
        public double Dim { get => _dim; set => Set(ref _dim, value); }
    }

    /// <summary>One macro: its chord and enabled state are editable here.</summary>
    private sealed class MacroRow : ObservableObject
    {
        private readonly Action _changed;
        private string _chord;
        private bool _enabled;
        private string _status = "";
        private bool _isProblem;

        public MacroRow(Macro macro, string savedStatus, bool savedIsProblem, Action changed)
        {
            Id = macro.Id;
            Name = macro.Name;
            SavedChord = macro.Hotkey;
            SavedEnabled = macro.Enabled;
            SavedStatus = savedStatus;
            SavedIsProblem = savedIsProblem;
            _chord = macro.Hotkey;
            _enabled = macro.Enabled;
            _changed = changed;
        }

        public string Id { get; }
        public string Name { get; }
        public string SavedChord { get; }
        public bool SavedEnabled { get; }
        public string SavedStatus { get; }
        public bool SavedIsProblem { get; }

        public bool IsChanged => Enabled != SavedEnabled || !SameChord(Chord, SavedChord);

        public string Chord
        {
            get => _chord;
            set
            {
                if (Set(ref _chord, value ?? ""))
                {
                    _changed();
                }
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (Set(ref _enabled, value))
                {
                    _changed();
                }
            }
        }

        public string Status { get => _status; set => Set(ref _status, value); }
        public bool IsProblem { get => _isProblem; set => Set(ref _isProblem, value); }
    }

    // ---- Load ---------------------------------------------------------------

    /// <summary>
    /// Reads the saved chords and what Windows actually granted. Discards any
    /// unsaved edits.
    /// </summary>
    public void Reload()
    {
        _loading = true;

        try
        {
            var s = _settings.Current;

            // --- Open the stash
            _savedOpen = s.Hotkey;
            OpenBox.Chord = s.Hotkey;

            if (_hotkeys.StashChord is null)
            {
                _savedOpenStatus = "Not working: this and every fallback are taken by other applications. Open Stash from the tray icon, or pick another.";
                _savedOpenIsProblem = true;
            }
            else if (!SameChord(_hotkeys.StashChord, s.Hotkey))
            {
                _savedOpenStatus = $"Taken by another application, so Stash is using {_hotkeys.StashChord} instead.";
                _savedOpenIsProblem = true;
            }
            else
            {
                _savedOpenStatus = "Working";
                _savedOpenIsProblem = false;
            }

            // --- Quick slots
            _savedSlotsEnabled = s.QuickSlotsEnabled;
            _savedPrefix = string.IsNullOrWhiteSpace(s.QuickSlotModifiers) ? "Ctrl+Alt" : s.QuickSlotModifiers;
            SlotsCheck.IsChecked = s.QuickSlotsEnabled;
            PrefixBox.Chord = _savedPrefix;

            foreach (var row in _slotRows)
            {
                var item = _history.BySlot(row.Slot);
                row.Contents = item is null ? "Empty" : Describe(item);

                if (!s.QuickSlotsEnabled)
                {
                    row.SavedStatus = "Off";
                    row.SavedIsProblem = false;
                }
                else if (_hotkeys.SlotChords.ContainsKey(row.Slot))
                {
                    row.SavedStatus = "Working";
                    row.SavedIsProblem = false;
                }
                else
                {
                    row.SavedStatus = "Taken by another application";
                    row.SavedIsProblem = true;
                }
            }

            // --- Macros
            _macroRows = _macros.All
                .Select(e =>
                {
                    var (status, problem) = e switch
                    {
                        { Macro.Enabled: false } => ("Disabled", false),
                        { Active: true } => ("Working", false),
                        _ => (e.Problem ?? "Not running", true),
                    };

                    return new MacroRow(e.Macro, status, problem, Recheck);
                })
                .ToList();

            MacroList.ItemsSource = _macroRows;
            NoMacrosText.Visibility = _macroRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MacrosOffText.Visibility = !s.MacrosEnabled && _macroRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _loading = false;
        }

        Recheck();
    }

    /// <summary>A one-line description of what a slot holds.</summary>
    private static string Describe(ClipItem item)
    {
        var text = item.Kind switch
        {
            ClipKind.Image => $"Image, {item.ImageWidth} × {item.ImageHeight}",
            ClipKind.Files when item.Files is { Count: > 1 } files => $"{files.Count} files",
            _ => item.Preview,
        };

        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 80 ? text[..80] + "…" : text;
    }

    // ---- Validation ---------------------------------------------------------

    /// <summary>A chord something wants, and who wants it.</summary>
    /// <param name="Group">Name used when several claims are summarised as one, e.g. "quick slots".</param>
    private sealed record Claim(string Chord, string Owner, string Group);

    /// <summary>Panel shortcuts a global chord would shadow, mapped to what they do.</summary>
    private static readonly Dictionary<string, string> PanelChords = BuildPanelChords();

    private static Dictionary<string, string> BuildPanelChords()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ctrl+D"] = "Ctrl+D (star a clip)",
            ["Alt+C"] = "Alt+C (copy without pasting)",
            ["Alt+O"] = "Alt+O (open)",
            ["Alt+Delete"] = "Alt+Delete (forget a clip)",
            ["Ctrl+Left"] = "Ctrl+Arrow (dock to an edge)",
            ["Ctrl+Right"] = "Ctrl+Arrow (dock to an edge)",
            ["Ctrl+Up"] = "Ctrl+Arrow (dock to an edge)",
            ["Ctrl+Down"] = "Ctrl+Arrow (dock to an edge)",
        };

        for (var n = 1; n <= 9; n++)
        {
            map[$"Ctrl+{n}"] = "Ctrl+1 to 9 (paste a card)";
            map[$"Alt+{n}"] = "Alt+1 to 9 (assign a quick slot)";
        }

        return map;
    }

    /// <summary>
    /// Re-validates everything against everything else and updates each row's
    /// status, the footer and whether Save is allowed.
    /// </summary>
    private void Recheck()
    {
        if (_loading)
        {
            return;
        }

        var blocking = new List<string>();
        var notes = new List<string>();
        var claims = new List<Claim>();

        var open = Normalise(OpenBox.Chord);
        if (open is null)
        {
            blocking.Add("Opening the stash needs a hotkey.");
        }
        else
        {
            claims.Add(new Claim(open, "opening the stash", "opening the stash"));
        }

        var slotsOn = SlotsCheck.IsChecked == true;
        var prefix = PrefixBox.Chord?.Trim() ?? "";
        var prefixOk = HotkeyChord.TryParse(prefix + "+1", out _);

        if (slotsOn && !prefixOk)
        {
            blocking.Add("Quick slots need a prefix, such as Ctrl+Alt.");
        }

        foreach (var row in _slotRows)
        {
            row.Chord = prefixOk ? Normalise($"{prefix}+{row.Slot}") ?? "" : "—";
            row.Dim = slotsOn ? 1.0 : 0.45;

            if (slotsOn && prefixOk)
            {
                claims.Add(new Claim(row.Chord, $"quick slot {row.Slot}", "quick slots"));
            }
        }

        foreach (var row in _macroRows.Where(r => r.Enabled))
        {
            var chord = Normalise(row.Chord);
            if (chord is null)
            {
                blocking.Add($"'{row.Name}' needs a hotkey, or untick it.");
            }
            else
            {
                claims.Add(new Claim(chord, $"'{row.Name}'", $"'{row.Name}'"));
            }
        }

        // Two things on one chord: only the first to register would ever fire.
        var clashes = claims
            .GroupBy(c => c.Chord, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (chord, owners) in clashes)
        {
            blocking.Add($"{chord} is set for both {JoinAnd(owners.Select(o => o.Owner))}.");
        }

        // A global chord that shadows a panel shortcut is allowed, but say so.
        foreach (var group in claims
                     .Where(c => PanelChords.ContainsKey(c.Chord))
                     .GroupBy(c => PanelChords[c.Chord]))
        {
            var who = JoinAnd(group.Select(c => c.Group).Distinct());
            notes.Add($"The panel's {group.Key} shortcut will not work, because {who} uses it.");
        }

        if (claims.FirstOrDefault(c => SameChord(c.Chord, RecorderStopChord)) is { } stop)
        {
            notes.Add($"{RecorderStopChord} also stops a recording, so it cannot trigger {stop.Owner} while one is running.");
        }

        // --- Row statuses
        string? ClashFor(string? chord, string owner)
        {
            if (chord is null || !clashes.TryGetValue(chord, out var list))
            {
                return null;
            }

            var others = list.Where(c => c.Owner != owner).Select(c => c.Owner);
            return $"Clashes with {JoinAnd(others)}";
        }

        var openChanged = !SameChord(OpenBox.Chord, _savedOpen);
        if (ClashFor(open, "opening the stash") is { } openClash)
        {
            SetOpenStatus(openClash, true);
        }
        else if (open is null)
        {
            SetOpenStatus("Needs a hotkey", true);
        }
        else
        {
            SetOpenStatus(openChanged ? "Not saved yet" : _savedOpenStatus, !openChanged && _savedOpenIsProblem);
        }

        var slotsChanged = slotsOn != _savedSlotsEnabled || !SameChord(prefix, _savedPrefix);
        foreach (var row in _slotRows)
        {
            if (slotsOn && ClashFor(row.Chord, $"quick slot {row.Slot}") is { } clash)
            {
                row.Status = clash;
                row.IsProblem = true;
            }
            else if (slotsChanged)
            {
                row.Status = slotsOn ? "Not saved yet" : "Off";
                row.IsProblem = false;
            }
            else
            {
                row.Status = row.SavedStatus;
                row.IsProblem = row.SavedIsProblem;
            }
        }

        foreach (var row in _macroRows)
        {
            var chord = Normalise(row.Chord);

            if (!row.Enabled)
            {
                row.Status = "Disabled";
                row.IsProblem = false;
            }
            else if (chord is null)
            {
                row.Status = "Needs a hotkey";
                row.IsProblem = true;
            }
            else if (ClashFor(chord, $"'{row.Name}'") is { } clash)
            {
                row.Status = clash;
                row.IsProblem = true;
            }
            else if (row.IsChanged || openChanged || slotsChanged)
            {
                // A change elsewhere may be what frees this macro's chord.
                row.Status = row.IsChanged ? "Not saved yet" : row.SavedIsProblem ? "Should work once saved" : row.SavedStatus;
                row.IsProblem = false;
            }
            else
            {
                row.Status = row.SavedStatus;
                row.IsProblem = row.SavedIsProblem;
            }
        }

        // --- Footer
        var dirty = openChanged || slotsChanged || _macroRows.Any(r => r.IsChanged);
        SaveButton.IsEnabled = dirty && blocking.Count == 0;

        if (blocking.Count > 0)
        {
            FooterText.Text = string.Join(" ", blocking);
            FooterText.Foreground = ProblemBrush;
        }
        else
        {
            FooterText.Text = string.Join(" ", notes);
            FooterText.ClearValue(ForegroundProperty);
        }
    }

    private void SetOpenStatus(string text, bool problem)
    {
        OpenStatus.Text = text;

        if (problem)
        {
            OpenStatus.Foreground = ProblemBrush;
        }
        else
        {
            OpenStatus.ClearValue(ForegroundProperty);
        }
    }

    // ---- Save ---------------------------------------------------------------

    private void Save()
    {
        Recheck();
        if (!SaveButton.IsEnabled)
        {
            return;
        }

        // Macros first: if the file cannot be written, nothing else has changed
        // yet and the screen still matches what is saved.
        var changes = _macroRows
            .Where(r => r.IsChanged)
            .ToDictionary(
                r => r.Id,
                r => (Normalise(r.Chord) ?? r.SavedChord, r.Enabled));

        if (changes.Count > 0 && !_macros.SetHotkeys(changes, out var error))
        {
            FooterText.Text = "Not saved — " + (error ?? "macros.json could not be written.");
            FooterText.Foreground = ProblemBrush;
            return;
        }

        var open = Normalise(OpenBox.Chord)!;
        var slotsOn = SlotsCheck.IsChecked == true;
        var prefix = PrefixBox.Chord?.Trim() ?? "";

        _settings.Update(s =>
        {
            s.Hotkey = open;
            s.QuickSlotsEnabled = slotsOn;

            // With slots off the prefix box may be blank; keep the old one.
            if (HotkeyChord.TryParse(prefix + "+1", out _))
            {
                s.QuickSlotModifiers = prefix;
            }
        });

        // The owner re-registers everything, so what Reload reads back is what
        // Windows actually granted.
        Saved?.Invoke();
        Reload();

        var failing = (_savedOpenIsProblem ? 1 : 0)
                      + _slotRows.Count(r => r.SavedIsProblem)
                      + _macroRows.Count(r => r.SavedIsProblem);

        FooterText.Text = failing == 0
            ? "Saved. Every hotkey is working."
            : $"Saved, but {failing} hotkey{(failing == 1 ? " is" : "s are")} not working. The reason is shown against each.";
        FooterText.Foreground = failing == 0 ? (Brush)FindResource("Brush.TextTertiary") : ProblemBrush;
    }

    // ---- Capture ------------------------------------------------------------

    private void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is HotkeyCaptureBox && !_capturing)
        {
            _capturing = true;
            CaptureStarted?.Invoke();
        }
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Moving straight from one capture box to another keeps the hotkeys off.
        if (e.NewFocus is not HotkeyCaptureBox)
        {
            EndCapture();
        }
    }

    private void EndCapture()
    {
        if (_capturing)
        {
            _capturing = false;
            CaptureEnded?.Invoke();
        }
    }

    // ---- Helpers ------------------------------------------------------------

    private static string? Normalise(string? chord)
        => HotkeyChord.TryParse(chord, out var parsed) ? parsed.Display : null;

    private static bool SameChord(string? a, string? b)
    {
        var na = Normalise(a) ?? a?.Trim() ?? "";
        var nb = Normalise(b) ?? b?.Trim() ?? "";
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    private static string JoinAnd(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "something else",
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        DwmEffects.SetDarkMode(this, SystemTheme.ResolveDark(_settings.Current.Theme));
        DwmEffects.SetRoundedCorners(this);
    }
}
