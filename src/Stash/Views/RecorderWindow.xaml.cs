using System.Windows;
using System.Windows.Media;
using Stash.Interop;
using Stash.Models;
using Stash.Services;

namespace Stash.Views;

/// <summary>
/// Records real key presses into a macro.
/// </summary>
/// <remarks>
/// Stays on top and does not take focus while recording, because the whole point
/// is to type into another application and have it captured. Stopping therefore
/// has to be a global chord rather than this window's button — see
/// <see cref="KeyboardRecorder.StopChordDisplay"/>.
/// </remarks>
public partial class RecorderWindow : Window
{
    private readonly MacroStore _macros;
    private readonly SettingsStore _settings;
    private readonly KeyboardRecorder _recorder = new();

    /// <summary>Raised after a macro is saved, so hotkeys can be re-registered.</summary>
    public event Action? Saved;

    public RecorderWindow(MacroStore macros, SettingsStore settings)
    {
        _macros = macros;
        _settings = settings;

        InitializeComponent();

        NameBox.Text = "My macro";
        HotkeyBox.Text = SuggestChord();
        PausesCheck.IsChecked = _recorder.RecordPauses;

        RecordButton.Click += (_, _) => Toggle();
        UndoButton.Click += (_, _) => _recorder.RemoveLast();
        ClearButton.Click += (_, _) => _recorder.Clear();
        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();

        PausesCheck.Checked += (_, _) => _recorder.RecordPauses = true;
        PausesCheck.Unchecked += (_, _) => _recorder.RecordPauses = false;

        _recorder.Changed += RefreshSteps;

        // The stop chord is seen by the hook, which runs on this thread, but
        // marshal anyway so the handler never runs inside the hook callback.
        _recorder.StopRequested += () => Dispatcher.BeginInvoke(StopRecording);

        Closed += (_, _) => _recorder.Dispose();

        RefreshSteps();
        UpdateStatus();
    }

    /// <summary>Row shape for the captured-steps list.</summary>
    private sealed record StepRow(string Kind, string Detail);

    // ---- Recording ----------------------------------------------------------

    private void Toggle()
    {
        if (_recorder.IsRecording)
        {
            StopRecording();
        }
        else
        {
            StartRecording();
        }
    }

    private void StartRecording()
    {
        ErrorText.Text = "";
        _recorder.Start();

        if (!_recorder.IsRecording)
        {
            ErrorText.Text = "Windows would not allow the keyboard hook. See stash.log.";
            return;
        }

        UpdateStatus();
    }

    private void StopRecording()
    {
        _recorder.Stop();
        UpdateStatus();
        Activate();
    }

    private void UpdateStatus()
    {
        if (_recorder.IsRecording)
        {
            RecordDot.Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));
            StatusText.Text = "Recording";
            StatusHint.Text =
                $"Switch to the app you want to type into and type normally. Press {KeyboardRecorder.StopChordDisplay} to stop. " +
                "Everything you type is captured while this is running, so do not type passwords.";
            RecordButton.Content = "Stop recording";
        }
        else
        {
            RecordDot.Fill = (Brush)FindResource("Brush.TextTertiary");
            StatusText.Text = _recorder.Steps.Count > 0 ? "Stopped" : "Not recording";
            StatusHint.Text =
                "Press Start, then switch to the app you want to type into. Stash captures the keys you press, " +
                "turning letters into text and keys like Tab or Enter into their own steps. Nothing is saved until you click Save macro.";
            RecordButton.Content = _recorder.Steps.Count > 0 ? "Record again" : "Start recording";
        }
    }

    private void RefreshSteps()
    {
        var rows = new List<StepRow>();

        foreach (var step in _recorder.Steps)
        {
            if (!string.IsNullOrEmpty(step.Text))
            {
                rows.Add(new StepRow("type", Quote(step.Text)));
            }
            else if (!string.IsNullOrWhiteSpace(step.Key))
            {
                rows.Add(new StepRow("press", step.Key));
            }
            else if (step.DelayMs is { } ms)
            {
                rows.Add(new StepRow("wait", $"{ms} ms"));
            }
        }

        StepList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Keep the newest step in view while recording.
        StepScroller.ScrollToEnd();
    }

    /// <summary>Makes whitespace visible in the step list.</summary>
    private static string Quote(string text)
    {
        var shown = text.Replace("\r", "").Replace("\n", "\\n").Replace("\t", "\\t");
        return $"\u201c{shown}\u201d";
    }

    // ---- Saving -------------------------------------------------------------

    private void Save()
    {
        if (_recorder.IsRecording)
        {
            StopRecording();
        }

        ErrorText.Text = "";

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "Give the macro a name.";
            return;
        }

        var chordText = HotkeyBox.Text.Trim();
        if (!HotkeyChord.TryParse(chordText, out var chord))
        {
            ErrorText.Text = "The hotkey needs at least one modifier plus a key, e.g. Ctrl+Alt+H.";
            return;
        }

        if (_macros.IsChordTaken(chord.Display))
        {
            ErrorText.Text = $"{chord.Display} is already used by another macro.";
            return;
        }

        var steps = _recorder.Build();
        if (steps.Count == 0)
        {
            ErrorText.Text = "Nothing was captured, so there is nothing to save.";
            return;
        }

        var macro = new Macro
        {
            Name = name,
            Hotkey = chord.Display,
            Enabled = true,
            Steps = steps,
        };

        if (!_macros.Append(macro, out var error))
        {
            ErrorText.Text = error ?? "The macro could not be saved.";
            return;
        }

        Saved?.Invoke();
        Close();
    }

    /// <summary>
    /// Proposes a chord that is not already taken, so the common case needs no
    /// thought. Walks the alphabet under the quick-slot prefix.
    /// </summary>
    private string SuggestChord()
    {
        var prefix = string.IsNullOrWhiteSpace(_settings.Current.QuickSlotModifiers)
            ? "Ctrl+Alt"
            : _settings.Current.QuickSlotModifiers;

        foreach (var letter in "HGJKLMNPQRTUWYZ")
        {
            var candidate = $"{prefix}+{letter}";
            if (HotkeyChord.TryParse(candidate, out var chord) && !_macros.IsChordTaken(chord.Display))
            {
                return chord.Display;
            }
        }

        return $"{prefix}+H";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        DwmEffects.SetDarkMode(this, SystemTheme.ResolveDark(_settings.Current.Theme));
        DwmEffects.SetRoundedCorners(this);
    }
}
