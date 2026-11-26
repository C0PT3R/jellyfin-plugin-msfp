using System;
using System.IO;
using System.Text;
using Jellyfin.Plugin.Msfp.Catalog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// A local title's one image: only pixels leave, as a JPEG within
// 1,000 by 1,500 and 1 MiB (a PNG's text, the file's own format, never);
// a link, a file that isn't there, a path that isn't whole, what isn't an
// image: none.
public sealed class LocalImageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("localimage").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static Movie With(string path)
    {
        var m = new Movie();
        m.SetImage(new ItemImageInfo { Path = path, Type = ImageType.Primary }, 0);
        return m;
    }

    private string Png(string name, int w, int h, string text = "")
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = data.ToArray();
        if (text.Length > 0)
        {
            // A tEXt chunk after the header: metadata a file may carry.
            var chunk = Encoding.Latin1.GetBytes("Comment\0" + text);
            var withText = new MemoryStream();
            withText.Write(bytes, 0, 33);
            var len = BitConverter.GetBytes(chunk.Length);
            Array.Reverse(len);
            withText.Write(len);
            var typed = new byte[4 + chunk.Length];
            Encoding.ASCII.GetBytes("tEXt").CopyTo(typed, 0);
            chunk.CopyTo(typed, 4);
            withText.Write(typed);
            var crc = BitConverter.GetBytes(Crc(typed));
            Array.Reverse(crc);
            withText.Write(crc);
            withText.Write(bytes, 33, bytes.Length - 33);
            bytes = withText.ToArray();
        }

        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static uint Crc(byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in b)
        {
            c ^= x;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
        }

        return c ^ 0xFFFFFFFFu;
    }

    [Fact]
    public void OnlyPixelsLeave()
    {
        var path = Png("poster.png", 2000, 3000, "secret-path /media/films/x.mkv");
        Assert.Contains("secret-path", Encoding.Latin1.GetString(File.ReadAllBytes(path)));
        var out_ = LocalImage.Of(With(path));
        Assert.NotNull(out_);
        Assert.True(out_!.Length <= LocalImage.MaxBytes);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, out_[..3]);
        Assert.DoesNotContain("secret-path", Encoding.Latin1.GetString(out_));
        using var codec = SKCodec.Create(new MemoryStream(out_));
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.Equal((1000, 1500), (codec.Info.Width, codec.Info.Height));

        // A wide one: its width bound, its shape kept; a small one, as it is.
        using var wide = SKCodec.Create(new MemoryStream(LocalImage.Of(With(Png("wide.png", 4000, 1000)))!));
        Assert.Equal((1000, 250), (wide.Info.Width, wide.Info.Height));
        using var small = SKCodec.Create(new MemoryStream(LocalImage.Of(With(Png("small.png", 300, 450)))!));
        Assert.Equal((300, 450), (small.Info.Width, small.Info.Height));
    }

    [Fact]
    public void NoneForWhatIsntAnImageFile()
    {
        var real = Png("real.png", 100, 150);
        var link = Path.Combine(_dir, "link.png");
        File.CreateSymbolicLink(link, real);
        var text = Path.Combine(_dir, "notes.png");
        File.WriteAllText(text, "not an image");
        foreach (var path in new[] { link, Path.Combine(_dir, "gone.png"), "relative/poster.png", text, _dir })
        {
            Assert.Null(LocalImage.Of(With(path)));
        }

        Assert.Null(LocalImage.Of(new Movie()));
    }
}
