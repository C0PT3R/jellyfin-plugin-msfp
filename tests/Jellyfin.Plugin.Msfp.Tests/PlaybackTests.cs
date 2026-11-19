using System.Collections.Concurrent;
using System.Linq;
using Jellyfin.Plugin.Msfp.Playback;
using MediaBrowser.Model.Entities;
using Msfp;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class PlaybackTests
{
    private static readonly MediaStream H264 = new() { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, Index = 0 };
    private static readonly MediaStream Aac = new() { Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, Index = 1, IsDefault = true };
    private static readonly MediaStream Ac3 = new() { Type = MediaStreamType.Audio, Codec = "ac3", Channels = 6, Index = 2 };

    private static FileFacts File(string container, MediaStream video, int kbps = 8000, params MediaStream[] audio) => new()
    {
        Container = container,
        MimeType = "video/" + container,
        Video = video,
        Picture = Catalog.JellyfinCatalogSource.PictureOf(video),
        AudioTracks = audio.Length > 0 ? audio : [Aac],
        Audio = (audio.Length > 0 ? audio : [Aac]).FirstOrDefault(a => a.IsDefault) ?? (audio.Length > 0 ? audio[0] : Aac),
        BitrateKbps = kbps,
    };

    private static StartRequest Ask(Capabilities caps, string audioTrack = "", int maxKbps = 120_000) =>
        new() { Capabilities = caps, AudioTrack = audioTrack, MaxBitrateKbps = maxKbps };

    private static readonly Capabilities Browser = new() { Containers = ["mp4", "webm"], VideoCodecs = ["h264", "vp9"], AudioCodecs = ["aac", "opus"] };

    [Fact]
    public void DirectWhenTheDevicePlaysTheFileAsItIs()
    {
        Assert.True(Direct.Plays(File("mp4", H264), Ask(Browser)));
        Assert.False(Direct.Plays(File("mkv", H264), Ask(Browser)));
        Assert.False(Direct.Plays(File("mp4", new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Width = 1920, Height = 1080 }), Ask(Browser)));
        // Nothing said about containers or video codecs: never direct (video 5.1).
        Assert.False(Direct.Plays(File("mp4", H264), Ask(new Capabilities())));
    }

    [Fact]
    public void DirectTakesTheAudioChosenItsChannelsHdrHeightAndBitrateIntoAccount()
    {
        var both = File("mp4", H264, 8000, Aac, Ac3);
        Assert.True(Direct.Plays(both, Ask(Browser, "a1")));
        Assert.False(Direct.Plays(both, Ask(Browser, "a2")));
        // Nothing said about audio codecs: AAC only.
        Assert.True(Direct.Plays(File("mp4", H264), Ask(Browser with { AudioCodecs = null })));
        Assert.False(Direct.Plays(File("mp4", H264, 8000, Ac3), Ask(Browser with { AudioCodecs = null })));
        Assert.False(Direct.Plays(File("mp4", H264), Ask(Browser with { MaxAudioChannels = 1 })));
        Assert.False(Direct.Plays(File("mp4", H264), Ask(Browser with { MaxHeight = 720 })));
        Assert.True(Direct.Plays(File("mp4", H264), Ask(Browser with { MaxHeight = 1080 })));
        Assert.False(Direct.Plays(File("mp4", H264, 8000), Ask(Browser, maxKbps: 4000)));
        Assert.False(Direct.Plays(File("mp4", H264, 8000), Ask(Browser with { MaxBitrateKbps = 4000 })));
        var hdr = new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, BitDepth = 10, ColorTransfer = "smpte2084", ColorPrimaries = "bt2020" };
        Assert.False(Direct.Plays(File("mp4", hdr), Ask(Browser)));
        Assert.True(Direct.Plays(File("mp4", hdr), Ask(Browser with { Hdr = ["hdr10"] })));
    }

    [Fact]
    public void HlsRemuxesWhatTheDevicePlaysElseTranscodesToH264()
    {
        var remux = Hls.Choose(File("mkv", H264), Aac, Ask(Browser));
        Assert.Equal(("remux", "h264", "aac", "ts"), (remux.Method, remux.VideoCodec, remux.AudioCodec, remux.Segments));
        var hevc = new MediaStream { Type = MediaStreamType.Video, Codec = "hevc", Width = 3840, Height = 2160 };
        var transcode = Hls.Choose(File("mkv", hevc, 40_000, Ac3), Ac3, Ask(Browser));
        Assert.Equal(("transcode", "h264", "aac", "ts", 2160), (transcode.Method, transcode.VideoCodec, transcode.AudioCodec, transcode.Segments, transcode.MaxHeight));
        var hevcDevice = Hls.Choose(File("mkv", hevc, 40_000, Ac3), Ac3, Ask(Browser with { VideoCodecs = ["hevc"], AudioCodecs = ["ac3"], MaxAudioChannels = 6 }));
        Assert.Equal(("remux", "hevc", "ac3", "mp4"), (hevcDevice.Method, hevcDevice.VideoCodec, hevcDevice.AudioCodec, hevcDevice.Segments));
        // Nothing said: H.264 and AAC; a smaller screen or a cap transcodes.
        Assert.Equal("remux", Hls.Choose(File("mkv", H264), Aac, Ask(new Capabilities())).Method);
        var small = Hls.Choose(File("mkv", H264), Aac, Ask(Browser with { MaxHeight = 720 }));
        Assert.Equal(("transcode", 720), (small.Method, small.MaxHeight));
        var capped = Hls.Choose(File("mkv", H264, 8000), Aac, Ask(Browser, maxKbps: 3000));
        Assert.Equal("transcode", capped.Method);
        Assert.True(capped.VideoBitrate <= 3_000_000);
    }

    private const string JellyfinMaster = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=592872,AVERAGE-BANDWIDTH=592872,VIDEO-RANGE=SDR,CODECS=\"avc1.64001E,mp4a.40.2\",RESOLUTION=640x360,FRAME-RATE=25\nmain.m3u8?MediaSourceId=6e58&DeviceId=msfp-x&ApiKey=secret\n";

    private const string JellyfinMedia = "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n" +
        "#EXT-X-MAP:URI=\"hls1/main/-1.mp4?MediaSourceId=6e58&ApiKey=secret\"\n#EXTINF:6.000000, nodesc\nhls1/main/0.mp4?MediaSourceId=6e58&ApiKey=secret&runtimeTicks=0\n" +
        "#EXTINF:4.000000, nodesc\nhls1/main/1.mp4?MediaSourceId=6e58&ApiKey=secret&runtimeTicks=60000000\n#EXT-X-ENDLIST\n";

    [Fact]
    public void PlaylistsAreRewrittenToMediasSubsetAndTheKeyNeverLeaves()
    {
        var names = new ConcurrentDictionary<string, string>();
        var master = Hls.Master(JellyfinMaster, names);
        Assert.Equal("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=592872,AVERAGE-BANDWIDTH=592872,VIDEO-RANGE=SDR,CODECS=\"avc1.64001E,mp4a.40.2\",RESOLUTION=640x360,FRAME-RATE=25\nv0.m3u8\n", master);
        Assert.StartsWith("main.m3u8?", names["v0.m3u8"]);
        var media = Hls.Media(JellyfinMedia, names);
        Assert.Equal("#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n" +
            "#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6.000000,\nseg0.mp4\n#EXTINF:4.000000,\nseg1.mp4\n#EXT-X-ENDLIST\n", media);
        Assert.DoesNotContain("secret", master + media);
        Assert.StartsWith("hls1/main/-1.mp4?", names["init.mp4"]);
        Assert.StartsWith("hls1/main/1.mp4?", names["seg1.mp4"]);
    }

    [Theory]
    [InlineData("#EXTM3U\nhttps://elsewhere.example/main.m3u8?x\n", true)]
    [InlineData("#EXTM3U\n/Videos/1/main.m3u8?x\n", true)]
    [InlineData("not a playlist\n", true)]
    [InlineData("#EXTM3U\n#EXTINF:6,\n../../etc/passwd\n", false)]
    [InlineData("#EXTM3U\n#EXTINF:6,\nhls1/main/0.ts\n", false)]
    public void APlaylistWithAnotherUriIsRefused(string playlist, bool master)
    {
        var names = new ConcurrentDictionary<string, string>();
        var e = Assert.Throws<MsfpException>(() => master ? Hls.Master(playlist, names) : Hls.Media(playlist, names));
        Assert.Equal(MsfpError.NotFound, e.Code);
    }

    [Fact]
    public void TagsOutsideTheSubsetAreDropped()
    {
        var names = new ConcurrentDictionary<string, string>();
        var media = Hls.Media("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\"\n#EXT-X-PROGRAM-DATE-TIME:2026\n# a comment\n#EXTINF:2.5,title\nhls1/main/0.ts?a=b\n", names);
        Assert.Equal("#EXTM3U\n#EXTINF:2.5,\nseg0.ts\n", media);
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
