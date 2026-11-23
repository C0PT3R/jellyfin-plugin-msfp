using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Msfp.Viewing;
using Msfp;
using Xunit;
using Copy = Msfp.Protocol.Copy;
using CopyAudio = Msfp.Protocol.CopyAudio;
using VideoColour = Msfp.Protocol.VideoColour;
using VideoStream = Msfp.Protocol.VideoStream;
using VideoTitle = Msfp.VideoTitle;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>The stand-ins wanted from the paired servers' titles (migration plan 4.1): holders by their copies, tmdb and tvdb joined, local titles apart, what this server holds left out.</summary>
public class WantedTests
{
    private static Copy C(string id, int height, bool hdr = false) =>
        new(id, 60_000, "video/matroska", 1000, "e" + id, [new CopyAudio("a1", "aac") { Codecs = "mp4a.40.2" }])
        {
            Video = new VideoStream("hevc", height * 16 / 9, height) { Codecs = "hvc1.2.4.L150.90", Colour = hdr ? new VideoColour(9, 16, 9) : null },
        };

    // A peer's title: its key, its copies (one 1080p by default for films
    // and episodes), and what else it carries.
    private static VideoTitle T(string key, Copy[]? copies = null, Place? place = null, Dictionary<string, string>? ids = null, string? name = null, int year = 0, bool image = false)
    {
        var playable = key.StartsWith("movie", System.StringComparison.Ordinal) || key.Contains("/e", System.StringComparison.Ordinal) || key.Contains("/a", System.StringComparison.Ordinal);
        return new VideoTitle
        {
            Key = key,
            Copies = playable ? (copies ?? [C("v1", 1080)]).ToList() : null,
            Place = place,
            Ids = ids,
            Name = name,
            Year = year,
            Image = image,
        };
    }

    private static Wanted Of(params (string Peer, VideoTitle[] Titles)[] peers) =>
        Wanted.Of(peers.Select(p => (p.Peer, p.Titles.Cast<Title>())), new Held());

    [Fact]
    public void OneStandInPerFilmTheHolderWithTheBestCopyFirst()
    {
        var w = Of(
            ("a", [T("movie/tmdb/603", [C("x", 1080), C("y", 720)])]),
            ("b", [T("movie/tmdb/603", [C("z", 720), C("hdr", 2160, hdr: true)])]));
        var f = Assert.Single(w.Films.Values);
        Assert.Equal("msfp://paired/movie/tmdb/603", f.Path);
        Assert.Equal(["b", "a"], f.Copy.Holders.Select(h => h.Peer));
        // The chosen holder's copies, best first: the stand-in's sources.
        Assert.Equal(["hdr", "z"], f.Copy.Chosen!.Copies.Select(c => c.Id));
        Assert.Same(f.Copy, w.ByPath[f.Path]);
    }

    [Fact]
    public void EqualCopiesTheHolderWithMoreThenByServer()
    {
        var w = Of(("b", [T("movie/tmdb/1", [C("x", 1080)])]), ("a", [T("movie/tmdb/1", [C("x", 1080)])]), ("c", [T("movie/tmdb/1", [C("x", 1080), C("y", 480)])]));
        Assert.Equal(["c", "a", "b"], w.Films.Values.Single().Copy.Holders.Select(h => h.Peer));
    }

    // A pairing ended and another began since the last pass: the copy the
    // pass found isn't fresh, and the one taken again names the new server.
    [Fact]
    public void ACopyNamingAServerNoLongerPairedIsTakenAgain()
    {
        var before = Of(("a", [T("movie/tmdb/348")]));
        var copy = StandIns.CopyOf(before, "msfp://paired/movie/tmdb/348")!;
        Assert.True(StandIns.Fresh(copy, new HashSet<string> { "a", "b" }));
        Assert.False(StandIns.Fresh(copy, new HashSet<string> { "b" }));
        var now = Of(("b", [T("movie/tmdb/348")]));
        Assert.Equal(["b"], StandIns.CopyOf(now, "msfp://paired/movie/tmdb/348")!.Holders.Select(h => h.Peer));
        Assert.Null(StandIns.CopyOf(now, "msfp://paired/movie/tmdb/1"));
    }

