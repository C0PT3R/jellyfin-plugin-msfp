using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Msfp;
using Msfp.Protocol;
using Picture = Msfp.Picture;
using VideoTitle = Msfp.VideoTitle;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>A paired server holding a title, with the picture of its best copy (null when it sent none).</summary>
internal sealed record Holder(string Peer, Picture? Picture);

/// <summary>
/// A film or an episode as the stand-in shows it: the paired servers holding
/// it, best copy first, and the languages and picture any of them offers.
/// </summary>
internal sealed class Playable
{
    public string Key { get; init; } = string.Empty;

    public List<Holder> Holders { get; } = [];

    public SortedSet<string> Audio { get; } = new(StringComparer.Ordinal);

    public SortedSet<string> Subtitles { get; } = new(StringComparer.Ordinal);

    /// <summary>The best picture of the holders' copies; null when none sent one.</summary>
    public Picture? Picture => Holders.FirstOrDefault()?.Picture;

    internal void Add(string peer, VideoTitle t)
    {
        Holders.Add(new Holder(peer, t.Video));
        Holders.Sort((a, b) => Better(a.Picture, b.Picture) ? -1 : Better(b.Picture, a.Picture) ? 1 : string.CompareOrdinal(a.Peer, b.Peer));
        Audio.UnionWith(t.Audio ?? []);
        Subtitles.UnionWith(t.Subtitles ?? []);
    }

    private static bool Better(Picture? a, Picture? b) => a is not null && (b is null || a.Better(b));
}

/// <summary>A series' episode as a stand-in: its place in TMDB's numbering, and TMDB's ID of it (0 for an episode TMDB doesn't list).</summary>
internal sealed class WantedEpisode
{
    public int Season { get; init; }

    public int Episode { get; init; }

    public long EpisodeId { get; init; }

    public Playable Copy { get; init; } = new();
}

/// <summary>A series as a stand-in: its TMDB ID, its other IDs, and the episodes this server doesn't hold.</summary>
internal sealed class WantedSeries
{
    public long Tmdb { get; init; }

    public Dictionary<string, string> Ids { get; } = new(StringComparer.Ordinal);

    /// <summary>By the episode's own key.</summary>
    public SortedDictionary<string, WantedEpisode> Episodes { get; } = new(StringComparer.Ordinal);
}

/// <summary>A film as a stand-in.</summary>
internal sealed class WantedFilm
{
    public long Tmdb { get; init; }

    public Dictionary<string, string> Ids { get; } = new(StringComparer.Ordinal);

    public Playable Copy { get; init; } = new();
}

/// <summary>
/// What this server holds itself, for the stand-ins to leave out: its films
/// by TMDB ID; its series' episodes by TMDB's ID of them, and by their
/// numbers where TMDB's ID isn't known.
/// </summary>
internal sealed class Held
{
    public HashSet<long> Films { get; } = [];

    public Dictionary<long, HeldSeries> Series { get; } = [];

    public HeldSeries SeriesOf(long tmdb)
    {
        if (!Series.TryGetValue(tmdb, out var s))
        {
            s = new HeldSeries();
            Series[tmdb] = s;
        }

        return s;
    }
}

/// <summary>A series this server holds: its episodes' TMDB IDs, and the numbers of those whose ID isn't known.</summary>
internal sealed class HeldSeries
{
    public HashSet<long> EpisodeIds { get; } = [];

    public HashSet<(int Season, int Episode)> Numbers { get; } = [];

    public bool Holds(long episodeId, int season, int episode) =>
        episodeId > 0 && EpisodeIds.Contains(episodeId) || Numbers.Contains((season, episode));
}

/// <summary>
/// The stand-ins wanted (DESIGN-NOTES): one per film or episode whatever the
/// number of paired servers holding it, none for what this server holds
/// itself (a series by its episodes), a series only with an episode to show.
/// </summary>
internal sealed class Wanted
{
    public SortedDictionary<long, WantedFilm> Films { get; } = [];

    public SortedDictionary<long, WantedSeries> Series { get; } = [];

