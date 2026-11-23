using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Msfp.Catalog;
using MediaBrowser.Model.Entities;
using Msfp.Describe;
using Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>
/// A version's file as an MSFP copy (the migration plan, section 3.1):
/// shaped from msfp-dotnet's describe fixtures (msfp-go's testdata, real
/// ffprobe outputs and a handmade Dolby Vision and Atmos one), with the
/// streams Jellyfin lists for them.
/// </summary>
public class CopiesTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "testdata", "describe");

    internal static JsonObject FixtureJson(string name) => (JsonObject)JsonNode.Parse(File.ReadAllBytes(Path.Combine(Dir, name + ".json")))!;

    internal static Probe Fixture(string name, bool frames = true)
    {
        var f = FixtureJson(name);
        var o = f["outputs"]!;
        byte[] Raw(JsonNode? n) => Encoding.UTF8.GetBytes(n!.ToJsonString());
        var data = (o["data"] as JsonArray)?.Select(Raw).ToList() ?? [];
        return Probe.Parse(f["mediaType"]!.GetValue<string>(), f["size"]!.GetValue<long>(),
            new Outputs(Raw(o["streams"]), data, frames && o["frames"] is { } fr ? Raw(fr) : null));
    }

    // Jellyfin's streams for a probe: the subtitle files beside it first
    // (Index 0…), then the file's streams in its order, attachments left out.
    internal static List<MediaStream> JellyfinStreams(Probe p, params MediaStream[] externals)
    {
        var list = new List<MediaStream>();
        var i = 0;
        foreach (var e in externals)
        {
            e.Index = i++;
            e.IsExternal = true;
            e.Type = MediaStreamType.Subtitle;
            list.Add(e);
        }

        foreach (var s in p.Streams.Where(s => s.CodecType != "attachment"))
        {
            list.Add(new MediaStream
            {
                Index = i++,
                Type = s.CodecType switch
                {
                    "video" when s.Disposition.AttachedPic == 1 => MediaStreamType.EmbeddedImage,
                    "video" => MediaStreamType.Video,
                    "audio" => MediaStreamType.Audio,
                    "subtitle" => MediaStreamType.Subtitle,
                    _ => MediaStreamType.Data,
                },
                Codec = s.CodecName,
                Language = s.Tags.Get("language"),
                Width = s.Width,
                Height = s.Height,
                Channels = s.Channels,
            });
        }

        return list;
    }

    private static Copy Golden(string name) => Copies.Read(File.ReadAllBytes(Path.Combine(Dir, name + ".copy.json")))!;

    private static Copy Done(Copy c) => c with { Id = "v1", ETag = "e1" };

    [Fact]
    public void CodecsStringsColourAndDolbyVision()
    {
        var p = Fixture("handmade-dv-atmos");
        var (c, _) = CopyFacts.Shape(p, JellyfinStreams(p));
        var v = c.Video!;
        Assert.Equal(("hevc", "hvc1.2.4.L153", 10, 3840, 2160), (v.Codec, v.Codecs, v.BitDepth, v.Width, v.Height));
        // H.273: BT.2020 primaries and matrix, PQ, limited range.
        Assert.Equal(new VideoColour(9, 16, 9, false), v.Colour);
        // Dolby Vision whenever there is: profile 8, its base hvc1 string.
        Assert.Equal(new VideoDv(8, 6, 1) { El = false }, v.Dv);
        // hdr only for HDR10+ (HDR10 is the colour's).
        Assert.Equal(["hdr10plus"], v.Hdr);
        Assert.Equal("24000/1001", v.Fps);
        Assert.Equal("video/mp4", c.MediaType);
    }

    [Fact]
    public void ObjectsAudioCodecsAndRoles()
    {
        var p = Fixture("handmade-dv-atmos");
        var (c, _) = CopyFacts.Shape(p, JellyfinStreams(p));
        Assert.Equal(
            [("a1", "eac3", "ec-3", "dolby-atmos"), ("a2", "truehd", "mlpa", "dolby-atmos"), ("a3", "dts", "dtsh", "dts-x"), ("a4", "aac", "mp4a.40.29", null)],
            c.AudioTracks.Select(a => (a.Id, a.Codec, a.Codecs, a.Objects)));
        // original from the file's flag only; roles from the dispositions.
        Assert.True(c.AudioTracks[0].Original);
        Assert.False(c.AudioTracks[1].Original);
        Assert.Equal("dub", c.AudioTracks[2].Role);
        Assert.Equal("description", c.AudioTracks[3].Role);
        Assert.Equal(["en", "en", "fr", "en"], c.AudioTracks.Select(a => a.Language));
    }

    [Theory]
    [InlineData("h264-aac-mp4", "video/mp4", "avc1.64000C")]
    [InlineData("hevc-hdr10-mkv", "video/matroska", "hvc1.2.4.L60.90")]
    [InlineData("av1-mp4", "video/mp4", null)]
    [InlineData("tone-flac", "audio/flac", null)]
    [InlineData("vp9-opus-webm", "video/webm", null)]
    public void MediaTypesAndVideoCodecs(string name, string mediaType, string? codecs)
    {
        var p = Fixture(name);
        var (c, _) = CopyFacts.Shape(p, JellyfinStreams(p));
        Assert.Equal(mediaType, c.MediaType);
        if (codecs is not null)
        {
            Assert.Equal(codecs, c.Video!.Codecs);
        }

        // The codecs string is required for these codecs (video section 3.5).
        if (c.Video is { Codec: "h264" or "hevc" or "av1" or "vp9" } v)
        {
            Assert.False(string.IsNullOrEmpty(v.Codecs));
        }

        // No picture but cover art: an audio type.
        Assert.Equal(mediaType.StartsWith("audio/", StringComparison.Ordinal), c.Video is null);
    }

    // Each fixture shaped as Describe does it, the plugin's only changes
    // being the files beside it (none here) and the chapters' generic names:
    // against Go's golden copy (with the golden's own external track).
    [Theory]
    [InlineData("h264-aac-mp4")]
    [InlineData("hevc-hdr10-mkv")]
    [InlineData("handmade-dv-atmos")]
    [InlineData("with-font-mkv")]
    [InlineData("dts-truehd-3d-mkv")]
    [InlineData("hevc-hlg-atc-ts")]
    [InlineData("av1-mp4")]
    public void ShapedAsDescribeAndGoWithTheFilesBeside(string name)
    {
        var p = Fixture(name);
        var (c, _) = CopyFacts.Shape(p, JellyfinStreams(p, new MediaStream { Codec = "srt", Language = "fre", IsForced = true }));
        var golden = Golden(name);
        golden = golden with { Chapters = CopyFacts.CleanChapters(golden.Chapters) };
        Assert.Equal(Encoding.UTF8.GetString(Copies.Write(Done(golden))), Encoding.UTF8.GetString(Copies.Write(Done(c))));
        // Read back unchanged by the protocol's reader, as a peer keeps it.
        var back = Copies.Read(Copies.Write(Done(c)));
        Assert.NotNull(back);
        Assert.Equal(Encoding.UTF8.GetString(Copies.Write(Done(c))), Encoding.UTF8.GetString(Copies.Write(back!)));
    }

    [Fact]
    public void TrackMapToJellyfinsIndexesExternalsFirst()
    {
        // hevc-hdr10-mkv: 0 hevc, 1 eac3, 2 ac3, 3 subrip, 4 ass. Jellyfin:
        // the files beside it first (srt 0, ass 1), then the file's streams.
        var p = Fixture("hevc-hdr10-mkv");
        var streams = JellyfinStreams(p,
            new MediaStream { Codec = "subrip", Language = "fre" },
            new MediaStream { Codec = "ass", Language = "eng" });
        var (c, map) = CopyFacts.Shape(p, streams);
        Assert.Equal(new Dictionary<string, int> { ["v"] = 2, ["a1"] = 3, ["a2"] = 4, ["s3"] = 5, ["s4"] = 6, ["s5"] = 0 }, map);
        // The ASS file beside it can't be served apart: left out. The one
        // listed is above the file's streams.
        var external = Assert.Single(c.SubtitleTracks, s => s.External);
        Assert.Equal(("s5", "subrip", "fr"), (external.Id, external.Format, external.Language));
        // Embedded ASS stays: it comes with the file's bytes.
        Assert.Contains(c.SubtitleTracks, s => s is { Id: "s4", Format: "ass", External: false });
    }

    [Fact]
    public void TrackMapSkipsAKindJellyfinCountsOtherwise()
    {
        var p = Fixture("hevc-hdr10-mkv");
        var streams = JellyfinStreams(p).Where(s => !(s.Type == MediaStreamType.Audio && s.Index == 2)).ToList();
        var (_, map) = CopyFacts.Shape(p, streams);
        Assert.DoesNotContain("a1", map.Keys);
        Assert.DoesNotContain("a2", map.Keys);
        Assert.Equal(0, map["v"]);
        Assert.Equal(3, map["s3"]);
    }

    [Fact]
    public void AttachmentsAndCoverArtAreNoTracks()
    {
        var p = Fixture("with-font-mkv");
        var (c, map) = CopyFacts.Shape(p, JellyfinStreams(p));
        Assert.Equal(["s1"], c.SubtitleTracks.Select(s => s.Id));
        Assert.Equal(1, map["s1"]);
        p = Fixture("handmade-dv-atmos");
        (c, map) = CopyFacts.Shape(p, JellyfinStreams(p));
        // The mjpeg cover (index 8) is no picture and no track.
        Assert.Equal(0, map["v"]);
        Assert.DoesNotContain(map.Keys, k => k.EndsWith('8'));
        Assert.Equal(3840, c.Video!.Width);
    }

    [Theory]
    [InlineData("Chapter 1", true)]
    [InlineData("chapter 12", true)]
    [InlineData("Chapitre 03", true)]
    [InlineData("Capítulo 2", true)]
    [InlineData("Kapitel 7", true)]
    [InlineData("Ch. 4", true)]
    [InlineData("12", true)]
    [InlineData("00:05:00.000", true)]
    [InlineData("1:02:03", true)]
    [InlineData("The Desert", false)]
    [InlineData("Chapter One", false)]
    [InlineData("1984", true)]
    [InlineData("Act 1", false)]
    public void GenericChapterNames(string name, bool generic) => Assert.Equal(generic, CopyFacts.GenericName(name));

    [Fact]
    public void ChaptersKeepTheirStartsAndCleanNames()
    {
        var cleaned = CopyFacts.CleanChapters([new CopyChapter(0, "Chapter 1"), new CopyChapter(1000, "The Desert"), new CopyChapter(2000, "www.example.org"),
            new CopyChapter(3000, "movie.mkv"), new CopyChapter(4000)]);
        Assert.Equal([new CopyChapter(0), new CopyChapter(1000, "The Desert"), new CopyChapter(2000), new CopyChapter(3000), new CopyChapter(4000)], cleaned);
    }

    [Theory]
    [InlineData("srt", "subrip")]
    [InlineData("SubRip", "subrip")]
    [InlineData("vtt", "webvtt")]
    [InlineData("PGSSUB", "hdmv_pgs_subtitle")]
    [InlineData("dvdsub", "dvd_subtitle")]
    [InlineData("dvbsub", "dvb_subtitle")]
    [InlineData("ass", "ass")]
    [InlineData("", "")]
    [InlineData("not a token", "")]
    public void SubtitleFormats(string codec, string format) => Assert.Equal(format, CopyFacts.SubtitleFormat(codec));

    [Fact]
    public void OnlyFilesServedApartAreListedBeside()
    {
        var ext = CopyFacts.Externals(
        [
            new MediaStream { Index = 3, IsExternal = true, Type = MediaStreamType.Subtitle, Codec = "ass" },
            new MediaStream { Index = 1, IsExternal = true, Type = MediaStreamType.Subtitle, Codec = "srt" },
            new MediaStream { Index = 2, IsExternal = true, Type = MediaStreamType.Subtitle, Codec = "PGSSUB" },
            new MediaStream { Index = 0, IsExternal = true, Type = MediaStreamType.Subtitle, Codec = "dvdsub" },
            new MediaStream { Index = 4, IsExternal = true, Type = MediaStreamType.Subtitle, Codec = "dvbsub" },
            new MediaStream { Index = 5, IsExternal = false, Type = MediaStreamType.Subtitle, Codec = "srt" },
        ]);
        Assert.Equal([1, 2], ext.Select(s => s.Index));
    }

    [Fact]
    public void MinimalFromJellyfinsData()
    {
        var streams = new List<MediaStream>
        {
            new() { Index = 0, IsExternal = true, Type = MediaStreamType.Subtitle, Codec = "srt" },
            new() { Index = 1, Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080 },
            new() { Index = 2, Type = MediaStreamType.Audio, Codec = "dca", Language = "fre", Channels = 6, IsDefault = true },
            new() { Index = 3, Type = MediaStreamType.Audio, Codec = "aac", Language = "eng", Channels = 2 },
        };
        var (c, map) = CopyFacts.Minimal("video/x-matroska", "mkv", 36_000_000_000, streams);
        Assert.Equal(("video/matroska", 3_600_000L), (c.MediaType, c.DurationMs));
        Assert.Equal(("h264", 1920, 1080, (string?)null), (c.Video!.Codec, c.Video.Width, c.Video.Height, c.Video.Codecs));
        // The file's indexes: Jellyfin's less the one file beside it.
        Assert.Equal([("a1", "dts", "fr", 6), ("a2", "aac", "en", 2)], c.AudioTracks.Select(a => (a.Id, a.Codec, a.Language!, a.Channels!.Value)));
        Assert.Equal(new Dictionary<string, int> { ["v"] = 1, ["a1"] = 2, ["a2"] = 3 }, map);
        // No first bytes known: Jellyfin's container.
        Assert.Equal("video/mp4", CopyFacts.Minimal(null, "mov,mp4,m4a,3gp,3g2,mj2", 0, streams).Copy.MediaType);
        // Streams not read yet: a video.
        Assert.StartsWith("video/", CopyFacts.Minimal(null, "mkv", 0, []).Copy.MediaType);
        Assert.NotNull(Copies.Read(Copies.Write(c with { Id = "v1", ETag = "e1", Size = 1000 })));
    }

    // ---- Fingerprint and etag ------------------------------------------------

    // Overfin's files.Fingerprint and copies.ETag (msfp-model), run on the
    // same files with the key 0, 1, … 31: the two give a file the same.
    [Theory]
    [InlineData(0, "0-af5570f5a1810b7af78caf4bc70a660f", "ZrTzNwPIxXzOsMmWnUnYYs")]
    [InlineData(1, "1-0edad088f6dae6d96a9708d797b88e9f", "MSNy5MhzmzWEeXG8h4Sge-")]
    [InlineData(1000, "rs-4f5a4a072883cebac5d9c9d66dd2acdb", "ZKU4ioQsdm00tG4Bfk3g-s")]
    [InlineData(262144, "5m9s-970aafecf8ec4f3ac3ad07807cf93e24", "FG13muQgvKzqcZPTCo4pcR")]
    [InlineData(600000, "cuyo-18424f89e5cd43fb3dc43ea41b58b91e", "swDmdj4gj3Y5SduGmkHEO5")]
    [InlineData(2097152, "18y68-51f909e60f7e2be574ac2af166b0bd1a", "iYqLKNYjEuXUKhLk1LiYUF")]
    [InlineData(5242883, "34dfn-e3b1163174e944ab17918733cc45cb2c", "l0bdMN9klDtmyZUc3QZE_l")]
    public void FingerprintAndEtagAsOverfins(long size, string fingerprint, string etag)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "f.bin");
        var b = new byte[size];
        for (long i = 0; i < size; i++)
        {
            b[i] = (byte)(((i * 31) + 7) & 0xff);
        }

        File.WriteAllBytes(path, b);
        using var h = File.OpenHandle(path);
        var fp = Fingerprints.Of(h, size);
        Assert.Equal(fingerprint, fp);
        Assert.True(Fingerprints.Form(fp));
        Assert.Equal(etag, Fingerprints.ETag(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), fp));
        Assert.True(global::Msfp.Protocol.Playback.Id(etag));
    }

    [Fact]
    public void TheEtagIsTheFilesContentsNotItsPlace()
    {
        using var dir = new TempDir();
        var a = Path.Combine(dir.Path, "a.mkv");
        File.WriteAllBytes(a, Enumerable.Range(0, 900_000).Select(i => (byte)i).ToArray());
        var key = Fingerprints.Key(dir.Path);
        string Etag(string p)
        {
            using var h = File.OpenHandle(p);
            return Fingerprints.ETag(key, Fingerprints.Of(h, new FileInfo(p).Length));
        }

        var before = Etag(a);
        // Moved and touched: the same.
        var b = Path.Combine(dir.Path, "sub", "b.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.Move(a, b);
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddDays(-3));
        Assert.Equal(before, Etag(b));
        // Edited at its start: another.
        using (var f = File.OpenWrite(b))
        {
            f.WriteByte(0xEE);
        }

        Assert.NotEqual(before, Etag(b));
        // Another key: another etag of the same file (nothing matched across servers).
        Assert.NotEqual(Fingerprints.ETag(key, "1-0edad088f6dae6d96a9708d797b88e9f"), Fingerprints.ETag(new byte[32], "1-0edad088f6dae6d96a9708d797b88e9f"));
    }

    [Fact]
    public void TheEtagKeyIsItsOwnFileMode600MadeOnce()
    {
        using var dir = new TempDir();
        var k1 = Fingerprints.Key(dir.Path);
        Assert.Equal(32, k1.Length);
        var path = Path.Combine(dir.Path, "etag.key");
        Assert.Equal(k1, File.ReadAllBytes(path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        Assert.Equal(k1, Fingerprints.Key(dir.Path));
        // Not 32 bytes: made again.
        File.WriteAllBytes(path, [1, 2, 3]);
        var k2 = Fingerprints.Key(dir.Path);
        Assert.Equal(32, k2.Length);
        Assert.NotEqual(k1, k2);
        Assert.False(File.Exists(path + ".new"));
    }

    // ---- Kept probes and the header read ---------------------------------------

    [Fact]
    public void KeptProbesHoldNoPathNorAttachment()
    {
        var p = Fixture("with-font-mkv");
        var tree = (JsonObject)JsonNode.Parse(p.Outputs.Streams)!;
        tree["format"]!["filename"] = "file:/proc/1/fd/7";
        ((JsonObject)tree["streams"]![0]!["tags"]!)["comment"] = "ripped from /media/Films/Secret/x.mkv";
        var withPath = Probe.Parse(p.MediaType, p.Size, p.Outputs with { Streams = Encoding.UTF8.GetBytes(tree.ToJsonString()) });
        var kept = Probes.Keepable(withPath, "/media/Films");
        var text = Encoding.UTF8.GetString(kept);
        Assert.DoesNotContain("/proc/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/media/Films", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"attachment\"", text, StringComparison.Ordinal);
        var back = Probes.Parse(kept)!;
        Assert.Equal(Copies.Write(Done(CopyFacts.Shape(p, JellyfinStreams(p)).Copy)), Copies.Write(Done(CopyFacts.Shape(back, JellyfinStreams(p)).Copy)));
    }

    // A header read's record as the read made for Overfin writes it: one run
    // with -show_data, each stream's extradata in its own entry.
    internal static JsonObject HeaderProbe(string name)
    {
        var f = FixtureJson(name);
        var first = (JsonObject)f["outputs"]!["streams"]!.DeepClone();
        foreach (var run in f["outputs"]!["data"] as JsonArray ?? [])
        {
            foreach (var ds in run!["streams"] as JsonArray ?? [])
            {
                var index = ds!["index"]!.GetValue<int>();
                var s = (first["streams"] as JsonArray)!.OfType<JsonObject>().Single(x => x["index"]!.GetValue<int>() == index);
                foreach (var k in new[] { "extradata", "extradata_size" })
                {
                    if (ds[k] is { } v)
                    {
                        s[k] = v.DeepClone();
                    }
                }

                s["extradata_hash"] = "adler32:00000000";
            }
        }

        first["format"]!["filename"] = "/media/Films/A (2001)/a.mkv";
        return first;
    }

    [Theory]
    [InlineData("h264-aac-mp4")]
    [InlineData("with-font-mkv")]
    [InlineData("hevc-hdr10-mkv")]
    [InlineData("handmade-dv-atmos")]
    public void AHeaderReadShapesAsTheProbersOwn(string name)
    {
        var own = Fixture(name, frames: false);
        var converted = Probes.Convert(own.MediaType, own.Size, HeaderProbe(name));
        Assert.Equal(Copies.Write(Done(CopyFacts.Shape(own, JellyfinStreams(own)).Copy)), Copies.Write(Done(CopyFacts.Shape(converted, JellyfinStreams(own)).Copy)));
        Assert.DoesNotContain("/media/", Encoding.UTF8.GetString(Probes.Keepable(converted, "/media")), StringComparison.Ordinal);
        // An HDR picture still owes its frame read.
        Assert.Equal(own.NeedsFrames(), converted.NeedsFrames());
    }

    [Fact]
    public void HeaderReadLinesPlainOrGzipped()
    {
        var text = "{\"a\":1}\n\n  \n{\"b\":2}\r\n" + "{\"c\":" + new string('1', 10) + "}";
        var plain = Encoding.UTF8.GetBytes(text);
        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, true))
        {
            z.Write(plain);
        }

        foreach (var bytes in new[] { plain, gz.ToArray() })
        {
            var lines = HeaderRead.Lines(new MemoryStream(bytes)).Select(l => l is null ? null : Encoding.UTF8.GetString(l).Trim()).ToList();
            Assert.Equal(["{\"a\":1}", "{\"b\":2}", "{\"c\":1111111111}"], lines);
        }
    }

    [Fact]
    public void AHeaderReadLineTooLongIsDropped()
    {
        var big = new string('x', HeaderRead.MaxLine + 10);
        var lines = HeaderRead.Lines(new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}\n" + big + "\n{\"b\":2}\n"))).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Null(lines[1]);
        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString(lines[2]!));
    }

    [Theory]
    [InlineData("Films/A (2001)/a.mkv", true)]
    [InlineData("a.mkv", true)]
    [InlineData("/media/a.mkv", false)]
    [InlineData("../a.mkv", false)]
    [InlineData("Films/../../etc/passwd", false)]
    [InlineData("Films//a.mkv", false)]
    [InlineData("./a.mkv", false)]
    [InlineData("", false)]
    [InlineData("a\0.mkv", false)]
    public void HeaderReadPathsStayInside(string path, bool ok) => Assert.Equal(ok, HeaderRead.LocalPath(path));

    [Fact]
    public void HeaderReadRecords()
    {
        Assert.NotNull(HeaderRead.Record(Encoding.UTF8.GetBytes("{\"size\":1,\"mtime\":2,\"path\":\"a.mkv\",\"probe\":{\"streams\":[]},\"stderr\":\"x\"}")));
        Assert.Null(HeaderRead.Record(Encoding.UTF8.GetBytes("{\"size\":-1,\"mtime\":2,\"path\":\"a.mkv\",\"probe\":{}}")));
        Assert.Null(HeaderRead.Record(Encoding.UTF8.GetBytes("{\"size\":1,\"mtime\":2,\"path\":\"a.mkv\"}")));
        Assert.Null(HeaderRead.Record(Encoding.UTF8.GetBytes("not json")));
        Assert.Null(HeaderRead.Record(Encoding.UTF8.GetBytes("{\"size\":\"1\",\"mtime\":2,\"path\":\"a.mkv\",\"probe\":{}}")));
    }

    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "msfp-copies-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
