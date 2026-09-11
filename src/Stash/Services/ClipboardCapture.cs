using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Stash.Interop;
using Stash.Models;

namespace Stash.Services;

/// <summary>A freshly read clipboard entry plus the image bytes still to be written to disk.</summary>
public sealed record CapturedClip(ClipItem Item, byte[]? ImagePng, byte[]? ThumbnailPng);

/// <summary>
/// Reads the current clipboard and turns it into a <see cref="ClipItem"/>.
/// </summary>
public static partial class ClipboardCapture
{
    /// <summary>
    /// Formats that applications use to opt a clipboard entry out of history.
    /// Password managers set these, and honouring them is the whole reason a
    /// clipboard manager is safe to leave running.
    /// </summary>
    private const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";

    /// <summary>Thumbnails are generated to fit this box, which covers the card size at 200% DPI.</summary>
    private const int ThumbMaxWidth = 560;
    private const int ThumbMaxHeight = 360;

    private const int PreviewMaxChars = 320;

    [GeneratedRegex(@"^\s*(?:https?|ftps?)://[^\s]+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^\s*(?:#(?:[0-9a-f]{3}|[0-9a-f]{4}|[0-9a-f]{6}|[0-9a-f]{8})|(?:rgb|rgba|hsl|hsla)\([^)]*\))\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>
    /// Attempts to read the clipboard. Returns false when there is nothing worth
    /// storing, when the content is marked sensitive, or when the clipboard could
    /// not be opened.
    /// </summary>
    /// <remarks>
    /// Must be called on an STA thread (the WPF UI thread qualifies).
    /// </remarks>
    public static bool TryCapture(
        AppSettings settings,
        AppIdentity source,
        [NotNullWhen(true)] out CapturedClip? captured,
        out string? skipReason)
    {
        captured = null;
        skipReason = null;

        if (IsIgnoredApp(settings, source))
        {
            skipReason = $"source app '{source.ProcessName}' is on the ignore list";
            return false;
        }

        if (!TryGetDataObject(out var data) || data is null)
        {
            skipReason = "clipboard could not be opened";
            return false;
        }

        if (settings.RespectSensitiveClipboardFlags && IsMarkedSensitive(data))
        {
            skipReason = "content is flagged as sensitive by the source application";
            return false;
        }

        // Order matters: a file copy also exposes text, and a browser image copy
        // also exposes HTML. Most specific format wins.
        if (TryCaptureFiles(data, source, out captured))
        {
            return true;
        }

        if (settings.CaptureImages && TryCaptureImage(data, settings, source, out captured, out skipReason))
        {
            return true;
        }

        if (skipReason is not null)
        {
            return false;
        }

        if (TryCaptureText(data, source, out captured))
        {
            return true;
        }

        skipReason = "no supported clipboard format present";
        return false;
    }

    private static bool IsIgnoredApp(AppSettings settings, AppIdentity source)
    {
        if (string.IsNullOrWhiteSpace(source.ProcessName) || settings.IgnoredApps.Count == 0)
        {
            return false;
        }

        return settings.IgnoredApps.Any(ignored =>
            !string.IsNullOrWhiteSpace(ignored)
            && source.ProcessName.Contains(ignored, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The clipboard is a shared, singly-locked resource: whichever app just wrote
    /// to it may still hold it open when our notification arrives. Retrying briefly
    /// turns a common transient failure into a successful read.
    /// </summary>
    private static bool TryGetDataObject(out IDataObject? data)
    {
        const int attempts = 6;

        for (var i = 0; i < attempts; i++)
        {
            try
            {
                data = Clipboard.GetDataObject();
                if (data is not null)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException)
            {
                // Another process holds the clipboard open. Back off and retry.
            }
            catch (Exception ex)
            {
                AppPaths.Log("Unexpected failure reading the clipboard.", ex);
                break;
            }

            Thread.Sleep(25 + (i * 15));
        }

        data = null;
        return false;
    }

    private static bool IsMarkedSensitive(IDataObject data)
    {
        try
        {
            // Presence alone means "do not process this in a clipboard monitor".
            if (data.GetDataPresent(ExcludeFromMonitoring))
            {
                return true;
            }

            if (data.GetDataPresent(CanIncludeInHistory))
            {
                // A DWORD of 0 is an explicit opt-out. If it cannot be read, assume
                // opt-out: guessing wrong in the other direction stores a secret.
                return ReadDword(data, CanIncludeInHistory) is not 1;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Could not evaluate clipboard sensitivity flags; treating as sensitive.", ex);
            return true;
        }

        return false;
    }

    private static int? ReadDword(IDataObject data, string format)
    {
        try
        {
            if (data.GetData(format) is MemoryStream stream)
            {
                Span<byte> buffer = stackalloc byte[4];
                stream.Position = 0;
                if (stream.Read(buffer) == 4)
                {
                    return BitConverter.ToInt32(buffer);
                }
            }
        }
        catch
        {
            // Fall through to null: caller treats unknown as opt-out.
        }

        return null;
    }

    // ---- Files --------------------------------------------------------------

    private static bool TryCaptureFiles(
        IDataObject data,
        AppIdentity source,
        [NotNullWhen(true)] out CapturedClip? captured)
    {
        captured = null;

        if (!data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return false;
        }

        var names = paths.Select(p =>
        {
            try
            {
                return Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar));
            }
            catch
            {
                return p;
            }
        }).Where(n => !string.IsNullOrEmpty(n)).ToList();

        var preview = names.Count == 1
            ? names[0]
            : $"{names[0]} + {names.Count - 1} more";

        var item = new ClipItem
        {
            Kind = ClipKind.Files,
            Files = paths.ToList(),
            Preview = preview,
            Text = string.Join(Environment.NewLine, paths),
            Hash = HashOf("files:" + string.Join("|", paths)),
            SourceApp = source.FriendlyName,
            LineCount = paths.Length,
        };

        captured = new CapturedClip(item, null, null);
        return true;
    }

    // ---- Images -------------------------------------------------------------

    private static bool TryCaptureImage(
        IDataObject data,
        AppSettings settings,
        AppIdentity source,
        [NotNullWhen(true)] out CapturedClip? captured,
        out string? skipReason)
    {
        captured = null;
        skipReason = null;

        var bitmap = ReadBitmap(data);
        if (bitmap is null)
        {
            return false;
        }

        try
        {
            var png = EncodePng(bitmap);

            if (png.LongLength > settings.MaxImageBytes)
            {
                skipReason = $"image is {png.LongLength / (1024 * 1024)} MB, above the {settings.MaxImageBytes / (1024 * 1024)} MB limit";
                return false;
            }

            // An image already smaller than the thumbnail box comes back unchanged,
            // in which case reuse the encoded bytes rather than producing an
            // identical second copy for the store to write twice.
            var scaled = Downscale(bitmap, ThumbMaxWidth, ThumbMaxHeight);
            var thumb = ReferenceEquals(scaled, bitmap) ? png : EncodePng(scaled);

            var item = new ClipItem
            {
                Kind = ClipKind.Image,
                ImageWidth = bitmap.PixelWidth,
                ImageHeight = bitmap.PixelHeight,
                Preview = $"{bitmap.PixelWidth} x {bitmap.PixelHeight}",
                Hash = HashOf(png),
                SourceApp = source.FriendlyName,
            };

            captured = new CapturedClip(item, png, thumb);
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Clipboard image could not be encoded.", ex);
            skipReason = "image could not be decoded";
            return false;
        }
    }

    /// <summary>
    /// Prefers the "PNG" clipboard format when present. Browsers and design tools
    /// publish it, and unlike the legacy DIB path it preserves transparency
    /// instead of compositing alpha onto black.
    /// </summary>
    private static BitmapSource? ReadBitmap(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream pngStream)
            {
                pngStream.Position = 0;
                var decoder = new PngBitmapDecoder(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 0)
                {
                    var frame = decoder.Frames[0];
                    frame.Freeze();
                    return frame;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("PNG clipboard format present but unreadable; falling back to DIB.", ex);
        }

        try
        {
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource source)
            {
                // Copy into a frozen, cached bitmap so it can cross threads.
                var cached = new CachedBitmap(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                cached.Freeze();
                return cached;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Bitmap clipboard format could not be read.", ex);
        }

        return null;
    }

    private static BitmapSource Downscale(BitmapSource source, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(
            maxWidth / (double)source.PixelWidth,
            maxHeight / (double)source.PixelHeight);

        if (scale >= 1.0)
        {
            return source;
        }

        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    // ---- Text ---------------------------------------------------------------

    private static bool TryCaptureText(
        IDataObject data,
        AppIdentity source,
        [NotNullWhen(true)] out CapturedClip? captured)
    {
        captured = null;

        string? text = null;

        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                text = data.GetData(DataFormats.UnicodeText) as string;
            }
            else if (data.GetDataPresent(DataFormats.Text))
            {
                text = data.GetData(DataFormats.Text) as string;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Clipboard text could not be read.", ex);
            return false;
        }

        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var kind = ClassifyText(text);

        var item = new ClipItem
        {
            Kind = kind,
            Text = text,
            Preview = BuildPreview(text),
            Hash = HashOf("text:" + text),
            SourceApp = source.FriendlyName,
            TextLength = text.Length,
            LineCount = CountLines(text),
        };

        captured = new CapturedClip(item, null, null);
        return true;
    }

    private static ClipKind ClassifyText(string text)
    {
        // Only single-line payloads are considered; a URL buried in a paragraph
        // is still a paragraph.
        if (text.Length <= 2048 && !text.AsSpan().ContainsAny('\r', '\n'))
        {
            if (UrlPattern().IsMatch(text))
            {
                return ClipKind.Link;
            }

            if (ColorPattern().IsMatch(text))
            {
                return ClipKind.Color;
            }
        }

        return ClipKind.Text;
    }

    private static string BuildPreview(string text)
    {
        var trimmed = text.Trim();

        var firstLineEnd = trimmed.AsSpan().IndexOfAny('\r', '\n');
        var firstLine = firstLineEnd >= 0 ? trimmed[..firstLineEnd] : trimmed;

        var collapsed = Whitespace().Replace(firstLine, " ").Trim();

        if (collapsed.Length == 0)
        {
            collapsed = Whitespace().Replace(trimmed, " ").Trim();
        }

        return collapsed.Length > PreviewMaxChars
            ? collapsed[..PreviewMaxChars] + "…"
            : collapsed;
    }

    private static int CountLines(string text)
    {
        var lines = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
        }
        return lines;
    }

    // ---- Hashing ------------------------------------------------------------

    private static string HashOf(string value) => HashOf(Encoding.UTF8.GetBytes(value));

    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
