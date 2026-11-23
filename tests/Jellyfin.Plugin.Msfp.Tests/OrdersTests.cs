using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Msfp.Playback;
using MediaBrowser.Model.Entities;
using Msfp;
using Msfp.Protocol;
using Xunit;
using PlaybackRules = Msfp.Protocol.Playback;

namespace Jellyfin.Plugin.Msfp.Tests;

// What a peer's play makes of a copy: the engine's decision (Served) as an
// order of Jellyfin's transcoder, or a refusal before anything starts.
public class OrdersTests
{
    private static readonly Guid Version = Guid.Parse("0123456789abcdef0123456789abcdef");

    private static readonly EncodersNow Cpu = new(["h264", "hevc"], ["aac", "ac3", "eac3", "mp3", "opus", "flac"], false, true);

    private static readonly Dictionary<string, int> Tracks = new() { ["a1"] = 2, ["a2"] = 3, ["s3"] = 0 };

    // Jellyfin's streams: an external subtitle first, then the file's.
    private static List<MediaStream> Streams(int width, int height, bool hdr = false, bool interlaced = false) =>
    [
        new() { Type = MediaStreamType.Subtitle, Index = 0, IsExternal = true, Codec = "subrip" },
        new() { Type = MediaStreamType.Video, Index = 1, Codec = hdr ? "hevc" : "h264", Width = width, Height = height, ColorTransfer = hdr ? "smpte2084" : "bt709", ColorPrimaries = hdr ? "bt2020" : "bt709", BitDepth = hdr ? 10 : 8, IsInterlaced = interlaced },
        new() { Type = MediaStreamType.Audio, Index = 2, Codec = "eac3", Channels = 6, SampleRate = 48000 },
        new() { Type = MediaStreamType.Audio, Index = 3, Codec = "aac", Channels = 2, SampleRate = 48000 },
    ];

    private static Copy Uhd(VideoDv? dv = null) => new("v", 60_000, "video/x-matroska", 600_000_000, "e",
        [new CopyAudio("a1", "eac3") { Channels = 6, Codecs = "ec-3", SampleRate = 48000 }, new CopyAudio("a2", "aac") { Channels = 2, Codecs = "mp4a.40.2", SampleRate = 48000 }])
    {
        Video = new VideoStream("hevc", 3840, 2160) { Codecs = "hvc1.2.4.L153.B0", BitDepth = 10, Colour = new VideoColour(9, 16, 9), Dv = dv },
        SubtitleTracks = [new CopySubtitle("s3", "subrip") { External = true }],
    };

    private static Copy Hd(bool interlaced = false, int width = 1920) => new("v", 60_000, "video/mp4", 60_000_000, "e",
        [new CopyAudio("a1", "eac3") { Channels = 6, Codecs = "ec-3", SampleRate = 48000 }, new CopyAudio("a2", "aac") { Channels = 2, Codecs = "mp4a.40.2", SampleRate = 48000 }])
    {
        Video = new VideoStream("h264", width, 1080) { Codecs = "avc1.640028", BitDepth = 8, Colour = new VideoColour(1, 1, 1), Interlaced = interlaced },
    };

    // The engine's decision for the baseline (Playback.Baseline, as its Serve puts it together).
    private static StartRequest BaselineOf(Copy c, string audio)
    {
        var b = PlaybackRules.Baseline(c, audio);
        return new StartRequest
        {
            AudioTrack = audio,
            Served = new Served { Method = b.Method, Video = b.Video ?? string.Empty, Audio = b.Audio, Transcodes = b.Method == PlaybackRules.MethodTranscode },
        };
    }

    private static StartRequest TranscodeOf(Copy c, string audio, TranscodeTargets t, int capKbps = 0)
    {
        var r = new global::Msfp.Protocol.PlayRequest { Delivery = PlaybackRules.DeliveryTranscode, Transcode = t };
        var s = r.TargetsServed(c.Video is not null)!;
        return new StartRequest
        {
            Delivery = PlaybackRules.DeliveryTranscode,
            Transcode = s,
            AudioTrack = audio,
            MaxBitrateKbps = capKbps,
            Served = new Served { Method = PlaybackRules.MethodTranscode, Video = s.VideoCodec, Audio = s.AudioCodec, Transcode = s, Transcodes = PlaybackRules.TakesTranscode(r, c, audio) },
        };
    }

    private static StartRequest RemuxOf(string mediaType, string audio) => new()
    {
        Delivery = PlaybackRules.DeliveryRemux,
        MediaType = mediaType,
        AudioTrack = audio,
        Served = new Served { Method = PlaybackRules.MethodRemux, Video = Names.Copy, Audio = Names.Copy, Segments = mediaType[(mediaType.IndexOf('/', StringComparison.Ordinal) + 1)..] },
    };

    private static string Refused(Action a) => Assert.Throws<DeliveryRefusedException>(a).Code;

