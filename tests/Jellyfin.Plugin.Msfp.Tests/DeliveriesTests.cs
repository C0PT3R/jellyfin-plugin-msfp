using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Msfp.Playback;
using Msfp;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// The checked deliveries ported from the companion plugin (0.9.0), with
// the baseline's options: what Jellyfin's ffmpeg command line says it
// makes, checked against the order.
public class DeliveriesTests
{
    // Jellyfin 12.2's command lines, as the delivery spikes read them (the
    // file's path replaced).
    private const string H264At720 = "-analyzeduration 200M -probesize 1G  -i file:\"/media/x.mkv\" -map_metadata -1 -map_chapters -1 -threads 0 -map 0:0 -map 0:1 -map -0:s -codec:v:0 libx264 -preset veryfast -crf 23 -maxrate 3000000 -bufsize 6000000 -x264opts:0 subme=0:me_range=16:rc_lookahead=10:me=hex:open_gop=0 -force_key_frames:0 \"expr:gte(t,n_forced*6)\" -sc_threshold:v:0 0 -vf \"setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,scale=trunc(min(max(iw\\,ih*a)\\,min(1280\\,720*a))/2)*2:trunc(min(max(iw/a\\,ih)\\,min(1280/a\\,720))/2)*2,format=yuv420p\" -codec:a:0 libfdk_aac -ac 2 -ab 192000 -af \"volume=2\" -copyts -avoid_negative_ts disabled -max_muxing_queue_size 2048 -f hls -max_delay 5000000 -hls_time 6 -hls_segment_type mpegts -start_number 0 -hls_segment_filename \"/cache/transcodes/8ebc5657beaabe475197e57c855191b2%d.ts\" -hls_playlist_type vod -hls_list_size 0 -y \"/cache/transcodes/8ebc5657beaabe475197e57c855191b2.m3u8\"";

    private const string DvCopy = "-analyzeduration 200M -probesize 1G -fflags +genpts  -i file:\"/media/x.mkv\" -map_metadata -1 -map_chapters -1 -threads 0 -map 0:0 -map 0:1 -map -0:s -codec:v:0 copy -tag:v:0 hvc1 -strict -2 -bsf:v hevc_mp4toannexb -start_at_zero -codec:a:0 copy -copyts -avoid_negative_ts disabled -max_muxing_queue_size 2048 -f hls -max_delay 5000000 -hls_time 6 -hls_segment_type fmp4 -hls_fmp4_init_filename \"fc64eea8dbc7b5a7121231a64a85e08a-1.mp4\" -start_number 0 -hls_segment_filename \"/cache/transcodes/fc64eea8dbc7b5a7121231a64a85e08a%d.mp4\" -hls_playlist_type vod -hls_list_size 0 -y \"/cache/transcodes/fc64eea8dbc7b5a7121231a64a85e08a.m3u8\"";

    // The baseline of a 2160p HDR10 HEVC, as spike 0c read it.
    private const string Baseline = "-i file:\"/media/x.mkv\" -map 0:0 -map 0:1 -map -0:s -codec:v:0 libx264 -preset veryfast -crf 23 -maxrate 8000000 -bufsize 16000000 -profile:v:0 high -level 41 -x264opts:0 subme=0 -vf \"setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc,scale=trunc(min(max(iw\\,ih*a)\\,min(1920\\,1080*a))/2)*2:trunc(min(max(iw/a\\,ih)\\,min(1920/a\\,1080))/2)*2,tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p\" -codec:a:0 libfdk_aac -ac 2 -ab 192000 -ar 48000 -hls_segment_type mpegts";

    private const string Interlaced = "-map 0:0 -map 0:1 -codec:v:0 libx264 -maxrate 8000000 -profile:v:0 high -level 41 -vf \"setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709,yadif=0:-1:0,format=yuv420p\" -codec:a:0 libfdk_aac -ac 2 -hls_segment_type mpegts";

    private const string WidthOnly = "-map 0:0 -map 0:1 -codec:v:0 libx264 -maxrate 1000000 -vf \"scale=trunc(min(max(iw\\,ih*a)\\,960)/2)*2:trunc(ow/a/2)*2,format=yuv420p\" -codec:a:0 libfdk_aac -ac 2 -hls_segment_type mpegts";

