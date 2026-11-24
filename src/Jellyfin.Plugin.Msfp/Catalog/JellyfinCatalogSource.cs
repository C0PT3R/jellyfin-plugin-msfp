using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>
/// This server's titles as Jellyfin holds them, keyed as MSFP video section
/// 4 says: films, series, seasons and episodes the plugin's identities give
/// a TMDB ID (of the film, or of the series); series Jellyfin places only on
/// TheTVDB under the tvdb scheme; films and series nothing identifies under
/// the local scheme (with their name, year and image: the engine shares
/// them only where the operator turns them on); and the extras of each
/// (trailers, featurettes, theme songs…) keyed under their title. Films,
/// episodes and extras carry their copies (<see cref="CopyKeeper"/>): one
/// without a copy is left out. Music isn't shared (README).
/// </summary>
public sealed class JellyfinCatalogSource : ICatalogSource
{
    private static readonly BaseItemKind[] Kinds = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode];

    /// <summary>A local title's name at most, in characters (catalog section 4.3).</summary>
    public const int MaxLocalName = 128;

    private readonly ILibraryManager _library;

    private readonly Func<Identities?> _identities;

    private readonly CopyKeeper? _copies;

    private readonly Func<string, string?> _opaque;

    /// <param name="library">Jellyfin's library.</param>
    /// <param name="identities">The plugin's identities of the films and series: TMDB keys come from there, never from Jellyfin's IDs.</param>
    /// <param name="copies">The copy keeper: the films', episodes' and extras' copies.</param>
    /// <param name="opaque">The engine's opaque key part of an item ID (local titles, extras); null while federation is off.</param>
    internal JellyfinCatalogSource(ILibraryManager library, Func<Identities?> identities, CopyKeeper? copies, Func<string, string?> opaque)
    {
        _library = library;
        _identities = identities;
        _copies = copies;
        _opaque = opaque;
    }

    /// <inheritdoc />
    public IReadOnlyList<Library> Libraries()
    {
        return _library.GetVirtualFolders()
            // The plugin's own two hold other servers' titles: never offered.
            .Where(f => Guid.TryParse(f.ItemId, out _) && !Viewing.StandIns.IsOurs(f))
            // No kind since MSFP's revision (a viewer types a library by its titles).
            .Select(f => new Library { Id = Guid.Parse(f.ItemId).ToString("N"), Name = f.Name ?? string.Empty })
            .ToList();
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> ImageAsync(Guid item, CancellationToken ct) =>
        ValueTask.FromResult(_library.GetItemById(item) is { } i ? LocalImage.Of(i) : null);

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

        var series = new Dictionary<Guid, Keyed?>();
        Keyed? SeriesOf(Guid id)
        {
            if (id == Guid.Empty)
            {
                return null;
            }

            if (!series.TryGetValue(id, out var k))
            {
                series[id] = k = _library.GetItemById(id) is Series s ? KeyOf(s, identities.Of(id), _opaque) : null;
            }

            return k;
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
            foreach (var item in items.Where(i => !Viewing.PairedSources.IsStandIn(i)))
            {
                // An alternate version is its film's or episode's copy.
                if (item is MediaBrowser.Controller.Entities.Video { PrimaryVersionId: not null })
                {
                    continue;
                }

                foreach (var entry in Of(item, lib.Id, identities.Of(item.Id), SeriesOf))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>A film's or a series' key as the plugin gives it, and what a local title carries.</summary>
    internal sealed record Keyed(VideoKey Key, IReadOnlyDictionary<string, string> Ids, string? Name = null, int Year = 0, bool Image = false);

    /// <summary>
    /// A film's or a series' key: the plugin's identity's TMDB title (the
    /// owner's choice, or a sure match); else, when the plugin looked and
    /// TMDB had nothing for it, a series Jellyfin places on TheTVDB and not
    /// on TMDB under its TheTVDB ID, any other title under a local key; else
    /// none (a match waiting for the owner, or not looked at yet), so that a
    /// title's key never changes once shared.
    /// </summary>
    internal static Keyed? KeyOf(BaseItem item, TitleIdentity? own, Func<string, string?> opaque)
    {
        var kind = item is Series ? "series" : "movie";
        if (own?.Taken is { } id)
        {
            return new Keyed(new VideoKey(kind, "tmdb", id), new Dictionary<string, string>());
        }

        if (own is not { Tmdb.Length: 0, Chosen.Length: 0 })
        {
            return null;
        }

        if (item is Series && TvdbOnly(item.ProviderIds) is { } tvdb)
        {
            var key = new VideoKey("series", "tvdb", tvdb);
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item.ProviderIds?.GetValueOrDefault("Imdb") is { } imdb && MsfpVideo.OtherId(key, "imdb", imdb))
            {
                ids["imdb"] = imdb;
            }

            return new Keyed(key, ids);
        }

        if (opaque(item.Id.ToString("N")) is { } local)
        {
            var name = LocalName(item.Name);
            if (name.Length == 0)
            {
                return null;
            }

            return new Keyed(new VideoKey(kind, "local", Local: local), new Dictionary<string, string>(), name,
                item.ProductionYear is >= 1800 and <= 9999 ? item.ProductionYear.Value : 0, item.HasImage(ImageType.Primary));
        }

        return null;
    }

    /// <summary>TheTVDB ID of a title Jellyfin places on TheTVDB and not on TMDB; null otherwise.</summary>
    internal static long? TvdbOnly(IReadOnlyDictionary<string, string>? providers)
    {
        if (providers is null || TmdbId(providers.GetValueOrDefault("Tmdb")) is not null)
        {
            return null;
        }

        return providers.GetValueOrDefault("Tvdb") is { } t && MsfpVideo.OtherId("tvdb", t) ? long.Parse(t, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>A local title's name as sent: plain text on one line, trimmed, at most <see cref="MaxLocalName"/> characters.</summary>
    internal static string LocalName(string? name)
    {
        var s = new string((name ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        while (s.Contains("  ", StringComparison.Ordinal))
        {
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (s.Length > MaxLocalName)
        {
            s = s[..MaxLocalName].TrimEnd();
            if (s.Length > 0 && char.IsHighSurrogate(s[^1]))
            {
                s = s[..^1];
            }
        }

        return s;
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

    // An item's entries (MSFP video section 4): a film or a series keyed
    // (KeyOf), with its extras; a season or an episode of a series keyed. An
    // episode TMDB lists goes by its TMDB ID and place; one it doesn't, by
    // its numbers.
    private IEnumerable<CatalogEntry> Of(BaseItem item, string library, TitleIdentity? own, Func<Guid, Keyed?> seriesOf)
    {
        switch (item)
        {
            case Movie when KeyOf(item, own, _opaque) is { } k:
                if (Copies(item.Id) is { Count: > 0 } copies)
                {
                    yield return Titled(new CatalogEntry(library, k.Key.ToString(), "movie") { Copies = copies }, k, item);
                }

                foreach (var x in Extras(item, library, k.Key))
                {
                    yield return x;
                }

                break;

            case Series when KeyOf(item, own, _opaque) is { } k:
                yield return Titled(new CatalogEntry(library, k.Key.ToString(), "series") { Ids = k.Ids }, k, item);
                foreach (var x in Extras(item, library, k.Key))
                {
                    yield return x;
                }

                break;

            case Season s when seriesOf(s.SeriesId) is { } k && s.IndexNumber is >= 0 and <= 9999:
                var seasonKey = k.Key with { Kind = "season", Season = s.IndexNumber.Value };
                yield return new CatalogEntry(library, seasonKey.ToString(), "season");
                foreach (var x in Extras(item, library, seasonKey))
                {
                    yield return x;
                }

                break;

            case Episode episode when seriesOf(episode.SeriesId) is { } k
                && episode.ParentIndexNumber is >= 0 and <= 9999 && episode.IndexNumber is >= 0 and <= 99999
                && Copies(item.Id) is { Count: > 0 } episodeCopies:
                // A file of several episodes: each of them, with the others
                // (MSFP video section 4.2).
                var season = episode.ParentIndexNumber.Value;
                var first = episode.IndexNumber.Value;
                var last = episode.IndexNumberEnd is { } end && end > first && end <= 99999 && end - first < 16 ? end : first;
                var tmdbSeries = k.Key.Scheme == "tmdb" ? seriesOf(episode.SeriesId) : null;
                var mapped = tmdbSeries is null ? null : _identities()?.Of(episode.SeriesId);
                var keys = Enumerable.Range(first, last - first + 1).Select(n =>
                    mapped is not null && mapped.Episodes.TryGetValue(TitleIdentity.EpisodeKey(season, n), out var e)
                        ? (Key: new VideoKey("episode", "tmdb", k.Key.Id, EpisodeId: e.EpisodeId), Place: (global::Msfp.Place?)new global::Msfp.Place { Season = e.Season, Episode = e.Episode })
                        : (Key: k.Key with { Kind = "episode", Season = season, Episode = n }, Place: null))
                    .DistinctBy(x => x.Key.ToString())
                    .ToList();
                var ids = EpisodeIds(keys[0].Key, item.ProviderIds);
                foreach (var key in keys)
                {
                    yield return new CatalogEntry(library, key.Key.ToString(), "episode")
                    {
                        Copies = episodeCopies,
                        Place = key.Place,
                        With = keys.Where(o => o.Key != key.Key).Select(o => o.Key.ToString()).Order(StringComparer.Ordinal).ToList(),
                        // A single episode's own IDs (several in one file share none).
                        Ids = keys.Count == 1 ? ids : new Dictionary<string, string>(),
                    };
                }

                break;
        }
    }

    // An episode's other IDs (video section 4.3): under the tvdb scheme,
    // Jellyfin's (that is where the key comes from); under the others none
    // (Jellyfin's IDs go with its own match, never sent beside the plugin's).
    private static Dictionary<string, string> EpisodeIds(VideoKey key, IReadOnlyDictionary<string, string>? providers)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (key.Scheme != "tvdb" || providers is null)
        {
            return ids;
        }

        foreach (var (scheme, provider) in new[] { ("tvdb", "Tvdb"), ("imdb", "Imdb"), ("tmdb", "Tmdb") })
        {
            if (providers.GetValueOrDefault(provider) is { } v && MsfpVideo.OtherId(key, scheme, v))
            {
                ids[scheme] = v;
            }
        }

        return ids;
    }

    private static CatalogEntry Titled(CatalogEntry e, Keyed k, BaseItem item) =>
        k.Key.Scheme == "local" ? e with { Name = k.Name, Year = k.Year, Image = k.Image, ImageItem = k.Image ? item.Id : null } : e;

    private IReadOnlyList<Copy> Copies(Guid item) => _copies?.CopiesOf(item) ?? [];

    // A title's extras of the types MSFP names, keyed under it
    // (<title key>/x/<opaque>), with their copies and no name.
    private IEnumerable<CatalogEntry> Extras(BaseItem item, string library, VideoKey parent)
    {
        if (_copies is null)
        {
            yield break;
        }

        foreach (var x in _copies.ExtrasOf(item))
        {
            if (CopyKeeper.ExtraTypeToken(x.ExtraType) is not { } type || _opaque(x.Id.ToString("N")) is not { } part
                || Copies(x.Id) is not { Count: > 0 } copies)
            {
                continue;
            }

            yield return new CatalogEntry(library, parent.ToString() + "/x/" + part, "extra") { Copies = copies, ExtraType = type };
        }
    }
}
