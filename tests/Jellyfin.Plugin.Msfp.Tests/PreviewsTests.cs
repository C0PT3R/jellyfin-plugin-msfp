using System.Collections.Generic;
using Jellyfin.Plugin.Msfp.Playback;
using Xunit;
using TrickplayInfo = Jellyfin.Database.Implementations.Entities.TrickplayInfo;

namespace Jellyfin.Plugin.Msfp.Tests;

public class PreviewsTests
{
    private static TrickplayInfo Set(int width, int height = 180, int columns = 10, int rows = 10, int interval = 10_000, int count = 600) => new()
    {
        Width = width, Height = height, TileWidth = columns, TileHeight = rows, Interval = interval, ThumbnailCount = count,
    };

    [Fact]
    public void TheSetClosestTo320WithinBoundsIsChosen()
    {
        var sets = new Dictionary<int, TrickplayInfo> { [160] = Set(160, 90), [320] = Set(320), [480] = Set(480, 270, 8, 8) };
        Assert.Equal(320, Previews.Choose(sets)!.Width);
        Assert.Equal(480, Previews.Choose(new Dictionary<int, TrickplayInfo> { [480] = Set(480, 270, 8, 8), [1280] = Set(1280, 720, 3, 3) })!.Width);
        // 480 with 10 columns is out of bounds: the 1,280 set (3 columns, 3,840 pixels) it is.
        Assert.Equal(1280, Previews.Choose(new Dictionary<int, TrickplayInfo> { [480] = Set(480, 270), [1280] = Set(1280, 720, 3, 3) })!.Width);
        // 10 tiles of 480 across is 4,800 pixels, past MSFP's 4,096: none.
        Assert.Null(Previews.Choose(new Dictionary<int, TrickplayInfo> { [480] = Set(480, 270, 10, 10) }));
        Assert.Null(Previews.Choose(null));
    }

    [Fact]
    public void OutOfBoundsSetsAreLeftOut()
    {
        Assert.True(Previews.InBounds(Set(320)));
        Assert.False(Previews.InBounds(Set(320, 180, 13, 10)));          // 4,160 pixels across
        Assert.False(Previews.InBounds(Set(320, 0)));                    // height not known
        Assert.False(Previews.InBounds(Set(320, interval: 50)));         // under 100 ms
        Assert.False(Previews.InBounds(Set(320, count: 0)));
        Assert.False(Previews.InBounds(Set(320, columns: 1, rows: 1, count: 1001))); // 1,001 sheets
        Assert.True(Previews.InBounds(Set(320, columns: 1, rows: 1, count: 1000)));
    }

    [Fact]
    public void OnlyJpegsAreServed()
    {
        Assert.True(Previews.IsJpeg([0xFF, 0xD8, 0xFF, 0xE0, 0x00]));
        Assert.False(Previews.IsJpeg([0x89, 0x50, 0x4E, 0x47, 0x0D]));
        Assert.False(Previews.IsJpeg([0xFF, 0xD8]));
    }
}
