namespace Stash.Models;

/// <summary>
/// Which clips a cleanup should remove.
/// </summary>
/// <remarks>
/// Every field narrows the selection, so the default — nothing set — matches
/// every clip that is not a favourite. That makes "remove non-favourites" the
/// natural base case rather than a special one, and it is the same rule the
/// existing Clear History command follows.
/// </remarks>
public sealed record PruneFilter
{
    /// <summary>Only clips captured more than this many days ago. Null for any age.</summary>
    public int? OlderThanDays { get; init; }

    /// <summary>
    /// Favourites are protected unless this is set, matching how the age and
    /// count limits treat them.
    /// </summary>
    public bool IncludeFavorites { get; init; }

    /// <summary>Only one kind of clip. Null for all kinds.</summary>
    public ClipKind? Kind { get; init; }

    /// <summary>Only clips that have never been pasted from the panel.</summary>
    public bool OnlyNeverPasted { get; init; }

    public bool Matches(ClipItem item)
    {
        if (!IncludeFavorites && item.IsFavorite)
        {
            return false;
        }

        if (Kind is { } kind && item.Kind != kind)
        {
            return false;
        }

        if (OnlyNeverPasted && item.UseCount > 0)
        {
            return false;
        }

        if (OlderThanDays is { } days && item.Captured > DateTimeOffset.Now.AddDays(-days))
        {
            return false;
        }

        return true;
    }

    /// <summary>A sentence describing what this will remove, for confirmation.</summary>
    public string Describe()
    {
        var parts = new List<string>(4);

        // "All clips" rather than a bare "clips", so the description still reads
        // as a sentence when no other filter is set.
        parts.Add(Kind switch
        {
            ClipKind.Text => "Text clips",
            ClipKind.Link => "Links",
            ClipKind.Image => "Images",
            ClipKind.Files => "File clips",
            ClipKind.Color => "Colours",
            _ => "All clips",
        });

        if (OlderThanDays is { } days)
        {
            parts.Add($"older than {days} day{(days == 1 ? "" : "s")}");
        }

        if (OnlyNeverPasted)
        {
            parts.Add("never pasted");
        }

        parts.Add(IncludeFavorites ? "including favorites" : "excluding favorites");

        return string.Join(", ", parts);
    }
}
