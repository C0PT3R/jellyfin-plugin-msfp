using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Msfp.Viewing;
using MediaBrowser.Model.Entities;
using Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>
/// A copy as a stand-in's streams (migration plan 4.1, spike 0a): written as
/// Jellyfin's own reading of the same file writes them. Checked against the
/// describe fixtures (ffprobe's outputs, which Jellyfin stores, and the
/// copies shaped from them), msfp-spec's codecs vectors, and the facts
/// Jellyfin 12.2 stored for the same files (phase 5, on the test bed).
/// </summary>
public class StandInStreamsTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "testdata");

    private static string? Three(string tag) => Identification.Languages.Terminology(tag) is { Length: > 0 } t ? t : null;

    private static Copy CopyOf(string name) => Copies.Read(File.ReadAllBytes(Path.Combine(Dir, "describe", name + ".copy.json")))!;

    private static StandInStreams.Built Built(Copy c) => StandInStreams.Of(c, Three, s => "http://127.0.0.1:8096/Msfp/Subtitle/i/s/" + s.Id);

    public static TheoryData<string> Fixtures => new()
    {
        "av1-mp4", "dts-truehd-3d-mkv", "h264-aac-mp4", "handmade-dv-atmos", "hevc-hdr10-mkv", "hevc-hlg-atc-ts", "tone-flac", "vp9-opus-webm", "with-font-mkv",
    };

    // Each stream as ffprobe gave it for the file (what Jellyfin stores):
    // codec, profile, level, pixel format, display aspect, frame rate,
    // colour, rotation, channels, sample rate, bit depth, flags.
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void StreamsAsJellyfinsReadingOfTheFile(string name)
    {
        var copy = CopyOf(name);
        var built = Built(copy);
        var probe = (JsonArray)CopiesTests.FixtureJson(name)["outputs"]!["streams"]!["streams"]!;
        var externals = copy.SubtitleTracks.Count(t => t.External);

        // Beside the file first, then the file's streams at their index.
        Assert.All(built.Streams.Take(externals), s => Assert.True(s.IsExternal && s.Type == MediaStreamType.Subtitle && s.Path!.StartsWith("http://", StringComparison.Ordinal)));
        Assert.Equal(Enumerable.Range(0, built.Streams.Count), built.Streams.Select(s => s.Index));

        foreach (var (id, index) in built.Tracks)
        {
            var s = built.Streams[index];
            if (s.IsExternal)
            {
                continue;
            }

            var n = index - externals;
            var p = probe.First(x => (int)x!["index"]! == n)!;
            Assert.Equal(id == "v" ? "video" : id[0] == 'a' ? "audio" : "subtitle", (string)p["codec_type"]!);
            var codec = (string)p["codec_name"]!;
            Assert.Equal(StandInStreams.SubtitleCodec(codec) ?? codec, s.Codec);
            var profile = (string?)p["profile"];
            if (id == "v")
            {
                Assert.Equal(profile, s.Profile);
                Assert.Equal((double?)(int?)p["level"], s.Level);
                Assert.Equal((string?)p["pix_fmt"], s.PixelFormat);
                Assert.Equal(1, s.RefFrames);
                Assert.Equal((int)p["width"]!, s.Width);
                Assert.Equal((int)p["height"]!, s.Height);
                if ((string?)p["display_aspect_ratio"] is { } dar)
                {
                    Assert.Equal(dar, s.AspectRatio);
                }

                Assert.Equal(Rate((string)p["r_frame_rate"]!), s.RealFrameRate);
                Assert.Equal(Rate((string)p["avg_frame_rate"]!), s.AverageFrameRate);
                Assert.Equal((string?)p["color_primaries"], s.ColorPrimaries);
                Assert.Equal((string?)p["color_space"], s.ColorSpace);
                var rotation = (p["side_data_list"] as JsonArray)?.Select(d => (int?)d!["rotation"]).FirstOrDefault(r => r is not null);
                Assert.Equal(rotation, s.Rotation);
                Assert.Equal(((string?)p["field_order"]) is { } fo && fo is not ("progressive" or "unknown"), s.IsInterlaced);
                if (codec == "h264")
                {
                    Assert.Equal((string?)p["is_avc"] == "true", s.IsAVC);
                }
            }
            else if (id[0] == 'a')
            {
                // ffprobe names an AAC, DTS or object-based track's profile; Jellyfin keeps it.
                Assert.Equal(profile == "unknown" ? null : profile, s.Profile);
                Assert.Equal((int?)p["channels"], s.Channels);
                Assert.Equal(int.Parse((string)p["sample_rate"]!, CultureInfo.InvariantCulture), s.SampleRate);
                Assert.Equal((int)p["disposition"]!["default"]! == 1, s.IsDefault);
            }
            else if (p["tags"]?["title"] is null)
            {
                // (A title saying "Forced" gives the copy the flag where the
                // file has none, which Jellyfin's reading doesn't take.)
                Assert.Equal((int)p["disposition"]!["forced"]! == 1, s.IsForced);
            }
        }
    }

    private static float? Rate(string r)
    {
        var parts = r.Split('/');
        var den = double.Parse(parts[1], CultureInfo.InvariantCulture);
        return den == 0 ? null : (float)(double.Parse(parts[0], CultureInfo.InvariantCulture) / den);
    }

    // As Jellyfin 12.2 stored the same files on the test bed (phase 5):
    // externals first, "fra" and "deu", "und" in ISO files, the picture
    // default in ISO files only, sample entries. The file's two data
    // streams after its tracks aren't listed (none of the copy's tracks
    // comes after them; Jellyfin's ffmpeg maps by place).
    [Fact]
    public void AnMp4WithAFileBeside()
    {
        var built = Built(CopyOf("h264-aac-mp4"));
        Assert.Equal([MediaStreamType.Subtitle, MediaStreamType.Video, MediaStreamType.Audio], built.Streams.Select(s => s.Type));
        var (sub, video, audio) = (built.Streams[0], built.Streams[1], built.Streams[2]);
        Assert.Equal(("subrip", "fra", true, true), (sub.Codec, sub.Language, sub.IsForced, sub.IsExternal));
        Assert.Equal(("avc1", "und", true, 90, "High", 12d, "4"), (video.CodecTag, video.Language, video.IsDefault, video.Rotation, video.Profile, video.Level, video.NalLengthSize));
        Assert.Equal(("mp4a", "und", "LC", "mono", true), (audio.CodecTag, audio.Language, audio.Profile, audio.ChannelLayout, audio.IsDefault));
        Assert.Equal(new Dictionary<string, int> { ["s4"] = 0, ["v"] = 1, ["a1"] = 2 }, built.Tracks);
    }

    [Fact]
    public void AMatroskaHdr10WithTwoAudioAndTwoSubtitles()
    {
        var built = Built(CopyOf("hevc-hdr10-mkv"));
        var v = built.Streams[built.Tracks["v"]];
        Assert.Equal(("hevc", "Main 10", 60d, 10, "yuv420p10le", "smpte2084", false, null), (v.Codec, v.Profile, v.Level, v.BitDepth, v.PixelFormat, v.ColorTransfer, v.IsDefault, v.CodecTag));
        Assert.Equal(VideoRangeType.HDR10, v.VideoRangeType);
        Assert.Equal(("eac3", "fra", "5.1", 6, true), (built.Streams[2].Codec, built.Streams[2].Language, built.Streams[2].ChannelLayout, built.Streams[2].Channels, built.Streams[2].IsDefault));
        Assert.Equal(("ac3", "eng", false), (built.Streams[3].Codec, built.Streams[3].Language, built.Streams[3].IsDefault));
        Assert.Equal(("subrip", "eng", true), (built.Streams[4].Codec, built.Streams[4].Language, built.Streams[4].IsForced));
        Assert.Equal(("ass", "fra", true), (built.Streams[5].Codec, built.Streams[5].Language, built.Streams[5].IsHearingImpaired));
        Assert.Equal(6, built.Streams.Count);
    }

    [Fact]
    public void HlgDolbyVisionAndAtmos()
    {
        var hlg = Built(CopyOf("hevc-hlg-atc-ts")).Streams.Single(s => s.Type == MediaStreamType.Video);
        Assert.Equal(("arib-std-b67", VideoRangeType.HLG), (hlg.ColorTransfer, hlg.VideoRangeType));

        var built = Built(CopyOf("handmade-dv-atmos"));
        var v = built.Streams[built.Tracks["v"]];
        Assert.Equal((8, 1, "dvh1"), (v.DvProfile, v.DvBlSignalCompatibilityId, v.CodecTag));
        Assert.Equal(VideoRange.HDR, v.VideoRange);
        Assert.Equal(VideoRangeType.DOVIWithHDR10Plus, v.VideoRangeType); // its hdr10plus too
        var a = built.Tracks.Where(t => t.Key[0] == 'a').Select(t => built.Streams[t.Value]).ToList();
        Assert.Equal(
            [AudioSpatialFormat.DolbyAtmos, AudioSpatialFormat.DolbyAtmos, AudioSpatialFormat.DTSX, AudioSpatialFormat.None],
            a.Select(s => s.AudioSpatialFormat));
        Assert.True(a[0].IsOriginal);
    }

    [Fact]
    public void AGapInTheFileIsADataStream()
    {
        var copy = new Copy("v1", 1000, "video/matroska", 10, "e", [new CopyAudio("a1", "aac") { Codecs = "mp4a.40.2" }])
        {
            Video = new VideoStream("h264", 1920, 1080) { Codecs = "avc1.640028" },
            SubtitleTracks = [new CopySubtitle("s3", "subrip"), new CopySubtitle("s6", "webvtt") { External = true, Language = "de" }],
        };
        var built = Built(copy);
        Assert.Equal(
            [MediaStreamType.Subtitle, MediaStreamType.Video, MediaStreamType.Audio, MediaStreamType.Data, MediaStreamType.Subtitle],
            built.Streams.Select(s => s.Type));
        Assert.Equal(new Dictionary<string, int> { ["s6"] = 0, ["v"] = 1, ["a1"] = 2, ["s3"] = 4 }, built.Tracks);
        Assert.Equal("deu", built.Streams[0].Language);
        Assert.Equal("webvtt", built.Streams[0].Codec);
    }

    [Fact]
    public void PictureSubtitlesAsJellyfinNamesThem()
    {
        Assert.Equal("PGSSUB", StandInStreams.SubtitleCodec("hdmv_pgs_subtitle"));
        Assert.Equal("DVDSUB", StandInStreams.SubtitleCodec("dvd_subtitle"));
        Assert.Equal("subrip", StandInStreams.SubtitleCodec("subrip"));
        Assert.Null(StandInStreams.SubtitleCodec(string.Empty));
    }

    // msfp-spec's codecs vectors: the profile and level they read, as
    // ffprobe names and Jellyfin stores them.
    [Fact]
    public void ProfilesAndLevelsFromTheSpecsCodecsVectors()
    {
        var names = new Dictionary<string, Dictionary<int, string>>
        {
            ["h264"] = new() { [66] = "Baseline", [77] = "Main", [100] = "High", [110] = "High 10" },
            ["hevc"] = new() { [1] = "Main", [2] = "Main 10", [4] = "Rext" },
            ["av1"] = new() { [0] = "Main", [1] = "High", [2] = "Professional" },
            ["vp9"] = new() { [0] = "Profile 0", [1] = "Profile 1", [2] = "Profile 2", [3] = "Profile 3" },
        };
        var cases = (JsonArray)JsonNode.Parse(File.ReadAllBytes(Path.Combine(Dir, "spec", "video-codecs.json")))!["codecs"]!;
        var checkedCount = 0;
        foreach (var c in cases)
        {
            var input = JsonNode.Parse((string)c!["text"]!)!;
            if (!(bool)c["valid"]! || (string?)input["codec"] is not { } codec || !names.ContainsKey(codec))
            {
                continue;
            }

            var output = JsonNode.Parse((string)c["output"]!)!;
            var dv = input["dv"] is JsonObject d ? new VideoDv((int)d["profile"]!, (int)d["level"]!, (int)d["compatibility"]!) : null;
            var facts = Rfc6381.Read(codec, (string?)input["codecs"], (int?)input["bitDepth"], dv, out _);
            Assert.NotNull(facts);
            var profile = (int)output["profile"]!;
            Assert.Equal(names[codec][profile], StandInStreams.VideoProfile(codec, (string?)input["codecs"], facts));
            Assert.Equal(codec switch { "av1" => 0, "vp9" => -99, _ => (double?)(int)output["level"]! }, StandInStreams.VideoLevel(codec, facts));
            checkedCount++;
        }

        Assert.True(checkedCount >= 20);
    }

    [Theory]
    [InlineData("avc1.42E01E", "Constrained Baseline")]
    [InlineData("avc1.42001E", "Baseline")]
    [InlineData("avc1.6E1028", "High 10 Intra")]
    [InlineData("avc1.F40028", "High 4:4:4 Predictive")]
    public void H264ConstraintFlagsName(string codecs, string profile) =>
        Assert.Equal(profile, StandInStreams.VideoProfile("h264", codecs, Rfc6381.Read("h264", codecs, null, null, out _)));

    // As Jellyfin stored the RExt file of the test bed (yuv422p10le) and
    // AV1 and VP9 4:4:4 ones.
    [Theory]
    [InlineData("hevc", "hvc1.4.10.L60.9D.08", 10, "yuv422p10le")]
    [InlineData("hevc", "hvc1.4.10.L60.98.88", 10, "yuv420p10le")]
    [InlineData("hevc", "hvc1.4.10.L60.98.08", 12, "yuv444p12le")]
    [InlineData("av1", "av01.1.04H.08.0.000.01.01.01.0", 8, "yuv444p")]
    [InlineData("vp9", "vp09.01.20.08.03", 8, "yuv444p")]
    [InlineData("h264", "avc1.7A0028", 10, "yuv422p10le")]
    [InlineData("mpeg2video", null, 8, "yuv420p")]
    public void PixelFormats(string codec, string? codecs, int depth, string expected) =>
        Assert.Equal(expected, StandInStreams.PixelFormat(codec, Rfc6381.Read(codec, codecs, depth, null, out _), depth));

    [Theory]
    [InlineData("video/matroska", "mkv")]
    [InlineData("video/mp4", "mp4")]
    [InlineData("video/mp2t", "ts")]
    [InlineData("video/vnd.dlna.mpeg-tts", "ts")]
    [InlineData("video/webm", "webm")]
    [InlineData("video/avi", "avi")]
    [InlineData("audio/flac", "flac")]
    [InlineData("video/x-unknown", null)]
    public void ContainersAsJellyfinNamesThem(string mediaType, string? container) =>
        Assert.Equal(container, StandInStreams.Container(mediaType));

    [Theory]
    [InlineData(90, -90)]
    [InlineData(180, 180)]
    [InlineData(270, 90)]
    public void RotationTheOtherWay(int msfp, int jellyfin) => Assert.Equal(jellyfin, StandInStreams.FfprobeRotation(msfp));

    [Theory]
    [InlineData(720, 576, "64:45", "16:9")]
    [InlineData(1920, 800, null, "12:5")]
    [InlineData(320, 180, null, "16:9")]
    public void DisplayAspects(int w, int h, string? sar, string dar) => Assert.Equal(dar, StandInStreams.DisplayAspect(w, h, sar));

    [Fact]
    public void BitrateSizeTimesEightOverDuration()
    {
        var c = new Copy("v", 2002, "video/matroska", 186390, "e", []);
        Assert.Equal(744815, StandInStreams.Bitrate(c)); // what Jellyfin stored for the same file
        Assert.Null(StandInStreams.Bitrate(c with { DurationMs = 0 }));
    }

    [Fact]
    public void ChaptersAsALocalFilesTheUnnamedNamedAsJellyfinDoes()
    {
        var c = new Copy("v", 2000, "video/matroska", 10, "e", []) { Chapters = [new CopyChapter(0), new CopyChapter(1000, "Second")] };
        var chapters = StandInStreams.Chapters(c, n => "Chapter " + n);
        Assert.Equal([(0L, "Chapter 1"), (10_000_000L, "Second")], chapters.Select(x => (x.StartPositionTicks, x.Name)));
        Assert.All(chapters, x => Assert.Null(x.ImagePath));
    }

    [Fact]
    public void LabelsFromFacts()
    {
        Copy V(string id, int w, int h, string codec = "hevc", int? transfer = null, string? edition = null, string? threeD = null, VideoDv? dv = null) =>
            new(id, 1000, "video/matroska", 10, "e", []) { Edition = edition, ThreeD = threeD, Video = new VideoStream(codec, w, h) { Colour = transfer is { } t ? new VideoColour(9, t, 9) : null, Dv = dv } };
        Assert.Equal(["2160p HDR10", "1080p"], StandInStreams.Labels([V("a", 3840, 1600, transfer: 16), V("b", 1920, 1080)]));
        Assert.Equal(["1080p HEVC", "1080p H.264"], StandInStreams.Labels([V("a", 1920, 1080), V("b", 1920, 1080, "h264")]));
        Assert.Equal(["1080p Director's Cut", "1080p Theatrical"], StandInStreams.Labels([V("a", 1920, 1080, edition: "directors"), V("b", 1920, 1080, edition: "theatrical")]));
        Assert.Equal(["2160p Dolby Vision", "1080p HLG 3D"], StandInStreams.Labels([V("a", 3840, 2160, transfer: 16, dv: new VideoDv(8, 6, 1)), V("b", 1920, 1080, transfer: 18, threeD: "sbs")]));
        Assert.Equal(["1080p HEVC (1)", "1080p HEVC (2)"], StandInStreams.Labels([V("a", 1920, 1080), V("b", 1920, 1080)]));
        Assert.Equal(["1080p"], StandInStreams.Labels([V("a", 1920, 1080)]));
    }

    [Fact]
    public void UnknownMarkersArePlainSkips()
    {
        var wanted = PairedSegments.Wanted([new CopySegment("outro", 5000, 6000), new CopySegment("intro", 0, 1000), new CopySegment("credits-scene", 7000, 8000), new CopySegment("recap", 3, 3)]);
        Assert.Equal(
            [(Jellyfin.Database.Implementations.Enums.MediaSegmentType.Intro, 0L, 10_000_000L), (Jellyfin.Database.Implementations.Enums.MediaSegmentType.Outro, 50_000_000L, 60_000_000L), (Jellyfin.Database.Implementations.Enums.MediaSegmentType.Unknown, 70_000_000L, 80_000_000L)],
            wanted);
    }
}