    internal static Source Hd(bool hdr = false) => new(1920, 1080, hdr, 6, "h264", "eac3", [(0, false), (1, false), (2, false)]) { AudioSampleRate = 48000 };

    private static Source Uhd() => new(3840, 2160, true, 6, "hevc", "eac3", [(0, false), (1, false)]) { AudioSampleRate = 48000 };

    [Fact]
    public void ReadsWhatWasMade()
    {
        var m = Made.Parse(H264At720);
        Assert.False(m.VideoCopied);
        Assert.Equal("h264", m.VideoCodec);
        Assert.Equal(3000000, m.VideoBitrate);
        Assert.Equal(720, m.ScaleHeight);
        Assert.Equal((1280, 720), m.Size(Hd()));
        Assert.False(m.ToneMapped);
        Assert.Equal("aac", m.AudioCodec);
        Assert.Equal(2, m.AudioChannels);
        Assert.Equal(1, m.AudioStreamIndex);
        Assert.Equal("ts", m.Segments);
        Assert.Equal("yuv420p", m.PixelFormat);
        Assert.Null(m.Profile);
        Assert.Null(m.AudioSampleRate);

        var dv = Made.Parse(DvCopy);
        Assert.True(dv.VideoCopied);
        Assert.True(dv.AudioCopied);
        Assert.Equal("mp4", dv.Segments);
        Assert.Null(dv.VideoCodec);
        Assert.Equal("hvc1", dv.VideoTag);

        var b = Made.Parse(Baseline);
        Assert.Equal("high", b.Profile);
        Assert.Equal(41, b.Level);
        Assert.True(b.ToneMapped);
        Assert.Equal("yuv420p", b.PixelFormat);
        Assert.Equal(48000, b.AudioSampleRate);
        Assert.Equal((1920, 1080), b.Size(Uhd()));
        Assert.True(Made.Parse(Interlaced).Deinterlaced);
        Assert.False(b.Deinterlaced);

        Assert.Equal((960, 540), Made.Parse(WidthOnly).Size(Hd()));
    }

    private static DeliveryOrder Order(string segments = "ts") => new()
    {
        ItemId = new string('1', 32), SourceId = new string('1', 32), Segments = segments,
        Video = new DeliveryVideo { Codec = "h264", MaxHeight = 720, MaxBitrate = 3_000_000 },
        Audio = new DeliveryAudio { Index = 1, Codec = "aac", Channels = 2 },
    };

    [Fact]
    public void ChecksWhatWasMade()
    {
        var order = Order();
        Assert.Empty(Made.Parse(H264At720).Differences(order, Hd()));

        // Asked 1080p at 1 Mbps, Jellyfin made 540p: refused.
        order.Video!.MaxHeight = 1080;
        order.Video.MaxBitrate = 1_000_000;
        Assert.Contains("height-cap", Made.Parse(WidthOnly).Differences(order, Hd()));

        // A bitrate lower than asked is fine; higher is not.
        order.Video.MaxHeight = 720;
        order.Video.MaxBitrate = 8_000_000;
        Assert.Empty(Made.Parse(H264At720).Differences(order, Hd()));
        order.Video.MaxBitrate = 2_000_000;
        Assert.Contains("bitrate-over", Made.Parse(H264At720).Differences(order, Hd()));
        order.Video.MaxBitrate = 3_000_000;

        // Tone mapping asked of a picture Jellyfin holds as SDR: refused
        // before ffmpeg starts (an HDR only in the frames), and never taken
        // as done.
        var toneMap = new DeliveryVideo { Codec = "h264", MaxHeight = 720, MaxBitrate = 3_000_000, ToneMap = true };
        Assert.Equal("sdr-to-jellyfin", Assert.Throws<DeliveryRefusedException>(() => DeliveryRules.CheckRange(toneMap, Hd())).Code);
        DeliveryRules.CheckRange(toneMap, Hd(hdr: true));
        DeliveryRules.CheckRange(new DeliveryVideo { Copy = true }, Hd());
        Assert.Contains("not-tone-mapped", Made.Parse(H264At720).Differences(new DeliveryOrder { Segments = "ts", Video = toneMap, Audio = order.Audio }, Hd(hdr: true)));

        // Another audio stream, more channels, other segments, the video copied.
        order.Audio!.Index = 2;
        Assert.Contains("audio-stream", Made.Parse(H264At720).Differences(order, Hd()));
        order.Audio.Index = 1;
        order.Audio.Channels = 1;
        Assert.Contains("audio-channels", Made.Parse(H264At720).Differences(order, Hd()));
        order.Audio.Channels = 2;
        order.Segments = "mp4";
        Assert.Contains("segments", Made.Parse(H264At720).Differences(order, Hd()));
        Assert.Contains("video-copied", Made.Parse(DvCopy).Differences(order, Hd()));
    }

