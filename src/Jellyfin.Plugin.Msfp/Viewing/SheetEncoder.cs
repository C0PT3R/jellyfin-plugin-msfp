using System;
using System.Threading;
using System.Threading.Tasks;
using Msfp;
using SkiaSharp;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// A paired server's preview sheet re-encoded before it reaches anyone
/// (MSFP video section 6.2): its size read from the JPEG's header first,
/// then decoded and encoded again, so that only pixels pass. With
/// SkiaSharp, the image library Jellyfin itself ships.
/// </summary>
public sealed class SheetEncoder : ISheetEncoder
{
    /// <inheritdoc />
    public ValueTask<byte[]> ReencodeAsync(ReadOnlyMemory<byte> jpeg, int maxWidth, int maxHeight, CancellationToken ct)
    {
        var bytes = jpeg.Span;
        if (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
        {
            throw new MsfpException(MsfpError.NotAnImage, "not a JPEG");
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new MsfpException(MsfpError.NotAnImage, "unreadable");
        if (codec.EncodedFormat != SKEncodedImageFormat.Jpeg || codec.Info.Width < 1 || codec.Info.Height < 1
            || codec.Info.Width > maxWidth || codec.Info.Height > maxHeight)
        {
            throw new MsfpException(MsfpError.NotAnImage, "not a JPEG within the size");
        }

        using var bitmap = SKBitmap.Decode(codec) ?? throw new MsfpException(MsfpError.NotAnImage, "undecodable");
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85) ?? throw new MsfpException(MsfpError.NotAnImage, "not encoded");
        return ValueTask.FromResult(encoded.ToArray());
    }
}
