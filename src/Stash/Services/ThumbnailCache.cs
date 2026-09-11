using System.Windows.Media.Imaging;

namespace Stash.Services;

/// <summary>
/// Decodes card thumbnails once and keeps them frozen and shared.
/// </summary>
/// <remarks>
/// Two details matter for scroll smoothness: DecodePixelWidth means the decoder
/// only ever produces pixels the card can show, and Freeze makes the bitmap
/// immutable so WPF skips per-render locking.
/// </remarks>
public sealed class ThumbnailCache
{
    /// <summary>Widest a card thumbnail is ever drawn, at 200% scaling.</summary>
    private const int DecodeWidth = 560;

    private const int MaxEntries = 256;

    private readonly Dictionary<string, BitmapImage> _cache = new(StringComparer.OrdinalIgnoreCase);

    public BitmapImage? Get(string? thumbFileName)
    {
        if (string.IsNullOrEmpty(thumbFileName))
        {
            return null;
        }

        if (_cache.TryGetValue(thumbFileName, out var cached))
        {
            return cached;
        }

        var path = AppPaths.ImagePath(thumbFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.DecodePixelWidth = DecodeWidth;

            // OnLoad plus a stream we own means the file is not left locked, so the
            // entry can still be deleted while its card is on screen.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();

            if (_cache.Count >= MaxEntries)
            {
                _cache.Clear();
            }

            _cache[thumbFileName] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Thumbnail '{thumbFileName}' could not be decoded.", ex);
            return null;
        }
    }

    public void Forget(string? thumbFileName)
    {
        if (!string.IsNullOrEmpty(thumbFileName))
        {
            _cache.Remove(thumbFileName);
        }
    }

    public void Clear() => _cache.Clear();
}
