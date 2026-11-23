using System.Text;
using Jellyfin.Plugin.Msfp.Playback;
using Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// The master playlist written for a delivery, and the check of what its
// first output holds.
public class HlsMasterTests
{
    private static Copy Dv(int profile, int compatibility) => new("v", 60_000, "video/x-matroska", 600_000_000, "e", [new CopyAudio("a1", "eac3") { Channels = 6, Codecs = "ec-3" }])
    {
        Video = new VideoStream("hevc", 3840, 2160) { Codecs = "hev1.2.4.L153.B0", BitDepth = 10, Colour = new VideoColour(9, 16, 9), Dv = new VideoDv(profile, 6, compatibility) },
    };

    private static DeliveryOrder Remux(string segments) => new()
    {
        Segments = segments,
        Video = new DeliveryVideo { Copy = true },
        Audio = new DeliveryAudio { Index = 1, Copy = true },
    };

    private static string Text(byte[] b) => Encoding.UTF8.GetString(b);

    [Fact]
    public void DolbyVisionProfile8KeepsHvc1WithSupplementalCodecs()
    {
        var m = Text(HlsMaster.Of(Dv(8, 1), "a1", Remux("mp4"), null));
        Assert.Contains("CODECS=\"hvc1.2.4.L153.B0,ec-3\"", m, System.StringComparison.Ordinal);
        Assert.Contains("SUPPLEMENTAL-CODECS=\"dvh1.08.06/db1p\"", m, System.StringComparison.Ordinal);
        Assert.Contains("VIDEO-RANGE=PQ", m, System.StringComparison.Ordinal);
        Assert.Contains("RESOLUTION=3840x2160", m, System.StringComparison.Ordinal);
        Assert.Contains("BANDWIDTH=80000000", m, System.StringComparison.Ordinal);
        Assert.StartsWith("#EXTM3U\n#EXT-X-VERSION:7\n", m, System.StringComparison.Ordinal);
        Assert.EndsWith("\nv0.m3u8\n", m, System.StringComparison.Ordinal);
        // In MPEG-TS, no Dolby Vision entry is announced.
        Assert.DoesNotContain("SUPPLEMENTAL", Text(HlsMaster.Of(Dv(8, 1), "a1", Remux("ts"), null)), System.StringComparison.Ordinal);
        // MSFP's subset of tags (media section 3.1).
        global::Msfp.Protocol.Media.FilterPlaylist(HlsMaster.Of(Dv(8, 1), "a1", Remux("mp4"), null), global::Msfp.Protocol.Media.MasterSubset, _ => true);
    }

    [Fact]
    public void DolbyVisionProfile5IsItsOwnEntry()
    {
        var m = Text(HlsMaster.Of(Dv(5, 0), "a1", Remux("mp4"), null));
        Assert.Contains("CODECS=\"dvh1.05.06,ec-3\"", m, System.StringComparison.Ordinal);
        Assert.DoesNotContain("SUPPLEMENTAL", m, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ABaselineFromWhatWasMade()
    {
        var c = Dv(8, 1);
        var o = new DeliveryOrder
        {
            Segments = "ts",
            Video = new DeliveryVideo { Codec = "h264", MaxHeight = 1080, MaxBitrate = 16_000_000, ToneMap = true, Profile = "high", Level = "41" },
            Audio = new DeliveryAudio { Index = 1, Codec = "aac", Channels = 2 },
        };
        var made = new DeliveryMade("x", Made.Parse("-codec:v:0 libx264 -maxrate 8000000 -profile:v:0 high -level 41 tonemapx -codec:a:0 libfdk_aac -ac 2"), 1920, 1080, string.Empty);
        var m = Text(HlsMaster.Of(c, "a1", o, made));
        Assert.Contains("CODECS=\"avc1.640029,mp4a.40.2\"", m, System.StringComparison.Ordinal);
        Assert.Contains("RESOLUTION=1920x1080", m, System.StringComparison.Ordinal);
        Assert.Contains("VIDEO-RANGE=SDR", m, System.StringComparison.Ordinal);
        Assert.Contains("BANDWIDTH=8192000", m, System.StringComparison.Ordinal);
        Assert.DoesNotContain("SUPPLEMENTAL", m, System.StringComparison.Ordinal);
        // What the output holds wins over what is foreseen.
        Assert.Contains("CODECS=\"avc1.640028,mp4a.40.2\"", Text(HlsMaster.Of(c, "a1", o, made, ("avc1.640028", null))), System.StringComparison.Ordinal);
    }

    [Fact]
    public void OutputCheckedAgainstTheOrder()
    {
        var source = Dv(8, 1);
        var o = new DeliveryOrder
        {
            Segments = "ts",
            Video = new DeliveryVideo { Codec = "h264", MaxHeight = 1080, MaxBitrate = 16_000_000, ToneMap = true, Profile = "high", Level = "41", PixelFormat = "yuv420p", Deinterlace = true },
            Audio = new DeliveryAudio { Index = 1, Codec = "aac", Channels = 2, MaxSampleRate = 48_000 },
        };
        Copy Out(string codecs = "avc1.640029", int height = 1080, int depth = 8, int transfer = 1, string audio = "mp4a.40.2", int channels = 2, int rate = 48000) =>
            new("o", 6000, "video/mp2t", 1, "e", [new CopyAudio("a1", "aac") { Codecs = audio, Channels = channels, SampleRate = rate }])
            {
                Video = new VideoStream("h264", height * 16 / 9, height) { Codecs = codecs, BitDepth = depth, Colour = new VideoColour(1, transfer, 1) },
            };
        Assert.Empty(OutputCheck.Differences(o, Out(), source, "a1"));
        Assert.Contains("out-profile-level", OutputCheck.Differences(o, Out("avc1.640032"), source, "a1"));
        Assert.Contains("out-profile-level", OutputCheck.Differences(o, Out("avc1.6E0029"), source, "a1"));
        Assert.Contains("out-height", OutputCheck.Differences(o, Out(height: 2160), source, "a1"));
        Assert.Contains("out-bit-depth", OutputCheck.Differences(o, Out(depth: 10), source, "a1"));
        Assert.Contains("out-hdr", OutputCheck.Differences(o, Out(transfer: 16), source, "a1"));
        Assert.Contains("out-aac-profile", OutputCheck.Differences(o, Out(audio: "mp4a.40.5"), source, "a1"));
        Assert.Contains("out-audio-channels", OutputCheck.Differences(o, Out(channels: 6), source, "a1"));
        Assert.Contains("out-sample-rate", OutputCheck.Differences(o, Out(rate: 96000), source, "a1"));
        Assert.Equal(("avc1.640029", "mp4a.40.2"), OutputCheck.Codecs(o, Out()));
        Assert.Equal(("avc1.640029", "mp4a.40.2"), OutputCheck.Codecs(o, Out("avc3.640029")));
    }
}