    [Fact]
    public void ChecksTheBaseline()
    {
        var order = new DeliveryOrder
        {
            ItemId = new string('1', 32), SourceId = new string('1', 32), Segments = "ts",
            Video = new DeliveryVideo { Codec = "h264", MaxHeight = 1080, MaxBitrate = 8_000_000, ToneMap = true, Profile = "high", Level = "41", PixelFormat = "yuv420p" },
            Audio = new DeliveryAudio { Index = 1, Codec = "aac", Channels = 2, MaxSampleRate = 48_000 },
        };
        Assert.Empty(Made.Parse(Baseline).Differences(order, Uhd()));

        // Another profile, a higher level, 10 bits, no tone mapping, no -ar
        // of a 96 kHz source: each refused.
        Assert.Contains("video-profile", Made.Parse(Baseline.Replace("-profile:v:0 high", "-profile:v:0 main", System.StringComparison.Ordinal)).Differences(order, Uhd()));
        Assert.Contains("video-level", Made.Parse(Baseline.Replace("-level 41", "-level 51", System.StringComparison.Ordinal)).Differences(order, Uhd()));
        Assert.Contains("video-level", Made.Parse(Baseline.Replace(" -level 41", string.Empty, System.StringComparison.Ordinal)).Differences(order, Uhd()));
        Assert.Contains("pixel-format", Made.Parse(Baseline.Replace(":format=yuv420p", ":format=yuv420p10le", System.StringComparison.Ordinal)).Differences(order, Uhd()));
        Assert.Contains("not-tone-mapped", Made.Parse(Baseline.Replace("tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:", string.Empty, System.StringComparison.Ordinal)).Differences(order, Uhd()));
        var hiRes = Uhd() with { AudioSampleRate = 96_000 };
        Assert.Contains("audio-sample-rate", Made.Parse(Baseline.Replace(" -ar 48000", string.Empty, System.StringComparison.Ordinal)).Differences(order, hiRes));
        Assert.Empty(Made.Parse(Baseline).Differences(order, hiRes));
        // A source at 44.1 kHz stays so (at most 48 kHz).
        Assert.Empty(Made.Parse(Baseline.Replace(" -ar 48000", string.Empty, System.StringComparison.Ordinal)).Differences(order, Uhd() with { AudioSampleRate = 44_100 }));
        // Not scaled at all while the picture is taller than asked.
        Assert.Contains("height-over", Made.Parse(Baseline.Replace("scale=trunc(min(max(iw\\,ih*a)\\,min(1920\\,1080*a))/2)*2:trunc(min(max(iw/a\\,ih)\\,min(1920/a\\,1080))/2)*2,", string.Empty, System.StringComparison.Ordinal)).Differences(order, Uhd()));

        // An interlaced picture: deinterlaced, or refused.
        var i = new DeliveryOrder
        {
            ItemId = order.ItemId, SourceId = order.SourceId, Segments = "ts",
            Video = new DeliveryVideo { Codec = "h264", MaxHeight = 1080, MaxBitrate = 8_000_000, Profile = "high", Level = "41", PixelFormat = "yuv420p", Deinterlace = true },
            Audio = new DeliveryAudio { Index = 1, Codec = "aac", Channels = 2, MaxSampleRate = 48_000 },
        };
        Assert.Empty(Made.Parse(Interlaced).Differences(i, Hd()));
        Assert.Contains("not-deinterlaced", Made.Parse(Interlaced.Replace("yadif=0:-1:0,", string.Empty, System.StringComparison.Ordinal)).Differences(i, Hd()));
    }

