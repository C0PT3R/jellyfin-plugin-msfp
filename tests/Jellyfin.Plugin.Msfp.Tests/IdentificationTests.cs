using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Identification;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// Overfin's identify tests (names_test.go, identify_test.go, match_test.go,
// fuzz_test.go), the same cases.
public class IdentificationTests
{
    // Names as folders and files carry them: IDs of every kind, scene names
    // with or without a year, and names that look like release tags but
    // aren't.
    [Theory]
    [InlineData("Spirited Away (2001) [imdbid-tt0245429]", "Spirited Away", 2001, "", "tt0245429", "")]
    [InlineData("Dark (2017) [tvdbid-334824]", "Dark", 2017, "", "", "334824")]
    [InlineData("Heat (1995) {imdb-TT0113277} [tmdbid-949]", "Heat", 1995, "949", "tt0113277", "")]
    [InlineData("Le Parrain [tt0068646]", "Le Parrain", 0, "", "tt0068646", "")]
    [InlineData("The.Candidate.1080p.WEB-DL.x264-GROUP.mkv", "The Candidate", 0, "", "", "")]
    [InlineData("Some.Show.2160p.UHD.BluRay.mkv", "Some Show", 0, "", "", "")]
    [InlineData("Dune.Part.Two.2024.MULTi.2160p.WEB.H265-ABC", "Dune Part Two", 2024, "", "", "")]
    [InlineData("The French Connection (1971)", "The French Connection", 1971, "", "", "")]
    [InlineData("The.French.Connection.REMUX.mkv", "The French Connection", 0, "", "", "")]
    [InlineData("Web (2013)", "Web", 2013, "", "", "")]
    [InlineData("Multi", "Multi", 0, "", "", "")]
    [InlineData("Mission-Impossible", "Mission-Impossible", 0, "", "", "")]
    [InlineData("[tvdbid-0]", "", 0, "", "", "")]
    // identify_test.go's TestParse.
    [InlineData("Heat (1995)", "Heat", 1995, "", "", "")]
    [InlineData("Heat (1995) [tmdbid-949]", "Heat", 1995, "949", "", "")]
    [InlineData("The.Candidate.2023.1080p.WEB.mkv", "The Candidate", 2023, "", "", "")]
    [InlineData("1917 (2019)", "1917", 2019, "", "", "")]
    [InlineData("1917", "1917", 0, "", "", "")]
    [InlineData("2001 A Space Odyssey (1968)", "2001 A Space Odyssey", 1968, "", "", "")]
    [InlineData("19-2", "19-2", 0, "", "", "")]
    [InlineData("Blade Runner 2049 (2017) {tmdb-335984}", "Blade Runner 2049", 2017, "335984", "", "")]
    public void NamesAreRead(string input, string name, int year, string tmdb, string imdb, string tvdb)
    {
        Assert.Equal(new Named(name, year, tmdb, imdb, tvdb), Names.Parse(input));
    }

