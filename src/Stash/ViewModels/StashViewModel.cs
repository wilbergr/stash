using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Controls;
using Stash.Models;
using Stash.Services;

namespace Stash.ViewModels;

/// <summary>
/// State and behaviour behind the stash window: which view is active, what the
/// search box has filtered down to, which card is selected, and what each of the
/// actions does.
/// </summary>
public sealed class StashViewModel : ObservableObject
{
    private readonly HistoryStore _history;
    private readonly PasteService _paste;
    private readonly ThumbnailCache _thumbnails;
    private readonly SettingsStore _settings;

    private string _searchText = "";
    private StashView _activeView = StashView.Recents;
    private int _selectedIndex = -1;
    private DockEdge _edge = DockEdge.Bottom;
    private string _hotkeyHint = "";
    private string? _toast;

    public StashViewModel(
        HistoryStore history,
        PasteService paste,
        ThumbnailCache thumbnails,
        SettingsStore settings)
    {
        _history = history;
        _paste = paste;
        _thumbnails = thumbnails;
        _settings = settings;

        Chips = new ObservableCollection<ViewChipViewModel>(
            StashViewExtensions.All.Select(v => new ViewChipViewModel(v)));

        ActivateCommand = new RelayCommand(p => _ = ActivateAsync(p as ClipCardViewModel));
        CopyOnlyCommand = new RelayCommand(p => CopyOnly(p as ClipCardViewModel));
        ToggleFavoriteCommand = new RelayCommand(p => ToggleFavorite(p as ClipCardViewModel));
        DeleteCommand = new RelayCommand(p => Delete(p as ClipCardViewModel));
        OpenCommand = new RelayCommand(p => Open(p as ClipCardViewModel));
        SelectViewCommand = new RelayCommand(p =>
        {
            if (p is ViewChipViewModel chip)
            {
                ActiveView = chip.View;
            }
        });
    }

    // ---- Window plumbing ----------------------------------------------------

    /// <summary>The window that had focus when the stash opened; paste target.</summary>
    public IntPtr TargetWindow { get; set; }

    /// <summary>Raised when an action means the stash should slide away.</summary>
    public event Action? RequestClose;

    /// <summary>Raised when the selection changes, so the view can scroll it into sight.</summary>
    public event Action<int>? RequestScrollTo;

    // ---- Bindable state ----------------------------------------------------

    public ObservableCollection<ClipCardViewModel> Cards { get; } = new();