    [Fact]
    public void ChecksTheSampleEntryInFragmentedMp4()
    {
        var copy = new DeliveryOrder
        {
            ItemId = new string('1', 32), SourceId = new string('1', 32), Segments = "mp4",
            Video = new DeliveryVideo { Copy = true, Tag = "hvc1" },
            Audio = new DeliveryAudio { Index = 1, Copy = true },
        };
        var s = new Source(3840, 2160, true, 6, "hevc", "eac3", [(0, false), (1, false)]);
        Assert.Empty(Made.Parse(DvCopy).Differences(copy, s));
        // Profile 5 must be dvh1: Jellyfin's hvc1 would be refused.
        copy.Video.Tag = "dvh1";
        Assert.Contains("sample-entry", Made.Parse(DvCopy).Differences(copy, s));
        Assert.Empty(Made.Parse(DvCopy.Replace("-tag:v:0 hvc1", "-tag:v:0 dvh1", System.StringComparison.Ordinal)).Differences(copy, s));
        // No tag at all (hev1, ffmpeg's own) is refused too.
        copy.Video.Tag = "hvc1";
        Assert.Contains("sample-entry", Made.Parse(DvCopy.Replace("-tag:v:0 hvc1 ", string.Empty, System.StringComparison.Ordinal)).Differences(copy, s));
    }

    [Fact]
    public void ChecksABurnedSubtitle()
    {
        var order = Order();
        order.Video!.BurnIndex = 3;
        Assert.Contains("not-burned", Made.Parse(H264At720).Differences(order, Hd()));
        Assert.Empty(Made.Parse(H264At720.Replace(",format=yuv420p", ",subtitles=f='/x.srt':si=0,format=yuv420p", System.StringComparison.Ordinal)).Differences(order, Hd()));
        order.Video.BurnIndex = -1;
        Assert.Contains("burned", Made.Parse(H264At720.Replace(",format=yuv420p", ",subtitles=f='/x.srt':si=0,format=yuv420p", System.StringComparison.Ordinal)).Differences(order, Hd()));
    }

    [Fact]
    public void NumbersStreamsAsJellyfinDoes()
    {
        // A subtitle file beside the video is numbered first by Jellyfin;
        // ffmpeg counts the file's own streams only.
        var streams = new List<(int, bool)> { (0, true), (1, false), (2, false), (3, false) };
        Assert.Equal(1, DeliveryRules.JellyfinIndex(streams, 0));
        Assert.Equal(3, DeliveryRules.JellyfinIndex(streams, 2));
        Assert.Equal(-1, DeliveryRules.JellyfinIndex(streams, 3));
    }

    private static Dictionary<string, string> QueryOf(DeliveryOrder o, Source s)
    {
        var d = new Delivery { Id = new string('c', 32), DeviceId = Deliveries.DevicePrefix + "cccccccccccc", Order = o, Source = s };
        return DeliveryRules.Query(o, d, s).Split('&').ToDictionary(p => p.Split('=')[0], p => System.Uri.UnescapeDataString(p.Split('=')[1]));
    }

    [Fact]
    public void QueryHasClosedValuesOnly()
    {
        var o = new DeliveryOrder
        {
            ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "mp4",
            Video = new DeliveryVideo { Codec = "hevc", MaxHeight = 720, MaxBitrate = 3_000_000, ToneMap = true },
            Audio = new DeliveryAudio { Index = 1, Copy = true },
        };
        var q = QueryOf(o, Hd());
        Assert.Equal(new string('b', 32), q["MediaSourceId"]);
        Assert.Equal(new string('c', 32), q["PlaySessionId"]);
        Assert.Equal("msfp-delivery-cccccccccccc", q["DeviceId"]);
        Assert.Equal("720", q["MaxHeight"]);
        Assert.Equal("1280", q["MaxWidth"]);
        Assert.Equal("SDR", q["hevc-rangetype"]);
        Assert.Equal("eac3", q["AudioCodec"]);
        Assert.Equal("true", q["AllowAudioStreamCopy"]);
        Assert.Equal("true", q["EnableAutoStreamCopy"]);
        Assert.Equal("Drop", q["SubtitleMethod"]);
        Assert.False(q.ContainsKey("ApiKey"));
        Assert.False(q.ContainsKey("api_key"));
        Assert.False(q.ContainsKey("hevc-profile"));
    }

