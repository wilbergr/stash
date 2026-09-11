namespace Stash.Models;

/// <summary>What a clipboard entry actually is, which decides how it renders on the stash.</summary>
public enum ClipKind
{
    Text,
    Link,
    Image,
    Files,
    Color,
}

/// <summary>
/// One captured clipboard entry. This is the on-disk shape too, so it is
/// deliberately plain: System.Text.Json serializes it directly.
/// </summary>
public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public ClipKind Kind { get; set; }

    /// <summary>Content hash, used to collapse re-copies of the same thing into one entry.</summary>
    public string Hash { get; set; } = "";

    /// <summary>Short single-line string shown when there is no room for the full payload.</summary>
    public string Preview { get; set; } = "";

    /// <summary>Full text payload for Text/Link/Color kinds. Null for images.</summary>
    public string? Text { get; set; }

    /// <summary>File name (not path) of the full-size PNG under the images folder.</summary>
    public string? ImageFile { get; set; }

    /// <summary>File name (not path) of the downscaled PNG used by the stash.</summary>
    public string? ThumbFile { get; set; }

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    /// <summary>Dropped/copied file paths for the Files kind.</summary>
    public List<string>? Files { get; set; }

    public DateTimeOffset Captured { get; set; } = DateTimeOffset.Now;

    /// <summary>Friendly name of the app that was in the foreground at capture time.</summary>
    public string? SourceApp { get; set; }

    /// <summary>
    /// Starred by the user. Favourites are exempt from both count- and age-based
    /// eviction, and they surface in their own view.
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Quick-slot number 1-9, or 0 for unassigned. A slotted favourite can be pasted
    /// by a global hotkey without opening the stash at all.
    /// </summary>
    public int FavoriteSlot { get; set; }

    /// <summary>How many times this entry has been pasted, for the "Most used" view.</summary>
    public int UseCount { get; set; }

    public DateTimeOffset? LastUsed { get; set; }

    /// <summary>Character count for text payloads, kept so the UI need not measure.</summary>
    public int TextLength { get; set; }

    /// <summary>Line count for text payloads.</summary>
    public int LineCount { get; set; }
}