    [Fact]
    public void TheBaselineOfA2160pHdrSource()
    {
        var c = Uhd();
        var o = Orders.Plan(BaselineOf(c, "a1"), c, Tracks, Version, Streams(3840, 2160, true), Cpu);
        Assert.Equal(Version.ToString("N"), o.ItemId);
        Assert.Equal("ts", o.Segments);
        var v = o.Video!;
        Assert.False(v.Copy);
        Assert.Equal("h264", v.Codec);
        Assert.Equal("high", v.Profile);
        Assert.Equal("41", v.Level);
        Assert.Equal(1080, v.MaxHeight);
        Assert.True(v.ToneMap);
        Assert.Equal("yuv420p", v.PixelFormat);
        Assert.False(v.Deinterlace);
        Assert.Equal(-1, v.BurnIndex);
        var a = o.Audio!;
        Assert.False(a.Copy);
        Assert.Equal("aac", a.Codec);
        Assert.Equal(2, a.Channels);
        Assert.Equal(2, a.Index);
        Assert.Equal(48_000, a.MaxSampleRate);
        DeliveryRules.Validate(o);
    }

    [Fact]
    public void TheBaselineLeavesWhatIsWithinIt()
    {
        var c = Hd();
        var o = Orders.Plan(BaselineOf(c, "a2"), c, Tracks, Version, Streams(1920, 1080), Cpu);
        Assert.True(o.Video!.Copy);
        Assert.True(o.Audio!.Copy);
        Assert.Equal(3, o.Audio.Index);
        Assert.Equal("ts", o.Segments);

        // The picture within, the E-AC-3 converted: the picture copied.
        o = Orders.Plan(BaselineOf(c, "a1"), c, Tracks, Version, Streams(1920, 1080), Cpu);
        Assert.True(o.Video!.Copy);
        Assert.Equal("aac", o.Audio!.Codec);
    }

    [Fact]
    public void TheBaselineDeinterlacesAndKeepsWithinLevel41()
    {
        var c = Hd(interlaced: true);
        var o = Orders.Plan(BaselineOf(c, "a2"), c, Tracks, Version, Streams(1920, 1080, interlaced: true), Cpu);
        Assert.False(o.Video!.Copy);
        Assert.True(o.Video.Deinterlace);

        // A wide picture (2560x1080, 10,880 macroblocks) is lower than 1080 lines.
        Assert.Equal(1080, Orders.BaselineHeight(1920, 1080, 1080));
        var wide = Orders.BaselineHeight(2560, 1080, 1080);
        Assert.True(wide < 1080);
        var w = (int)Math.Round(wide * 2560.0 / 1080 / 2) * 2;
        Assert.True(((w + 15) / 16) * ((wide + 15) / 16) <= Orders.Level41Macroblocks);
        Assert.Equal(720, Orders.BaselineHeight(1280, 720, 720));
    }

    [Fact]
    public void ATranscodeWithItsAudioCopied()
    {
        var c = Uhd();
        var o = Orders.Plan(TranscodeOf(c, "a1", new TranscodeTargets { VideoCodec = "hevc", MaxHeight = 1080, AudioCodec = Names.Copy }), c, Tracks, Version, Streams(3840, 2160, true), Cpu);
        Assert.Equal("hevc", o.Video!.Codec);
        Assert.Equal(1080, o.Video.MaxHeight);
        Assert.Equal(16_000_000, o.Video.MaxBitrate);
        Assert.Null(o.Video.Profile);
        Assert.True(o.Video.ToneMap);
        Assert.True(o.Audio!.Copy);
        Assert.Equal("mp4", o.Segments);
        Assert.Equal("hvc1", o.Video.Tag);

        // h264 with E-AC-3 copied: MPEG-TS carries both.
        o = Orders.Plan(TranscodeOf(c, "a1", new TranscodeTargets { VideoCodec = "h264", MaxHeight = 720, MaxBitrateKbps = 4000, AudioCodec = Names.Copy }), c, Tracks, Version, Streams(3840, 2160, true), Cpu);
        Assert.Equal("ts", o.Segments);
        Assert.Equal(4_000_000, o.Video!.MaxBitrate);
        Assert.Equal(720, o.Video.MaxHeight);
    }

    [Fact]
    public void ATranscodeWithinTheCap()
    {
        var c = Hd();
        var o = Orders.Plan(TranscodeOf(c, "a1", new TranscodeTargets { VideoCodec = "h264", AudioCodec = "aac", AudioChannels = 6 }, capKbps: 3000), c, Tracks, Version, Streams(1920, 1080), Cpu);
        Assert.Equal(1080, o.Video!.MaxHeight);
        Assert.Equal((3000 - 384) * 1000, o.Video.MaxBitrate);
        Assert.Equal(6, o.Audio!.Channels);

        // Never upmixed: a stereo track asked as 6 channels stays 2.
        o = Orders.Plan(TranscodeOf(c, "a2", new TranscodeTargets { VideoCodec = "h264", AudioCodec = "ac3", AudioChannels = 6 }), c, Tracks, Version, Streams(1920, 1080), Cpu);
        Assert.Equal(2, o.Audio!.Channels);
        Assert.Equal(0, o.Audio.MaxSampleRate);
    }

