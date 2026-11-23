using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Msfp;
using Msfp.Protocol;
using Picture = Jellyfin.Plugin.Msfp.Federation.Picture;
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
        // Since MSFP's revision a title's picture and languages are its
        // copies' (phase 5 builds stand-ins from the copies themselves).
        Holders.Add(new Holder(peer, PictureOf(t.BestCopy())));
        Holders.Sort((a, b) => Better(a.Picture, b.Picture) ? -1 : Better(b.Picture, a.Picture) ? 1 : string.CompareOrdinal(a.Peer, b.Peer));
        Audio.UnionWith(t.AudioLanguages());
        Subtitles.UnionWith(t.SubtitleLanguages());
    }

    private static bool Better(Picture? a, Picture? b) => a is not null && (b is null || a.Better(b));

    /// <summary>A copy's picture as the holders are ranked: its height, its HDR (hdr10plus, else the range its transfer says).</summary>
    internal static Picture? PictureOf(global::Msfp.Protocol.Copy? copy)
    {
        if (copy?.Video is not { } v)
        {
            return null;
        }

        var hdr = v.Hdr.ToList();
        if (v.Colour?.Transfer is global::Msfp.Protocol.VideoColour.TransferPq)
        {
            hdr.Add("hdr10");
        }
        else if (v.Colour?.Transfer is global::Msfp.Protocol.VideoColour.TransferHlg)
        {
            hdr.Add("hlg");
        }

        if (v.Dv is not null)
        {
            hdr.Add("dolby-vision");
        }

        return new Picture { Height = v.Height, Hdr = hdr.Count > 0 ? hdr : null };
    }
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
                // Only TMDB keys until phase 5 of the migration (tvdb, local
                // and extras' keys of MSFP's revision).
                if (!VideoKey.TryParse(t.Key, out var k) || k.Scheme != "tmdb" || k.Kind == "extra")
                {
                    continue;
                }

                switch (k.Kind)
                {
                    case "movie" when !held.Films.Contains(k.Id):
                        if (!w.Films.TryGetValue(k.Id, out var f))
                        {
                            f = new WantedFilm { Tmdb = k.Id, Copy = new Playable { Key = k.ToString() } };
                            w.Films[k.Id] = f;
                        }

                        f.Copy.Add(peer, t);
                        AddIds(f.Ids, t);
                        break;

                    case "series":
                        // Its IDs only: a series is shown for its episodes.
                        if (w.Series.TryGetValue(k.Id, out var known))
                        {
                            AddIds(known.Ids, t);
                        }
                        else
                        {
                            w.SeriesIds(k.Id, t);
                        }

                        break;

                    case "episode":
                        // By TMDB's ID, at its place; by its numbers otherwise.
                        var (season, episode) = k.ByEpisodeId
                            ? t.Place is { } p ? (p.Season, p.Episode) : (-1, -1)
                            : (k.Season, k.Episode);
                        if (season < 0 || held.Series.TryGetValue(k.Id, out var hs) && hs.Holds(k.EpisodeId, season, episode))
                        {
                            continue;
                        }

                        var s = w.SeriesNamed(k.Id);
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
        new VideoKey("season", "tmdb", series, Season: season).ToString();

    public static string SeriesKey(long series) => new VideoKey("series", "tmdb", series).ToString();

    public static string FilmKey(long film) => new VideoKey("movie", "tmdb", film).ToString();

    internal static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);
}
