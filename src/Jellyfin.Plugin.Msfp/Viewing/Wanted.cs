using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Msfp.Protocol;
using Title = Msfp.Title;
using VideoTitle = Msfp.VideoTitle;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>A paired server holding a title: its key for it there, and its copies, best first (MSFP video section 3.7's order).</summary>
internal sealed record Holder(string Peer, string Key, IReadOnlyList<Copy> Copies)
{
    /// <summary>Its best copy.</summary>
    public Copy Best => Copies[0];
}

/// <summary>
/// A film or an episode as a stand-in shows it: the paired servers holding
/// it, the one with the best copy first. The stand-in's sources are that
/// holder's copies (<see cref="Chosen"/>); the others' are taken at play
/// when it has none free (migration phase 6).
/// </summary>
internal sealed class Playable
{
    /// <summary>The stand-in's path.</summary>
    public string Path { get; init; } = string.Empty;

    public List<Holder> Holders { get; } = [];

    /// <summary>The holder whose copies are the stand-in's sources; null for none.</summary>
    public Holder? Chosen => Holders.Count > 0 ? Holders[0] : null;

    internal void Add(string peer, VideoTitle t)
    {
        if (t.Copies is not { Count: > 0 } copies || Holders.Exists(h => h.Peer == peer && h.Key == t.Key))
        {
            return;
        }

        Holders.Add(new Holder(peer, t.Key, Copies.Sorted(copies)));
        Holders.Sort(Order);
    }

    // The holder with the better best copy first, then the one with more
    // copies; then by server and key, the same at every pass.
    private static int Order(Holder a, Holder b)
    {
        var c = Copies.Compare(a.Best, b.Best);
        if (c != 0)
        {
            return c;
        }

        c = b.Copies.Count.CompareTo(a.Copies.Count);
        if (c != 0)
        {
            return c;
        }

        c = string.CompareOrdinal(a.Peer, b.Peer);
        return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
    }
}

/// <summary>
/// The owner's description of a local title (MSFP video section 3.10): its
/// name, year and whether it shares an image. Shown as the owner's, never
/// looked up elsewhere, never passed on.
/// </summary>
internal sealed record LocalTitle(string Peer, string Key, string Name, int Year, bool Image);

/// <summary>An episode as a stand-in: its place (season, episode), its IDs (tmdb, tvdb, imdb, as Jellyfin's provider IDs), its copies.</summary>
internal sealed class WantedEpisode
{
    public int Season { get; set; }

    public int Episode { get; set; }

    /// <summary>TMDB's ID of it; 0 for none.</summary>
    public long EpisodeId { get; set; }

    public Dictionary<string, string> Ids { get; } = new(StringComparer.Ordinal);

    public Playable Copy { get; init; } = new();
}

/// <summary>
/// A series as a stand-in: its TMDB ID and its TheTVDB ID (either may be 0:
/// a series one paired server keys under tvdb and another under tmdb is
/// one, MSFP video section 4.3), its other IDs, its local description for a
/// local one, and the episodes this server doesn't hold, by their
/// stand-ins' paths.
/// </summary>
internal sealed class WantedSeries
{
    public string Path { get; init; } = string.Empty;

    public long Tmdb { get; set; }

    public long Tvdb { get; set; }

    public LocalTitle? Local { get; init; }

    public Dictionary<string, string> Ids { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, WantedEpisode> Episodes { get; } = new(StringComparer.Ordinal);
}

/// <summary>A film as a stand-in: by TMDB, or a paired server's local film.</summary>
internal sealed class WantedFilm
{
    public string Path { get; init; } = string.Empty;

    public long Tmdb { get; init; }

    public LocalTitle? Local { get; init; }

    public Playable Copy { get; init; } = new();
}

/// <summary>
/// What this server holds itself, for the stand-ins to leave out: its films
/// by TMDB ID; its series by TMDB and by TheTVDB ID, their episodes by
/// TMDB's or TheTVDB's ID of them, and by their numbers where neither is
/// known.
/// </summary>
internal sealed class Held
{
    public HashSet<long> Films { get; } = [];

    public Dictionary<long, HeldSeries> Series { get; } = [];

    public Dictionary<long, HeldSeries> TvdbSeries { get; } = [];

    public HeldSeries SeriesOf(long tmdb) => Of(Series, tmdb);

    public HeldSeries TvdbSeriesOf(long tvdb) => Of(TvdbSeries, tvdb);

    private static HeldSeries Of(Dictionary<long, HeldSeries> all, long id)
    {
        if (!all.TryGetValue(id, out var s))
        {
            s = new HeldSeries();
            all[id] = s;
        }

        return s;
    }
}

/// <summary>A series this server holds: its episodes' TMDB and TheTVDB IDs, and the numbers of those whose ID isn't known.</summary>
internal sealed class HeldSeries
{
    public HashSet<long> EpisodeIds { get; } = [];

    public HashSet<long> TvdbEpisodeIds { get; } = [];

    public HashSet<(int Season, int Episode)> Numbers { get; } = [];

    public bool Holds(long episodeId, int season, int episode) =>
        episodeId > 0 && EpisodeIds.Contains(episodeId) || Numbers.Contains((season, episode));
}

/// <summary>
/// The stand-ins wanted (DESIGN-NOTES): one per film or episode whatever the
/// number of paired servers holding it, none for what this server holds
/// itself (a series by its episodes), a series only with an episode to
/// show. Films and series of the tmdb scheme are one stand-in across
/// servers; a series keyed under tvdb joins the tmdb one its ids (or
/// Jellyfin's metadata of the stand-in, <c>links</c>) name, its episodes
/// matched by their ids (MSFP video section 4.3); local titles are each
/// server's own (section 4.5). Extras: migration phase 7.
/// </summary>
internal sealed class Wanted
{
    public SortedDictionary<string, WantedFilm> Films { get; } = new(StringComparer.Ordinal);

    public SortedDictionary<string, WantedSeries> Series { get; } = new(StringComparer.Ordinal);

    /// <summary>Every film and episode, by its stand-in's path.</summary>
    public Dictionary<string, Playable> ByPath { get; } = new(StringComparer.Ordinal);

    public int Count => Films.Count + Series.Values.Sum(s => s.Episodes.Count);

    /// <summary>
    /// The stand-ins for the titles of the paired servers given (each with
    /// the titles read of it), less what this server holds. <paramref name="links"/>
    /// are TMDB series known to be TheTVDB series (Jellyfin's metadata of the stand-ins).
    /// </summary>
    public static Wanted Of(IEnumerable<(string Peer, IEnumerable<Title> Titles)> peers, Held held, IReadOnlyDictionary<long, long>? links = null)
    {
        var w = new Wanted();
        var all = new List<(string Peer, VideoTitle Title, VideoKey Key)>();
        foreach (var (peer, titles) in peers)
        {
            foreach (var t in titles.OfType<VideoTitle>())
            {
                if (VideoKey.TryParse(t.Key, out var k) && k.Kind != "extra")
                {
                    all.Add((peer, t, k));
                }
            }
        }

        // A tvdb series joins the tmdb one it names, or Jellyfin's metadata does.
        var groups = new Groups();
        foreach (var (_, t, k) in all)
        {
            if (k.Scheme == Video.SchemeLocal || k.Kind == "movie")
            {
                continue;
            }

            groups.Add(Node(k));
            if (k.Kind == "series" && k.Scheme == Video.SchemeTvdb && Id(t.Ids, "tmdb") is { } tmdb)
            {
                groups.Join(Node(k), "tmdb:" + Number(tmdb));
            }
        }

        foreach (var (tmdb, tvdb) in links ?? new Dictionary<long, long>())
        {
            if (groups.Has("tmdb:" + Number(tmdb)) && groups.Has("tvdb:" + Number(tvdb)))
            {
                groups.Join("tmdb:" + Number(tmdb), "tvdb:" + Number(tvdb));
            }
        }

        foreach (var (peer, t, k) in all.Where(x => x.Key.Kind == "movie"))
        {
            if (k.Scheme == Video.SchemeLocal)
            {
                if (Local(peer, t) is { } local)
                {
                    var path = LocalPath(peer, t.Key);
                    var f = new WantedFilm { Path = path, Local = local, Copy = new Playable { Path = path } };
                    f.Copy.Add(peer, t);
                    if (f.Copy.Chosen is not null)
                    {
                        w.Films[path] = f;
                    }
                }

                continue;
            }

            if (held.Films.Contains(k.Id))
            {
                continue;
            }

            var p = PathOf(FilmKey(k.Id));
            if (!w.Films.TryGetValue(p, out var film))
            {
                film = new WantedFilm { Path = p, Tmdb = k.Id, Copy = new Playable { Path = p } };
                w.Films[p] = film;
            }

            film.Copy.Add(peer, t);
        }

        // A tvdb series' own IDs (imdb), for the group.
        var seriesIds = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (_, t, k) in all.Where(x => x.Key.Kind == "series" && x.Key.Scheme == Video.SchemeTvdb))
        {
            var ids = Ensure(seriesIds, groups.Root(Node(k)));
            foreach (var (scheme, value) in t.Ids ?? [])
            {
                ids.TryAdd(scheme, value);
            }
        }

        var locals = new Dictionary<string, LocalTitle>(StringComparer.Ordinal);
        foreach (var (peer, t, k) in all.Where(x => x.Key.Kind == "series" && x.Key.Scheme == Video.SchemeLocal))
        {
            if (Local(peer, t) is { } local)
            {
                locals[peer + " " + k.Series] = local;
            }
        }

        // Episodes keyed under tmdb first: those under tvdb join them by their ids.
        var episodes = all.Where(x => x.Key.Kind == "episode")
            .OrderBy(x => x.Key.Scheme == Video.SchemeTmdb ? 0 : 1)
            .ThenBy(x => x.Title.Key, StringComparer.Ordinal)
            .ThenBy(x => x.Peer, StringComparer.Ordinal);
        var byTmdbEpisode = new Dictionary<(string, long), WantedEpisode>();
        var byTvdbEpisode = new Dictionary<(string, long), WantedEpisode>();
        var groupIds = new Dictionary<string, (long Tmdb, long Tvdb)>(StringComparer.Ordinal);
        foreach (var (peer, t, k) in episodes)
        {
            if (k.Scheme == Video.SchemeLocal)
            {
                if (locals.TryGetValue(peer + " " + k.Series, out var local))
                {
                    var ls = w.SeriesAt(LocalPath(peer, k.Series), local);
                    Episode(ls, LocalPath(peer, t.Key), k.Season, k.Episode).Copy.Add(peer, t);
                }

                continue;
            }

            var root = groups.Root(Node(k));
            if (!groupIds.TryGetValue(root, out var gi))
            {
                gi = Ids(groups.Members(root));
                groupIds[root] = gi;
            }

            var (tmdbSeries, tvdbSeries) = gi;
            var ids = new Dictionary<string, string>(t.Ids ?? [], StringComparer.Ordinal);
            var tmdbEpisode = k.ByEpisodeId ? k.EpisodeId : Id(ids, "tmdb") ?? 0;
            var tvdbEpisode = Id(ids, "tvdb") ?? 0;
            int season, number;
            if (k.ByEpisodeId)
            {
                if (t.Place is not { } place)
                {
                    continue;
                }

                (season, number) = (place.Season, place.Episode);
            }
            else if (k.Absolute)
            {
                // TheTVDB's absolute order, as the series' first season.
                (season, number) = (1, k.Episode);
            }
            else
            {
                (season, number) = (k.Season, k.Episode);
            }

            if (HeldOf(held, tmdbSeries, tvdbSeries) is { } hs
                && (hs.Holds(tmdbEpisode, season, number) || tvdbEpisode > 0 && hs.TvdbEpisodeIds.Contains(tvdbEpisode)))
            {
                continue;
            }

            WantedEpisode? found = null;
            if (tmdbEpisode > 0)
            {
                byTmdbEpisode.TryGetValue((root, tmdbEpisode), out found);
            }

            if (found is null && tvdbEpisode > 0)
            {
                byTvdbEpisode.TryGetValue((root, tvdbEpisode), out found);
            }

            var series = w.SeriesAt(tmdbSeries > 0 ? PathOf(SeriesKey(tmdbSeries)) : PathOf("series/tvdb/" + Number(tvdbSeries)), null);
            series.Tmdb = tmdbSeries;
            series.Tvdb = tvdbSeries;
            if (seriesIds.TryGetValue(root, out var sids))
            {
                foreach (var (scheme, value) in sids)
                {
                    series.Ids.TryAdd(scheme, value);
                }
            }

            if (found is null)
            {
                // A TMDB episode at the series' TMDB key, any other at its own.
                var key = tmdbEpisode > 0 && tmdbSeries > 0 ? new VideoKey("episode", Video.SchemeTmdb, tmdbSeries, EpisodeId: tmdbEpisode).ToString() : t.Key;
                found = Episode(series, PathOf(key), season, number);
            }
            else if (k.ByEpisodeId)
            {
                // TMDB's place before another numbering's.
                (found.Season, found.Episode) = (season, number);
            }

            if (tmdbEpisode > 0)
            {
                found.EpisodeId = tmdbEpisode;
                found.Ids.TryAdd("tmdb", Number(tmdbEpisode));
                byTmdbEpisode.TryAdd((root, tmdbEpisode), found);
            }

            if (tvdbEpisode > 0)
            {
                found.Ids.TryAdd("tvdb", Number(tvdbEpisode));
                byTvdbEpisode.TryAdd((root, tvdbEpisode), found);
            }

            if (ids.TryGetValue("imdb", out var imdb))
            {
                found.Ids.TryAdd("imdb", imdb);
            }

            found.Copy.Add(peer, t);
        }

        foreach (var f in w.Films.Values)
        {
            w.ByPath[f.Path] = f.Copy;
        }

        foreach (var s in w.Series.Values)
        {
            foreach (var (path, e) in s.Episodes)
            {
                w.ByPath[path] = e.Copy;
            }
        }

        return w;
    }

    private WantedSeries SeriesAt(string path, LocalTitle? local)
    {
        if (!Series.TryGetValue(path, out var s))
        {
            s = new WantedSeries { Path = path, Local = local };
            Series[path] = s;
        }

        return s;
    }

    private static WantedEpisode Episode(WantedSeries s, string path, int season, int episode)
    {
        if (!s.Episodes.TryGetValue(path, out var e))
        {
            e = new WantedEpisode { Season = season, Episode = episode, Copy = new Playable { Path = path } };
            s.Episodes[path] = e;
        }

        return e;
    }

    // What this server holds of a series known by these IDs; null for none.
    private static HeldSeries? HeldOf(Held held, long tmdb, long tvdb)
    {
        held.Series.TryGetValue(tmdb, out var a);
        held.TvdbSeries.TryGetValue(tvdb, out var b);
        if (a is null || b is null)
        {
            return a ?? b;
        }

        var both = new HeldSeries();
        foreach (var h in new[] { a, b })
        {
            both.EpisodeIds.UnionWith(h.EpisodeIds);
            both.TvdbEpisodeIds.UnionWith(h.TvdbEpisodeIds);
            both.Numbers.UnionWith(h.Numbers);
        }

        return both;
    }

    // The TMDB and TheTVDB IDs a group of series gives: the lowest of each.
    private static (long Tmdb, long Tvdb) Ids(IEnumerable<string> nodes)
    {
        long tmdb = 0, tvdb = 0;
        foreach (var n in nodes)
        {
            var id = long.Parse(n.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture);
            if (n.StartsWith("tmdb:", StringComparison.Ordinal))
            {
                tmdb = tmdb == 0 ? id : Math.Min(tmdb, id);
            }
            else
            {
                tvdb = tvdb == 0 ? id : Math.Min(tvdb, id);
            }
        }

        return (tmdb, tvdb);
    }

    private static LocalTitle? Local(string peer, VideoTitle t) =>
        string.IsNullOrWhiteSpace(t.Name) ? null : new LocalTitle(peer, t.Key, t.Name.Trim(), t.Year, t.Image);

    private static string Node(VideoKey k) => k.Scheme + ":" + Number(k.Id);

    private static long? Id(Dictionary<string, string>? ids, string scheme) =>
        ids is not null && ids.TryGetValue(scheme, out var v) && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    private static Dictionary<string, string> Ensure(Dictionary<string, Dictionary<string, string>> all, string key)
    {
        if (!all.TryGetValue(key, out var d))
        {
            d = new Dictionary<string, string>(StringComparer.Ordinal);
            all[key] = d;
        }

        return d;
    }

    /// <summary>The stand-in's path: msfp://paired/ and its key, the same whenever it comes back.</summary>
    public static string PathOf(string key) => "msfp://paired/" + key;

    /// <summary>A paired server's local title's stand-in path: under the server's ID, local titles never matching another server's.</summary>
    public static string LocalPath(string peer, string key) => PathOf("local/" + peer + "/" + key);

    /// <summary>A season's stand-in path, under its series'.</summary>
    public static string SeasonPath(string seriesPath, int season) => seriesPath + "/s" + Number(season);

    public static string SeriesKey(long series) => new VideoKey("series", Video.SchemeTmdb, series).ToString();

    public static string FilmKey(long film) => new VideoKey("movie", Video.SchemeTmdb, film).ToString();

    internal static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);

    // Series ("tmdb:1399", "tvdb:81189") joined into groups.
    private sealed class Groups
    {
        private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);

        public bool Has(string n) => _parent.ContainsKey(n);

        public void Add(string n) => _parent.TryAdd(n, n);

        public string Root(string n)
        {
            Add(n);
            while (_parent[n] != n)
            {
                _parent[n] = _parent[_parent[n]];
                n = _parent[n];
            }

            return n;
        }

        public void Join(string a, string b)
        {
            var (x, y) = (Root(a), Root(b));
            if (x == y)
            {
                return;
            }

            // The lesser is the root, whatever the order of the joins.
            if (string.CompareOrdinal(x, y) < 0)
            {
                _parent[y] = x;
            }
            else
            {
                _parent[x] = y;
            }
        }

        public IEnumerable<string> Members(string root) => _parent.Keys.ToList().Where(n => Root(n) == root).ToList();
    }
}