    // The cases found on a real library: each wrong one is told, and right
    // ones aren't, even named in another language than TMDB's answers.
    [Fact]
    public void ReasonsTellWrongTitlesOnly()
    {
        static LibraryTitle Film(string folder, string name, int year, TitleDetails d) =>
            new() { Kind = "movie", FolderName = folder, Tmdb = "1", TmdbName = name, TmdbYear = year, Details = d };

        var cases = new (string About, LibraryTitle Title, string[] Want)[]
        {
            ("taken for another film", Film("The Swedish Connection (2026)", "L'Odyssée", 2026,
                new TitleDetails { OriginalName = "The Odyssey" }), ["name"]),
            ("right, in French on TMDB, English here", Film("Spirited Away (2001)", "Le Voyage de Chihiro", 2001,
                new TitleDetails { OriginalName = "千と千尋の神隠し", Names = ["Spirited Away"] }), []),
            ("a remake of the same name", Film("Trick 'r Treat (2025)", "Trick 'r Treat", 2007, new TitleDetails()), ["year"]),
            ("a subtitle left out", Film("Dune Part Two (2024)", "Dune : Deuxième partie", 2024,
                new TitleDetails { OriginalName = "Dune: Part Two" }), []),
            ("no TMDB title", new LibraryTitle { Kind = "movie", FolderName = "Home movie" }, ["no-tmdb"]),
            ("its folder names another", Film("Heat (1995) [tmdbid-949]", "Heat", 1995, new TitleDetails()), ["tmdb-tag"]),
            ("chosen by an admin", new LibraryTitle { Kind = "movie", FolderName = "Anything", Tmdb = "1", TmdbName = "Else", Overridden = true }, []),
            ("far longer than TMDB says", new LibraryTitle
            {
                Kind = "movie", FolderName = "Heat (1995)", Tmdb = "1", TmdbName = "Heat", TmdbYear = 1995,
                RuntimeMs = 24 * 60_000, Details = new TitleDetails { Runtime = 170 },
            }, ["runtime"]),
            ("the remake of a series: more seasons here", new LibraryTitle
            {
                Kind = "series", FolderName = "19-2", Tmdb = "61546", TmdbName = "19-2", TmdbYear = 2014,
                Details = new TitleDetails { Seasons = [new SeasonInfo(1, string.Empty, 10), new SeasonInfo(2, string.Empty, 10)] },
                Episodes = new Dictionary<int, int> { [0] = 2, [1] = 10, [2] = 10, [3] = 10, [4] = 10 },
            }, ["seasons"]),
            ("an extended cut", new LibraryTitle
            {
                Kind = "movie", FolderName = "Watchmen (2009)", Tmdb = "1", TmdbName = "Watchmen", TmdbYear = 2009,
                RuntimeMs = 215 * 60_000, Details = new TitleDetails { Runtime = 162 },
            }, []),
            ("a double episode split in two", new LibraryTitle
            {
                Kind = "series", FolderName = "Friends", Tmdb = "1", TmdbName = "Friends",
                Details = new TitleDetails { Seasons = [new SeasonInfo(1, string.Empty, 24), new SeasonInfo(2, string.Empty, 23)] },
                Episodes = new Dictionary<int, int> { [1] = 24, [2] = 24 },
            }, []),
            ("an anime TMDB lists as one season", new LibraryTitle
            {
                Kind = "series", FolderName = "Bleach", Tmdb = "1", TmdbName = "Bleach",
                Details = new TitleDetails { Seasons = [new SeasonInfo(1, string.Empty, 366)] },
                Episodes = new Dictionary<int, int> { [1] = 20, [2] = 21, [3] = 22, [4] = 300 },
            }, []),
        };
        foreach (var (about, title, want) in cases)
        {
            var got = Doubts.Reasons(title).Select(r => r.Code).ToArray();
            Assert.True(want.SequenceEqual(got), $"{about}: [{string.Join(", ", got)}], want [{string.Join(", ", want)}]");
        }

        // Its own TMDB ID in the folder: right, whatever else.
        Assert.Empty(Doubts.Reasons(Film("Whatever (1950) [tmdbid-1]", "Heat", 1995, new TitleDetails())));
    }

    [Fact]
    public void ReasonsSayWhatTheyFound()
    {
        var title = new LibraryTitle
        {
            Kind = "series", FolderName = "19-2", Tmdb = "61546", TmdbName = "19-2",
            Details = new TitleDetails { Seasons = [new SeasonInfo(1, string.Empty, 10), new SeasonInfo(2, string.Empty, 10)] },
            Episodes = new Dictionary<int, int> { [1] = 10, [2] = 14, [3] = 10, [4] = 10, [5] = 10 },
        };
        Assert.Equal(
            "Season 2 has 14 episodes here, 10 on TMDB; season 3 isn't on TMDB; season 4 isn't on TMDB; ….",
            Assert.Single(Doubts.Reasons(title)).Text);
        var film = new LibraryTitle
        {
            Kind = "movie", FolderName = "The Swedish Connection (2026)", Tmdb = "1", TmdbName = "L'Odyssée", TmdbYear = 2026,
            Details = new TitleDetails { OriginalName = "The Odyssey" },
        };
        Assert.Equal("Named “The Swedish Connection” here; TMDB calls it “L'Odyssée” (originally “The Odyssey”).",
            Assert.Single(Doubts.Reasons(film)).Text);
    }

