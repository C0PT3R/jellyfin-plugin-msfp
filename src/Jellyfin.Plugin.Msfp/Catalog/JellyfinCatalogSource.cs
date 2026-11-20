using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Msfp.Identification;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Msfp;
using Msfp.Protocol;
using MsfpVideo = Msfp.Protocol.Video;
using Picture = Msfp.Picture;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>
/// This server's titles as Jellyfin holds them: films, series, seasons and
/// episodes with a TMDB ID (of the film, or of the series), keyed as MSFP
/// video section 4 says. Items without one are left out: they have no title
/// key. Music isn't shared (README).
/// </summary>
public sealed class JellyfinCatalogSource : ICatalogSource
{
    private static readonly BaseItemKind[] Kinds = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode];

    private readonly ILibraryManager _library;

    private readonly Func<Identities?> _identities;

    /// <param name="library">Jellyfin's library.</param>
    /// <param name="identities">The plugin's identities of the films and series: their keys come from there, never from Jellyfin's IDs.</param>
    internal JellyfinCatalogSource(ILibraryManager library, Func<Identities?> identities)
    {
        _library = library;
        _identities = identities;
    }

    /// <inheritdoc />
    public IReadOnlyList<Library> Libraries()
    {
        return _library.GetVirtualFolders()
            // The plugin's own two hold other servers' titles: never offered.
            .Where(f => Guid.TryParse(f.ItemId, out _) && !Viewing.StandIns.IsOurs(f))
            .Select(f => new Library { Id = Guid.Parse(f.ItemId).ToString("N"), Name = f.Name ?? string.Empty, Kind = Kind(f.CollectionType?.ToString()) })
            .ToList();
    }

    /// <summary>Jellyfin's collection type as a library kind of MSFP catalog section 2.</summary>
    internal static string Kind(string? collectionType)
    {
        if (collectionType == null)
        {
            return "mixed";
        }

        var k = collectionType.ToLowerInvariant();
        return k is "movies" or "tvshows" or "music" or "musicvideos" or "homevideos" or "boxsets" or "books" or "photos" or "mixed" ? k : string.Empty;
    }

    /// <inheritdoc />
    public IEnumerable<CatalogEntry> Entries()
    {
        var identities = _identities();
        if (identities is null)
        {
            yield break;
        }

        TitleIdentity? SeriesOf(Guid id) => id == Guid.Empty ? null : identities.Of(id);

        foreach (var lib in Libraries())
        {
            var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = Kinds,
                Recursive = true,
                IsVirtualItem = false,
                AncestorIds = [Guid.ParseExact(lib.Id, "N")],
            });
            foreach (var item in items.Where(i => !Viewing.PairedSources.IsStandIn(i)))
            {
                foreach (var entry in Of(item, lib.Id, identities.Of(item.Id), SeriesOf))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>A TMDB ID as a title key takes it: 1 to 9,999,999,999, no leading zero; null otherwise.</summary>
    internal static long? TmdbId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 10 || !id.All(char.IsAsciiDigit) || id[0] == '0')
        {
            return null;
        }

        var n = long.Parse(id, NumberStyles.None, CultureInfo.InvariantCulture);
        return n is > 0 and <= 9_999_999_999 ? n : null;
    }

    // An item's entries, keyed by the plugin's identities (MSFP video
    // section 4): a film or a series taken (sure, or chosen by the owner); a
    // season or an episode of a series taken. An episode TMDB lists goes by
    // its TMDB ID and place; one it doesn't, by its numbers.
    private static IEnumerable<CatalogEntry> Of(BaseItem item, string library, TitleIdentity? own, Func<Guid, TitleIdentity?> seriesOf)
    {
        switch (item)
        {
            case Movie when own?.Taken is { } id:
                yield return Copy(item, library, new VideoKey("movie", id), []) with { Ids = OtherIds(own) };
                break;

            case Series when own?.Taken is { } id:
                yield return new CatalogEntry(library, new VideoKey("series", id).ToString(), "series", null) { Ids = OtherIds(own) };
                break;

            case Season s when seriesOf(s.SeriesId)?.Taken is { } id && s.IndexNumber is >= 0 and <= 9999:
                yield return new CatalogEntry(library, new VideoKey("season", id, s.IndexNumber.Value).ToString(), "season", null);
                break;

            case Episode episode when seriesOf(episode.SeriesId) is { Taken: { } id } series
                && episode.ParentIndexNumber is >= 0 and <= 9999 && episode.IndexNumber is >= 0 and <= 99999:
                // A file of several episodes: each of them, with the others
                // (MSFP video section 4.2).
                var season = episode.ParentIndexNumber.Value;
                var first = episode.IndexNumber.Value;
                var last = episode.IndexNumberEnd is { } end && end > first && end <= 99999 && end - first < 16 ? end : first;
                var keys = Enumerable.Range(first, last - first + 1).Select(n =>
                    series.Episodes.TryGetValue(TitleIdentity.EpisodeKey(season, n), out var e)
                        ? (Key: new VideoKey("episode", id, EpisodeId: e.EpisodeId), Place: (global::Msfp.Place?)new global::Msfp.Place { Season = e.Season, Episode = e.Episode })
                        : (Key: new VideoKey("episode", id, season, n), Place: null))
                    .DistinctBy(k => k.Key.ToString())
                    .ToList();
                foreach (var k in keys)
                {
                    yield return Copy(item, library, k.Key, keys.Where(o => o.Key != k.Key).Select(o => o.Key.ToString()).Order(StringComparer.Ordinal).ToList()) with { Place = k.Place };
                }

                break;
        }
    }

    private static CatalogEntry Copy(BaseItem item, string library, VideoKey key, IReadOnlyList<string> with)
    {
        var streams = item.GetMediaStreams();
        return new CatalogEntry(library, key.ToString(), key.Kind, item.Id.ToString("N"))
        {
            Audio = Languages(streams.Where(s => s.Type == MediaStreamType.Audio)),
            // Only the subtitles play can list: the text ones, served as
            // WebVTT, and PGS (Playback/JellyfinPlayback.cs).
            Subtitles = Languages(streams.Where(s => s.Type == MediaStreamType.Subtitle && (s.IsTextSubtitleStream || s.IsPgsSubtitleStream))),
            Picture = PictureOf(streams.FirstOrDefault(s => s.Type == MediaStreamType.Video)),
            With = with,
            Ids = new Dictionary<string, string>(),
            CopyName = item.Name ?? string.Empty,
        };
    }

    private static List<string> Languages(IEnumerable<MediaStream> streams) =>
        streams.Select(s => s.Language?.Trim()).OfType<string>().Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// A copy's picture (MSFP video section 3.2): its height, or a 16:9
    /// picture's as wide when it is wider; its HDR formats.
    /// </summary>
    internal static Picture? PictureOf(MediaStream? video)
    {
        if (video?.Height is not > 0 || video.Width is not > 0)
        {
            return null;
        }

        var height = Math.Max(video.Height.Value, (int)((long)video.Width.Value * 9 / 16));
        var hdr = HdrOf(video.VideoRangeType.ToString(), video.ColorTransfer);
        return new Picture { Height = height, Hdr = hdr.Count > 0 ? hdr : null };
    }

    /// <summary>
    /// The HDR formats of Jellyfin's range type ("DOVIWithHDR10" gives dv and
    /// hdr10); a Dolby Vision Jellyfin finds invalid is its base layer, by its
    /// transfer.
    /// </summary>
    internal static List<string> HdrOf(string rangeType, string? transfer)
    {
        if (rangeType == "DOVIInvalid")
        {
            rangeType = transfer switch { "smpte2084" => "HDR10", "arib-std-b67" => "HLG", _ => "SDR" };
        }

        var hdr = new List<string>();
        if (rangeType.StartsWith("DOVI", StringComparison.Ordinal))
        {
            hdr.Add("dv");
        }

        if (rangeType.Contains("HDR10", StringComparison.Ordinal))
        {
            hdr.Add("hdr10");
        }

        if (rangeType.Contains("HLG", StringComparison.Ordinal))
        {
            hdr.Add("hlg");
        }

        return hdr;
    }

    // The title's IMDb and TheTVDB IDs, as TMDB gives them for the title
    // the plugin took (never Jellyfin's, which go with its own match).
    private static Dictionary<string, string> OtherIds(TitleIdentity t)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (t.Imdb.Length > 0 && MsfpVideo.OtherId("imdb", t.Imdb))
        {
            ids["imdb"] = t.Imdb;
        }

        if (t.Tvdb.Length > 0 && MsfpVideo.OtherId("tvdb", t.Tvdb))
        {
            ids["tvdb"] = t.Tvdb;
        }

        return ids;
    }
}
