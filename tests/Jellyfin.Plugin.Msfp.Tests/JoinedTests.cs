using Jellyfin.Plugin.Msfp.Playback;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class JoinedTests
{
    [Fact]
    public void ThePlaylistKeepsMsfpsSubset()
    {
        var ffmpeg = "#EXTM3U\r\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:EVENT\n"
            + "#EXT-X-MAP:URI=\"init.mp4\"\n#EXT-X-PROGRAM-DATE-TIME:2026-11-20T00:00:00Z\n#EXTINF:6.000000,\nseg0.m4s\n# a comment\n#EXTINF:4.5,\nseg1.m4s\n../escape.m4s\n";
        Assert.Equal(
            "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:EVENT\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6.000000,\nseg0.m4s\n#EXTINF:4.5,\nseg1.m4s\n",
            Joined.Subset(ffmpeg));
    }

    [Theory]
    [InlineData("seg0.ts", true)]
    [InlineData("seg123.m4s", true)]
    [InlineData("init.mp4", true)]
    [InlineData("seg.ts", false)]
    [InlineData("seg1.mp4", false)]
    [InlineData("parts.txt", false)]
    [InlineData("main.m3u8", false)]
    [InlineData("seg1234567.ts", false)]
    public void OnlyItsSegmentsAreServed(string name, bool segment) => Assert.Equal(segment, Joined.IsSegment(name));
}