    [Fact]
    public void NothingForWhatThisServerHolds()
    {
        var held = new Held();
        held.Films.Add(603);
        var hs = held.SeriesOf(1920);
        hs.EpisodeIds.Add(127615);   // 1x1, by TMDB's ID
        hs.Numbers.Add((1, 2));      // 1x2, by its numbers
        held.TvdbSeriesOf(78914).TvdbEpisodeIds.Add(555);
        var w = Wanted.Of(
        [
            ("a",
            [
                T("movie/tmdb/603"), T("movie/tmdb/604"),
                T("series/tmdb/1920"),
                T("series/tmdb/1920/e127615", place: new Place { Season = 1, Episode = 1 }),
                T("series/tmdb/1920/e127625", place: new Place { Season = 1, Episode = 2 }),
                T("series/tmdb/1920/e127650", place: new Place { Season = 2, Episode = 1 }),
                T("series/tmdb/1920/s3/e1"),
                T("series/tmdb/1920/e9", place: null), // no place: skipped
                T("series/tvdb/78914/s1/e1", ids: new() { ["tvdb"] = "555" }),
                T("series/tvdb/78914/s1/e2", ids: new() { ["tvdb"] = "556" }),
            ]),
        ], held);
        Assert.Equal(["msfp://paired/movie/tmdb/604"], w.Films.Keys);
        var s = w.Series["msfp://paired/series/tmdb/1920"];
        Assert.Equal(["msfp://paired/series/tmdb/1920/e127650", "msfp://paired/series/tmdb/1920/s3/e1"], s.Episodes.Keys);
        var e = s.Episodes["msfp://paired/series/tmdb/1920/e127650"];
        Assert.Equal((2, 1, 127650L, "127650"), (e.Season, e.Episode, e.EpisodeId, e.Ids["tmdb"]));
        var lain = w.Series["msfp://paired/series/tvdb/78914"];
        Assert.Equal(["msfp://paired/series/tvdb/78914/s1/e2"], lain.Episodes.Keys);
        Assert.Equal((0L, 78914L), (lain.Tmdb, lain.Tvdb));
        Assert.Equal(4, w.Count); // a film and three episodes
    }

    [Fact]
    public void ASeriesOnlyWithAnEpisodeToShow()
    {
        var w = Of(("a", [T("series/tmdb/1399"), T("series/tmdb/1399/s1")]));
        Assert.Empty(w.Series);
    }

    // MSFP video section 4.3: a series keyed under tvdb whose ids give TMDB's
    // is the tmdb one; its episodes join TMDB's by their ids, others stay at
    // their own numbers and key.
    [Fact]
    public void ATvdbSeriesJoinsTheTmdbOneThroughIds()
    {
        var w = Of(
            ("a",
            [
                T("series/tmdb/1396/e62085", place: new Place { Season = 1, Episode = 1 }, ids: new() { ["tvdb"] = "349232", ["imdb"] = "tt0959621" }),
            ]),
            ("b",
            [
                T("series/tvdb/81189", ids: new() { ["tmdb"] = "1396", ["imdb"] = "tt0903747" }),
                T("series/tvdb/81189/s1/e1", ids: new() { ["tvdb"] = "349232" }),
                T("series/tvdb/81189/s1/e2", ids: new() { ["tmdb"] = "62086" }),
                T("series/tvdb/81189/s1/e3"),
            ]));
        var s = Assert.Single(w.Series.Values);
        Assert.Equal(("msfp://paired/series/tmdb/1396", 1396L, 81189L, "tt0903747"), (s.Path, s.Tmdb, s.Tvdb, s.Ids["imdb"]));
        Assert.Equal(
            ["msfp://paired/series/tmdb/1396/e62085", "msfp://paired/series/tmdb/1396/e62086", "msfp://paired/series/tvdb/81189/s1/e3"],
            s.Episodes.Keys);
        var first = s.Episodes["msfp://paired/series/tmdb/1396/e62085"];
        Assert.Equal(["a", "b"], first.Copy.Holders.Select(h => h.Peer));
        Assert.Equal(["series/tmdb/1396/e62085", "series/tvdb/81189/s1/e1"], first.Copy.Holders.Select(h => h.Key));
        Assert.Equal("349232", first.Ids["tvdb"]);
        Assert.Equal((1, 2), (s.Episodes["msfp://paired/series/tmdb/1396/e62086"].Season, s.Episodes["msfp://paired/series/tmdb/1396/e62086"].Episode));
    }

