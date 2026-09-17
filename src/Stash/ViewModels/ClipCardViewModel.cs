using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stash.Models;
using Stash.Services;

namespace Stash.ViewModels;

/// <summary>
/// One card on the stash. Everything the template needs is precomputed here so
/// the XAML stays free of converters and the render pass stays cheap.
/// </summary>
public sealed class ClipCardViewModel : ObservableObject
{
    /// <summary>Text cards show at most this many lines before trailing off.</summary>
    private const int BodyLineLimit = 5;

    private readonly ThumbnailCache _thumbnails;
    private bool _isSelected;

    public ClipCardViewModel(ClipItem item, ThumbnailCache thumbnails)
    {
        Item = item;
        _thumbnails = thumbnails;
    }

    public ClipItem Item { get; }

    public ClipKind Kind => Item.Kind;

    // ---- Per-kind visibility flags (cheaper and clearer than converters) ----

    public bool IsText => Item.Kind == ClipKind.Text;

    public bool IsLink => Item.Kind == ClipKind.Link;

    public bool IsImage => Item.Kind == ClipKind.Image;

    public bool IsFiles => Item.Kind == ClipKind.Files;

    public bool IsColor => Item.Kind == ClipKind.Color;

    // ---- Content ------------------------------------------------------------

    public BitmapImage? Thumbnail => Item.Kind == ClipKind.Image
        ? _thumbnails.Get(Item.ThumbFile)
        : null;

    public string Preview => Item.Preview;

    /// <summary>The first few lines of a text clip, for the multi-line card body.</summary>
    public string Body
    {
        get
        {
            if (Item.Text is null)
            {
                return Item.Preview;
            }

            var lines = Item.Text.Replace("\r\n", "\n").Split('\n');

            var kept = lines
                .Take(BodyLineLimit)
                .Select(l => l.TrimEnd())
                .ToList();

            // Drop leading blank lines so the card body starts on real content.
            while (kept.Count > 1 && string.IsNullOrWhiteSpace(kept[0]))
            {
                kept.RemoveAt(0);
            }

            var text = string.Join("\n", kept);
            return lines.Length > BodyLineLimit ? text + "\n…" : text;
        }
    }

    /// <summary>Host name of a link clip, shown as the card's headline.</summary>
    public string Domain
    {
        get
        {
            if (Item.Text is null)
            {
                return "";
            }

            try
            {
                var uri = new Uri(Item.Text.Trim());
                return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                    ? uri.Host[4..]
                    : uri.Host;
            }
            catch
            {
                return Item.Preview;
            }
        }
    }

    /// <summary>Path portion of a link, shown beneath the domain.</summary>
    public string LinkPath
    {
        get
        {
            if (Item.Text is null)
            {
                return "";
            }

            try
            {
                var uri = new Uri(Item.Text.Trim());
                var path = uri.PathAndQuery;
                return path == "/" ? "" : path;
            }
            catch
            {
                return "";
            }
        }
    }

    public IReadOnlyList<string> FileNames
    {
        get
        {
            if (Item.Files is null)
            {
                return Array.Empty<string>();
            }

            return Item.Files
                .Take(4)
                .Select(p =>
                {
                    try
                    {
                        return Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar));
                    }
                    catch
                    {
                        return p;
                    }
                })
                .ToList();
        }
    }

    public int ExtraFileCount => Item.Files is null ? 0 : Math.Max(0, Item.Files.Count - 4);

    /// <summary>Swatch brush for a colour clip, or null when the text will not parse.</summary>
    public Brush? ColorSwatch
    {
        get
        {
            if (Item.Kind != ClipKind.Color || Item.Text is null)
            {
                return null;
            }

            try
            {
                var converted = ColorConverter.ConvertFromString(Item.Text.Trim());
                if (converted is Color color)
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
            }
            catch
            {
                // rgb()/hsl() forms WPF cannot parse fall through to no swatch.
            }

            return null;
        }
    }

    // ---- Metadata line ------------------------------------------------------

    public string KindIconKey => Item.Kind switch
    {
        ClipKind.Link => "Icon.Link",
        ClipKind.Image => "Icon.Image",
        ClipKind.Files => "Icon.Folder",
        ClipKind.Color => "Icon.Color",
        _ => "Icon.Text",
    };

    public string KindLabel => Item.Kind switch
    {
        ClipKind.Link => "Link",
        ClipKind.Image => "Image",
        ClipKind.Files => Item.Files is { Count: > 1 } ? $"{Item.Files.Count} files" : "File",
        ClipKind.Color => "Color",
        _ => Item.LineCount > 1 ? $"{Item.LineCount} lines" : $"{Item.TextLength} chars",
    };

    /// <summary>
    /// Whether "open" means anything for this clip. Text and colour clips have no
    /// external target, so the card hides the action rather than offering a button
    /// that appears to do nothing.
    /// </summary>
    public bool CanOpen => Item.Kind is ClipKind.Link or ClipKind.Files or ClipKind.Image;

    /// <summary>Names what opening will actually do, which differs per kind.</summary>
    public string OpenLabel => Item.Kind switch
    {
        ClipKind.Link => "Open in browser (Alt+O)",
        ClipKind.Files => "Show in Explorer (Alt+O)",
        ClipKind.Image => "Open image (Alt+O)",
        _ => "Open (Alt+O)",
    };

    public string SourceApp => Item.SourceApp ?? "Unknown app";

    /// <summary>Compact relative age, recomputed whenever the stash opens.</summary>
    public string TimeAgo
    {
        get
        {
            var elapsed = DateTimeOffset.Now - Item.Captured;

            if (elapsed < TimeSpan.FromSeconds(45)) return "just now";
            if (elapsed < TimeSpan.FromMinutes(60)) return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed < TimeSpan.FromHours(24)) return $"{(int)elapsed.TotalHours}h ago";
            if (elapsed < TimeSpan.FromDays(7)) return $"{(int)elapsed.TotalDays}d ago";

            return Item.Captured.ToString("d MMM");
        }
    }

    // ---- Favourite state ----------------------------------------------------

    public bool IsFavorite => Item.IsFavorite;

    public bool HasSlot => Item.FavoriteSlot is >= 1 and <= 9;

    public string SlotLabel => HasSlot ? Item.FavoriteSlot.ToString() : "";

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>Re-reads everything derived from the model after it is mutated.</summary>
    public void Refresh()
    {
        Raise(nameof(IsFavorite));
        Raise(nameof(HasSlot));
        Raise(nameof(SlotLabel));
        Raise(nameof(TimeAgo));
        Raise(nameof(KindLabel));
    }
}
