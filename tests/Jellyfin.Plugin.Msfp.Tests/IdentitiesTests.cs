using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Identification;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class IdentitiesTests
{
    // TMDB as the episode mapping asks it: Twin Peaks (1920), season 1.
    private sealed class FakeTmdb : ITmdbSource
    {
        public int Seasons { get; private set; }

        public Task<IReadOnlyList<TmdbFound>> SearchKindAsync(string kind, string query, int year, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbFound>>([]);

        public Task<TmdbTitle?> TitleAsync(string kind, int tmdbId, CancellationToken ct = default) =>
            Task.FromResult<TmdbTitle?>(tmdbId == 1920 ? new TmdbTitle { Id = 1920, Kind = "series", Name = "Twin Peaks", Imdb = "tt0098936", Tvdb = 70533 } : null);

        public Task<int> FindAsync(string kind, string source, string id, CancellationToken ct = default) => Task.FromResult(0);

        public Task<TmdbEpisodeFound?> FindEpisodeAsync(string source, string id, CancellationToken ct = default) =>
            Task.FromResult<TmdbEpisodeFound?>((source, id) switch
            {
                // TheTVDB numbers this one 1x8; TMDB has it at 2x1.
                ("tvdb", "999") => new TmdbEpisodeFound(1920, 2, 1, 127650),
                // Of another series: not taken.
                ("imdb", "tt0000001") => new TmdbEpisodeFound(1399, 1, 1, 63056),
                _ => null,
            });

        public Task<IReadOnlyList<TmdbEpisode>?> SeasonAsync(int seriesId, int season, CancellationToken ct = default)
        {
            Seasons++;
            return Task.FromResult<IReadOnlyList<TmdbEpisode>?>(seriesId == 1920 && season == 1
                ? [new TmdbEpisode(1, 127615, "Pilot"), new TmdbEpisode(2, 127625, "Traces to Nowhere")]
                : []);
        }
    }

    [Fact]
    public async Task EpisodesGetTheirTmdbIdsByTheirOwnIdsElseByTheSeasonList()
    {
        var tmdb = new FakeTmdb();
        var map = await Identifier.MapEpisodesAsync(tmdb, 1920,
        [
            new EpisodeFile(1, 1, string.Empty, string.Empty),
            new EpisodeFile(1, 2, "tt0000001", string.Empty), // its IMDb ID is another series': the season list
            new EpisodeFile(1, 8, string.Empty, "999"),       // its TheTVDB ID: TMDB's place, not the file's
            new EpisodeFile(1, 9, string.Empty, string.Empty), // TMDB doesn't list it: by its numbers
        ], default);
        Assert.Equal(new EpisodeIdentity(127615, 1, 1), map["1/1"]);
        Assert.Equal(new EpisodeIdentity(127625, 1, 2), map["1/2"]);
        Assert.Equal(new EpisodeIdentity(127650, 2, 1), map["1/8"]);
        Assert.False(map.ContainsKey("1/9"));
        Assert.Equal(1, tmdb.Seasons); // one season list, read once
    }

    [Fact]
    public void TakenIsTheOwnersChoiceElseASureMatch()
    {
        Assert.Equal(603, new TitleIdentity { Tmdb = "603", Sure = true }.Taken);
        Assert.Null(new TitleIdentity { Tmdb = "603", Sure = false }.Taken);
        Assert.True(new TitleIdentity { Tmdb = "603", Sure = false }.ToCheck);
        Assert.Equal(604, new TitleIdentity { Tmdb = "603", Sure = false, Chosen = "604" }.Taken);
        Assert.False(new TitleIdentity { Tmdb = "", Chosen = "604" }.ToCheck);
        Assert.Null(new TitleIdentity { Tmdb = "", Sure = false }.Taken);
        Assert.Null(new TitleIdentity { Tmdb = "0", Sure = true }.Taken);
    }

    [Fact]
    public async Task ATakenSeriesGetsItsOtherIdsAndEpisodes()
    {
        var t = new TitleIdentity { Kind = "series", Tmdb = "1920", Sure = true };
        await Identifier.CompleteAsync(new FakeTmdb(), t, true, [new EpisodeFile(1, 1, string.Empty, string.Empty)], true, default);
        Assert.Equal("tt0098936", t.Imdb);
        Assert.Equal("70533", t.Tvdb);
        Assert.Equal(new EpisodeIdentity(127615, 1, 1), t.Episodes["1/1"]);
        Assert.Equal(1920, t.MappedFor);

        var doubtful = new TitleIdentity { Kind = "series", Tmdb = "1920", Sure = false, Imdb = "x", Episodes = t.Episodes };
        await Identifier.CompleteAsync(new FakeTmdb(), doubtful, true, [], true, default);
        Assert.Empty(doubtful.Episodes);
        Assert.Equal(string.Empty, doubtful.Imdb);
    }

    [Fact]
    public void IdentitiesAreKeptInTheirFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "msfp-identities-" + System.Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var item = System.Guid.NewGuid();
            var a = new Identities(path);
            var changed = 0;
            a.Changed += (_, _) => changed++;
            a.Put(item, new TitleIdentity { Kind = "movie", Tmdb = "603", Sure = false, Chosen = "604", Reasons = [new Reason("year", "From 1998 here; TMDB says 1999.")] });
            Assert.Equal(1, changed);

            var b = new Identities(path);
            var t = b.Of(item)!;
            Assert.Equal("604", t.Chosen);
            Assert.Equal("year", t.Reasons[0].Code);
            Assert.True(t.SameAs(t.Copy()));

            b.KeepOnly(new HashSet<string>());
            Assert.Null(new Identities(path).Of(item));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
