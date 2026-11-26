using System.Collections.Concurrent;
using Jellyfin.Plugin.Msfp.Playback;
using Msfp;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class PlaybackTests
{
    private const string JellyfinMedia = "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n" +
        "#EXT-X-MAP:URI=\"hls1/main/-1.mp4?MediaSourceId=6e58&ApiKey=secret\"\n#EXTINF:6.000000, nodesc\nhls1/main/0.mp4?MediaSourceId=6e58&ApiKey=secret&runtimeTicks=0\n" +
        "#EXTINF:4.000000, nodesc\nhls1/main/1.mp4?MediaSourceId=6e58&ApiKey=secret&runtimeTicks=60000000\n#EXT-X-ENDLIST\n";

    [Fact]
    public void PlaylistsAreRewrittenToMediasSubsetAndTheKeyNeverLeaves()
    {
        var names = new ConcurrentDictionary<string, string>();
        var media = Hls.Media(JellyfinMedia, names);
        Assert.Equal("#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n" +
            "#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6.000000,\nseg0.mp4\n#EXTINF:4.000000,\nseg1.mp4\n#EXT-X-ENDLIST\n", media);
        Assert.DoesNotContain("secret", media);
        Assert.StartsWith("hls1/main/-1.mp4?", names["init.mp4"]);
        Assert.StartsWith("hls1/main/1.mp4?", names["seg1.mp4"]);
    }

    [Theory]
    [InlineData("#EXTM3U\nhttps://elsewhere.example/hls1/main/0.ts?x\n")]
    [InlineData("not a playlist\n")]
    [InlineData("#EXTM3U\n#EXTINF:6,\n../../etc/passwd\n")]
    [InlineData("#EXTM3U\n#EXTINF:6,\nhls1/main/0.ts\n")]
    public void APlaylistWithAnotherUriIsRefused(string playlist)
    {
        var names = new ConcurrentDictionary<string, string>();
        var e = Assert.Throws<MsfpException>(() => Hls.Media(playlist, names));
        Assert.Equal(MsfpError.NotFound, e.Code);
    }

    [Fact]
    public void TagsOutsideTheSubsetAreDropped()
    {
        var names = new ConcurrentDictionary<string, string>();
        var media = Hls.Media("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n#EXT-X-PROGRAM-DATE-TIME:2026\n# a comment\n#EXTINF:2.5,title\nhls1/main/0.ts?a=b\n", names);
        Assert.Equal("#EXTM3U\n#EXTINF:2.5,\nseg0.ts\n", media);
    }

    // A segment's duration passes alone, as MSFP's check reads it: its
    // title never, nor, without a comma, what follows the colon; one that
    // isn't a number refuses the playlist; another tag holding an address
    // is dropped (Overfin's own rewrite does the same).
    [Theory]
    [InlineData("#EXTINF:6.000000, nodesc", "#EXTINF:6.000000,")]
    [InlineData("#EXTINF:6", "#EXTINF:6,")]
    [InlineData("#EXTINF:4.5,uri=https://x.example.org", "#EXTINF:4.5,")]
    public void ExtinfDurationOnly(string line, string want)
    {
        var names = new ConcurrentDictionary<string, string>();
        var media = Hls.Media("#EXTM3U\n#EXT-X-VERSION:https://x.example.org\n" + line + "\nhls1/main/0.ts?a=b\n", names);
        Assert.Equal("#EXTM3U\n" + want + "\nseg0.ts\n", media);
    }

    [Theory]
    [InlineData("#EXTINF://")]
    [InlineData("#EXTINF:")]
    [InlineData("#EXTINF:-1,")]
    [InlineData("#EXTINF:1e3,")]
    public void ExtinfNotANumberIsRefused(string line)
    {
        var names = new ConcurrentDictionary<string, string>();
        var e = Assert.Throws<MsfpException>(() => Hls.Media("#EXTM3U\n" + line + "\nhls1/main/0.ts?a=b\n", names));
        Assert.Equal(MsfpError.NotFound, e.Code);
    }

    [Theory]
    [InlineData("/m/Film.mp4", "mp4", "video/mp4")]
    [InlineData("/m/Film.MKV", "mkv", "video/x-matroska")]
    [InlineData("/m/Film.m2ts", "ts", "video/mp2t")]
    [InlineData("/m/Film.avi", "", "application/octet-stream")]
    public void ContainersByExtension(string path, string container, string mime)
    {
        Assert.Equal(container, JellyfinPlayback.ContainerOf(path));
        Assert.Equal(mime, JellyfinPlayback.MimeOf(path));
    }
}