    [Fact]
    public void LanguagesAreComparedHoweverWritten()
    {
        // Overfin's lang TestSame.
        foreach (var (a, b, same) in new[]
        {
            ("fr", "fre", true), ("fr-CA", "fra", true), ("en", "eng", true), ("FR", "fr", true), ("de", "ger", true),
            ("fr", "eng", false), ("und", "und", false), (string.Empty, string.Empty, false), ("xx", "xx", false), ("pt-BR", "por", true),
        })
        {
            Assert.True(Languages.Same(a, b) == same, $"{a} {b}");
        }

        Assert.Equal("fr", Languages.Base("fr_CA"));
        Assert.Equal(string.Empty, Languages.Base("zzz"));
    }

    // Any folder or file name gives a year from 1900 to 2099 or none, a TMDB
    // ID written as TMDB writes it or none, and a name with no runs of
    // spaces: fuzz_test.go's seeds, and random names made of their pieces.
    [Fact]
    public void AnyNameParsesSafely()
    {
        var seeds = new List<string>
        {
            "Heat (1995) [tmdbid-949]", "The.Candidate.2023.1080p.mkv", "1917 (2019)", "1917",
            "Show {tmdb=1399}", "Film [tmdbid-0949]", "[tmdb-0]", "  - ( [ ",
        };
        string[] pieces = ["Heat", " ", ".", "(", ")", "[", "]", "{", "}", "-", "_", "1995", "2019", "1899", "2100", "tmdbid-",
            "tmdb=", "imdbid-tt", "0113277", "949", "0", "1080p", "WEB", "-GROUP", ".mkv", "é", "千", "🎬", "\t", "\n"];
        var random = new Random(1);
        for (var i = 0; i < 5000; i++)
        {
            var b = new StringBuilder();
            for (var n = random.Next(1, 12); n > 0; n--)
            {
                b.Append(pieces[random.Next(pieces.Length)]);
            }

            seeds.Add(b.ToString());
        }

        foreach (var s in seeds)
        {
            var n = Names.Parse(s);
            Assert.True(n.Year == 0 || (n.Year >= 1900 && n.Year <= 2099), $"{s} -> year {n.Year}");
            if (n.Tmdb.Length > 0)
            {
                Assert.True(int.TryParse(n.Tmdb, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                    && id.ToString(CultureInfo.InvariantCulture) == n.Tmdb, $"{s} -> TMDB {n.Tmdb}");
            }

            Assert.DoesNotContain("  ", n.Name, StringComparison.Ordinal);
            Assert.True(IsValidUtf16(n.Name), $"{s} -> name {n.Name}");
        }
    }

    // The cases Identification caught on a real library, matched right from
    // the start: by name and year, by a film's length, by a series' seasons,
    // by its language; sure when one clearly fits, said when not.
    [Fact]
    public async Task TitlesAreMatched()
    {
        var (src, cases) = MatchFixture();
        foreach (var (name, f, want, sure) in cases)
        {
            var got = await Matcher.MatchAsync(src, f);
            Assert.True(got.Tmdb == want && got.Sure == sure,
                $"{name}: {got.Tmdb} sure={got.Sure} [{string.Join("; ", got.Reasons.Select(r => r.Text))}], want {want} sure={sure}");
            Assert.True(got.Sure || got.Reasons.Count > 0, $"{name}: unsure without a reason");
        }
    }

    // A film at the root of its library: the library's folder isn't its
    // name, its file's is. Before, "films" was compared with TMDB's name,
    // and every such film was held to check.
    [Theory]
    [InlineData("/media/films/The Matrix (1999) [tmdbid-603].mp4", "")]
    [InlineData("/media/films/The Matrix (1999)/The Matrix (1999).mp4", "The Matrix (1999)")]
    [InlineData("/elsewhere/Heat (1995).mkv", "elsewhere")]
    public void FolderOfAFilmAtALibraryRoot(string path, string folder) =>
        Assert.Equal(folder, Identifier.FolderOf(path, ["/media/films/", "/media/films2"]));

    [Fact]
    public async Task AFilmAtALibraryRootIsNamedByItsFile()
    {
        var (src, _) = MatchFixture();
        var got = await Matcher.MatchAsync(src, new Files { Kind = "movie", Folder = "", File = "Heat (1995) [tmdbid-949].mkv" });
        Assert.Equal("949", got.Tmdb);
        Assert.True(got.Sure, string.Join("; ", got.Reasons.Select(r => r.Text)));
        got = await Matcher.MatchAsync(src, new Files { Kind = "movie", Folder = "films", File = "Heat (1995) [tmdbid-949].mkv" });
        Assert.False(got.Sure);
    }

    [Fact]
    public async Task IdsInNamesAreTakenAndChecked()
    {
        var (src, _) = MatchFixture();

        // A TMDB ID in the name is taken without a search, and what the
        // files contradict is said, not undone.
        src.Asked.Clear();
        var got = await Matcher.MatchAsync(src, new Files { Kind = "movie", Folder = "Heat (1995) [tmdbid-949]", RuntimeMs = 60 * 60_000 });
        Assert.Equal("949", got.Tmdb);
        Assert.False(got.Sure);
        Assert.Equal("runtime", Assert.Single(got.Reasons).Code);
        Assert.DoesNotContain(src.Asked, a => a.StartsWith("search", StringComparison.Ordinal));

        // A file under a minute long says nothing of its length.
        Assert.True((await Matcher.MatchAsync(src, new Files { Kind = "movie", Folder = "Heat (1995) [tmdbid-949]", RuntimeMs = 20_000 })).Sure);

        // One TMDB doesn't know is searched for instead.
        Assert.Equal("949", (await Matcher.MatchAsync(src, new Files { Kind = "movie", Folder = "Heat (1995) [tmdbid-424242]" })).Tmdb);

        // Nothing found, nothing to search: no title, and why.
        foreach (var folder in new[] { "A Film Nobody Made", "[1080p]" })
        {
            got = await Matcher.MatchAsync(src, new Files { Kind = "movie", Folder = folder });
            Assert.Equal(string.Empty, got.Tmdb);
            Assert.False(got.Sure);
            Assert.Single(got.Reasons);
        }

        await Assert.ThrowsAsync<ArgumentException>(() => Matcher.MatchAsync(src, new Files { Kind = "episode" }));
    }

    [Fact]
    public async Task SeveralFittingAreSaid()
    {
        var a = Film(1, "Twin", 2000, 100, "en");
        var b = Film(2, "Twin", 2000, 100, "en");
        var src = new FakeTmdb();
        src.Pages["movie/1"] = a;
        src.Pages["movie/2"] = b;
        src.Searches["movie/twin/2000"] = [Found(a, 10), Found(b, 10)];
        var got = await Matcher.MatchAsync(src, new Files { Kind = "movie", Folder = "Twin (2000)" });
        Assert.Equal("1", got.Tmdb);
        Assert.False(got.Sure);
        Assert.Equal("Several titles on TMDB fit: “Twin” (2000) and “Twin” (2000).", Assert.Single(got.Reasons).Text);
        Assert.Equal(2, got.Candidates.Count);
    }

    private static bool IsValidUtf16(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static TmdbTitle Film(int id, string name, int year, int runtime, string language, params string[] names) => new()
    {
        Id = id, Kind = "movie", Name = name, Year = year, OriginalName = name, Language = language, Runtime = runtime, Names = names,
    };

    private static TmdbTitle Series(int id, string name, int year, string language, params int[] seasons) => new()
    {
        Id = id, Kind = "series", Name = name, Year = year, OriginalName = name, Language = language,
        Seasons = seasons.Select((n, i) => new SeasonInfo(i + 1, string.Empty, n)).ToList(),
    };

    private static TmdbFound Found(TmdbTitle t, int votes) => new(t.Id, t.Kind, t.Name, t.Year, votes);

    private static (FakeTmdb Src, (string Name, Files F, string Want, bool Sure)[] Cases) MatchFixture()
    {
        TmdbTitle heat = Film(949, "Heat", 1995, 170, "en"), heat86 = Film(10000, "Heat", 1986, 101, "en");
        TmdbTitle spirited = Film(129, "Spirited Away", 2001, 125, "ja", "Le Voyage de Chihiro"), special = Film(535075, "Spirited Away", 2001, 48, "ja");
        TmdbTitle quebec = Series(38527, "19-2", 2011, "fr", 10, 10, 10), remake = Series(61546, "19-2", 2014, "en", 10, 10, 10, 8);
        TmdbTitle titan = Series(1429, "Attack on Titan", 2013, "ja", 25, 12, 22, 28), chibi = Series(224499, "Attack on Titan", 2013, "ja", 10);
        TmdbTitle swedish = Film(1261188, "The Swedish Connection", 2026, 95, "sv"), odyssey = Film(1368337, "The Odyssey", 2026, 150, "en");
        var src = new FakeTmdb();
        foreach (var t in new[] { heat, heat86, spirited, special, quebec, remake, titan, chibi, swedish, odyssey })
        {
            src.Pages[t.Kind + "/" + t.Id.ToString(CultureInfo.InvariantCulture)] = t;
        }

        src.Searches["movie/heat/1995"] = [Found(heat, 8000)];
        src.Searches["movie/spirited away/2001"] = [Found(special, 40), Found(spirited, 17000)];
        src.Searches["series/19-2/0"] = [Found(remake, 300), Found(quebec, 40)];
        src.Searches["series/attack on titan/0"] = [Found(chibi, 60), Found(titan, 7000)];
        src.Searches["movie/the swedish connection/2026"] = [Found(odyssey, 900), Found(swedish, 20)];
        src.Searches["movie/a film nobody made/0"] = [];
        src.Searches["movie/heat/1990"] = [];
        src.Searches["movie/heat/0"] = [Found(heat, 8000), Found(heat86, 20)];
        src.Finds["imdb/tt0245429"] = 129;

        var cases = new (string, Files, string, bool)[]
        {
            ("name and year", new Files { Kind = "movie", Folder = "Heat (1995)", RuntimeMs = 170 * 60_000 }, "949", true),
            ("a film's length over a special", new Files { Kind = "movie", Folder = "Spirited Away (2001)", RuntimeMs = 125 * 60_000 }, "129", true),
            ("the audio's language", new Files
            {
                Kind = "series", Folder = "19-2", Audio = ["fre"], Episodes = new Dictionary<int, int> { [1] = 10, [2] = 10, [3] = 10 },
            }, "38527", false),
            ("a series' seasons over a spin-off", new Files
            {
                Kind = "series", Folder = "Attack on Titan", Episodes = new Dictionary<int, int> { [1] = 25, [2] = 12, [3] = 22 },
            }, "1429", true),
            ("the name over the more known", new Files { Kind = "movie", Folder = "The Swedish Connection (2026)", RuntimeMs = 95 * 60_000 }, "1261188", true),
            ("a year off by a few: searched again without it", new Files { Kind = "movie", Folder = "Heat (1990)" }, "949", false),
            ("an IMDb ID in the name", new Files
            {
                Kind = "movie", Folder = "Le Voyage de Chihiro [imdbid-tt0245429]", RuntimeMs = 125 * 60_000,
            }, "129", true),
        };
        return (src, cases);
    }

    // FakeTmdb answers matching's questions from what it's given: searches
    // by kind, name and year (0: any), pages by kind and ID, IDs by source.
    private sealed class FakeTmdb : ITmdbSource
    {
        public Dictionary<string, List<TmdbFound>> Searches { get; } = [];

        public Dictionary<string, TmdbTitle> Pages { get; } = [];

        public Dictionary<string, int> Finds { get; } = [];

        public List<string> Asked { get; } = [];

        public Task<IReadOnlyList<TmdbFound>> SearchKindAsync(string kind, string query, int year, CancellationToken ct = default)
        {
            var key = kind + "/" + query.ToLowerInvariant() + "/" + year.ToString(CultureInfo.InvariantCulture);
            Asked.Add("search " + key);
            return Task.FromResult<IReadOnlyList<TmdbFound>>(Searches.GetValueOrDefault(key) ?? []);
        }

        public Task<TmdbTitle?> TitleAsync(string kind, int tmdbId, CancellationToken ct = default)
        {
            var key = kind + "/" + tmdbId.ToString(CultureInfo.InvariantCulture);
            Asked.Add("page " + key);
            return Task.FromResult(Pages.GetValueOrDefault(key));
        }

        public Task<int> FindAsync(string kind, string source, string id, CancellationToken ct = default) =>
            Task.FromResult(Finds.GetValueOrDefault(source + "/" + id));

        public Task<TmdbEpisodeFound?> FindEpisodeAsync(string source, string id, CancellationToken ct = default) =>
            Task.FromResult<TmdbEpisodeFound?>(null);

        public Task<IReadOnlyList<TmdbEpisode>?> SeasonAsync(int seriesId, int season, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbEpisode>?>(null);
    }
}
