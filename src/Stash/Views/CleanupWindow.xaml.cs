using System.Windows;
using System.Windows.Controls;
using Stash.Interop;
using Stash.Models;
using Stash.Services;

namespace Stash.Views;

/// <summary>
/// Targeted history cleanup: remove clips by age, kind or whether they have ever
/// been used.
/// </summary>
/// <remarks>
/// The existing Clear History command is the blunt version of this and remains
/// for the common case. This exists because "I have accumulated a lot of clips"
/// usually wants a scalpel: drop the images eating the disk, or everything from
/// last month, or the great mass of things copied once and never reused, while
/// keeping the rest.
///
/// Every change re-counts against the live history and shows what would go
/// before anything is removed, because deletion here is not undoable.
/// </remarks>
public partial class CleanupWindow : Window
{
    private readonly HistoryStore _history;
    private readonly SettingsStore _settings;
    private readonly ThumbnailCache _thumbnails;

    private bool _loading = true;

    /// <summary>Raised after clips are removed, so the panel can rebuild.</summary>
    public event Action? Cleaned;

    public CleanupWindow(HistoryStore history, SettingsStore settings, ThumbnailCache thumbnails)
    {
        _history = history;
        _settings = settings;
        _thumbnails = thumbnails;

        InitializeComponent();

        DaysBox.Text = "30";
        KindAny.IsChecked = true;

        // Any change to any control re-runs the preview.
        foreach (var box in new[] { OlderCheck, NeverPastedCheck, IncludeFavoritesCheck })
        {
            box.Checked += (_, _) => Refresh();
            box.Unchecked += (_, _) => Refresh();
        }

        foreach (var radio in new[] { KindAny, KindText, KindLink, KindImage, KindFiles })
        {
            radio.Checked += (_, _) => Refresh();
        }

        DaysBox.TextChanged += (_, _) => Refresh();

        RemoveButton.Click += (_, _) => Remove();
        CancelButton.Click += (_, _) => Close();

        _loading = false;
        ShowTotals();
        Refresh();
    }

    // ---- Current state ------------------------------------------------------

    private void ShowTotals()
    {
        var items = _history.Items;
        var favorites = items.Count(i => i.IsFavorite);
        var bytes = _history.TotalImageBytes();

        TotalsText.Text = $"{items.Count} clip{(items.Count == 1 ? "" : "s")} stored, {favorites} favorite{(favorites == 1 ? "" : "s")}";

        var byKind = new[] { ClipKind.Text, ClipKind.Link, ClipKind.Image, ClipKind.Files, ClipKind.Color }
            .Select(k => new { Kind = k, Count = items.Count(i => i.Kind == k) })
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Count} {Label(x.Kind, x.Count)}");

        var breakdown = string.Join(" · ", byKind);

        BreakdownText.Text = bytes > 0
            ? $"{breakdown} — images occupy {Size(bytes)} on disk"
            : breakdown;
    }

    private static string Label(ClipKind kind, int count) => kind switch
    {
        ClipKind.Text => count == 1 ? "text clip" : "text clips",
        ClipKind.Link => count == 1 ? "link" : "links",
        ClipKind.Image => count == 1 ? "image" : "images",
        ClipKind.Files => count == 1 ? "file clip" : "file clips",
        _ => count == 1 ? "colour" : "colours",
    };

    private static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };

    // ---- Filter and preview -------------------------------------------------

    private PruneFilter BuildFilter()
    {
        int? days = null;

        if (OlderCheck.IsChecked == true && int.TryParse(DaysBox.Text.Trim(), out var parsed) && parsed >= 0)
        {
            days = parsed;
        }

        ClipKind? kind = null;
        if (KindText.IsChecked == true) kind = ClipKind.Text;
        else if (KindLink.IsChecked == true) kind = ClipKind.Link;
        else if (KindImage.IsChecked == true) kind = ClipKind.Image;
        else if (KindFiles.IsChecked == true) kind = ClipKind.Files;

        return new PruneFilter
        {
            OlderThanDays = days,
            Kind = kind,
            OnlyNeverPasted = NeverPastedCheck.IsChecked == true,
            IncludeFavorites = IncludeFavoritesCheck.IsChecked == true,
        };
    }

    private void Refresh()
    {
        if (_loading)
        {
            return;
        }

        FavoriteWarning.Visibility = IncludeFavoritesCheck.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

        // An age filter with an unreadable number would silently widen the
        // selection to every clip, so say so rather than preview a lie.
        if (OlderCheck.IsChecked == true && !int.TryParse(DaysBox.Text.Trim(), out var days))
        {
            PreviewText.Text = "Enter a number of days.";
            PreviewDetail.Text = "";
            RemoveButton.IsEnabled = false;
            return;
        }

        var filter = BuildFilter();
        var count = _history.CountMatching(filter);
        var bytes = _history.BytesMatching(filter);
        var total = _history.Items.Count;

        RemoveButton.IsEnabled = count > 0;
        RemoveButton.Content = count > 0 ? $"Remove {count}" : "Remove";

        if (count == 0)
        {
            PreviewText.Text = "Nothing matches — no clips would be removed.";
            PreviewDetail.Text = filter.Describe();
            return;
        }

        PreviewText.Text = bytes > 0
            ? $"Will remove {count} of {total} clips, freeing {Size(bytes)}"
            : $"Will remove {count} of {total} clips";

        PreviewDetail.Text = $"{filter.Describe()} · {total - count} will remain";
    }

    // ---- Removal ------------------------------------------------------------

    private void Remove()
    {
        var filter = BuildFilter();
        var count = _history.CountMatching(filter);

        if (count == 0)
        {
            return;
        }

        var favorites = _history.Items.Count(i => filter.Matches(i) && i.IsFavorite);

        var message = favorites > 0
            ? $"Remove {count} clip(s), including {favorites} favorite(s)?\n\n{filter.Describe()}.\n\nThis cannot be undone, and any quick slots on those favorites will stop working."
            : $"Remove {count} clip(s)?\n\n{filter.Describe()}.\n\nThis cannot be undone.";

        var answer = MessageBox.Show(
            this,
            message,
            "Clean up history",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        var removed = _history.Prune(filter);

        // Thumbnails for deleted images would otherwise linger in memory.
        _thumbnails.Clear();

        Cleaned?.Invoke();

        ShowTotals();
        Refresh();

        PreviewText.Text = $"Removed {removed} clip{(removed == 1 ? "" : "s")}.";
        PreviewDetail.Text = $"{_history.Items.Count} remaining.";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        DwmEffects.SetDarkMode(this, SystemTheme.ResolveDark(_settings.Current.Theme));
        DwmEffects.SetRoundedCorners(this);
    }
}
