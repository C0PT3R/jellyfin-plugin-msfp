using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Msfp.Catalog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Msfp.Protocol;
using Xunit;
using TempDir = Jellyfin.Plugin.Msfp.Tests.CopiesTests.TempDir;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>Which files the owner side reads, and the header read imported on a made-up corpus.</summary>
public class CopyFilesTests
{
    [Fact]
    public void OnlyRegularFilesInsideTheLibrarysFolders()
    {
        using var dir = new TempDir();
        var lib = Directory.CreateDirectory(Path.Combine(dir.Path, "Films")).FullName;
        var inner = Directory.CreateDirectory(Path.Combine(lib, "Kids")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(dir.Path, "Private")).FullName;
        File.WriteAllBytes(Path.Combine(lib, "a.mkv"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(inner, "k.mkv"), [1]);
        File.WriteAllBytes(Path.Combine(outside, "secret.mkv"), [9]);
        var files = new LibraryFiles([lib, inner, Path.Combine(dir.Path, "gone")]);
        using (var f = files.Open(Path.Combine(lib, "a.mkv")))
        {
            Assert.NotNull(f);
            Assert.Equal((3L, "a.mkv"), (f!.Size, f.Rel));
        }

        // The deepest folder holding it.
        using (var f = files.Open(Path.Combine(lib, "Kids", "k.mkv")))
        {
            Assert.Equal(("k.mkv", inner), (f!.Rel, f.Root));
        }

        Assert.Null(files.Open(Path.Combine(outside, "secret.mkv")));
        Assert.Null(files.Open(Path.Combine(lib, "..", "Private", "secret.mkv")));
        Assert.Null(files.Open(Path.Combine(lib, "missing.mkv")));
        Assert.Null(files.Open(lib));
        Assert.Null(files.Open("a.mkv"));
        Assert.Null(files.Open(null));
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A link leading out of the library: refused; one inside: its target.
        File.CreateSymbolicLink(Path.Combine(lib, "out.mkv"), Path.Combine(outside, "secret.mkv"));
        Assert.Null(files.Open(Path.Combine(lib, "out.mkv")));
        Directory.CreateSymbolicLink(Path.Combine(lib, "dirlink"), outside);
        Assert.Null(files.Open(Path.Combine(lib, "dirlink", "secret.mkv")));
        File.CreateSymbolicLink(Path.Combine(lib, "in.mkv"), Path.Combine(lib, "a.mkv"));
        using (var f = files.Open(Path.Combine(lib, "in.mkv")))
        {
            Assert.Equal("a.mkv", f!.Rel);
        }

        // A FIFO is never opened (it would block).
        var fifo = Path.Combine(lib, "fifo.mkv");
        using (var p = Process.Start("mkfifo", fifo))
        {
            p.WaitForExit();
        }

        Assert.True(File.Exists(fifo));
        Assert.Null(files.Open(fifo));
        Assert.True(new LibraryFiles([Path.Combine(dir.Path, "none")]).Empty);
    }

    [Theory]
    [InlineData(ExtraType.Trailer, "trailer")]
    [InlineData(ExtraType.Featurette, "featurette")]
    [InlineData(ExtraType.BehindTheScenes, "behindthescenes")]
    [InlineData(ExtraType.DeletedScene, "deletedscene")]
    [InlineData(ExtraType.Interview, "interview")]
    [InlineData(ExtraType.Scene, "scene")]
    [InlineData(ExtraType.Short, "short")]
    [InlineData(ExtraType.Clip, "clip")]
    [InlineData(ExtraType.Sample, "sample")]
    [InlineData(ExtraType.ThemeSong, "themesong")]
    [InlineData(ExtraType.ThemeVideo, "themevideo")]
    [InlineData(ExtraType.Unknown, null)]
    [InlineData(null, null)]
    public void ExtraTypes(ExtraType? type, string? token) => Assert.Equal(token, CopyKeeper.ExtraTypeToken(type));

    [Fact]
    public void OneFileOnly()
    {
        Assert.True(CopyKeeper.OneFile(new Movie { Path = "/m/a.mkv", VideoType = VideoType.VideoFile }));
        Assert.False(CopyKeeper.OneFile(new Movie { Path = "/m/a", VideoType = VideoType.BluRay }));
        Assert.False(CopyKeeper.OneFile(new Movie { Path = "/m/a.iso", VideoType = VideoType.Iso }));
        Assert.False(CopyKeeper.OneFile(new Movie { Path = "/m/a-cd1.avi", VideoType = VideoType.VideoFile, AdditionalParts = ["/m/a-cd2.avi"] }));
        Assert.False(CopyKeeper.OneFile(new Movie { Path = "/m/a.strm", VideoType = VideoType.VideoFile, IsShortcut = true }));
        Assert.False(CopyKeeper.OneFile(new Movie { VideoType = VideoType.VideoFile }));
    }

    [Fact]
    public void SegmentsInMsfpsKinds()
    {
        const long s = TimeSpan.TicksPerSecond;
        var got = CopyKeeper.Segments(
        [
            (MediaSegmentType.Outro, 3000 * s, 3100 * s),
            (MediaSegmentType.Intro, 10 * s, 70 * s),
            (MediaSegmentType.Unknown, 0, 5 * s),
            (MediaSegmentType.Recap, 5 * s, 5 * s),
            (MediaSegmentType.Commercial, 3050 * s, 3200 * s),
            (MediaSegmentType.Preview, 3300 * s, 3400 * s),
        ], 3150 * s);
        Assert.Equal([new CopySegment("intro", 10_000, 70_000), new CopySegment("outro", 3_000_000, 3_100_000), new CopySegment("commercial", 3_050_000, 3_150_000)], got);
    }

    [Fact]
    public void InputsChangeWithTheFilesBesideNotTheirTitles()
    {
        var a = new List<MediaStream> { new() { Index = 0, Type = MediaStreamType.Video }, new() { Index = 1, Type = MediaStreamType.Subtitle, IsExternal = true, Codec = "srt", Language = "fre", Title = "x" } };
        var b = new List<MediaStream> { new() { Index = 0, Type = MediaStreamType.Video }, new() { Index = 1, Type = MediaStreamType.Subtitle, IsExternal = true, Codec = "srt", Language = "fre", Title = "y" } };
        var c = new List<MediaStream> { new() { Index = 0, Type = MediaStreamType.Video }, new() { Index = 1, Type = MediaStreamType.Subtitle, IsExternal = true, Codec = "srt", Language = "eng" } };
        Assert.Equal(CopyKeeper.InputsOf(a), CopyKeeper.InputsOf(b));
        Assert.NotEqual(CopyKeeper.InputsOf(a), CopyKeeper.InputsOf(c));
    }

    // ---- The header read, on a made-up corpus -------------------------------

    private static void MakeFile(string path, byte[] head, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var b = new byte[size];
        head.CopyTo(b, 0);
        for (var i = head.Length; i < size; i++)
        {
            b[i] = (byte)(i * 7);
        }

        File.WriteAllBytes(path, b);
    }

    // An ISO BMFF file's first box (ftyp isom) and a Matroska EBML header.
    private static readonly byte[] Mp4Head = [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0,
        (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'i', (byte)'s', (byte)'o', (byte)'2', (byte)'a', (byte)'v', (byte)'c', (byte)'1', (byte)'m', (byte)'p', (byte)'4', (byte)'1'];

    private static readonly byte[] MkvHead = [0x1A, 0x45, 0xDF, 0xA3, 0xA3, 0x42, 0x86, 0x81, 0x01, 0x42, 0xF7, 0x81, 0x01, 0x42, 0xF2, 0x81, 0x04, 0x42, 0xF3, 0x81, 0x08,
        0x42, 0x82, 0x88, (byte)'m', (byte)'a', (byte)'t', (byte)'r', (byte)'o', (byte)'s', (byte)'k', (byte)'a', 0x42, 0x87, 0x81, 0x04, 0x42, 0x85, 0x81, 0x02];

    private static string Line(string rel, string path, string probe, long? mtime = null) =>
        new JsonObject
        {
            ["size"] = new FileInfo(path).Length,
            ["mtime"] = mtime ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToUnixTimeSeconds(),
            ["path"] = rel,
            ["probe"] = CopiesTests.HeaderProbe(probe),
            ["stderr"] = "",
        }.ToJsonString();

    [Fact]
    public void ImportsAMadeUpHeaderRead()
    {
        using var dir = new TempDir();
        var media = Directory.CreateDirectory(Path.Combine(dir.Path, "media")).FullName;
        var films = Path.Combine(media, "Films");
        var a = Path.Combine(films, "A (2001)", "a.mp4");
        var b = Path.Combine(films, "B (2002)", "b.mkv");
        var c = Path.Combine(films, "C (2003)", "c.mkv");
        var d = Path.Combine(films, "D (2004)", "d.mkv");
        var e = Path.Combine(media, "Elsewhere", "e.mkv");
        MakeFile(a, Mp4Head, 700_000);
        MakeFile(b, MkvHead, 300_000);
        MakeFile(c, MkvHead, 400_000);
        MakeFile(d, [0x47, 0x40, 0, 0x10], 200_000);
        MakeFile(e, MkvHead, 100_000);
        var files = new LibraryFiles([films]);
        var listed = new Dictionary<string, (BaseItem, LibraryFiles)>(StringComparer.Ordinal);
        foreach (var p in new[] { a, b, c, d, e })
        {
            listed[p] = (new Movie { Path = p, VideoType = VideoType.VideoFile }, files);
        }

        var read = string.Join('\n',
            Line("Films/A (2001)/a.mp4", a, "h264-aac-mp4"),
            Line("Films/B (2002)/b.mkv", b, "with-font-mkv"),
            // Changed since the read.
            Line("Films/C (2003)/c.mkv", c, "with-font-mkv", mtime: 1),
            // First bytes of no type that is probed (a lone TS packet header).
            Line("Films/D (2004)/d.mkv", d, "with-font-mkv"),
            // Listed, but outside its library's folders.
            Line("Elsewhere/e.mkv", e, "with-font-mkv"),
            // Not a file this server lists; a path out of the root; not a record.
            Line("Films/A (2001)/a.mp4", a, "h264-aac-mp4").Replace("Films/A (2001)/a.mp4", "Films/Z/z.mkv", StringComparison.Ordinal),
            Line("Films/A (2001)/a.mp4", a, "h264-aac-mp4").Replace("Films/A (2001)/a.mp4", "../media/Films/A (2001)/a.mp4", StringComparison.Ordinal),
            "{\"size\": 1, \"path\": \"x\"}",
            "") + "\n";
        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, true))
        {
            z.Write(Encoding.UTF8.GetBytes(read));
        }

        var probes = new Probes(Path.Combine(dir.Path, "probes"));
        var counts = CopyKeeper.Import(probes, new MemoryStream(gz.ToArray()), media, listed, default);
        Assert.Equal((8, 2, 0, 1, 1, 2, 1, 1), (counts.Records, counts.Imported, counts.Already, counts.Changed, counts.Unknown, counts.Refused, counts.Unlisted, counts.Bad));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(dir.Path, "probes")).Length);
        // Kept by fingerprint, without the paths.
        foreach (var f in Directory.GetFiles(Path.Combine(dir.Path, "probes")))
        {
            Assert.True(Fingerprints.Form(Path.GetFileNameWithoutExtension(f)));
            var text = File.ReadAllText(f);
            Assert.DoesNotContain(media, text, StringComparison.Ordinal);
            Assert.DoesNotContain("/media/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("A (2001)", text, StringComparison.Ordinal);
        }

        // The copy shaped from the import is the prober's own, the file's size.
        using (var h = File.OpenHandle(a))
        {
            var kept = probes.Get(Fingerprints.Of(h, 700_000))!;
            Assert.Equal(700_000, kept.Size);
            Assert.Equal("video/mp4", kept.MediaType);
            var own = CopiesTests.Fixture("h264-aac-mp4");
            var mine = CopyFacts.Shape(kept, CopiesTests.JellyfinStreams(own)).Copy with { Id = "v1", ETag = "e1", Size = 1 };
            var theirs = CopyFacts.Shape(own, CopiesTests.JellyfinStreams(own)).Copy with { Id = "v1", ETag = "e1", Size = 1 };
            Assert.Equal(Copies.Write(theirs), Copies.Write(mine));
        }

        // Run again: what was kept is kept.
        var again = CopyKeeper.Import(probes, new MemoryStream(gz.ToArray()), media, listed, default);
        Assert.Equal((0, 2), (again.Imported, again.Already));
    }
}