    public ObservableCollection<ViewChipViewModel> Chips { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                Rebuild();
                Raise(nameof(HasSearch));
            }
        }
    }

    public bool HasSearch => _searchText.Length > 0;

    public StashView ActiveView
    {
        get => _activeView;
        set
        {
            if (Set(ref _activeView, value))
            {
                Rebuild();
            }
        }
    }

    public DockEdge Edge
    {
        get => _edge;
        set
        {
            if (Set(ref _edge, value))
            {
                Raise(nameof(IsVertical));
                Raise(nameof(CardOrientation));
            }
        }
    }

    public bool IsVertical => _edge.IsVertical();

    public Orientation CardOrientation => _edge.CardOrientation();

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var clamped = Cards.Count == 0 ? -1 : Math.Clamp(value, 0, Cards.Count - 1);

            if (_selectedIndex >= 0 && _selectedIndex < Cards.Count)
            {
                Cards[_selectedIndex].IsSelected = false;
            }

            if (Set(ref _selectedIndex, clamped))
            {
                Raise(nameof(SelectedCard));
            }

            if (clamped >= 0 && clamped < Cards.Count)
            {
                Cards[clamped].IsSelected = true;
                RequestScrollTo?.Invoke(clamped);
            }
        }
    }

    public ClipCardViewModel? SelectedCard
        => _selectedIndex >= 0 && _selectedIndex < Cards.Count ? Cards[_selectedIndex] : null;

    public bool IsEmpty => Cards.Count == 0;

    /// <summary>Explains an empty stash in terms of why it is empty.</summary>
    public string EmptyMessage
    {
        get
        {
            if (HasSearch)
            {
                return $"Nothing matches “{_searchText}”";
            }

            return _activeView switch
            {
                StashView.Favorites => "No favorites yet — press F on a card to star it",
                StashView.MostUsed => "Nothing pasted from the stash yet",
                StashView.Images => "No images copied yet",
                StashView.Links => "No links copied yet",
                StashView.Files => "No files copied yet",
                _ => "Nothing on the stash yet — copy something",
            };
        }
    }

    public string HotkeyHint
    {
        get => _hotkeyHint;
        set => Set(ref _hotkeyHint, value);
    }

    /// <summary>Transient confirmation message shown in the footer.</summary>
    public string? Toast
    {
        get => _toast;
        set
        {
            if (Set(ref _toast, value))
            {
                Raise(nameof(HasToast));
            }
        }
    }

    public bool HasToast => !string.IsNullOrEmpty(_toast);

    // ---- Commands -----------------------------------------------------------

    public RelayCommand ActivateCommand { get; }

    public RelayCommand CopyOnlyCommand { get; }

    public RelayCommand ToggleFavoriteCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand SelectViewCommand { get; }

    // ---- Filtering ----------------------------------------------------------

    /// <summary>
    /// Rebuilds the card list from the history for the active view and search.
    /// </summary>
    /// <param name="preserveSelection">
    /// Keeps the same clip selected where it survives the rebuild, so typing in
    /// the search box does not yank the selection around. Opening the stash passes
    /// false: the newest clip must be selected then, or "hotkey, Enter" would
    /// paste whatever happened to be highlighted last time.
    /// </param>
    public void Rebuild(bool preserveSelection = true)
    {
        var previouslySelected = preserveSelection ? SelectedCard?.Item : null;

        var matches = _history.Items
            .Where(i => _activeView.Matches(i))
            .Where(MatchesSearch);

        matches = _activeView switch
        {
            StashView.Favorites => matches
                // Slotted favourites first, in slot order, so the hotkey numbers
                // line up with what the user sees.
                .OrderBy(i => i.FavoriteSlot is >= 1 and <= 9 ? i.FavoriteSlot : int.MaxValue)
                .ThenByDescending(i => i.Captured),

            StashView.MostUsed => matches
                .OrderByDescending(i => i.UseCount)
                .ThenByDescending(i => i.LastUsed ?? i.Captured),

            _ => matches.OrderByDescending(i => i.Captured),
        };

        Cards.Clear();
        foreach (var item in matches)
        {
            Cards.Add(new ClipCardViewModel(item, _thumbnails));
        }

        UpdateChipCounts();

        Raise(nameof(IsEmpty));
        Raise(nameof(EmptyMessage));

        // Keep the same clip selected across a rebuild where possible, so typing
        // in the search box does not yank the selection around.
        var restored = previouslySelected is null
            ? -1
            : Cards.ToList().FindIndex(c => ReferenceEquals(c.Item, previouslySelected));

        _selectedIndex = -1;
        SelectedIndex = restored >= 0 ? restored : (Cards.Count > 0 ? 0 : -1);
    }

    private bool MatchesSearch(ClipItem item)
    {
        if (_searchText.Length == 0)
        {
            return true;
        }

        const StringComparison ci = StringComparison.OrdinalIgnoreCase;
        var q = _searchText;

        if (item.Preview.Contains(q, ci))
        {
            return true;
        }

        if (item.Text is not null && item.Text.Contains(q, ci))
        {
            return true;
        }

        if (item.SourceApp is not null && item.SourceApp.Contains(q, ci))
        {
            return true;
        }

        return false;
    }

    private void UpdateChipCounts()
    {
        foreach (var chip in Chips)
        {
            chip.Count = _history.Items.Count(i => chip.View.Matches(i));
            chip.IsActive = chip.View == _activeView;
        }
    }

    /// <summary>Refreshes relative timestamps and counts without rebuilding the list.</summary>
    public void RefreshTimestamps()
    {
        foreach (var card in Cards)
        {
            card.Refresh();
        }
    }

    // ---- Keyboard navigation ------------------------------------------------

    public void MoveSelection(int delta)
    {
        if (Cards.Count == 0)
        {
            return;
        }

        // Wrap, so holding the arrow key cycles instead of dead-ending.
        var next = (_selectedIndex + delta) % Cards.Count;
        if (next < 0)
        {
            next += Cards.Count;
        }

        SelectedIndex = next;
    }

    public void SelectFirst() => SelectedIndex = Cards.Count > 0 ? 0 : -1;

    public void SelectLast() => SelectedIndex = Cards.Count - 1;

    /// <summary>Cycles the active chip by <paramref name="delta"/> (Tab / Shift+Tab).</summary>
    public void CycleView(int delta)
    {
        var all = StashViewExtensions.All;
        var index = Array.IndexOf(all, _activeView);
        if (index < 0)
        {
            index = 0;
        }

        var next = (index + delta) % all.Length;
        if (next < 0)
        {
            next += all.Length;
        }

        ActiveView = all[next];
    }

    /// <summary>Activates the Nth visible card, for Ctrl+1..9.</summary>
    public Task ActivateAtAsync(int oneBasedIndex)
    {
        var index = oneBasedIndex - 1;
        return index >= 0 && index < Cards.Count
            ? ActivateAsync(Cards[index])
            : Task.CompletedTask;
    }

    // ---- Actions ------------------------------------------------------------

    /// <summary>Pastes a card into the target window and dismisses the stash.</summary>
    public async Task ActivateAsync(ClipCardViewModel? card)
    {
        card ??= SelectedCard;
        if (card is null)
        {
            AppPaths.Log("Activate ignored: nothing is selected.");
            return;
        }

        var target = TargetWindow;
        var sendKeystroke = _settings.Current.PasteOnSelect;

        AppPaths.Log($"Activating {card.Item.Kind} clip into 0x{target:X}.");

        // Close first: the stash must be out of the way before focus goes back,
        // otherwise the paste lands in our own window.
        RequestClose?.Invoke();

        await _paste.PasteInto(card.Item, target, sendKeystroke);
        card.Refresh();
    }

    private void CopyOnly(ClipCardViewModel? card)
    {
        card ??= SelectedCard;
        if (card is null)
        {
            return;
        }

        if (_paste.CopyToClipboard(card.Item))
        {
            Toast = "Copied to clipboard";
        }
        else
        {
            Toast = "Clipboard is locked by another app — try again";
        }
    }

    public void ToggleFavorite(ClipCardViewModel? card)
    {
        card ??= SelectedCard;
        if (card is null)
        {
            return;
        }

        _history.ToggleFavorite(card.Item);
        card.Refresh();
        UpdateChipCounts();

        Toast = card.Item.IsFavorite ? "Added to favorites" : "Removed from favorites";

        // A card can drop out of the Favorites view the moment it is unstarred.
        if (_activeView == StashView.Favorites && !card.Item.IsFavorite)
        {
            Rebuild();
        }
    }

    /// <summary>Binds the selected card to a quick slot, for Alt+1..9.</summary>
    public void AssignSlot(int slot, ClipCardViewModel? card = null)
    {
        card ??= SelectedCard;
        if (card is null || slot is < 0 or > 9)
        {
            return;
        }

        // Assigning a slot the card already holds clears it, so Alt+N toggles.
        var clearing = card.Item.FavoriteSlot == slot;
        _history.AssignSlot(card.Item, clearing ? 0 : slot);

        foreach (var other in Cards)
        {
            other.Refresh();
        }

        UpdateChipCounts();

        Toast = clearing
            ? $"Cleared quick slot {slot}"
            : $"Assigned to quick slot {slot}";
    }

    public void Delete(ClipCardViewModel? card)
    {
        card ??= SelectedCard;
        if (card is null)
        {
            return;
        }

        var index = Cards.IndexOf(card);

        _thumbnails.Forget(card.Item.ThumbFile);
        _history.Remove(card.Item);

        Rebuild();

        // Land the selection where the deleted card was, not back at the start.
        if (Cards.Count > 0)
        {
            SelectedIndex = Math.Min(index, Cards.Count - 1);
        }

        Toast = "Removed from history";
    }

    /// <summary>Opens a link in the browser, or reveals files in Explorer.</summary>
    private void Open(ClipCardViewModel? card)
    {
        card ??= SelectedCard;
        if (card is null)
        {
            return;
        }

        try
        {
            switch (card.Item.Kind)
            {
                case ClipKind.Link when card.Item.Text is not null:
                    RequestClose?.Invoke();
                    Process.Start(new ProcessStartInfo(card.Item.Text.Trim()) { UseShellExecute = true });
                    break;

                case ClipKind.Files when card.Item.Files is { Count: > 0 }:
                    var first = card.Item.Files.FirstOrDefault(File.Exists)
                                ?? card.Item.Files.FirstOrDefault(Directory.Exists);
                    if (first is null)
                    {
                        Toast = "Those files no longer exist";
                        return;
                    }

                    RequestClose?.Invoke();
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"")
                    {
                        UseShellExecute = true,
                    });
                    break;

                case ClipKind.Image when card.Item.ImageFile is not null:
                    var path = AppPaths.ImagePath(card.Item.ImageFile);
                    if (!File.Exists(path))
                    {
                        Toast = "That image is no longer on disk";
                        return;
                    }

                    RequestClose?.Invoke();
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    break;

                default:
                    CopyOnly(card);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not open the clip.", ex);
            Toast = "Windows could not open that";
        }
    }

    /// <summary>Resets to the default view and clears the search box.</summary>
    public void ResetForShow()
    {
        _searchText = "";
        Raise(nameof(SearchText));
        Raise(nameof(HasSearch));

        _activeView = StashViewExtensions.Parse(_settings.Current.DefaultView);
        Raise(nameof(ActiveView));

        Toast = null;
        Rebuild(preserveSelection: false);
    }
}