    public int Count => Films.Count + Series.Values.Sum(s => s.Episodes.Count);

    /// <summary>
    /// The stand-ins for the titles of the paired servers given (each with
    /// the titles read of it), less what this server holds.
    /// </summary>
    public static Wanted Of(IEnumerable<(string Peer, IEnumerable<Title> Titles)> peers, Held held)
    {
        var w = new Wanted();
        foreach (var (peer, titles) in peers)
        {
            foreach (var t in titles.OfType<VideoTitle>())
            {
                if (!VideoKey.TryParse(t.Key, out var k))
                {
                    continue;
                }

                switch (k.Kind)
                {
                    case "movie" when !held.Films.Contains(k.Tmdb):
                        if (!w.Films.TryGetValue(k.Tmdb, out var f))
                        {
                            f = new WantedFilm { Tmdb = k.Tmdb, Copy = new Playable { Key = k.ToString() } };
                            w.Films[k.Tmdb] = f;
                        }

                        f.Copy.Add(peer, t);
                        AddIds(f.Ids, t);
                        break;

                    case "series":
                        // Its IDs only: a series is shown for its episodes.
                        if (w.Series.TryGetValue(k.Tmdb, out var known))
                        {
                            AddIds(known.Ids, t);
                        }
                        else
                        {
                            w.SeriesIds(k.Tmdb, t);
                        }

                        break;

                    case "episode":
                        // By TMDB's ID, at its place; by its numbers otherwise.
                        var (season, episode) = k.ByEpisodeId
                            ? t.Place is { } p ? (p.Season, p.Episode) : (-1, -1)
                            : (k.Season, k.Episode);
                        if (season < 0 || held.Series.TryGetValue(k.Tmdb, out var hs) && hs.Holds(k.EpisodeId, season, episode))
                        {
                            continue;
                        }

                        var s = w.SeriesNamed(k.Tmdb);
                        var key = k.ToString();
                        if (!s.Episodes.TryGetValue(key, out var e))
                        {
                            e = new WantedEpisode { Season = season, Episode = episode, EpisodeId = k.EpisodeId, Copy = new Playable { Key = key } };
                            s.Episodes[key] = e;
                        }

                        e.Copy.Add(peer, t);
                        break;
                }
            }
        }

        // A series' IDs were kept aside until an episode of it showed.
        foreach (var (id, ids) in w._seriesIds)
        {
            if (w.Series.TryGetValue(id, out var s))
            {
                foreach (var (scheme, value) in ids)
                {
                    s.Ids.TryAdd(scheme, value);
                }
            }
        }

        return w;
    }

    private readonly Dictionary<long, Dictionary<string, string>> _seriesIds = [];

    private void SeriesIds(long tmdb, VideoTitle t)
    {
        if (!_seriesIds.TryGetValue(tmdb, out var ids))
        {
            ids = new Dictionary<string, string>(StringComparer.Ordinal);
            _seriesIds[tmdb] = ids;
        }

        AddIds(ids, t);
    }

    private WantedSeries SeriesNamed(long tmdb)
    {
        if (!Series.TryGetValue(tmdb, out var s))
        {
            s = new WantedSeries { Tmdb = tmdb };
            Series[tmdb] = s;
        }

        return s;
    }

    private static void AddIds(Dictionary<string, string> ids, VideoTitle t)
    {
        foreach (var (scheme, value) in t.Ids ?? [])
        {
            ids.TryAdd(scheme, value);
        }
    }

    /// <summary>The stand-in's path: msfp://paired/ and its key, the same whenever it comes back.</summary>
    public static string PathOf(string key) => "msfp://paired/" + key;

    /// <summary>A season's key, for its stand-in's path.</summary>
    public static string SeasonKey(long series, int season) =>
        new VideoKey("season", series, season).ToString();

    public static string SeriesKey(long series) => new VideoKey("series", series).ToString();

    public static string FilmKey(long film) => new VideoKey("movie", film).ToString();

    internal static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);
}