    // Jellyfin's metadata of a tmdb stand-in gives its TheTVDB ID: another
    // server's tvdb series is that one.
    [Fact]
    public void JellyfinsIdsJoinATmdbSeriesAndATvdbOne()
    {
        var peers = new (string, IEnumerable<Title>)[]
        {
            ("a", [T("series/tmdb/1396/e62085", place: new Place { Season = 1, Episode = 1 })]),
            ("b", [T("series/tvdb/81189/s1/e5")]),
        };
        Assert.Equal(2, Wanted.Of(peers, new Held()).Series.Count);
        var w = Wanted.Of(peers, new Held(), new Dictionary<long, long> { [1396] = 81189 });
        var s = Assert.Single(w.Series.Values);
        Assert.Equal((1396L, 81189L), (s.Tmdb, s.Tvdb));
        Assert.Contains("msfp://paired/series/tvdb/81189/s1/e5", s.Episodes.Keys);
    }

    [Fact]
    public void AbsoluteNumbersInTheFirstSeason()
    {
        var w = Of(("a", [T("series/tvdb/79824/a27")]));
        var e = w.Series["msfp://paired/series/tvdb/79824"].Episodes["msfp://paired/series/tvdb/79824/a27"];
        Assert.Equal((1, 27), (e.Season, e.Episode));
    }

    // MSFP video sections 3.10 and 4.5: a local title is its server's own,
    // with the owner's description; never one stand-in for two servers.
    [Fact]
    public void LocalTitlesAreEachServersOwn()
    {
        var w = Of(
            ("a", [T("movie/local/q3Zp0Lw8", name: " Summer 2019 ", year: 2019, image: true), T("movie/local/nameless"),
                T("series/local/Hk2v9TnA", name: "Home videos"), T("series/local/Hk2v9TnA/s1/e3"), T("series/local/Other/s1/e1")]),
            ("b", [T("movie/local/q3Zp0Lw8", name: "Another")]));
        Assert.Equal(["msfp://paired/local/a/movie/local/q3Zp0Lw8", "msfp://paired/local/b/movie/local/q3Zp0Lw8"], w.Films.Keys);
        var f = w.Films["msfp://paired/local/a/movie/local/q3Zp0Lw8"];
        Assert.Equal(new LocalTitle("a", "movie/local/q3Zp0Lw8", "Summer 2019", 2019, true), f.Local);
        Assert.Equal(0, f.Tmdb);
        var s = Assert.Single(w.Series.Values);
        Assert.Equal("Home videos", s.Local!.Name);
        var e = Assert.Single(s.Episodes);
        Assert.Equal("msfp://paired/local/a/series/local/Hk2v9TnA/s1/e3", e.Key);
        Assert.Equal((1, 3), (e.Value.Season, e.Value.Episode));
    }

    [Fact]
    public void ExtrasWaitForPhase7()
    {
        Assert.Empty(Of(("a", [T("movie/tmdb/348/x/Tr7aQ1", copies: [C("v", 720)])])).Films);
    }

    [Fact]
    public void PathsComeFromKeys()
    {
        Assert.Equal("msfp://paired/movie/tmdb/603", Wanted.PathOf(Wanted.FilmKey(603)));
        Assert.Equal("msfp://paired/series/tmdb/1920/s0", Wanted.SeasonPath(Wanted.PathOf(Wanted.SeriesKey(1920)), 0));
        Assert.Equal("msfp://paired/local/p1/series/local/X/s2", Wanted.SeasonPath(Wanted.LocalPath("p1", "series/local/X"), 2));
    }

    [Fact]
    public void LibraryNamesInTheServersLanguage()
    {
        Assert.Equal("Séries des serveurs associés", Names.Of(StandIns.Shelf.Series, "fr-CA"));
        Assert.Equal("Films from paired servers", Names.Of(StandIns.Shelf.Films, "ja"));
    }
}
