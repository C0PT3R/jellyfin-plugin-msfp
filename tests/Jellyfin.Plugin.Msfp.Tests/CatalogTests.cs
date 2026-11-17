using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Protocol;
using Jellyfin.Plugin.Msfp.Viewing;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class TitleKeyTests
{
    [Theory]
    [InlineData("movie/tmdb/949", "movie")]
    [InlineData("movie/tmdb/9999999999", "movie")]
    [InlineData("series/tmdb/1399", "series")]
    [InlineData("series/tmdb/1399/s0", "season")]
    [InlineData("series/tmdb/1399/s1", "season")]
    [InlineData("series/tmdb/1399/s9999", "season")]
    [InlineData("series/tmdb/1399/e63056", "episode")]
    [InlineData("series/tmdb/1399/s1/e2", "episode")]
    [InlineData("series/tmdb/1399/s1/e99999", "episode")]
    public void Parses(string key, string kind)
    {
        Assert.True(TitleKey.TryParse(key, out var k));
        Assert.Equal(kind, k);
    }

    [Theory]
    [InlineData("movie/tmdb/0949")]
    [InlineData("movie/tmdb/0")]
    [InlineData("movie/tmdb/10000000000")]
    [InlineData("movie/imdb/tt0113277")]
    [InlineData("series/tvdb/121361")]
    [InlineData("item/0123456789abcdef0123456789abcdef")]
    [InlineData("series/tmdb/1399/s01")]
    [InlineData("series/tmdb/1399/s1/e02")]
    [InlineData("series/tmdb/1399/s10000")]
    [InlineData("series/tmdb/1399/s1/e100000")]
    [InlineData("series/tmdb/1399/e0")]
    [InlineData("series/tmdb/1399/e63056/s1")]
    [InlineData("series/tmdb/1399/")]
    [InlineData("Movie/tmdb/949")]
    [InlineData("movie/tmdb/949 ")]
    [InlineData("")]
    public void RefusesOtherForms(string key)
    {
        Assert.False(TitleKey.TryParse(key, out _));
    }

    [Theory]
    [InlineData("949", "949")]
    [InlineData("0949", "949")]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("12a", null)]
    [InlineData("12345678901", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CanonicalIds(string? id, string? expected)
    {
        Assert.Equal(expected, TitleKey.CanonicalId(id));
    }
}

public class LanguagesTests
{
    [Theory]
    [InlineData("eng", "en")]
    [InlineData("fre", "fr")]
    [InlineData("fra", "fr")]
    [InlineData("ger", "de")]
    [InlineData("FRE", "fr")]
    [InlineData("fr-CA", "fr-ca")]
    [InlineData("und", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("x", null)]
    [InlineData("français", null)]
    public void FromJellyfin(string? language, string? tag)
    {
        Assert.Equal(tag, Languages.FromJellyfin(language));
    }

    [Fact]
    public void CleanKeepsValidTagsOnceAtMost12()
    {
        var many = Enumerable.Range(0, 20).Select(i => "l" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(12, Languages.Clean(many).Count);
        Assert.Equal(new[] { "fr", "en" }, Languages.Clean(new[] { "fr", "FR", "x", null, "en", "a b", "abcdefghijklm" }));
    }

    [Fact]
    public void KindsFromJellyfin()
    {
        Assert.Equal("movies", JellyfinCatalogSource.Kind("Movies"));
        Assert.Equal("tvshows", JellyfinCatalogSource.Kind("TvShows"));
        Assert.Equal("homevideos", JellyfinCatalogSource.Kind("HomeVideos"));
        Assert.Equal("mixed", JellyfinCatalogSource.Kind(null));
        Assert.Equal(string.Empty, JellyfinCatalogSource.Kind("Playlists"));
    }
}

/// <summary>The owning side's catalog and change log, alone.</summary>
public class OwnCatalogTests
{
    private readonly ManualClock _clock = ManualClock.AtSeconds(1_800_000_000);
    private readonly FakeSource _source = new();
    private readonly OwnCatalog _catalog;

    public OwnCatalogTests()
    {
        _source.Libs.Add(new LibraryInfo("aaaa", "Films", "movies"));
        _source.Libs.Add(new LibraryInfo("bbbb", "Other films", "movies"));
        _catalog = new OwnCatalog(_source, _clock, null);
    }

    private string A => _catalog.PeerId("aaaa");

    private string B => _catalog.PeerId("bbbb");

    private TitlesPage Page(long since = 0, string after = "", long offersSeq = 0, int pageSize = OwnCatalog.PageSize, string[]? offered = null) =>
        _catalog.Page(new[] { A, B }, offered ?? new[] { "aaaa", "bbbb" }, after, since, offersSeq, pageSize);

    [Fact]
    public void PeerIdsAreOpaqueAndStable()
    {
        Assert.StartsWith("lib_", A);
        Assert.DoesNotContain("aaaa", A);
        Assert.Equal(A, _catalog.PeerId("aaaa"));
        Assert.NotEqual(A, B);
        Assert.NotEqual(A, new OwnCatalog(_source, _clock, null).PeerId("aaaa"));
    }

    [Fact]
    public void WholeListInByteOrderOneKeyOnce()
    {
        _source.Movie("aaaa", 949, "en");
        _source.Movie("bbbb", 949, "fr");
        _source.Movie("aaaa", 10);
        _source.Movie("bbbb", 2);
        _catalog.Refresh();
        var page = Page();
        Assert.False(page.Changes);
        Assert.Null(page.Next);
        Assert.Equal(new[] { "movie/tmdb/10", "movie/tmdb/2", "movie/tmdb/949" }, page.Titles.Select(t => t.Key));
        var both = page.Titles.Single(t => t.Key == "movie/tmdb/949");
        Assert.Equal(string.CompareOrdinal(A, B) < 0 ? A : B, both.Library);
        Assert.Equal(new[] { "en", "fr" }, both.Audio);
    }

    [Fact]
    public void OnlyRequestedAndOfferedLibraries()
    {
        _source.Movie("aaaa", 1);
        _source.Movie("bbbb", 2);
        _catalog.Refresh();
        Assert.Equal(new[] { "movie/tmdb/1" }, Page(offered: new[] { "aaaa" }).Titles.Select(t => t.Key));
        Assert.Equal(new[] { "movie/tmdb/2" }, _catalog.Page(new[] { B, "lib_unknown" }, new[] { "aaaa", "bbbb" }, string.Empty, 0, 0).Titles.Select(t => t.Key));

        // Asking by the media server's own ID gets nothing.
        Assert.Empty(_catalog.Page(new[] { "aaaa" }, new[] { "aaaa" }, string.Empty, 0, 0).Titles);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4999, 1)]
    [InlineData(5000, 2)]
    [InlineData(5001, 2)]
    [InlineData(10000, 3)]
    public void WholeListPagesOf5000(int count, int pages)
    {
        for (var i = 1; i <= count; i++)
        {
            _source.Movie("aaaa", i);
        }

        _catalog.Refresh();
        var seen = new List<string>();
        var after = string.Empty;
        var n = 0;
        while (true)
        {
            var page = Page(after: after);
            n++;
            Assert.True(page.Titles.Count <= OwnCatalog.PageSize);
            seen.AddRange(page.Titles.Select(t => t.Key));
            if (page.Next == null)
            {
                break;
            }

            Assert.Equal(OwnCatalog.PageSize, page.Titles.Count);
            Assert.Equal(page.Titles[^1].Key, page.Next);
            after = page.Next;
        }

        // A multiple of 5,000 ends with one empty page (open question 3).
        Assert.Equal(pages, n);
        Assert.Equal(count, seen.Count);
        Assert.Equal(seen.OrderBy(k => k, StringComparer.Ordinal), seen);
    }

    [Fact]
    public void ChangesSinceASeq()
    {
        _source.Movie("aaaa", 1, "en");
        _source.Movie("aaaa", 2);
        _source.Movie("aaaa", 3);
        _catalog.Refresh();
        var since = Page().Seq;

        // Nothing changed: a changes answer with nothing in it.
        var none = Page(since);
        Assert.True(none.Changes);
        Assert.Empty(none.Titles);
        Assert.Empty(none.Gone);
        Assert.Equal(since, none.Seq);

        _source.Items.RemoveAll(e => e.Key == "movie/tmdb/2");
        _source.Items[0] = _source.Items[0] with { Audio = new[] { "en", "fr" } };
        _source.Movie("aaaa", 4);
        Assert.Equal(3, _catalog.Refresh());
        var page = Page(since);
        Assert.True(page.Changes);
        Assert.True(page.Seq > since);
        Assert.Equal(new[] { "movie/tmdb/1", "movie/tmdb/4" }, page.Titles.Select(t => t.Key));
        Assert.Equal(new[] { "en", "fr" }, page.Titles[0].Audio);
        Assert.Equal(new[] { "movie/tmdb/2" }, page.Gone);

        // From the new seq: nothing again.
        Assert.Empty(Page(page.Seq).Titles);
        Assert.Equal(0, _catalog.Refresh());
    }

    [Fact]
    public void ATitleLeavingOneOfItsLibrariesIsAChangeNotAGone()
    {
        _source.Movie("aaaa", 1);
        _source.Movie("bbbb", 1);
        _catalog.Refresh();
        var since = _catalog.Seq;
        _source.Items.RemoveAll(e => e.Library == "aaaa");
        _catalog.Refresh();
        var page = Page(since);
        Assert.Equal(new[] { "movie/tmdb/1" }, page.Titles.Select(t => t.Key));
        Assert.Equal(B, page.Titles[0].Library);
        Assert.Empty(page.Gone);
    }

    [Fact]
    public void ChangesPagesHaveNextWhenThereIsMore()
    {
        for (var i = 1; i <= 5; i++)
        {
            _source.Movie("aaaa", i);
        }

        _catalog.Refresh();
        var since = _catalog.Seq;
        _source.Items.RemoveAt(0);
        for (var i = 6; i <= 9; i++)
        {
            _source.Movie("aaaa", i);
        }

        _catalog.Refresh();
        var keys = new List<string>();
        var after = string.Empty;
        do
        {
            var page = Page(since, after, pageSize: 2);
            Assert.True(page.Changes);
            Assert.True(page.Titles.Count + page.Gone.Count <= 2);
            keys.AddRange(page.Titles.Select(t => t.Key).Concat(page.Gone));
            after = page.Next ?? string.Empty;
        }
        while (after.Length > 0);

        Assert.Equal(new[] { "movie/tmdb/1", "movie/tmdb/6", "movie/tmdb/7", "movie/tmdb/8", "movie/tmdb/9" }, keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void SinceZeroOrBelowIsAWholeList(long since)
    {
        _source.Movie("aaaa", 1);
        _catalog.Refresh();
        Assert.False(Page(since).Changes);
    }

    [Fact]
    public void ASinceAboveTheSeqIsAWholeList()
    {
        _source.Movie("aaaa", 1);
        _catalog.Refresh();
        var page = Page(_catalog.Seq + 1);
        Assert.False(page.Changes);
        Assert.Single(page.Titles);
    }

    [Fact]
    public void AnOfferChangedSinceIsAWholeList()
    {
        _source.Movie("aaaa", 1);
        _catalog.Refresh();
        var since = _catalog.Seq;
        var offersSeq = _catalog.Bump();
        Assert.False(Page(since, offersSeq: offersSeq).Changes);
        Assert.True(Page(offersSeq, offersSeq: offersSeq).Changes);
    }

    [Fact]
    public void ChangesOlderThanTheLogAreAWholeList()
    {
        _source.Movie("aaaa", 1);
        _source.Movie("aaaa", 2);
        _catalog.Refresh();
        var since = _catalog.Seq;
        _source.Items.RemoveAt(0);
        _catalog.Refresh();
        var recent = _catalog.Seq;
        Assert.True(Page(since).Changes);

        _clock.Advance(OwnCatalog.KeepLogFor + TimeSpan.FromSeconds(1));
        _catalog.Refresh();
        Assert.False(Page(since).Changes);
        Assert.True(Page(recent).Changes);
    }

    [Fact]
    public void SeqAndLogSurviveARestart()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "msfp-catalog-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var c1 = new OwnCatalog(_source, _clock, path);
            _source.Movie("aaaa", 1);
            c1.Refresh();
            var seq = c1.Seq;
            var id = c1.PeerId("aaaa");
            var c2 = new OwnCatalog(_source, _clock, path);
            Assert.Equal(seq, c2.Seq);
            Assert.Equal(id, c2.PeerId("aaaa"));
            Assert.Equal(0, c2.Refresh());
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}

/// <summary>libraries and titles between two servers, through the reader.</summary>
public class CatalogExchangeTests
{
    private readonly ManualClock _clock = ManualClock.AtSeconds(1_800_000_000);
    private readonly Network _net = new();
    private readonly TestServer _a;
    private readonly TestServer _b;

    public CatalogExchangeTests()
    {
        _a = new TestServer(_net, _clock, "https://a.example.org", "Server A");
        _b = new TestServer(_net, _clock, "http://192.0.2.10:8096", "Server B");
        _a.Source.Libs.Add(new LibraryInfo("aaaa", "Films", "movies"));
        _a.Source.Libs.Add(new LibraryInfo("bbbb", "Séries", "tvshows"));
        _a.Source.Libs.Add(new LibraryInfo("cccc", "Private", "homevideos"));
        _a.Source.Movie("aaaa", 949, "en", "fr");
        _a.Source.Items.Add(new CatalogEntry("bbbb", "series/tmdb/1399", "series", Array.Empty<string>(), Array.Empty<string>()));
        _a.Source.Items.Add(new CatalogEntry("bbbb", "series/tmdb/1399/s1", "season", Array.Empty<string>(), Array.Empty<string>()));
        _a.Source.Items.Add(new CatalogEntry("bbbb", "series/tmdb/1399/e63056", "episode", new[] { "en" }, new[] { "fr" }));
        _a.Source.Movie("cccc", 5);
        _a.Catalog.Refresh();
        _clock.Advance(TimeSpan.FromSeconds(1));
    }

    private string AKey => _a.KeyText;

    private async Task PairAsync()
    {
        Assert.True((await _b.Service.RedeemAsync(_a.Service.MakeCode().Format())).Ok);
        await _a.Service.AcceptAsync(_b.KeyText);
        Assert.Equal(PeerState.Paired, _b.PeerOf(_a)!.State);
    }

    private PeerCatalog BCatalog => _b.Catalogs.Store.Get(AKey);

    [Fact]
    public async Task NothingOfferedNothingRead()
    {
        await PairAsync();
        var result = await _b.Catalogs.ReadAsync(AKey);
        Assert.Contains("Nothing to read", result);
        Assert.Empty(BCatalog.Titles);
        Assert.Empty(BCatalog.Libraries);
    }

    [Fact]
    public async Task OffersAreReadWholeThenByChanges()
    {
        await PairAsync();
        _a.Service.SetOffers(_b.KeyText, new[] { "aaaa", "bbbb" });
        Assert.Contains("whole list", await _b.Catalogs.ReadAsync(AKey));

        var c = BCatalog;
        Assert.Equal(new[] { "Films", "Séries" }, c.Libraries.Select(l => l.Name));
        Assert.Equal(new[] { "movies", "tvshows" }, c.Libraries.Select(l => l.Kind));
        Assert.DoesNotContain(c.Libraries, l => l.Id.Contains("aaaa", StringComparison.Ordinal));
        Assert.Equal(new[] { "movie/tmdb/949", "series/tmdb/1399", "series/tmdb/1399/e63056", "series/tmdb/1399/s1" }, c.Titles.Keys);
        Assert.Equal(new[] { "en", "fr" }, c.Titles["movie/tmdb/949"].Audio);
        Assert.Equal(new[] { "fr" }, c.Titles["series/tmdb/1399/e63056"].Subtitles);
        Assert.Equal("Server A", _b.PeerOf(_a)!.Name);

        // A change at A: B reads only the changes.
        _a.Source.Items.RemoveAll(e => e.Key == "series/tmdb/1399/s1");
        _a.Source.Movie("aaaa", 550);
        _a.Catalog.Refresh();
        Assert.Contains("changes: 1 new or changed, 1 gone", await _b.Catalogs.ReadAsync(AKey));
        Assert.Equal(new[] { "movie/tmdb/550", "movie/tmdb/949", "series/tmdb/1399", "series/tmdb/1399/e63056" }, BCatalog.Titles.Keys);
    }

    [Fact]
    public async Task ALibraryUnofferedMidwayLeavesAtTheNextReading()
    {
        await PairAsync();
        _a.Service.SetOffers(_b.KeyText, new[] { "aaaa", "bbbb" });
        await _b.Catalogs.ReadAsync(AKey);
        Assert.Equal(4, BCatalog.Titles.Count);

        _a.Service.SetOffers(_b.KeyText, new[] { "aaaa" });
        Assert.Contains("whole list", await _b.Catalogs.ReadAsync(AKey));
        Assert.Equal(new[] { "movie/tmdb/949" }, BCatalog.Titles.Keys);
        Assert.Single(BCatalog.Libraries);
    }

    [Fact]
    public async Task AnOfferChangedButTheSameLibrariesAskedIsStillAWholeReading()
    {
        await PairAsync();
        _a.Service.SetOffers(_b.KeyText, new[] { "aaaa", "bbbb" });
        _b.Service.SetWants(AKey, false, new[] { _a.Catalog.PeerId("aaaa") });
        await _b.Catalogs.ReadAsync(AKey);
        Assert.Equal(new[] { "movie/tmdb/949" }, BCatalog.Titles.Keys);

        // A changes its offer in a way that keeps what B asks for.
        _a.Service.SetOffers(_b.KeyText, new[] { "aaaa" });
        Assert.Contains("whole list", await _b.Catalogs.ReadAsync(AKey));
    }

    [Fact]
    public async Task WantsPickWhichOfferedLibrariesAreRead()
    {
        await PairAsync();
        _a.Service.SetOffers(_b.KeyText, new[] { "aaaa", "bbbb" });
        _b.Service.SetWants(AKey, false, new[] { _a.Catalog.PeerId("bbbb"), "lib_not_offered" });
        await _b.Catalogs.ReadAsync(AKey);
        Assert.Equal(new[] { "series/tmdb/1399", "series/tmdb/1399/e63056", "series/tmdb/1399/s1" }, BCatalog.Titles.Keys);
        Assert.Equal(new[] { _a.Catalog.PeerId("bbbb") }, BCatalog.Requested);
    }

    [Fact]
    public async Task APeerNotPairedGetsSigned404s()
    {
        Assert.True((await _b.Service.RedeemAsync(_a.Service.MakeCode().Format())).Ok);
        _a.Service.SetOffers(_b.KeyText, new[] { "aaaa" });

        // B is "ours" at A: not accepted yet.
        Assert.Equal("Not paired.", await _b.Catalogs.ReadAsync(AKey));
        var answer = Call(_a, _b, "libraries", "{}");
        Assert.Equal(404, answer.Status);
        Assert.NotNull(answer.Signature);
        answer = Call(_a, _b, "titles", "{\"libraries\":[]}");
        Assert.Equal(404, answer.Status);
        Assert.NotNull(answer.Signature);
    }

    [Fact]
    public async Task StrangersGetPlain404s()
    {
        var c = new TestServer(_net, _clock, "https://c.example.org", "C");
        var answer = Call(_a, c, "libraries", "{}");
        Assert.Equal(404, answer.Status);
        Assert.Null(answer.Signature);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"libraries\":\"lib_x\"}")]
    [InlineData("{\"libraries\":[1]}")]
    [InlineData("{\"libraries\":[],\"since\":\"5\"}")]
    [InlineData("{\"libraries\":[],\"since\":1.5}")]
    [InlineData("{\"libraries\":[],\"after\":5}")]
    public async Task BadTitlesBodiesGetSigned404s(string body)
    {
        await PairAsync();
        var answer = Call(_a, _b, "titles", body);
        Assert.Equal(404, answer.Status);
        Assert.NotNull(answer.Signature);
    }

    [Fact]
    public async Task AnAfterOver512BytesIsRefused()
    {
        await PairAsync();
        Assert.Equal(200, Call(_a, _b, "titles", $"{{\"libraries\":[],\"after\":\"{new string('a', 512)}\"}}").Status);
        Assert.Equal(404, Call(_a, _b, "titles", $"{{\"libraries\":[],\"after\":\"{new string('a', 513)}\"}}").Status);
    }

    [Fact]
    public async Task TheAnswerNamesNoMediaServerIds()
    {
        await PairAsync();
        _a.Service.SetOffers(_b.KeyText, new[] { "aaaa", "bbbb" });
        var libs = Encoding.UTF8.GetString(Call(_a, _b, "libraries", "{}").Body);
        var titles = Encoding.UTF8.GetString(Call(_a, _b, "titles", $"{{\"libraries\":[\"{_a.Catalog.PeerId("aaaa")}\",\"{_a.Catalog.PeerId("bbbb")}\"]}}").Body);
        foreach (var text in new[] { libs, titles })
        {
            Assert.DoesNotContain("aaaa", text);
            Assert.DoesNotContain("bbbb", text);
            Assert.DoesNotContain("cccc", text);
        }

        Assert.DoesNotContain("Private", libs);
        Assert.DoesNotContain("movie/tmdb/5\"", titles);
        Assert.DoesNotContain("limits", libs);
    }

    [Fact]
    public async Task ASignedRefusalMakesTheReaderSayHello()
    {
        await PairAsync();
        _a.Service.SetOffers(_b.KeyText, new[] { "aaaa" });
        _net.Down.Add("192.0.2.10:8096");
        Assert.True(await _a.Service.EndAsync(_b.KeyText));
        _net.Down.Clear();

        var hellos = _net.Paths.Count(p => p.EndsWith("/hello", StringComparison.Ordinal));
        await _b.Catalogs.ReadAsync(AKey);
        Assert.Equal(hellos + 1, _net.Paths.Count(p => p.EndsWith("/hello", StringComparison.Ordinal)));
        Assert.Equal(PeerState.Gone, _b.PeerOf(_a)!.State);
    }

    [Fact]
    public async Task ReadingsAreDueEvery30Minutes()
    {
        await PairAsync();
        _a.Service.SetOffers(_b.KeyText, new[] { "aaaa" });
        await _b.Catalogs.ReadDueAsync(default);
        var reads = _net.Paths.Count(p => p.EndsWith("/libraries", StringComparison.Ordinal));
        Assert.Equal(1, reads);
        _clock.Advance(TimeSpan.FromMinutes(29));
        await _b.Catalogs.ReadDueAsync(default);
        Assert.Equal(1, _net.Paths.Count(p => p.EndsWith("/libraries", StringComparison.Ordinal)));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _b.Catalogs.ReadDueAsync(default);
        Assert.Equal(2, _net.Paths.Count(p => p.EndsWith("/libraries", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ACatalogGoesWithItsPeer()
    {
        await PairAsync();
        _a.Service.SetOffers(_b.KeyText, new[] { "aaaa" });
        await _b.Catalogs.ReadAsync(AKey);
        Assert.Single(BCatalog.Titles);
        Assert.True(await _b.Service.EndAsync(AKey));
        await _b.Catalogs.ReadDueAsync(default);
        Assert.Empty(_b.Catalogs.Store.FileNames());
    }

    private Answer Call(TestServer to, TestServer from, string call, string body)
    {
        Assert.True(Address.TryGetHost(to.Address, out var host));
        var bytes = Encoding.UTF8.GetBytes(body);
        var path = "/api/v1/federation/" + call;
        var headers = Signing.SignRequest(from.Service.Key, "POST", host, path, bytes, _clock.Now);
        return to.Service.HandleAsync(call, path, headers, bytes).GetAwaiter().GetResult();
    }
}

/// <summary>What the reader keeps of a hostile or sloppy peer's answers (section 9.4).</summary>
public class CatalogReaderTests
{
    private readonly ManualClock _clock = ManualClock.AtSeconds(1_800_000_000);
    private readonly Network _net = new();
    private readonly TestServer _a;
    private readonly TestServer _b;

    public CatalogReaderTests()
    {
        _a = new TestServer(_net, _clock, "https://a.example.org", "Server A");
        _b = new TestServer(_net, _clock, "http://192.0.2.10:8096", "Server B");
        _clock.Advance(TimeSpan.FromSeconds(1));
    }

    private async Task PairThenFake(Func<string, string, (int, string)> answer)
    {
        Assert.True((await _b.Service.RedeemAsync(_a.Service.MakeCode().Format())).Ok);
        await _a.Service.AcceptAsync(_b.KeyText);
        _net.Fakes["a.example.org"] = (_a.Service.Key, answer);
    }

    private static string Libraries(params string[] ids) =>
        JsonSerializer.Serialize(new { name = "Faked A", libraries = ids.Select(i => new { id = i, name = "L " + i, kind = "movies" }) });

    [Fact]
    public async Task SkipsWhatSection94SaysToSkip()
    {
        await PairThenFake((path, body) => path.EndsWith("libraries", StringComparison.Ordinal)
            ? (200, Libraries("L1", "L2"))
            : (200, """
            {"titles":[
              {"key":"movie/tmdb/1","kind":"movie","library":"L1","audio":["en","EN","x","fr"]},
              {"key":"movie/tmdb/01","kind":"movie","library":"L1"},
              {"key":"movie/imdb/tt1","kind":"movie","library":"L1"},
              {"key":"item/0123456789abcdef0123456789abcdef","kind":"movie","library":"L1"},
              {"key":"movie/tmdb/2","kind":"series","library":"L1"},
              {"key":"movie/tmdb/3","kind":"movie","library":"L9"},
              {"key":"series/tmdb/4/s1","kind":"season","library":"L2"},
              {"key":5,"kind":"movie","library":"L1"},
              "junk"
            ],"seq":7}
            """));
        Assert.Contains("whole list", await _b.Catalogs.ReadAsync(_a.KeyText));
        var c = _b.Catalogs.Store.Get(_a.KeyText);
        Assert.Equal(new[] { "movie/tmdb/1", "series/tmdb/4/s1" }, c.Titles.Keys);
        Assert.Equal(new[] { "en", "fr" }, c.Titles["movie/tmdb/1"].Audio);
        Assert.Equal(7, c.Seq);
        Assert.Equal("Faked A", _b.PeerOf(_a)!.Name);
    }

    [Fact]
    public async Task LibrariesAreCleaned()
    {
        var libs = new List<object> { new { id = "", name = "x", kind = "movies" }, new { id = new string('i', 65), name = "x", kind = "movies" }, new { id = "ok", name = "\u0007", kind = "weird" }, new { id = "ok", name = "twice", kind = "movies" } };
        libs.AddRange(Enumerable.Range(0, 300).Select(i => (object)new { id = "l" + i.ToString(CultureInfo.InvariantCulture), name = "n", kind = "music" }));
        var json = JsonDocument.Parse(JsonSerializer.Serialize(new { libraries = libs })).RootElement;
        var kept = CatalogReader.ReadLibraries(json);
        Assert.Equal(CatalogReader.MaxLibraries, kept.Count);
        Assert.Equal(new PeerLibrary("ok", "?", string.Empty), kept[0]);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("lib.A")]
    [InlineData("lib/A")]
    [InlineData("lïb")]
    public void LibraryIdsOutsideTheirFormAreSkipped(string id)
    {
        var json = JsonDocument.Parse(JsonSerializer.Serialize(new { libraries = new[] { new { id, name = "x", kind = "movies" }, new { id = "lib_Ok-1", name = "y", kind = "movies" } } })).RootElement;
        Assert.Equal(new[] { "lib_Ok-1" }, CatalogReader.ReadLibraries(json).Select(l => l.Id));
    }

    [Fact]
    public void SeriesAndSeasonsKeepNoLanguages()
    {
        var asked = new HashSet<string> { "L" };
        var series = JsonDocument.Parse("""{"key":"series/tmdb/4","kind":"series","library":"L","audio":["en"],"subtitles":["fr"]}""").RootElement;
        var episode = JsonDocument.Parse("""{"key":"series/tmdb/4/s1/e2","kind":"episode","library":"L","audio":["en"],"subtitles":["fr"]}""").RootElement;
        Assert.Empty(CatalogReader.ReadTitle(series, asked)!.Value.Title.Audio);
        Assert.Empty(CatalogReader.ReadTitle(series, asked)!.Value.Title.Subtitles);
        Assert.Equal(new[] { "en" }, CatalogReader.ReadTitle(episode, asked)!.Value.Title.Audio);
        Assert.Equal(new[] { "fr" }, CatalogReader.ReadTitle(episode, asked)!.Value.Title.Subtitles);
    }

    [Fact]
    public async Task FollowsNextAndStopsWhenItDoesntMoveOn()
    {
        var pages = 0;
        await PairThenFake((path, body) =>
        {
            if (path.EndsWith("libraries", StringComparison.Ordinal))
            {
                return (200, Libraries("L1"));
            }

            pages++;
            var after = JsonDocument.Parse(body).RootElement.GetProperty("after").GetString();
            return after switch
            {
                "" => (200, """{"titles":[{"key":"movie/tmdb/1","kind":"movie","library":"L1"}],"next":"movie/tmdb/1","seq":3}"""),
                "movie/tmdb/1" => (200, """{"titles":[{"key":"movie/tmdb/2","kind":"movie","library":"L1"}],"next":"movie/tmdb/1","seq":3}"""),
                _ => (500, "{}"),
            };
        });
        await _b.Catalogs.ReadAsync(_a.KeyText);
        Assert.Equal(2, pages);
        Assert.Equal(new[] { "movie/tmdb/1", "movie/tmdb/2" }, _b.Catalogs.Store.Get(_a.KeyText).Titles.Keys);
    }

    [Fact]
    public async Task AMixOfWholeAndChangesPagesFailsAndKeepsTheOldCatalog()
    {
        var mixed = false;
        await PairThenFake((path, body) =>
        {
            if (path.EndsWith("libraries", StringComparison.Ordinal))
            {
                return (200, Libraries("L1"));
            }

            var after = JsonDocument.Parse(body).RootElement.GetProperty("after").GetString();
            if (!mixed)
            {
                return (200, """{"titles":[{"key":"movie/tmdb/1","kind":"movie","library":"L1"}],"seq":3}""");
            }

            return after == ""
                ? (200, """{"titles":[{"key":"movie/tmdb/9","kind":"movie","library":"L1"}],"next":"movie/tmdb/9","seq":4}""")
                : (200, """{"titles":[],"changes":true,"gone":["movie/tmdb/1"],"seq":4}""");
        });
        await _b.Catalogs.ReadAsync(_a.KeyText);
        mixed = true;
        var result = await _b.Catalogs.ReadAsync(_a.KeyText);
        Assert.Contains("mixed", result);
        Assert.Equal(new[] { "movie/tmdb/1" }, _b.Catalogs.Store.Get(_a.KeyText).Titles.Keys);
    }

    [Fact]
    public async Task AReceiverWithoutSeqIsAskedForTheWholeListEachTime()
    {
        var sinces = new List<bool>();
        await PairThenFake((path, body) =>
        {
            if (path.EndsWith("libraries", StringComparison.Ordinal))
            {
                return (200, Libraries("L1"));
            }

            sinces.Add(JsonDocument.Parse(body).RootElement.TryGetProperty("since", out _));
            return (200, """{"titles":[{"key":"movie/tmdb/1","kind":"movie","library":"L1"}]}""");
        });
        await _b.Catalogs.ReadAsync(_a.KeyText);
        await _b.Catalogs.ReadAsync(_a.KeyText);
        Assert.Equal(new[] { false, false }, sinces);
    }

    [Fact]
    public async Task ChangesKeepTitlesAndDropGone()
    {
        var round = 0;
        var sentSince = new List<long>();
        await PairThenFake((path, body) =>
        {
            if (path.EndsWith("libraries", StringComparison.Ordinal))
            {
                return (200, Libraries("L1"));
            }

            var b = JsonDocument.Parse(body).RootElement;
            sentSince.Add(b.TryGetProperty("since", out var s) ? s.GetInt64() : 0);
            return round == 0
                ? (200, """{"titles":[{"key":"movie/tmdb/1","kind":"movie","library":"L1"},{"key":"movie/tmdb/2","kind":"movie","library":"L1"}],"seq":3}""")
                : (200, $$"""{"titles":[{"key":"movie/tmdb/3","kind":"movie","library":"L1"}],"changes":true,"gone":["movie/tmdb/1","{{new string('x', 129)}}"],"seq":5}""");
        });
        await _b.Catalogs.ReadAsync(_a.KeyText);
        round = 1;
        await _b.Catalogs.ReadAsync(_a.KeyText);
        Assert.Equal(new long[] { 0, 3 }, sentSince);
        var c = _b.Catalogs.Store.Get(_a.KeyText);
        Assert.Equal(new[] { "movie/tmdb/2", "movie/tmdb/3" }, c.Titles.Keys);
        Assert.Equal(5, c.Seq);
    }

    [Fact]
    public async Task AnUnsignedAnswerChangesNothing()
    {
        await PairThenFake((path, body) => path.EndsWith("libraries", StringComparison.Ordinal) ? (200, Libraries("L1")) : (200, """{"titles":[{"key":"movie/tmdb/1","kind":"movie","library":"L1"}],"seq":3}"""));
        await _b.Catalogs.ReadAsync(_a.KeyText);

        // Answers now signed by another key: as from someone in between.
        var mallory = ServerKey.Generate();
        _net.Fakes["a.example.org"] = (mallory, (path, body) => path.EndsWith("libraries", StringComparison.Ordinal) ? (200, Libraries("L1")) : (200, """{"titles":[],"seq":9}"""));
        Assert.Contains("couldn't be reached", await _b.Catalogs.ReadAsync(_a.KeyText));
        Assert.Single(_b.Catalogs.Store.Get(_a.KeyText).Titles);
    }
}
