using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Msfp.Protocol;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>
/// This server's titles as Jellyfin holds them: films, series, seasons and
/// episodes with a TMDB ID (of the film, or of the series), keyed as MSFP
/// section 11 says. Items without one are left out: they have no title key.
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
    public IReadOnlyList<LibraryInfo> Libraries()
    {
        return _library.GetVirtualFolders()
            .Where(f => Guid.TryParse(f.ItemId, out _))
            .Select(f => new LibraryInfo(Guid.Parse(f.ItemId).ToString("N"), f.Name ?? string.Empty, Kind(f.CollectionType?.ToString())))
            .ToList();
    }

    /// <summary>Jellyfin's collection type as a kind of section 9.3.</summary>
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
        var seriesIds = new Dictionary<Guid, string?>();
        string? SeriesTmdb(Guid id)
        {
            if (!seriesIds.TryGetValue(id, out var tmdb))
            {
                tmdb = _library.GetItemById(id) is { } s ? TitleKey.CanonicalId(s.GetProviderId(MetadataProvider.Tmdb)) : null;
                seriesIds[id] = tmdb;
            }

            return tmdb;
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
                foreach (var entry in Of(item, lib.Id, SeriesTmdb))
                {
                    yield return entry;
                }
            }
        }
    }

    private static IEnumerable<CatalogEntry> Of(BaseItem item, string library, Func<Guid, string?> seriesTmdb)
    {
        switch (item)
        {
            case Movie when TitleKey.CanonicalId(item.GetProviderId(MetadataProvider.Tmdb)) is { } id:
            {
                var (audio, subs) = Streams(item);
                yield return new CatalogEntry(library, TitleKey.ForMovie(id), TitleKey.Movie, audio, subs);
                break;
            }

            case Series when TitleKey.CanonicalId(item.GetProviderId(MetadataProvider.Tmdb)) is { } id:
                yield return new CatalogEntry(library, TitleKey.ForSeries(id), TitleKey.Series, Array.Empty<string>(), Array.Empty<string>());
                break;

            case Season season when seriesTmdb(season.SeriesId) is { } id && season.IndexNumber is >= 0 and <= 9999:
                yield return new CatalogEntry(library, TitleKey.ForSeason(id, season.IndexNumber.Value), TitleKey.Season, Array.Empty<string>(), Array.Empty<string>());
                break;

            case Episode episode when seriesTmdb(episode.SeriesId) is { } id:
            {
                var (audio, subs) = Streams(item);
                if (TitleKey.CanonicalId(item.GetProviderId(MetadataProvider.Tmdb)) is { } episodeId)
                {
                    yield return new CatalogEntry(library, TitleKey.ForEpisodeId(id, episodeId), TitleKey.Episode, audio, subs);
                }
                else if (episode.ParentIndexNumber is >= 0 and <= 9999 && episode.IndexNumber is >= 0 and <= 99999)
                {
                    // A file holding several episodes: each of them (SPEC-GAPS.md, 11).
                    var last = episode.IndexNumberEnd is { } end && end > episode.IndexNumber && end <= 99999 ? end : episode.IndexNumber.Value;
                    for (var n = episode.IndexNumber.Value; n <= last; n++)
                    {
                        yield return new CatalogEntry(library, TitleKey.ForEpisode(id, episode.ParentIndexNumber.Value, n), TitleKey.Episode, audio, subs);
                    }
                }

                break;
            }
        }
    }

    private static (List<string> Audio, List<string> Subtitles) Streams(BaseItem item)
    {
        var streams = item.GetMediaStreams();
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).Select(s => Languages.FromJellyfin(s.Language)).Where(t => t != null);
        var subs = streams.Where(s => s.Type == MediaStreamType.Subtitle).Select(s => Languages.FromJellyfin(s.Language)).Where(t => t != null);
        return (Languages.Clean(audio), Languages.Clean(subs));
    }
}

/// <summary>
/// Keeps the catalog's change log up to date: a fresh reading compared with
/// the last one, 30 seconds after Jellyfin's library events settle, and
/// every 10 minutes whatever happens (SPEC-GAPS.md and README: why a diff).
/// </summary>
public sealed class CatalogUpdater : IHostedService, IDisposable
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _library;
    private readonly OwnCatalog _catalog;
    private readonly ILogger<CatalogUpdater> _log;
    private readonly object _lock = new();
    private Timer? _settle;
    private Timer? _every;
    private int _running;

    public CatalogUpdater(ILibraryManager library, OwnCatalog catalog, ILogger<CatalogUpdater> log)
    {
        _library = library;
        _catalog = catalog;
        _log = log;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += Changed;
        _library.ItemUpdated += Changed;
        _library.ItemRemoved += Changed;
        _every = new Timer(_ => Run(), null, TimeSpan.FromSeconds(15), Every);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= Changed;
        _library.ItemUpdated -= Changed;
        _library.ItemRemoved -= Changed;
        _every?.Change(Timeout.Infinite, Timeout.Infinite);
        _settle?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _every?.Dispose();
        _settle?.Dispose();
    }

    private void Changed(object? sender, ItemChangeEventArgs e)
    {
        lock (_lock)
        {
            // Each event pushes the reading back: a scan is read once, after it.
            _settle ??= new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
            _settle.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Run()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var changed = _catalog.Refresh();
            if (changed > 0)
            {
                _log.LogInformation("MSFP: {Count} catalog changes, seq {Seq}", changed, _catalog.Seq);
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "MSFP: reading the catalog failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
