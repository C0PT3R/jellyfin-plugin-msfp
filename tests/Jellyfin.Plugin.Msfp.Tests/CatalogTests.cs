using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Federation;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.Msfp.Identification;
using Msfp;
using Msfp.Protocol;
using VideoTitle = Msfp.VideoTitle;
using Place = Msfp.Place;
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

        public ValueTask<byte[]?> ImageAsync(Guid item, CancellationToken ct) =>
            ValueTask.FromResult<byte[]?>(item == Poster ? [0xFF, 0xD8, 0xFF] : null);
    }

    private static readonly Guid Poster = Guid.NewGuid();

    private static readonly Library[] Libs =
    [
        new() { Id = "aaaa", Name = "Films" },
        new() { Id = "bbbb", Name = "Kids" },
        new() { Id = "cccc", Name = "Series" },
    ];

    private static HostCatalog Catalog(out Source source, params CatalogEntry[] entries)
    {
        source = new Source(Libs, entries);
        return new HostCatalog(source);
    }

    private static Copy C(string id, int height = 1080) =>
        new(id, 3_600_000, "video/matroska", 1000, "e" + id, [new CopyAudio("a1", "aac") { Codecs = "mp4a.40.2", Language = "en" }])
        {
            Video = new VideoStream("h264", height * 16 / 9, height) { Codecs = "avc1.640028" },
        };

    [Fact]
    public async Task TitlesAreJoinedByKeyInByteOrderWithTheirLibraries()
    {
        var c = Catalog(out _,
            new CatalogEntry("bbbb", "movie/tmdb/603", "movie") { Copies = [C("i2")] },
            new CatalogEntry("aaaa", "movie/tmdb/603", "movie") { Copies = [C("i1")] },
            new CatalogEntry("cccc", "series/tmdb/1920", "series"),
            new CatalogEntry("aaaa", "movie/tmdb/10331", "movie") { Copies = [C("i3")] });
        var page = await c.TitlesAsync(["aaaa", "bbbb", "cccc"], "", 100, default);
        Assert.False(page.More);
        Assert.Equal(["movie/tmdb/10331", "movie/tmdb/603", "series/tmdb/1920"], page.Titles.Select(t => t.Key));
        var matrix = Assert.IsType<VideoTitle>(page.Titles[1]);
        Assert.Equal(["bbbb", "aaaa"], matrix.Libraries);
        Assert.Equal(["i2", "i1"], matrix.Copies!.Select(x => x.Id));
        Assert.Null(Assert.IsType<VideoTitle>(page.Titles[2]).Copies);
    }

    [Fact]
    public async Task TitlesCarryTheirCopiesInTheOwnersOrderOnlyToTheLibrariesHoldingThem()
    {
        var c = Catalog(out _,
            new CatalogEntry("aaaa", "movie/tmdb/603", "movie") { Copies = [C("default", 720), C("uhd", 2160), C("sd", 480)] },
            new CatalogEntry("bbbb", "movie/tmdb/603", "movie") { Copies = [C("kids", 4320), C("uhd", 2160)] });
        // Jellyfin's order (its default version first), never re-ranked.
        var both = (VideoTitle)(await c.TitlesAsync(["aaaa", "bbbb"], "", 10, default)).Titles.Single();
        Assert.Equal(["default", "uhd", "sd", "kids"], both.Copies!.Select(x => x.Id));
        // A peer offered Kids only never sees Films' copies.
        var kids = (VideoTitle)(await c.TitlesAsync(["bbbb"], "", 10, default)).Titles.Single();
        Assert.Equal(["uhd", "kids"], kids.Copies!.Select(x => x.Id));
        Assert.Equal(["bbbb"], kids.Libraries);
        Assert.Equal("default", ((VideoTitle)(await c.TitlesAsync(["aaaa"], "", 10, default)).Titles.Single()).BestCopy()!.Id);
    }

    [Fact]
    public async Task AFilmWithNoCopyInTheLibrariesAskedIsntListed()
    {
        var c = Catalog(out _,
            new CatalogEntry("aaaa", "movie/tmdb/1", "movie") { Copies = [C("i1")] },
            new CatalogEntry("bbbb", "movie/tmdb/1", "movie"));
        Assert.Empty((await c.TitlesAsync(["bbbb"], "", 10, default)).Titles);
        Assert.Single((await c.TitlesAsync(["aaaa", "bbbb"], "", 10, default)).Titles);
    }

    [Fact]
    public async Task AtMostSixteenCopies()
    {
        var c = Catalog(out _, new CatalogEntry("aaaa", "movie/tmdb/1", "movie") { Copies = Enumerable.Range(0, 20).Select(i => C("i" + i)).ToList() });
        var t = (VideoTitle)(await c.TitlesAsync(["aaaa"], "", 10, default)).Titles.Single();
        Assert.Equal(CopyKeeper.MaxCopies, t.Copies!.Count);
        Assert.Equal("i0", t.Copies[0].Id);
    }

    [Fact]
    public async Task ExtrasAndLocalTitles()
    {
        var c = Catalog(out _,
            new CatalogEntry("aaaa", "movie/local/q3Zp0Lw8q3Zp0Lw8q3Zp0L", "movie") { Copies = [C("l1")], Name = "Home Movie", Year = 1999, Image = true, ImageItem = Poster },
            new CatalogEntry("aaaa", "movie/tmdb/348/x/Tr7aQ1Tr7aQ1Tr7aQ1Tr7a", "extra") { Copies = [C("x1")], ExtraType = "trailer" },
            new CatalogEntry("cccc", "series/tvdb/81189", "series") { Ids = new Dictionary<string, string> { ["imdb"] = "tt0903747" } });
        var titles = (await c.TitlesAsync(["aaaa", "cccc"], "", 10, default)).Titles.Cast<VideoTitle>().ToDictionary(t => t.Key);
        var local = titles["movie/local/q3Zp0Lw8q3Zp0Lw8q3Zp0L"];
        Assert.Equal(("Home Movie", 1999, true), (local.Name, local.Year, local.Image));
        Assert.Equal("trailer", titles["movie/tmdb/348/x/Tr7aQ1Tr7aQ1Tr7aQ1Tr7a"].ExtraType);
        Assert.Equal("tt0903747", titles["series/tvdb/81189"].Ids!["imdb"]);
        Assert.Equal([0xFF, 0xD8, 0xFF], await c.ImageAsync("movie/local/q3Zp0Lw8q3Zp0Lw8q3Zp0L", default));
        var e = await Assert.ThrowsAsync<MsfpException>(async () => await c.ImageAsync("movie/tmdb/348", default));
        Assert.Equal(MsfpError.NotFound, e.Code);
    }

    [Fact]
    public async Task AnEpisodeKeyedByItsTmdbIdCarriesItsPlace()
    {
        var c = Catalog(out _,
            new CatalogEntry("cccc", "series/tmdb/1920/e127615", "episode") { Copies = [C("i5")], Place = new Place { Season = 1, Episode = 1 } },
            new CatalogEntry("cccc", "series/tmdb/1920/s1/e9", "episode") { Copies = [C("i6")] });
        var page = await c.TitlesAsync(["cccc"], "", 10, default);
        var byId = Assert.IsType<VideoTitle>(page.Titles.Single(t => t.Key == "series/tmdb/1920/e127615"));
        Assert.Equal(new Place { Season = 1, Episode = 1 }, byId.Place);
        Assert.Null(Assert.IsType<VideoTitle>(page.Titles.Single(t => t.Key == "series/tmdb/1920/s1/e9")).Place);
    }

    [Fact]
    public async Task OnlyTheLibrariesAskedAndPages()
    {
        var c = Catalog(out _,
            new CatalogEntry("aaaa", "movie/tmdb/1", "movie") { Copies = [C("i1")] },
            new CatalogEntry("bbbb", "movie/tmdb/2", "movie") { Copies = [C("i2")] },
            new CatalogEntry("aaaa", "movie/tmdb/3", "movie") { Copies = [C("i3")] },
            new CatalogEntry("aaaa", "movie/tmdb/4", "movie") { Copies = [C("i4")] });
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
        var c = Catalog(out var source, new CatalogEntry("aaaa", "movie/tmdb/1", "movie") { Copies = [C("i1")], With = ["movie/tmdb/2"] });
        var t = (VideoTitle)(await c.TitlesAsync(["aaaa"], "", 10, default)).Titles[0];
        t.With!.Add("movie/tmdb/3");
        t.Libraries!.Clear();
        t.Copies!.Clear();
        var again = (VideoTitle)(await c.TitlesAsync(["aaaa"], "", 10, default)).Titles[0];
        Assert.Equal(["movie/tmdb/2"], again.With);
        Assert.Equal(["aaaa"], again.Libraries);
        Assert.Equal(["i1"], again.Copies!.Select(x => x.Id));
        Assert.Equal(1, source.Reads);
        c.Changed();
        await c.TitlesAsync(["aaaa"], "", 10, default);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public void FilmAndSeriesKeys()
    {
        static string? Opaque(string id) => "q3Zp0Lw8q3Zp0Lw8q3Zp0L";
        var film = new MediaBrowser.Controller.Entities.Movies.Movie { Name = "  Home\tMovie  ", ProductionYear = 1999 };
        // The plugin's identity: TMDB's title, sure or chosen.
        Assert.Equal("movie/tmdb/603", JellyfinCatalogSource.KeyOf(film, new TitleIdentity { Tmdb = "603", Sure = true }, Opaque)!.Key.ToString());
        Assert.Equal("movie/tmdb/604", JellyfinCatalogSource.KeyOf(film, new TitleIdentity { Tmdb = "603", Chosen = "604" }, Opaque)!.Key.ToString());
        // Waiting for the owner, or not looked at yet: none.
        Assert.Null(JellyfinCatalogSource.KeyOf(film, new TitleIdentity { Tmdb = "603" }, Opaque));
        Assert.Null(JellyfinCatalogSource.KeyOf(film, null, Opaque));
        // TMDB has nothing for it: local, with its name and year.
        var local = JellyfinCatalogSource.KeyOf(film, new TitleIdentity(), Opaque)!;
        Assert.Equal(("movie/local/q3Zp0Lw8q3Zp0Lw8q3Zp0L", "Home Movie", 1999), (local.Key.ToString(), local.Name, local.Year));
        Assert.Null(JellyfinCatalogSource.KeyOf(film, new TitleIdentity(), _ => null));
        // A series Jellyfin places on TheTVDB only: tvdb.
        var series = new MediaBrowser.Controller.Entities.TV.Series { Name = "S", ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "81189", ["Imdb"] = "tt0903747" } };
        var tvdb = JellyfinCatalogSource.KeyOf(series, new TitleIdentity(), Opaque)!;
        Assert.Equal("series/tvdb/81189", tvdb.Key.ToString());
        Assert.Equal("tt0903747", tvdb.Ids["imdb"]);
        Assert.True(global::Msfp.Protocol.VideoKey.TryParse(tvdb.Key.ToString(), out _));
        // Its TMDB match waiting for the owner, or not looked at yet: not shared, not even under tvdb.
        Assert.Null(JellyfinCatalogSource.KeyOf(series, new TitleIdentity { Tmdb = "1396" }, Opaque));
        Assert.Null(JellyfinCatalogSource.KeyOf(series, null, Opaque));
        // Confirmed: under its TMDB key, and never under tvdb.
        Assert.Equal("series/tmdb/1396", JellyfinCatalogSource.KeyOf(series, new TitleIdentity { Tmdb = "1396", Chosen = "1396" }, Opaque)!.Key.ToString());
        Assert.Equal("series/tmdb/1396", JellyfinCatalogSource.KeyOf(series, new TitleIdentity { Tmdb = "1396", Sure = true }, Opaque)!.Key.ToString());
        // On TMDB too (by Jellyfin), but not taken by the plugin: local only when TMDB had nothing.
        series.ProviderIds["Tmdb"] = "1396";
        Assert.Equal("series/local/q3Zp0Lw8q3Zp0Lw8q3Zp0L", JellyfinCatalogSource.KeyOf(series, new TitleIdentity(), Opaque)!.Key.ToString());
        Assert.Null(JellyfinCatalogSource.KeyOf(series, new TitleIdentity { Tmdb = "1396" }, Opaque));
        // A film is never keyed by TheTVDB.
        film.ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "1" };
        Assert.Equal("local", JellyfinCatalogSource.KeyOf(film, new TitleIdentity(), Opaque)!.Key.Scheme);
    }

    [Theory]
    [InlineData("A Film", "A Film")]
    [InlineData("  two  spaces\n", "two spaces")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void LocalNames(string? name, string sent) => Assert.Equal(sent, JellyfinCatalogSource.LocalName(name));

    [Fact]
    public void LocalNamesAreCut() => Assert.Equal(JellyfinCatalogSource.MaxLocalName, JellyfinCatalogSource.LocalName(new string('x', 300)).Length);

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

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("/", "")]
    [InlineData("jellyfin", "/jellyfin")]
    [InlineData("/jellyfin/", "/jellyfin")]
    public void PrefixFromTheBaseUrl(string? baseUrl, string prefix) => Assert.Equal(prefix, EngineHost.PrefixOf(baseUrl));
}
