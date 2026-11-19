using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Msfp.Viewing;
using Msfp;
using Xunit;
using Picture = Msfp.Picture;
using VideoTitle = Msfp.VideoTitle;

namespace Jellyfin.Plugin.Msfp.Tests;

public class WantedTests
{
    private static VideoTitle T(string key, int? height = null, string[]? audio = null, Place? place = null, Dictionary<string, string>? ids = null) => new()
    {
        Key = key,
        Kind = key.StartsWith("movie", System.StringComparison.Ordinal) ? "movie" : key.Contains("/e", System.StringComparison.Ordinal) ? "episode" : key.Contains("/s", System.StringComparison.Ordinal) ? "season" : "series",
        Audio = audio?.ToList(),
        Video = height is { } h ? new Picture { Height = h } : null,
        Place = place,
        Ids = ids,
    };

    [Fact]
    public void OneStandInPerFilmTheBestCopyFirst()
    {
        var w = Wanted.Of(
        [
            ("a", [T("movie/tmdb/603", 1080, ["en"])]),
            ("b", [T("movie/tmdb/603", 2160, ["fr"])]),
        ], new Held());
        var f = Assert.Single(w.Films.Values);
        Assert.Equal(["b", "a"], f.Copy.Holders.Select(h => h.Peer));
        Assert.Equal(2160, f.Copy.Picture!.Height);
        Assert.Equal(["en", "fr"], f.Copy.Audio);
    }

    [Fact]
    public void NothingForWhatThisServerHolds()
    {
        var held = new Held();
        held.Films.Add(603);
        var hs = held.SeriesOf(1920);
        hs.EpisodeIds.Add(127615);   // 1x1, mapped by the identity
        hs.Numbers.Add((1, 2));      // 1x2, not mapped: by its numbers
        var w = Wanted.Of(
        [
            ("a",
            [
                T("movie/tmdb/603"), T("movie/tmdb/604"),
                T("series/tmdb/1920", ids: new() { ["tvdb"] = "70533" }),
                T("series/tmdb/1920/e127615", place: new Place { Season = 1, Episode = 1 }),
                T("series/tmdb/1920/e127625", place: new Place { Season = 1, Episode = 2 }),
                T("series/tmdb/1920/e127650", place: new Place { Season = 2, Episode = 1 }),
                T("series/tmdb/1920/s3/e1"),
                T("series/tmdb/1920/e9", place: null), // no place: skipped
            ]),
        ], held);
        Assert.Equal([604L], w.Films.Keys);
        var s = Assert.Single(w.Series.Values);
        Assert.Equal("70533", s.Ids["tvdb"]);
        Assert.Equal(["series/tmdb/1920/e127650", "series/tmdb/1920/s3/e1"], s.Episodes.Keys);
        Assert.Equal((2, 1, 127650L), (s.Episodes["series/tmdb/1920/e127650"].Season, s.Episodes["series/tmdb/1920/e127650"].Episode, s.Episodes["series/tmdb/1920/e127650"].EpisodeId));
        Assert.Equal(3, w.Count); // a film and two episodes
    }

    [Fact]
    public void ASeriesOnlyWithAnEpisodeToShow()
    {
        var w = Wanted.Of([("a", [T("series/tmdb/1399"), T("series/tmdb/1399/s1")])], new Held());
        Assert.Empty(w.Series);
    }

    [Fact]
    public void PathsComeFromKeys()
    {
        Assert.Equal("msfp://paired/movie/tmdb/603", Wanted.PathOf(Wanted.FilmKey(603)));
        Assert.Equal("msfp://paired/series/tmdb/1920/s0", Wanted.PathOf(Wanted.SeasonKey(1920, 0)));
    }

    [Fact]
    public void StreamsSayTheLanguagesAndPicture()
    {
        var p = new Playable { Key = "movie/tmdb/603" };
        p.Add("a", T("movie/tmdb/603", 1080, ["fr", "en"]) with { Subtitles = ["en"] });
        var streams = Streams.Of(p, l => l == "fr" ? "fre" : l == "en" ? "eng" : null);
        Assert.Equal(1080, streams[0].Height);
        Assert.Equal(["eng", "fre"], streams.Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Audio).Select(s => s.Language));
        Assert.Equal("eng", streams.Single(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle).Language);
        Assert.Equal(Enumerable.Range(0, streams.Count), streams.Select(s => s.Index));

        var none = Streams.Of(new Playable(), _ => null);
        Assert.Single(none); // one audio stream, no language
    }

    [Fact]
    public void LibraryNamesInTheServersLanguage()
    {
        Assert.Equal("Séries des serveurs associés", Names.Of(StandIns.Shelf.Series, "fr-CA"));
        Assert.Equal("Films from paired servers", Names.Of(StandIns.Shelf.Films, "ja"));
    }
}
