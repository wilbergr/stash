namespace Stash.Models;

/// <summary>The filter chips across the stash header.</summary>
public enum StashView
{
    Recents,
    Favorites,
    Text,
    Links,
    Images,
    Files,
    MostUsed,
}

public static class StashViewExtensions
{
    /// <summary>Order the chips appear in.</summary>
    public static readonly StashView[] All =
    {
        StashView.Recents,
        StashView.Favorites,
        StashView.Text,
        StashView.Links,
        StashView.Images,
        StashView.Files,
        StashView.MostUsed,
    };

    public static string Label(this StashView view) => view switch
    {
        StashView.Recents => "Recents",
        StashView.Favorites => "Favorites",
        StashView.Text => "Text",
        StashView.Links => "Links",
        StashView.Images => "Images",
        StashView.Files => "Files",
        StashView.MostUsed => "Most used",
        _ => view.ToString(),
    };

    /// <summary>
    /// Resource key of the vector geometry for this chip. Stash draws its own icons
    /// rather than relying on Segoe Fluent Icons, whose codepoints move between
    /// Windows releases and whose presence is not guaranteed.
    /// </summary>
    public static string IconKey(this StashView view) => view switch
    {
        StashView.Recents => "Icon.History",
        StashView.Favorites => "Icon.Star",
        StashView.Text => "Icon.Text",
        StashView.Links => "Icon.Link",
        StashView.Images => "Icon.Image",
        StashView.Files => "Icon.Folder",
        StashView.MostUsed => "Icon.Trending",
        _ => "Icon.History",
    };

    /// <summary>Whether an entry belongs in this view.</summary>
    public static bool Matches(this StashView view, ClipItem item) => view switch
    {
        StashView.Recents => true,
        StashView.Favorites => item.IsFavorite,
        StashView.Text => item.Kind is ClipKind.Text or ClipKind.Color,
        StashView.Links => item.Kind == ClipKind.Link,
        StashView.Images => item.Kind == ClipKind.Image,
        StashView.Files => item.Kind == ClipKind.Files,
        StashView.MostUsed => item.UseCount > 0,
        _ => true,
    };

    public static StashView Parse(string? text)
        => Enum.TryParse<StashView>(text, ignoreCase: true, out var view) ? view : StashView.Recents;
}
