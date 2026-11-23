using System;
using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using SkiaSharp;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>
/// A local title's one image (MSFP catalog section 4.4): Jellyfin's primary
/// image of it, decoded and encoded again as a JPEG of at most 1 MiB and
/// 1,000 by 1,500 pixels (SkiaSharp, the image library Jellyfin ships), so
/// that only pixels leave: no metadata, no other format.
/// </summary>
internal static class LocalImage
{
    /// <summary>The image file read at most.</summary>
    public const long MaxSource = 32 << 20;

    /// <summary>The largest image sent (the engine's bound).</summary>
    public const int MaxBytes = 1 << 20;

    public static byte[]? Of(BaseItem item)
    {
        string? path;
        try
        {
            path = item.GetImagePath(ImageType.Primary, 0);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxSource || info.LinkTarget is not null)
            {
                return null;
            }

            using var data = SKData.Create(path);
            using var codec = data is null ? null : SKCodec.Create(data);
            if (codec is null || codec.Info.Width < 1 || codec.Info.Height < 1 || (long)codec.Info.Width * codec.Info.Height > 64_000_000)
            {
                return null;
            }

            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap is null)
            {
                return null;
            }

            foreach (var (w, h, q) in new[] { (1000, 1500, 85), (600, 900, 75) })
            {
                var scale = Math.Min(1.0, Math.Min((double)w / bitmap.Width, (double)h / bitmap.Height));
                var info2 = new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale)));
                using var resized = bitmap.Resize(info2, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (resized is null)
                {
                    return null;
                }

                using var image = SKImage.FromBitmap(resized);
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, q);
                if (encoded is not null && encoded.Size <= MaxBytes)
                {
                    return encoded.ToArray();
                }
            }

            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or TypeInitializationException)
        {
            return null;
        }
    }
}
