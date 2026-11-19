using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Federation;
using MediaBrowser.Model.Entities;
using Msfp;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class CatalogTests
{
    private sealed class Source(IReadOnlyList<Library> libraries, IReadOnlyList<CatalogEntry> entries) : ICatalogSource
    {
        public int Reads { get; private set; }

        public IReadOnlyList<Library> Libraries() => libraries;

        public IEnumerable<CatalogEntry> Entries()
        {
            Reads++;
            return entries;
        }
    }

    private static readonly Library[] Libs =
    [
        new() { Id = "aaaa", Name = "Films", Kind = "movies" },
        new() { Id = "bbbb", Name = "Kids", Kind = "movies" },
        new() { Id = "cccc", Name = "Series", Kind = "tvshows" },
    ];

    private static HostCatalog Catalog(out Source source, params CatalogEntry[] entries)
    {
        source = new Source(Libs, entries);
        return new HostCatalog(source);
    }

    [Fact]
    public async Task TitlesAreJoinedByKeyInByteOrderWithTheirLibraries()
    {
        var c = Catalog(out _,
            new CatalogEntry("bbbb", "movie/tmdb/603", "movie", "i2") { Audio = ["fre"], Picture = new Picture { Height = 1080 } },
            new CatalogEntry("aaaa", "movie/tmdb/603", "movie", "i1") { Audio = ["eng", "FRE"], Subtitles = ["eng"], Picture = new Picture { Height = 720 } },
            new CatalogEntry("cccc", "series/tmdb/1920", "series", null),
            new CatalogEntry("aaaa", "movie/tmdb/10331", "movie", "i3"));
        var page = await c.TitlesAsync(["aaaa", "bbbb", "cccc"], "", 100, default);
        Assert.False(page.More);
        Assert.Equal(["movie/tmdb/10331", "movie/tmdb/603", "series/tmdb/1920"], page.Titles.Select(t => t.Key));
        var matrix = Assert.IsType<VideoTitle>(page.Titles[1]);
        Assert.Equal(["bbbb", "aaaa"], matrix.Libraries);
        Assert.Equal(["fre", "eng"], matrix.Audio); // FRE is fre again, without case
        Assert.Equal(["eng"], matrix.Subtitles);
        Assert.Equal(2, matrix.Pictures!.Count);
    }

    [Fact]
    public async Task OnlyTheLibrariesAskedAndPages()
    {
        var c = Catalog(out _,
            new CatalogEntry("aaaa", "movie/tmdb/1", "movie", "i1"),
            new CatalogEntry("bbbb", "movie/tmdb/2", "movie", "i2"),
            new CatalogEntry("aaaa", "movie/tmdb/3", "movie", "i3"),
            new CatalogEntry("aaaa", "movie/tmdb/4", "movie", "i4"));
        var first = await c.TitlesAsync(["aaaa"], "", 2, default);
        Assert.True(first.More);
        Assert.Equal(["movie/tmdb/1", "movie/tmdb/3"], first.Titles.Select(t => t.Key));
        Assert.All(first.Titles, t => Assert.Equal(["aaaa"], t.Libraries));
        var next = await c.TitlesAsync(["aaaa"], "movie/tmdb/3", 2, default);
        Assert.False(next.More);
        Assert.Equal(["movie/tmdb/4"], next.Titles.Select(t => t.Key));
        Assert.Empty((await c.TitlesAsync(["dddd"], "", 10, default)).Titles);
    }

    [Fact]
    public async Task WhatTheEngineChangesIsntTheCatalogs()
    {
        var c = Catalog(out var source, new CatalogEntry("aaaa", "movie/tmdb/1", "movie", "i1") { Audio = ["eng"] });
        var t = (VideoTitle)(await c.TitlesAsync(["aaaa"], "", 10, default)).Titles[0];
        t.Audio!.Add("ger");
        t.Libraries!.Clear();
        var again = (VideoTitle)(await c.TitlesAsync(["aaaa"], "", 10, default)).Titles[0];
        Assert.Equal(["eng"], again.Audio);
        Assert.Equal(["aaaa"], again.Libraries);
        Assert.Equal(1, source.Reads);
        c.Changed();
        await c.TitlesAsync(["aaaa"], "", 10, default);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task CopiesOfTheLibrariesAskedBestFirst()
    {
        var c = Catalog(out _,
            new CatalogEntry("aaaa", "movie/tmdb/603", "movie", "sd") { Picture = new Picture { Height = 480 } },
            new CatalogEntry("aaaa", "movie/tmdb/603", "movie", "hdr") { Picture = new Picture { Height = 2160, Hdr = ["hdr10"] } },
            new CatalogEntry("aaaa", "movie/tmdb/603", "movie", "uhd") { Picture = new Picture { Height = 2160 } },
            new CatalogEntry("bbbb", "movie/tmdb/603", "movie", "kids") { Picture = new Picture { Height = 4320 } },
            new CatalogEntry("aaaa", "movie/tmdb/603", "movie", "unknown"));
        var copies = await c.CopiesAsync("movie/tmdb/603", ["aaaa"], default);
        Assert.Equal(["hdr", "uhd", "sd", "unknown"], copies.Select(x => x.Handle));
        Assert.All(copies, x => Assert.Equal("aaaa", x.Library));
        Assert.Empty(await c.CopiesAsync("movie/tmdb/1", ["aaaa"], default));
        Assert.Empty(await c.CopiesAsync("series/tmdb/1920", ["cccc"], default));
    }

    [Theory]
    [InlineData("603", 603L)]
    [InlineData("9999999999", 9999999999L)]
    [InlineData("0603", null)]
    [InlineData("0", null)]
    [InlineData("10000000000", null)]
    [InlineData("tt0133093", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TmdbIds(string? id, long? expected) => Assert.Equal(expected, JellyfinCatalogSource.TmdbId(id));

    [Theory]
    [InlineData(null, "mixed")]
    [InlineData("movies", "movies")]
    [InlineData("tvshows", "tvshows")]
    [InlineData("music", "music")]
    [InlineData("livetv", "")]
    public void Kinds(string? collectionType, string kind) => Assert.Equal(kind, JellyfinCatalogSource.Kind(collectionType));

    [Fact]
    public void PicturesAsVideoSaysThem()
    {
        // Wider than 16:9: a 16:9 picture's as wide (video section 3.2).
        var scope = JellyfinCatalogSource.PictureOf(new MediaStream { Type = MediaStreamType.Video, Width = 3840, Height = 1606 });
        Assert.Equal(2160, scope!.Height);
        Assert.Null(scope.Hdr);
        // A Dolby Vision Jellyfin finds invalid plays as its base layer.
        var invalid = JellyfinCatalogSource.PictureOf(new MediaStream { Type = MediaStreamType.Video, Width = 1920, Height = 1080, BitDepth = 10, ColorTransfer = "smpte2084", ColorPrimaries = "bt2020", DvVersionMajor = 1, DvProfile = 8, RpuPresentFlag = 1, BlPresentFlag = 1, DvBlSignalCompatibilityId = 1 });
        Assert.Equal(["hdr10"], invalid!.Hdr);
        var hlg = JellyfinCatalogSource.PictureOf(new MediaStream { Type = MediaStreamType.Video, Width = 1920, Height = 1080, BitDepth = 10, ColorTransfer = "arib-std-b67", ColorPrimaries = "bt2020" });
        Assert.Equal(["hlg"], hlg!.Hdr);
        Assert.Null(JellyfinCatalogSource.PictureOf(new MediaStream { Type = MediaStreamType.Video }));
        Assert.Null(JellyfinCatalogSource.PictureOf(null));
    }

    [Theory]
    [InlineData("SDR", null, new string[0])]
    [InlineData("HDR10", null, new[] { "hdr10" })]
    [InlineData("HDR10Plus", null, new[] { "hdr10" })]
    [InlineData("HLG", null, new[] { "hlg" })]
    [InlineData("DOVI", null, new[] { "dv" })]
    [InlineData("DOVIWithHDR10", null, new[] { "dv", "hdr10" })]
    [InlineData("DOVIWithHLG", null, new[] { "dv", "hlg" })]
    [InlineData("DOVIWithSDR", null, new[] { "dv" })]
    [InlineData("DOVIInvalid", "smpte2084", new[] { "hdr10" })]
    [InlineData("DOVIInvalid", "bt709", new string[0])]
    public void HdrFromJellyfinsRangeTypes(string range, string? transfer, string[] hdr) =>
        Assert.Equal(hdr, JellyfinCatalogSource.HdrOf(range, transfer));

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("/", "")]
    [InlineData("jellyfin", "/jellyfin")]
    [InlineData("/jellyfin/", "/jellyfin")]
    public void PrefixFromTheBaseUrl(string? baseUrl, string prefix) => Assert.Equal(prefix, EngineHost.PrefixOf(baseUrl));
}
