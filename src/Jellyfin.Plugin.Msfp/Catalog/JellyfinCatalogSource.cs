using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
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

    public JellyfinCatalogSource(ILibraryManager library)
    {
        _library = library;
    }

    /// <inheritdoc />
    public IReadOnlyList<Library> Libraries()
    {
        return _library.GetVirtualFolders()
            .Where(f => Guid.TryParse(f.ItemId, out _))
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
        var series = new Dictionary<Guid, (long? Tmdb, bool TmdbOrder)>();
        (long? Tmdb, bool TmdbOrder) SeriesOf(Guid id)
        {
            if (id == Guid.Empty)
            {
                // An episode or season Jellyfin hasn't placed in a series (yet).
                return (null, false);
            }

            if (!series.TryGetValue(id, out var info))
            {
                info = _library.GetItemById(id) is Series s ? (TmdbId(s.GetProviderId(MetadataProvider.Tmdb)), InTmdbOrder(s.DisplayOrder)) : (null, false);
                series[id] = info;
            }

            return info;
        }

        foreach (var lib in Libraries())
        {
            var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = Kinds,
                Recursive = true,
                IsVirtualItem = false,
                AncestorIds = [Guid.ParseExact(lib.Id, "N")],
            });
            foreach (var item in items)
            {
                foreach (var entry in Of(item, lib.Id, SeriesOf))
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

    /// <summary>
    /// Whether a series' episode numbers in Jellyfin are TMDB's: its default
    /// order (aired), which Jellyfin's TMDB provider fetched each episode by.
    /// Another order (DVD, absolute, an episode group) numbers them its own
    /// way.
    /// </summary>
    internal static bool InTmdbOrder(string? displayOrder) =>
        string.IsNullOrEmpty(displayOrder) || string.Equals(displayOrder, "aired", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<CatalogEntry> Of(BaseItem item, string library, Func<Guid, (long? Tmdb, bool TmdbOrder)> seriesOf)
    {
        switch (item)
        {
            case Movie when TmdbId(item.GetProviderId(MetadataProvider.Tmdb)) is { } id:
                yield return Copy(item, library, new VideoKey("movie", id), []);
                break;

            case Series when TmdbId(item.GetProviderId(MetadataProvider.Tmdb)) is { } id:
                yield return new CatalogEntry(library, new VideoKey("series", id).ToString(), "series", null) { Ids = OtherIds(item) };
                break;

            case Season season when seriesOf(season.SeriesId).Tmdb is { } id && season.IndexNumber is >= 0 and <= 9999:
                yield return new CatalogEntry(library, new VideoKey("season", id, season.IndexNumber.Value).ToString(), "season", null);
                break;

            case Episode episode when seriesOf(episode.SeriesId) is { Tmdb: { } id } series:
                if (TmdbId(item.GetProviderId(MetadataProvider.Tmdb)) is { } episodeId)
                {
                    // Keyed by its TMDB ID, with its place in TMDB's numbering
                    // (MSFP video section 4.1): Jellyfin's numbers when the
                    // series is in TMDB's order; left out otherwise.
                    if (series.TmdbOrder && episode.ParentIndexNumber is >= 0 and <= 9999 && episode.IndexNumber is >= 0 and <= 99999)
                    {
                        yield return Copy(item, library, new VideoKey("episode", id, EpisodeId: episodeId), []) with
                        {
                            Place = new global::Msfp.Place { Season = episode.ParentIndexNumber.Value, Episode = episode.IndexNumber.Value },
                        };
                    }
                }
                else if (episode.ParentIndexNumber is >= 0 and <= 9999 && episode.IndexNumber is >= 0 and <= 99999)
                {
                    // A file holding several episodes: each of them, with the
                    // others (MSFP video section 4.2).
                    var season = episode.ParentIndexNumber.Value;
                    var first = episode.IndexNumber.Value;
                    var last = episode.IndexNumberEnd is { } end && end > first && end <= 99999 && end - first < 16 ? end : first;
                    var keys = Enumerable.Range(first, last - first + 1).Select(n => new VideoKey("episode", id, season, n)).ToList();
                    foreach (var k in keys)
                    {
                        yield return Copy(item, library, k, keys.Where(o => o != k).Select(o => o.ToString()).ToList());
                    }
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
            // WebVTT (Playback/JellyfinPlayback.cs).
            Subtitles = Languages(streams.Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream)),
            Picture = PictureOf(streams.FirstOrDefault(s => s.Type == MediaStreamType.Video)),
            With = with,
            Ids = OtherIds(item),
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

    private static Dictionary<string, string> OtherIds(BaseItem item)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.GetProviderId(MetadataProvider.Imdb) is { Length: > 0 } imdb && MsfpVideo.OtherId("imdb", imdb))
        {
            ids["imdb"] = imdb;
        }

        if (item.GetProviderId(MetadataProvider.Tvdb) is { Length: > 0 } tvdb && MsfpVideo.OtherId("tvdb", tvdb))
        {
            ids["tvdb"] = tvdb;
        }

        return ids;
    }
}
