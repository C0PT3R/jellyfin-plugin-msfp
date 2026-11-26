using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Viewing;
using Msfp;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// A paired server's preview sheet before it reaches anyone (MSFP video
// section 6.2): a JPEG within the size, encoded again so that only its
// pixels pass (a comment it carried never); anything else, not an image.
public class SheetEncoderTests
{
    private static byte[] Encode(SKEncodedImageFormat format, int w, int h)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(SKColors.Orange);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    // A JPEG comment segment (COM) after SOI: what a sheet may smuggle.
    private static byte[] WithComment(byte[] jpeg, string text)
    {
        var body = Encoding.ASCII.GetBytes(text);
        var len = body.Length + 2;
        return [.. jpeg[..2], 0xFF, 0xFE, (byte)(len >> 8), (byte)len, .. body, .. jpeg[2..]];
    }

    [Fact]
    public async Task OnlyPixelsPass()
    {
        var sheet = WithComment(Encode(SKEncodedImageFormat.Jpeg, 320, 180), "tracker key 0123456789");
        Assert.Contains("tracker key", Encoding.ASCII.GetString(sheet));
        var out_ = await new SheetEncoder().ReencodeAsync(sheet, 320, 180, default);
        Assert.DoesNotContain("tracker key", Encoding.ASCII.GetString(out_));
        using var codec = SKCodec.Create(new MemoryStream(out_));
        Assert.Equal((SKEncodedImageFormat.Jpeg, 320, 180), (codec.EncodedFormat, codec.Info.Width, codec.Info.Height));
    }

    [Fact]
    public async Task AnythingElseIsNotAnImage()
    {
        var jpeg = Encode(SKEncodedImageFormat.Jpeg, 320, 180);
        foreach (var (bytes, w, h) in new[]
        {
            (jpeg, 319, 180), // wider than allowed
            (jpeg, 320, 179),
            (Encode(SKEncodedImageFormat.Png, 32, 32), 320, 180),
            (new byte[] { 0xFF, 0xD8, 0xFF, 0, 1, 2 }, 320, 180), // a JPEG's start only
            (new byte[] { 0xFF, 0xD8 }, 320, 180),
            (Array.Empty<byte>(), 320, 180),
            (Encoding.ASCII.GetBytes("<svg/>"), 320, 180),
        })
        {
            var e = await Assert.ThrowsAsync<MsfpException>(() => new SheetEncoder().ReencodeAsync(bytes, w, h, default).AsTask());
            Assert.Equal(MsfpError.NotAnImage, e.Code);
        }
    }
}