    [Fact]
    public void RefusesWhatIsntMadeHere()
    {
        var c = Uhd();
        var s = Streams(3840, 2160, true);
        Assert.Equal("video-codec-not-made", Refused(() => Orders.Plan(TranscodeOf(c, "a1", new TranscodeTargets { VideoCodec = "av1" }), c, Tracks, Version, s, Cpu)));
        Assert.Equal("audio-codec-not-made", Refused(() => Orders.Plan(TranscodeOf(c, "a1", new TranscodeTargets { VideoCodec = "h264", AudioCodec = "alac" }), c, Tracks, Version, s, Cpu)));
        Assert.Equal("range-keep", Refused(() => Orders.Plan(TranscodeOf(c, "a1", new TranscodeTargets { VideoCodec = "h264", Range = Names.RangeKeep }), c, Tracks, Version, s, Cpu)));
        Assert.Equal("track-not-mapped", Refused(() => Orders.Plan(TranscodeOf(c, "a9", new TranscodeTargets { VideoCodec = "h264" }), c, Tracks, Version, s, Cpu)));
        Assert.Equal("burn-with-copy", Refused(() => Orders.Plan(TranscodeOf(c, "a1", new TranscodeTargets { VideoCodec = Names.Copy, BurnSubtitle = "s3" }), c, Tracks, Version, s, Cpu)));
        // A burned subtitle by Jellyfin's index of it.
        Assert.Equal(0, Orders.Plan(TranscodeOf(c, "a1", new TranscodeTargets { VideoCodec = "h264", BurnSubtitle = "s3" }), c, Tracks, Version, s, Cpu).Video!.BurnIndex);
        // TrueHD copied: neither segment format carries it.
        var th = c with { AudioTracks = [new CopyAudio("a1", "truehd") { Channels = 8 }] };
        Assert.Equal("segments-cannot-carry", Refused(() => Orders.Plan(TranscodeOf(th, "a1", new TranscodeTargets { VideoCodec = "h264", AudioCodec = Names.Copy }), th, Tracks, Version, s, Cpu)));
    }

    [Fact]
    public void DolbyVisionSampleEntries()
    {
        // Profile 8 (compatible base): hvc1 kept in fragmented MP4.
        var p8 = Uhd(new VideoDv(8, 6, 1));
        var o = Orders.Plan(RemuxOf("video/mp4", "a1"), p8, Tracks, Version, Streams(3840, 2160, true), Cpu);
        Assert.Equal("mp4", o.Segments);
        Assert.True(o.Video!.Copy);
        Assert.Equal("hvc1", o.Video.Tag);
        // Profile 5 (no compatible base): dvh1.
        Assert.Equal("dvh1", Orders.Plan(RemuxOf("video/mp4", "a1"), Uhd(new VideoDv(5, 6, 0)), Tracks, Version, Streams(3840, 2160, true), Cpu).Video!.Tag);
        // Profile 7 (compatible base, which Jellyfin would write dvh1): refused in MP4, made in MPEG-TS.
        var p7 = Uhd(new VideoDv(7, 6, 6));
        Assert.Equal("dv-sample-entry", Refused(() => Orders.Plan(RemuxOf("video/mp4", "a1"), p7, Tracks, Version, Streams(3840, 2160, true), Cpu)));
        o = Orders.Plan(RemuxOf("video/mp2t", "a1"), p7, Tracks, Version, Streams(3840, 2160, true), Cpu);
        Assert.Equal("ts", o.Segments);
        Assert.Null(o.Video!.Tag);
        // Plain HEVC: hvc1; H.264: nothing to check.
        Assert.Equal("hvc1", Orders.SampleEntry(Uhd().Video));
        Assert.Null(Orders.SampleEntry(Hd().Video));
        Assert.Equal("dv-sample-entry", Refused(() => Orders.SampleEntry(new VideoStream("av1", 3840, 2160) { Dv = new VideoDv(10, 6, 1) })));
    }

    [Fact]
    public void ThePositionAndTheSource()
    {
        var c = Hd();
        var r = BaselineOf(c, "a1") with { StartMs = 30_000 };
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, Orders.Plan(r, c, Tracks, Version, Streams(1920, 1080), Cpu).StartTicks);
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, Orders.Plan(r with { StartMs = 99_000_000 }, c, Tracks, Version, Streams(1920, 1080), Cpu).StartTicks);

        var src = Orders.SourceOf(Streams(3840, 2160, true), 2);
        Assert.True(src.Hdr);
        Assert.Equal(6, src.AudioChannels);
        Assert.Equal("eac3", src.AudioCodec);
        Assert.Equal(48000, src.AudioSampleRate);
        Assert.Equal(new List<(int, bool)> { (0, true), (1, false), (2, false), (3, false) }, src.Streams.ToList());
        Assert.Equal("no-such-audio", Refused(() => Orders.SourceOf(Streams(1920, 1080), 9)));
    }
}