    [Fact]
    public void QueryOfTheBaseline()
    {
        var o = new DeliveryOrder
        {
            ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "ts",
            Video = new DeliveryVideo { Codec = "h264", MaxHeight = 1080, MaxBitrate = 16_000_000, ToneMap = true, Profile = "high", Level = "41", PixelFormat = "yuv420p", Deinterlace = true },
            Audio = new DeliveryAudio { Index = 1, Codec = "aac", Channels = 2, MaxSampleRate = 48_000 },
        };
        var q = QueryOf(o, Uhd() with { AudioSampleRate = 96_000 });
        Assert.Equal("high", q["h264-profile"]);
        Assert.Equal("41", q["h264-level"]);
        Assert.Equal("true", q["h264-deinterlace"]);
        Assert.Equal("1080", q["MaxHeight"]);
        Assert.Equal("1920", q["MaxWidth"]);
        Assert.Equal("48000", q["AudioSampleRate"]);
        Assert.Equal("2", q["TranscodingMaxAudioChannels"]);
        // A source within 48 kHz: its own rate kept.
        Assert.False(QueryOf(o, Uhd()).ContainsKey("AudioSampleRate"));

        o.Video.BurnIndex = 4;
        q = QueryOf(o, Uhd());
        Assert.Equal("4", q["SubtitleStreamIndex"]);
        Assert.Equal("Encode", q["SubtitleMethod"]);
    }

    [Fact]
    public void RefusesMalformedOrders()
    {
        var good = new DeliveryOrder { ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "ts", Video = new DeliveryVideo { Copy = true } };
        DeliveryRules.Validate(good);
        foreach (var bad in new[]
        {
            new DeliveryOrder { ItemId = "../x", SourceId = new string('b', 32), Segments = "ts", Video = new DeliveryVideo { Copy = true } },
            new DeliveryOrder { ItemId = new string('A', 32), SourceId = new string('b', 32), Segments = "ts", Video = new DeliveryVideo { Copy = true } },
            new DeliveryOrder { ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "m3u8", Video = new DeliveryVideo { Copy = true } },
            new DeliveryOrder { ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "ts", Video = new DeliveryVideo { Codec = "-vf evil", MaxHeight = 720, MaxBitrate = 1_000_000 } },
            new DeliveryOrder { ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "ts", Video = new DeliveryVideo { Codec = "h264", MaxHeight = 99999, MaxBitrate = 1_000_000 } },
            new DeliveryOrder { ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "ts", Video = new DeliveryVideo { Codec = "h264", MaxHeight = 720, MaxBitrate = 1_000_000, Profile = "high&x=1" } },
            new DeliveryOrder { ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "ts", Video = new DeliveryVideo { Codec = "h264", MaxHeight = 720, MaxBitrate = 1_000_000, Level = "62" } },
            new DeliveryOrder { ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "ts", Video = new DeliveryVideo { Copy = true, Tag = "hev1 -x" } },
            new DeliveryOrder { ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "ts", Audio = new DeliveryAudio { Index = 1, Codec = "aac", Channels = 99 } },
            new DeliveryOrder { ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "ts", Audio = new DeliveryAudio { Index = 1, Codec = "aac", Channels = 2, MaxSampleRate = 1 } },
            new DeliveryOrder { ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "ts" },
            new DeliveryOrder { ItemId = new string('a', 32), SourceId = new string('b', 32), Segments = "ts", StartTicks = -1, Video = new DeliveryVideo { Copy = true } },
        })
        {
            Assert.Throws<DeliveryRefusedException>(() => DeliveryRules.Validate(bad));
        }
    }

    [Fact]
    public void RefusalsAnswerTheirProblems()
    {
        Assert.Equal(MsfpError.DeliveryRefused, new DeliveryRefusedException("made-differs:height-cap").ToMsfp().Code);
        Assert.Equal(MsfpError.ServerBusy, new DeliveryRefusedException("busy", true).ToMsfp().Code);
    }

    [Fact]
    public void SegmentsOfAPlaylist()
    {
        var p = "#EXTM3U\n#EXT-X-MAP:URI=\"hls1/main/-1.mp4?a=b\"\n#EXTINF:6.0, nodesc\nhls1/main/0.mp4?a=b\n#EXTINF:6.0, nodesc\nhls1/main/1.mp4?a=b\n#EXT-X-ENDLIST\n";
        Assert.Equal(new List<string> { "hls1/main/0.mp4?a=b", "hls1/main/1.mp4?a=b" }, DeliveryRules.Segments(p));
        Assert.True(Made.IsOutputStem("fc64eea8dbc7b5a7121231a64a85e08a"));
        Assert.False(Made.IsOutputStem("../etc"));
    }
}
