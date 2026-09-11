using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using Stash.Models;

namespace Stash.Services;

/// <summary>
/// The clipboard history: an in-memory, newest-first list backed by a JSON index
/// plus PNG files on disk. Small enough to keep entirely in memory (the cap is a
/// few hundred entries), which is what makes the stash open instantly.
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;

    /// <summary>Newest first. Bound directly by the stash, so it is UI-thread affine.</summary>
    public ObservableCollection<ClipItem> Items { get; } = new();

    public event Action? Changed;

    public HistoryStore(SettingsStore settings)
    {
        _settings = settings;

        // Coalesce bursts of clipboard activity into one write.
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _saveTimer.Tick += (_, _) => FlushIfDirty();
        _saveTimer.Start();
    }

    // ---- Load / save --------------------------------------------------------

    public void Load()
    {
        AppPaths.EnsureCreated();

        List<ClipItem>? loaded = null;

        try
        {
            if (File.Exists(AppPaths.HistoryFile))
            {
                using var stream = File.OpenRead(AppPaths.HistoryFile);
                loaded = JsonSerializer.Deserialize<List<ClipItem>>(stream, Options);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("History index unreadable; starting with an empty history.", ex);
        }

        Items.Clear();

        if (loaded is not null)
        {
            // Drop entries whose backing image vanished, so cards never render blank.
            foreach (var item in loaded.Where(IsIntact).OrderByDescending(i => i.Captured))
            {
                Items.Add(item);
            }
        }

        Evict();
        PruneOrphanedImages();
        _dirty = true;
    }

    private static bool IsIntact(ClipItem item)
    {
        if (item.Kind != ClipKind.Image)
        {
            return true;
        }

        return item.ThumbFile is not null
            && File.Exists(AppPaths.ImagePath(item.ThumbFile));
    }

    public void FlushIfDirty()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        var snapshot = Items.ToList();

        try
        {
            AppPaths.EnsureCreated();
            var temp = AppPaths.HistoryFile + ".tmp";

            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, snapshot, Options);
            }

            File.Move(temp, AppPaths.HistoryFile, overwrite: true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("History index could not be written.", ex);
        }
    }

    // ---- Mutations ----------------------------------------------------------

    /// <summary>
    /// Adds a capture, or promotes the existing entry when the same content is
    /// copied again. Returns the entry now at the front.
    /// </summary>
    public ClipItem Add(CapturedClip captured)
    {
        var existing = Items.FirstOrDefault(i => i.Hash == captured.Item.Hash);

        if (existing is not null)
        {
            // Re-copying something should move it to the front, not duplicate it,
            // and must preserve the favourite status the user already assigned.
            existing.Captured = captured.Item.Captured;
            existing.SourceApp = captured.Item.SourceApp ?? existing.SourceApp;

            var index = Items.IndexOf(existing);
            if (index > 0)
            {
                Items.Move(index, 0);
            }

            MarkChanged();
            return existing;
        }

        var item = captured.Item;

        if (captured.ImagePng is not null)
        {
            item.ImageFile = WriteImage(item.Id + ".png", captured.ImagePng);

            // Capture hands back the very same array when the image needed no
            // downscaling, so point both at one file instead of duplicating it.
            item.ThumbFile = ReferenceEquals(captured.ThumbnailPng, captured.ImagePng)
                ? item.ImageFile
                : WriteImage(item.Id + ".thumb.png", captured.ThumbnailPng ?? captured.ImagePng);

            if (item.ThumbFile is null)
            {
                // Without a thumbnail the card cannot render, so refuse the entry
                // rather than adding a permanently blank tile — and take the
                // full-size PNG we already wrote back out with it.
                AppPaths.Log("Image capture dropped because its thumbnail could not be saved.");
                DeleteImages(item);
                return item;
            }
        }

        Items.Insert(0, item);
        Evict();
        MarkChanged();
        return item;
    }

    private static string? WriteImage(string fileName, byte[] bytes)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllBytes(AppPaths.ImagePath(fileName), bytes);
            return fileName;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Image '{fileName}' could not be saved.", ex);
            return null;
        }
    }

    public void ToggleFavorite(ClipItem item)
    {
        item.IsFavorite = !item.IsFavorite;

        if (!item.IsFavorite)
        {
            item.FavoriteSlot = 0;
        }

        MarkChanged();
    }

    /// <summary>
    /// Binds an entry to quick slot 1-9, taking the slot from whichever entry held
    /// it before so a slot always maps to exactly one clip. Slot 0 clears.
    /// </summary>
    public void AssignSlot(ClipItem item, int slot)
    {
        if (slot is < 0 or > 9)
        {
            return;
        }

        if (slot > 0)
        {
            foreach (var other in Items.Where(i => i.FavoriteSlot == slot && !ReferenceEquals(i, item)))
            {
                other.FavoriteSlot = 0;
            }

            // A slotted clip is implicitly a favourite; otherwise eviction could
            // silently unbind the hotkey.
            item.IsFavorite = true;
        }

        item.FavoriteSlot = slot;
        MarkChanged();
    }

    public ClipItem? BySlot(int slot)
        => slot is < 1 or > 9 ? null : Items.FirstOrDefault(i => i.FavoriteSlot == slot);

    public void RecordUse(ClipItem item)
    {
        item.UseCount++;
        item.LastUsed = DateTimeOffset.Now;
        MarkChanged();
    }

    public void Remove(ClipItem item)
    {
        Items.Remove(item);
        DeleteImages(item);
        MarkChanged();
    }

    /// <summary>Clears history. Favourites are kept unless <paramref name="includeFavorites"/>.</summary>
    public void Clear(bool includeFavorites)
    {
        foreach (var item in Items.ToList())
        {
            if (item.IsFavorite && !includeFavorites)
            {
                continue;
            }

            Items.Remove(item);
            DeleteImages(item);
        }

        MarkChanged();
    }

    // ---- Eviction -----------------------------------------------------------

    private void Evict()
    {
        var settings = _settings.Current;

        if (settings.RetentionDays > 0)
        {
            var cutoff = DateTimeOffset.Now.AddDays(-settings.RetentionDays);

            foreach (var stale in Items.Where(i => !i.IsFavorite && i.Captured < cutoff).ToList())
            {
                Items.Remove(stale);
                DeleteImages(stale);
            }
        }

        if (settings.MaxItems > 0)
        {
            // Count only evictable entries against the cap; favourites are permanent.
            var evictable = Items.Where(i => !i.IsFavorite).ToList();

            for (var i = settings.MaxItems; i < evictable.Count; i++)
            {
                Items.Remove(evictable[i]);
                DeleteImages(evictable[i]);
            }
        }
    }

    private static void DeleteImages(ClipItem item)
    {
        foreach (var file in new[] { item.ImageFile, item.ThumbFile })
        {
            if (file is null)
            {
                continue;
            }

            try
            {
                var path = AppPaths.ImagePath(file);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                AppPaths.Log($"Image '{file}' could not be deleted.", ex);
            }
        }
    }

    /// <summary>
    /// Removes PNGs with no surviving index entry — the residue of a crash between
    /// writing an image and writing the index.
    /// </summary>
    private void PruneOrphanedImages()
    {
        try
        {
            if (!Directory.Exists(AppPaths.Images))
            {
                return;
            }

            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Items)
            {
                if (item.ImageFile is not null) referenced.Add(item.ImageFile);
                if (item.ThumbFile is not null) referenced.Add(item.ThumbFile);
            }

            foreach (var path in Directory.EnumerateFiles(AppPaths.Images, "*.png"))
            {
                if (!referenced.Contains(Path.GetFileName(path)))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Orphaned image cleanup failed.", ex);
        }
    }

    private void MarkChanged()
    {
        _dirty = true;
        Changed?.Invoke();
    }

    public void Shutdown()
    {
        _saveTimer.Stop();
        FlushIfDirty();
    }
}
