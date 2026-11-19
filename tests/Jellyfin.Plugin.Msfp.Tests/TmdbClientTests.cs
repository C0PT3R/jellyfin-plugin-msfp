using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Identification;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public sealed class TmdbClientTests : IDisposable
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiIwMTIzNDU2Nzg5YWJjZGVmIn0.c2lnbmF0dXJlc2lnbmF0dXJl";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "msfp-tmdb-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHandler _handler = new();
    private readonly Clock _clock = new();

    public void Dispose()
    {
        _handler.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task SearchesAskForTheKindAndYear()
    {
        _handler.Answer("/3/search/movie", """{"results":[{"id":949,"title":"Heat","release_date":"1995-12-15","vote_count":8000},{"id":0,"title":"No ID"}]}""");
        _handler.Answer("/3/search/tv", """{"results":[{"id":1399,"name":"Game of Thrones","first_air_date":"2011-04-17","vote_count":25000}]}""");
        using var client = Client(Key);

        var films = await client.SearchKindAsync("movie", "Heat & Dust", 1995);
        Assert.Equal(new TmdbFound(949, "movie", "Heat", 1995, 8000), Assert.Single(films));
        var series = await client.SearchKindAsync("series", "Game of Thrones", 2011);
        Assert.Equal(new TmdbFound(1399, "series", "Game of Thrones", 2011, 25000), Assert.Single(series));

        var film = Query(_handler.Requests[0]);
        Assert.Equal("Heat & Dust", film["query"]);
        Assert.Equal("1995", film["year"]);
        Assert.Equal("false", film["include_adult"]);
        Assert.Equal("en-US", film["language"]);
        Assert.Equal(Key, film["api_key"]);
        Assert.Null(_handler.Requests[0].Headers.Authorization);
        var tv = Query(_handler.Requests[1]);
        Assert.Equal("2011", tv["first_air_date_year"]);
        Assert.False(tv.ContainsKey("year"));
        Assert.Equal("https", _handler.Requests[0].RequestUri!.Scheme);
        Assert.Equal("api.themoviedb.org", _handler.Requests[0].RequestUri!.Host);

        // Without a year, none is sent; another kind isn't asked.
        await client.SearchKindAsync("movie", "Heat", 0);
        Assert.False(Query(_handler.Requests[2]).ContainsKey("year"));
        Assert.Empty(await client.SearchKindAsync("episode", "Heat", 0));
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task ATokenGoesAsBearer()
    {
        _handler.Answer("/3/search/movie", """{"results":[]}""");
        using var client = Client(Token);
        await client.SearchKindAsync("movie", "Heat", 0);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        Assert.False(Query(request).ContainsKey("api_key"));
    }

    [Fact]
    public async Task APageGathersItsNames()
    {
        _handler.Answer("/3/movie/129", """
            {"id":129,"title":"Spirited Away","original_title":"千と千尋の神隠し","original_language":"ja","release_date":"2001-07-20",
             "runtime":125,"vote_count":17000,
             "alternative_titles":{"titles":[{"title":"Sen to Chihiro no Kamikakushi"},{"title":"Spirited Away"},{"title":"Le  Voyage‎ de Chihiro"}]},
             "translations":{"translations":[{"iso_639_1":"fr","data":{"title":"Le Voyage de Chihiro"}},{"iso_639_1":"de","data":{"title":"Chihiros Reise ins Zauberland"}},{"iso_639_1":"ja","data":{"title":""}}]},
             "external_ids":{"imdb_id":"tt0245429"}}
            """);
        _handler.Answer("/3/tv/1429", """
            {"id":1429,"name":"Attack on Titan","original_name":"進撃の巨人","original_language":"ja","first_air_date":"2013-04-07",
             "episode_run_time":[24,25],
             "seasons":[{"season_number":0,"name":"Specials","episode_count":36},{"season_number":1,"name":"Season 1","episode_count":25}],
             "alternative_titles":{"results":[{"title":"AoT"}]},
             "translations":{"translations":[{"iso_639_1":"fr","data":{"name":"L'Attaque des Titans"}}]},
             "external_ids":{"imdb_id":"tt2560140","tvdb_id":267440}}
            """);
        using var client = Client(Key);

        var film = await client.TitleAsync("movie", 129);
        Assert.NotNull(film);
        Assert.Equal("Spirited Away", film.Name);
        Assert.Equal("千と千尋の神隠し", film.OriginalName);
        Assert.Equal("ja", film.Language);
        Assert.Equal(2001, film.Year);
        Assert.Equal(125, film.Runtime);
        Assert.Equal("tt0245429", film.Imdb);
        // Each once, the title's own names aside.
        Assert.Equal(["Sen to Chihiro no Kamikakushi", "Le Voyage de Chihiro", "Chihiros Reise ins Zauberland"], film.Names);
        Assert.Equal("alternative_titles,translations,external_ids", Query(_handler.Requests[0])["append_to_response"]);

        var series = await client.TitleAsync("series", 1429);
        Assert.NotNull(series);
        Assert.Equal(24, series.Runtime);
        Assert.Equal(267440, series.Tvdb);
        Assert.Equal(["AoT", "L'Attaque des Titans"], series.Names);
        Assert.Equal([new SeasonInfo(0, "Specials", 36), new SeasonInfo(1, "Season 1", 25)], series.Seasons);
        Assert.Equal("/3/tv/1429", _handler.Requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task WhatTmdbDoesntKnowIsNothing()
    {
        using var client = Client(Key);
        Assert.Null(await client.TitleAsync("movie", 424242));
        Assert.Null(await client.SeasonAsync(1399, 99));
        Assert.Equal(0, await client.FindAsync("movie", "imdb", "tt0000001"));
        // Kept: not asked again.
        Assert.Null(await client.TitleAsync("movie", 424242));
        Assert.Equal(3, _handler.Requests.Count);
        // An ID of another title than asked: none either.
        _handler.Answer("/3/movie/5", """{"id":6,"title":"Other"}""");
        Assert.Null(await client.TitleAsync("movie", 5));
    }

    [Fact]
    public async Task TooManyRequestsAreRetried()
    {
        _handler.Answer("/3/search/movie", """{"results":[{"id":949,"title":"Heat"}]}""", tooManyFirst: 2);
        using var client = Client(Key);
        Assert.Single(await client.SearchKindAsync("movie", "Heat", 0));
        Assert.Equal(3, _handler.Requests.Count);

        // Past three retries, it fails, without the key in its words.
        _handler.Answer("/3/search/tv", """{"results":[]}""", tooManyFirst: 10);
        var e = await Assert.ThrowsAsync<TmdbException>(() => client.SearchKindAsync("series", "Heat", 0));
        Assert.DoesNotContain(Key, e.Message, StringComparison.Ordinal);
        Assert.Equal(3 + 4, _handler.Requests.Count);
    }

    [Fact]
    public async Task AnswersAreKeptFiveMonths()
    {
        _handler.Answer("/3/movie/949", """{"id":949,"title":"Heat"}""");
        using var client = Client(Key);
        await client.TitleAsync("movie", 949);
        await client.TitleAsync("movie", 949);
        Assert.Single(_handler.Requests);

        // The key is in no file, and no file is named by it.
        foreach (var file in Directory.GetFiles(_folder))
        {
            Assert.DoesNotContain(Key, file, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, File.ReadAllText(file), StringComparison.Ordinal);
        }

        _clock.Now += TimeSpan.FromDays(149);
        await client.TitleAsync("movie", 949);
        Assert.Single(_handler.Requests);
        _clock.Now += TimeSpan.FromDays(2);
        await client.TitleAsync("movie", 949);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task NotFoundIsKeptAWeek()
    {
        using var client = Client(Key);
        await client.TitleAsync("movie", 424242);
        _clock.Now += TimeSpan.FromDays(6);
        await client.TitleAsync("movie", 424242);
        Assert.Single(_handler.Requests);
        _clock.Now += TimeSpan.FromDays(2);
        await client.TitleAsync("movie", 424242);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task ExpiredAnswersArePruned()
    {
        _handler.Answer("/3/movie/949", """{"id":949,"title":"Heat"}""");
        using var client = Client(Key);
        await client.TitleAsync("movie", 949);
        Assert.Single(Directory.GetFiles(_folder));
        _clock.Now += TimeSpan.FromDays(151);
        client.PruneCache();
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task FindsGiveTitlesAndEpisodes()
    {
        _handler.Answer("/3/find/tt0944947", """{"movie_results":[],"tv_results":[{"id":1399}],"tv_episode_results":[]}""");
        _handler.Answer("/3/find/3254641", """{"movie_results":[],"tv_results":[],"tv_episode_results":[{"id":63056,"show_id":1399,"season_number":1,"episode_number":1}]}""");
        _handler.Answer("/3/tv/1399/season/1", """{"episodes":[{"episode_number":1,"id":63056,"name":"Winter Is Coming"},{"episode_number":2,"id":63057,"name":"The Kingsroad"}]}""");
        using var client = Client(Key);

        Assert.Equal(1399, await client.FindAsync("series", "imdb", "tt0944947"));
        Assert.Equal(0, await client.FindAsync("movie", "imdb", "tt0944947"));
        Assert.Equal("imdb_id", Query(_handler.Requests[0])["external_source"]);
        Assert.Equal(new TmdbEpisodeFound(1399, 1, 1, 63056), await client.FindEpisodeAsync("tvdb", "3254641"));
        Assert.Equal("tvdb_id", Query(_handler.Requests.Last())["external_source"]);
        var episodes = await client.SeasonAsync(1399, 1);
        Assert.Equal([new TmdbEpisode(1, 63056, "Winter Is Coming"), new TmdbEpisode(2, 63057, "The Kingsroad")], episodes);

        // IDs that can't be one aren't asked.
        var asked = _handler.Requests.Count;
        Assert.Equal(0, await client.FindAsync("movie", "imdb", "../search"));
        Assert.Equal(0, await client.FindAsync("movie", "anidb", "123"));
        Assert.Equal(asked, _handler.Requests.Count);
    }

    [Fact]
    public async Task OtherFailuresThrow()
    {
        _handler.Answer("/3/movie/1", string.Empty, status: HttpStatusCode.Unauthorized);
        using var client = Client(Key);
        var e = await Assert.ThrowsAsync<TmdbException>(() => client.TitleAsync("movie", 1));
        Assert.DoesNotContain(Key, e.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.Exists(_folder) ? Directory.GetFiles(_folder) : []);
    }

    private static Dictionary<string, string> Query(HttpRequestMessage request) =>
        request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));

    private TmdbClient Client(string key) => new(key, _folder, handler: _handler, time: _clock);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // FakeHandler answers by path: a body, after some 429s; 404 for a path
    // it wasn't given. It keeps the requests made.
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, HttpStatusCode Status, int TooMany)> _answers = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        public void Answer(string path, string body, int tooManyFirst = 0, HttpStatusCode status = HttpStatusCode.OK) =>
            _answers[path] = (body, status, tooManyFirst);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            if (!_answers.TryGetValue(path, out var answer))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"success":false}""") });
            }

            if (answer.TooMany > 0)
            {
                _answers[path] = answer with { TooMany = answer.TooMany - 1 };
                var tooMany = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                tooMany.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(tooMany);
            }

            return Task.FromResult(new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") });
        }
    }
}
