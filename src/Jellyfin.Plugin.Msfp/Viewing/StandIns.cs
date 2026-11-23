using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Identification;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// The paired servers' titles in this server's Jellyfin (DESIGN-NOTES): two
/// libraries the plugin makes, "Films from paired servers" and "Series
/// from paired servers", with a stand-in for each film and episode read of
/// a paired server that this server doesn't hold itself. A stand-in has no
/// file: an msfp:// path from its key, the paired servers' channel, one
/// source per copy of the holder with the best copy, each with its streams
/// as Jellyfin's reading of the file would give them (StandInStreams), the
/// copy's length, size, chapters and skip markers, the day this server
/// first saw the title (FirstSeen), and Jellyfin's own metadata from its
/// TMDB or TheTVDB ID; a local title the owner's name, year and image,
/// never looked up elsewhere.
///
/// Kept in step 30 seconds after a reading of a peer's catalog, a pairing's
/// end or a change of the libraries settles, and every hour. A stand-in
/// goes when no paired server lists it any more, when this server holds
/// the title itself, or when its servers haven't been heard from for 7 days
/// (back at their next reading). Federation off empties the libraries,
/// which stay.
/// </summary>
public sealed class StandIns : IHostedService, IDisposable
{
    /// <summary>How long a paired server unheard of keeps its titles here.</summary>
    public static readonly TimeSpan Unheard = TimeSpan.FromDays(7);

    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    private readonly ILibraryManager _library;
    private readonly IChannelManager _channels;
    private readonly IMediaStreamRepository _streams;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fs;
    private readonly ILocalizationManager _localization;
    private readonly IServerConfigurationManager _server;
    private readonly EngineHost _host;
    private readonly Identifier _identifier;
    private readonly IUserDataManager _userData;
    private readonly IUserManager _users;
    private readonly MediaBrowser.Controller.Chapters.IChapterManager _chapters;
    private readonly MediaBrowser.Controller.MediaSegments.IMediaSegmentManager _segments;
    private readonly PeerPreviews _previews;
    private readonly ILogger<StandIns> _log;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _running = new(1, 1);
    private Timer? _settle;
    private Timer? _every;

    public StandIns(
        ILibraryManager library,
        IChannelManager channels,
        IMediaStreamRepository streams,
        IProviderManager providers,
        IFileSystem fs,
        ILocalizationManager localization,
        IServerConfigurationManager server,
        EngineHost host,
        Identifier identifier,
        IUserDataManager userData,
        IUserManager users,
        MediaBrowser.Controller.Chapters.IChapterManager chapters,
        MediaBrowser.Controller.MediaSegments.IMediaSegmentManager segments,
        PeerPreviews previews,
        ILogger<StandIns> log)
    {
        _previews = previews;
        _chapters = chapters;
        _segments = segments;
        _library = library;
        _channels = channels;
        _streams = streams;
        _providers = providers;
        _fs = fs;
        _localization = localization;
        _server = server;
        _host = host;
        _identifier = identifier;
        _userData = userData;
        _users = users;
        _log = log;
    }

    /// <summary>The two libraries, films then series.</summary>
    internal enum Shelf
    {
        Films,
        Series,
    }

    private volatile Wanted? _wanted;

    /// <summary>
    /// A film's or an episode's copies, by its stand-in's path, as the last
    /// pass found them: the paired servers holding it, best copy first; null
    /// when none.
    /// </summary>
    internal Playable? CopyOf(string path) => CopyOf(_wanted, path);

    internal static Playable? CopyOf(Wanted? w, string path) =>
        w is not null && w.ByPath.TryGetValue(path, out var p) ? p : null;

    /// <summary>
    /// <see cref="CopyOf"/> for a playback: the copies are taken from the
    /// catalogs as stored, as a pass takes them, when the last pass is
    /// behind: before the first one since Jellyfin started (2 minutes, or a
    /// catalog read), and when a server it found has stopped being paired
    /// (the next pass comes 30 seconds after; a server paired since may
    /// hold the title).
    /// </summary>
    internal async Task<Playable?> CopyOfAsync(string path, CancellationToken ct)
    {
        if (_host.Engine is not { } engine)
        {
            return CopyOf(path);
        }

        var cached = CopyOf(path);
        if (cached is not null)
        {
            var paired = (await engine.PeersAsync(ct).ConfigureAwait(false))
                .Where(p => p.State == PeerState.Paired).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            if (Fresh(cached, paired))
            {
                return cached;
            }
        }

        var wanted = Wanted.Of(await LivePeersAsync(engine, ct).ConfigureAwait(false), Held(), Links(), Themes);
        lock (_lock)
        {
            _wanted ??= wanted;
        }

        return CopyOf(wanted, path);
    }

    /// <summary>The skip markers of a stand-in's best copy, as Jellyfin's segments (the provider's answer, <see cref="PairedSegments"/>).</summary>
    internal IReadOnlyList<MediaBrowser.Model.MediaSegments.MediaSegmentDto> SegmentsOf(Guid itemId) =>
        _library.GetItemById(itemId) is { Path: { } path } && CopyOf(path)?.Chosen is { } h
            ? PairedSegments.Wanted(StandInStreams.Segments(h.Best)).Select(m => new MediaBrowser.Model.MediaSegments.MediaSegmentDto
            {
                ItemId = itemId,
                Type = m.Type,
                StartTicks = m.StartTicks,
                EndTicks = m.EndTicks,
            }).ToList()
            : [];

    /// <summary>Whether the operator lets paired servers' theme songs and videos in (off by default).</summary>
    internal static bool Themes => Plugin.Instance?.Configuration.PeersThemes == true;

    /// <summary>Whether every server a copy names is still paired.</summary>
    internal static bool Fresh(Playable copy, IReadOnlySet<string> paired) => copy.Holders.All(h => paired.Contains(h.Peer));

    /// <summary>What the page shows: the libraries and what is in them.</summary>
    public StandInStatus Status { get; private set; } = new();

    /// <summary>The folder a library's stand-ins are under (with a README: Jellyfin gives an empty folder no item).</summary>
    internal static string FolderOf(string dataFolder, Shelf shelf) =>
        Path.Combine(dataFolder, "paired", shelf == Shelf.Films ? "films" : "series");

    /// <summary>Whether a Jellyfin library is one of the plugin's two, by its folders.</summary>
    public static bool IsOurs(VirtualFolderInfo folder) =>
        Plugin.Instance is { } p && (folder.Locations ?? []).Any(l =>
            SamePath(l, FolderOf(p.DataFolderPath, Shelf.Films)) || SamePath(l, FolderOf(p.DataFolderPath, Shelf.Series)));

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += Changed;
        _library.ItemRemoved += Changed;
        _host.Events += Event;
        _identifier.Identities.Changed += IdentitiesChanged;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += SettingsSaved;
        }

        _every = new Timer(_ => Run(), null, TimeSpan.FromMinutes(2), Every);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= Changed;
        _library.ItemRemoved -= Changed;
        _host.Events -= Event;
        _identifier.Identities.Changed -= IdentitiesChanged;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= SettingsSaved;
        }

        _every?.Change(Timeout.Infinite, Timeout.Infinite);
        _settle?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _every?.Dispose();
        _settle?.Dispose();
        _running.Dispose();
    }

    /// <summary>Keeps the stand-ins in step soon: once things settle.</summary>
    public void Soon()
    {
        lock (_lock)
        {
            _settle ??= new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
            _settle.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Makes a library again that its owner removed, then keeps it in step.</summary>
    internal async Task MakeAgainAsync(CancellationToken ct)
    {
        var made = Made();
        made.Films = made.Series = false;
        SaveMade(made);
        await KeepAsync(ct).ConfigureAwait(false);
    }

    // This server's own titles changed: what it holds may have.
    private void Changed(object? sender, ItemChangeEventArgs e)
    {
        if (!PairedSources.IsStandIn(e.Item) && e.Item is Movie or Series or Episode)
        {
            Soon();
        }
    }

    private void IdentitiesChanged(object? sender, EventArgs e) => Soon();

    // The settings saved: themes may have been let in or out.
    private void SettingsSaved(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e) => Soon();

    private void Event(MsfpEvent e)
    {
        if (e is CatalogReadEnded { Error: null } or PeerEnded or PeerGone or PeerSuspended or PeerResumed)
        {
            Soon();
        }
    }

    private void Run()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await KeepAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogError(e, "MSFP: keeping the paired servers' titles failed");
            }
        });
    }

    /// <summary>One pass: the libraries made, the stand-ins as the catalogs and this server's titles say.</summary>
    internal async Task KeepAsync(CancellationToken ct)
    {
        if (Plugin.Instance is not { } plugin || !await _running.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            List<(string Peer, IEnumerable<Title> Titles)> peers;
            if (_host.Engine is { } engine)
            {
                peers = await LivePeersAsync(engine, ct).ConfigureAwait(false);
            }
            else if (_host.Problem.Length > 0)
            {
                // Federation off: the libraries empty, kept.
                peers = [];
            }
            else
            {
                // Opening again (the settings were saved): the next pass.
                return;
            }

            // The channel's own item, which Jellyfin makes when it lists its
            // channels (at a scan): without it, a stand-in's refresh fails.
            if (_library.GetItemById(ChannelId()) is null)
            {
                await _channels.GetChannelsInternalAsync(new MediaBrowser.Model.Channels.ChannelQuery()).ConfigureAwait(false);
            }

            var wanted = Wanted.Of(peers, Held(), Links(), Themes);
            _wanted = wanted;
            var films = Library(plugin, Shelf.Films);
            var series = Library(plugin, Shelf.Series);
            var counts = new StandInStatus
            {
                Films = films.Status,
                Series = series.Status,
                FilmsName = films.Name,
                SeriesName = series.Name,
            };

            // "Recently added": when this server first saw each title.
            var seenFile = FirstSeen.PathIn(plugin.DataFolderPath);
            var seen = FirstSeen.Load(seenFile);
            if (!seen.Existed)
            {
                var before = (films.Folder is { } a ? Existing(a).Values : Enumerable.Empty<BaseItem>()).Concat(series.Folder is { } b ? Existing(b).Values : Enumerable.Empty<BaseItem>()).ToList();
                foreach (var item in before)
                {
                    seen.Seed(item.Path!, item.DateCreated);
                }

                // An update: the paired servers were read before.
                if (before.Count > 0)
                {
                    seen.Read(peers.Select(p => p.Peer));
                }
            }

            var pass = new Pass(seen, DateTime.UtcNow);
            if (films.Folder is { } ff)
            {
                counts.FilmCount = await KeepFilmsAsync(ff, wanted, pass, ct).ConfigureAwait(false);
            }

            if (series.Folder is { } sf)
            {
                (counts.SeriesCount, counts.EpisodeCount) = await KeepSeriesAsync(sf, wanted, pass, ct).ConfigureAwait(false);
            }

            counts.ExtraCount = await KeepExtrasAsync(wanted, ct).ConfigureAwait(false);

            // Only once their titles are dated: a first reading's count as old.
            seen.Read(peers.Where(p => p.Titles.Any()).Select(p => p.Peer));
            seen.Prune(pass.Now);
            seen.Save(seenFile);
            counts.Kept = DateTimeOffset.UtcNow;
            Status = counts;
        }
        finally
        {
            _running.Release();
        }
    }

    // A pass's clock and records of when titles were first seen.
    private sealed record Pass(FirstSeen Seen, DateTime Now);

    // The paired servers heard from within 7 days, each with the titles read of it.
    private async Task<List<(string Peer, IEnumerable<Title> Titles)>> LivePeersAsync(MsfpEngine engine, CancellationToken ct)
    {
        var store = _host.Store;
        var peers = new List<(string, IEnumerable<Title>)>();
        if (store is null)
        {
            return peers;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var p in await engine.PeersAsync(ct).ConfigureAwait(false))
        {
            if (p.State != PeerState.Paired || now - (p.Seen ?? p.Paired ?? p.Created) > Unheard)
            {
                continue;
            }

            var titles = new List<Title>();
            var after = string.Empty;
            while (true)
            {
                var page = await store.ReadAsync(tx => tx.PeerTitlesAsync(p.Id, after, 5000), ct).ConfigureAwait(false);
                titles.AddRange(page);
                if (page.Count < 5000)
                {
                    break;
                }

                after = page[^1].Key;
            }

            peers.Add((p.Id, titles));
        }

        return peers;
    }

    // What this server holds itself, by TMDB and TheTVDB: the plugin's
    // identity of a title where there is one (the libraries offered),
    // Jellyfin's IDs otherwise; a series' episodes by TMDB's or TheTVDB's ID
    // where known (the identity's map, Jellyfin's IDs), by their numbers
    // otherwise.
    private Held Held()
    {
        var held = new Held();
        var identities = _identifier.Identities;
        var seriesIds = new Dictionary<Guid, (long Tmdb, long Tvdb, TitleIdentity? Identity)>();
        foreach (var item in _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            IsVirtualItem = false,
        }))
        {
            if (PairedSources.IsStandIn(item))
            {
                continue;
            }

            var identity = identities.Of(item.Id);
            var tmdb = identity?.Taken ?? Catalog.JellyfinCatalogSource.TmdbId(item.GetProviderId(MetadataProvider.Tmdb)) ?? 0;
            if (item is Movie)
            {
                if (tmdb > 0)
                {
                    held.Films.Add(tmdb);
                }

                continue;
            }

            var tvdb = Catalog.JellyfinCatalogSource.TmdbId(item.GetProviderId(MetadataProvider.Tvdb)) ?? 0;
            if (tmdb > 0 || tvdb > 0)
            {
                seriesIds[item.Id] = (tmdb, tvdb, identity);
            }
        }

        foreach (var item in _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
        }))
        {
            if (item is not Episode e || PairedSources.IsStandIn(e) || !seriesIds.TryGetValue(e.SeriesId, out var s)
                || e.ParentIndexNumber is not { } season || e.IndexNumber is not { } first)
            {
                continue;
            }

            var sides = new List<HeldSeries>();
            if (s.Tmdb > 0)
            {
                sides.Add(held.SeriesOf(s.Tmdb));
            }

            if (s.Tvdb > 0)
            {
                sides.Add(held.TvdbSeriesOf(s.Tvdb));
            }

            var tvdbEpisode = Catalog.JellyfinCatalogSource.TmdbId(e.GetProviderId(MetadataProvider.Tvdb));
            var tmdbEpisode = Catalog.JellyfinCatalogSource.TmdbId(e.GetProviderId(MetadataProvider.Tmdb));
            var last = e.IndexNumberEnd is { } end && end > first && end - first < 16 ? end : first;
            foreach (var hs in sides)
            {
                if (tvdbEpisode is { } tv)
                {
                    hs.TvdbEpisodeIds.Add(tv);
                }

                if (tmdbEpisode is { } tm && last == first)
                {
                    hs.EpisodeIds.Add(tm);
                }

                for (var n = first; n <= last; n++)
                {
                    if (s.Identity?.Episodes.TryGetValue(TitleIdentity.EpisodeKey(season, n), out var mapped) == true)
                    {
                        hs.EpisodeIds.Add(mapped.EpisodeId);
                    }
                    else
                    {
                        hs.Numbers.Add((season, n));
                    }
                }
            }
        }

        return held;
    }

    // The TMDB series of the stand-ins that Jellyfin's metadata gives a
    // TheTVDB ID too: a series another paired server keys under tvdb is the
    // same show (MSFP video section 4.3).
    private Dictionary<long, long> Links()
    {
        var links = new Dictionary<long, long>();
        foreach (var item in _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series], Recursive = true }))
        {
            if (PairedSources.IsStandIn(item)
                && Catalog.JellyfinCatalogSource.TmdbId(item.GetProviderId(MetadataProvider.Tmdb)) is { } tmdb
                && Catalog.JellyfinCatalogSource.TmdbId(item.GetProviderId(MetadataProvider.Tvdb)) is { } tvdb)
            {
                links.TryAdd(tmdb, tvdb);
            }
        }

        return links;
    }

    // A library of the plugin's: found by its folder, made when it never
    // was; one its owner removed isn't made again (the page offers to).
    private (Folder? Folder, string Status, string Name) Library(Plugin plugin, Shelf shelf)
    {
        var dir = FolderOf(plugin.DataFolderPath, shelf);
        Directory.CreateDirectory(dir);
        var readme = Path.Combine(dir, "README.txt");
        if (!File.Exists(readme))
        {
            File.WriteAllText(readme, "Titles from the servers this one is paired with (MSFP Federation plugin). Kept by the plugin: nothing to put here.\n");
        }

        var existing = _library.GetVirtualFolders().FirstOrDefault(f => (f.Locations ?? []).Any(l => SamePath(l, dir)));
        var made = Made();
        if (existing is null)
        {
            if (shelf == Shelf.Films ? made.Films : made.Series)
            {
                return (null, "removed", string.Empty);
            }

            var name = Names.Of(shelf, _server.Configuration.UICulture);
            var options = new LibraryOptions
            {
                PathInfos = [new MediaPathInfo(dir)],
                EnableRealtimeMonitor = false,
                EnableTrickplayImageExtraction = false,
                ExtractTrickplayImagesDuringLibraryScan = false,
                EnableChapterImageExtraction = false,
                ExtractChapterImagesDuringLibraryScan = false,
                SaveLocalMetadata = false,
                PreferredMetadataLanguage = _server.Configuration.PreferredMetadataLanguage,
                MetadataCountryCode = _server.Configuration.MetadataCountryCode,
            };
            _library.AddVirtualFolder(name, shelf == Shelf.Films ? CollectionTypeOptions.movies : CollectionTypeOptions.tvshows, options, true).GetAwaiter().GetResult();
            if (shelf == Shelf.Films)
            {
                made.Films = true;
            }
            else
            {
                made.Series = true;
            }

            SaveMade(made);
            _log.LogInformation("MSFP: library {Name} made for the paired servers' titles", name);
            return (null, "making", name);
        }

        // Its folder's item comes with the library's first scan.
        return _library.FindByPath(dir, true) is Folder folder ? (folder, "ready", existing.Name ?? string.Empty) : (null, "making", existing.Name ?? string.Empty);
    }

    private async Task<int> KeepFilmsAsync(Folder library, Wanted wanted, Pass pass, CancellationToken ct)
    {
        var existing = Existing(library);
        var created = new List<Guid>();
        foreach (var f in wanted.Films.Values)
        {
            var path = f.Path;
            var date = pass.Seen.Of(path, f.Copy.Holders.Select(h => h.Peer), pass.Now);
            if (!existing.Remove(path, out var item))
            {
                var movie = new Movie { Id = _library.GetNewItemId(path, typeof(Movie)), Path = path };
                Prepare(movie, library.Id, date);
                if (f.Local is null)
                {
                    movie.SetProviderId(MetadataProvider.Tmdb, Wanted.Number(f.Tmdb));
                }

                movie.PresentationUniqueKey = movie.CreatePresentationUniqueKey();
                _library.CreateItem(movie, library);
                await movie.ReattachUserDataAsync(ct).ConfigureAwait(false);
                if (f.Local is null)
                {
                    created.Add(movie.Id);
                }

                item = movie;
            }
            else if (string.IsNullOrEmpty(item.Name) && f.Local is null)
            {
                // Its metadata never came (TMDB unreachable, ...): asked again.
                created.Add(item.Id);
            }

            await DescribeAsync(item, library, f.Copy, date, f.Local, ct).ConfigureAwait(false);
        }

        await RemoveAsync(existing.Values, ct).ConfigureAwait(false);
        Refresh(created);
        return wanted.Films.Count;
    }

    private async Task<(int Series, int Episodes)> KeepSeriesAsync(Folder library, Wanted wanted, Pass pass, CancellationToken ct)
    {
        var existing = Existing(library);
        var created = new List<Guid>();
        var episodes = 0;
        foreach (var w in wanted.Series.Values)
        {
            if (w.Episodes.Count == 0)
            {
                continue;
            }

            var holders = w.Episodes.Values.SelectMany(e => e.Copy.Holders.Select(h => h.Peer)).Distinct().ToList();
            var seriesDate = pass.Seen.Of(w.Path, holders, pass.Now);
            if (!existing.Remove(w.Path, out var seriesItem) || seriesItem is not Series series)
            {
                series = new Series { Id = _library.GetNewItemId(w.Path, typeof(Series)), Path = w.Path };
                Prepare(series, library.Id, seriesDate);
                SetIds(series, w);
                series.PresentationUniqueKey = series.CreatePresentationUniqueKey();
                _library.CreateItem(series, library);
                if (w.Local is null)
                {
                    created.Add(series.Id);
                }
            }
            else if (string.IsNullOrEmpty(series.Name) && w.Local is null)
            {
                created.Add(series.Id);
            }

            await OutOfChannelAsync(series, library, ct).ConfigureAwait(false);
            await KeepIdsAsync(series, library, w, ct).ConfigureAwait(false);
            await DescribeLocalAsync(series, library, w.Local, ct).ConfigureAwait(false);
            await DateAsync(series, library, seriesDate, ct).ConfigureAwait(false);

            var seasons = new Dictionary<int, Season>();
            foreach (var (path, e) in w.Episodes)
            {
                if (!seasons.TryGetValue(e.Season, out var season))
                {
                    var seasonPath = Wanted.SeasonPath(w.Path, e.Season);
                    var inSeason = w.Episodes.Values.Where(x => x.Season == e.Season).SelectMany(x => x.Copy.Holders.Select(h => h.Peer)).Distinct().ToList();
                    var seasonDate = pass.Seen.Of(seasonPath, inSeason, pass.Now);
                    if (!existing.Remove(seasonPath, out var seasonItem) || seasonItem is not Season s)
                    {
                        s = new Season
                        {
                            Id = _library.GetNewItemId(seasonPath, typeof(Season)),
                            Path = seasonPath,
                            IndexNumber = e.Season,
                            SeriesId = series.Id,
                            SeriesName = series.Name,
                        };
                        Prepare(s, series.Id, seasonDate);
                        // Named as Jellyfin names its own (else by its path's end, "s1").
                        s.Name = SeasonName(e.Season);
                        if (w.Local is not null)
                        {
                            s.IsLocked = true;
                        }

                        Keys(s, series);
                        _library.CreateItem(s, series);
                        if (w.Local is null)
                        {
                            created.Add(s.Id);
                        }
                    }
                    else if ((s.Name != SeasonName(e.Season) && s.Name == Path.GetFileName(seasonPath)) || Keys(s, series))
                    {
                        if (s.Name == Path.GetFileName(seasonPath))
                        {
                            s.Name = SeasonName(e.Season);
                        }

                        await _library.UpdateItemAsync(s, series, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                    }

                    await OutOfChannelAsync(s, series, ct).ConfigureAwait(false);
                    await DateAsync(s, series, seasonDate, ct).ConfigureAwait(false);
                    season = s;
                    seasons[e.Season] = season;
                }

                var date = pass.Seen.Of(path, e.Copy.Holders.Select(h => h.Peer), pass.Now);
                existing.Remove(path, out var item);
                if (item is Episode moved && (moved.ParentIndexNumber != e.Season || moved.IndexNumber != e.Episode))
                {
                    // Moved (TMDB's place, another numbering): made again at its new place, its user data kept.
                    Remove([moved]);
                    item = null;
                }

                if (item is null)
                {
                    var episode = new Episode
                    {
                        Id = _library.GetNewItemId(path, typeof(Episode)),
                        Path = path,
                        IndexNumber = e.Episode,
                        ParentIndexNumber = e.Season,
                        SeasonId = season.Id,
                        SeasonName = season.Name,
                        SeriesId = series.Id,
                        SeriesName = series.Name,
                    };
                    Prepare(episode, season.Id, date);
                    SetIds(episode, e.Ids);
                    Keys(episode, series);
                    _library.CreateItem(episode, season);
                    await episode.ReattachUserDataAsync(ct).ConfigureAwait(false);
                    if (w.Local is null)
                    {
                        created.Add(episode.Id);
                    }

                    item = episode;
                }
                else if (string.IsNullOrEmpty(item.Name) && w.Local is null)
                {
                    created.Add(item.Id);
                }
                else if (Keys(item, series))
                {
                    await _library.UpdateItemAsync(item, season, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                }

                var local = w.Local is null ? null : w.Local with { Name = EpisodeName(e), Year = 0, Image = false };
                await DescribeAsync(item, season, e.Copy, date, local, ct).ConfigureAwait(false);
                episodes++;
            }
        }

        // Episodes first, then their seasons and series.
        await RemoveAsync(existing.Values.OrderBy(i => i is Episode ? 0 : i is Season ? 1 : 2), ct).ConfigureAwait(false);
        Refresh(created);
        return (wanted.Series.Values.Count(s => s.Episodes.Count > 0), episodes);
    }

    // The extras: Jellyfin's extras of the stand-ins they belong to (their
    // OwnerId, their type), in the paired servers' channel like a film,
    // described from their copies like one; named by their type and number,
    // never looked up elsewhere. Those no longer wanted (their owner gone,
    // themes turned off) removed.
    private async Task<int> KeepExtrasAsync(Wanted wanted, CancellationToken ct)
    {
        var existing = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
        foreach (var item in _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Video, BaseItemKind.Audio, BaseItemKind.Trailer],
            HasOwnerId = true,
        }))
        {
            if (PairedSources.IsStandIn(item))
            {
                existing.TryAdd(item.Path!, item);
            }
        }

        var kept = 0;
        var language = _server.Configuration.UICulture;
        foreach (var x in wanted.Extras.Values)
        {
            if (OwnerOf(x) is not { } owner)
            {
                continue;
            }

            var type = ExtraNames.TypeOf(x.Type);
            var name = ExtraNames.Of(x.Type, x.Number, language);
            var audio = x.Type == "themesong";
            if (!existing.Remove(x.Path, out var item) || (item is MediaBrowser.Controller.Entities.Audio.Audio) != audio)
            {
                if (item is not null)
                {
                    Remove([item]);
                }

                BaseItem made = audio
                    ? new MediaBrowser.Controller.Entities.Audio.Audio { Id = _library.GetNewItemId(x.Path, typeof(MediaBrowser.Controller.Entities.Audio.Audio)) }
                    : new Video { Id = _library.GetNewItemId(x.Path, typeof(Video)) };
                made.Path = x.Path;
                made.OwnerId = owner.Id;
                made.ExtraType = type;
                made.Name = name;
                made.IsLocked = true;
                made.ChannelId = ChannelId();
                made.DateCreated = owner.DateCreated;
                made.DateModified = DateTime.UtcNow;

                // Its own: Jellyfin's lists for a user group items by it, and
                // extras without one came as one (spike 0e's empty special features).
                made.PresentationUniqueKey = made.CreatePresentationUniqueKey();
                _library.CreateItem(made, null);
                item = made;
            }
            else if (item.OwnerId != owner.Id || item.ExtraType != type || item.Name != name || !item.IsLocked || string.IsNullOrEmpty(item.PresentationUniqueKey))
            {
                item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
                item.OwnerId = owner.Id;
                item.ExtraType = type;
                item.Name = name;
                item.IsLocked = true;
                await _library.UpdateItemAsync(item, owner, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }

            await DescribeAsync(item, owner, x.Copy, owner.DateCreated, null, ct).ConfigureAwait(false);
            kept++;
        }

        Remove(existing.Values);
        return kept;
    }

    // The stand-in an extra belongs to, once made: by its path, as its ID is made.
    private BaseItem? OwnerOf(WantedExtra x)
    {
        foreach (var type in new[] { typeof(Movie), typeof(Series), typeof(Season) })
        {
            if (_library.GetItemById(_library.GetNewItemId(x.Owner, type)) is { } owner && owner.Path == x.Owner)
            {
                return owner;
            }
        }

        return null;
    }

    // A season's or an episode's keys to its series, which Jellyfin's
    // metadata refresh sets otherwise (a local title gets none): without
    // them, the series lists none of its episodes. True when they changed.
    private static bool Keys(BaseItem item, Series series)
    {
        var key = series.GetPresentationUniqueKey();
        var changed = false;
        if (item is Season season && season.SeriesPresentationUniqueKey != key)
        {
            season.SeriesPresentationUniqueKey = key;
            changed = true;
        }
        else if (item is Episode episode && episode.SeriesPresentationUniqueKey != key)
        {
            episode.SeriesPresentationUniqueKey = key;
            changed = true;
        }

        if (string.IsNullOrEmpty(item.PresentationUniqueKey))
        {
            item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
            changed = true;
        }

        return changed;
    }

    // A local series' episode has no name of its own: its numbers name it (MSFP video section 3.10).
    private static string EpisodeName(WantedEpisode e) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"S{e.Season:00}E{e.Episode:00}");

    // The stand-ins under a library, by path.
    private Dictionary<string, BaseItem> Existing(Folder library)
    {
        var all = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
        foreach (var item in _library.GetItemList(new InternalItemsQuery
        {
            AncestorIds = [library.Id],
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
        }))
        {
            if (PairedSources.IsStandIn(item))
            {
                all.TryAdd(item.Path!, item);
            }
        }

        return all;
    }

    // A new stand-in. Only what plays (a film, an episode) belongs to the
    // channel, which gives it a Play button; a series or a season stays a
    // library item, or Jellyfin counts none of its episodes for it (unplayed
    // count, watched percentage: Folder.SupportsUserDataFromChildren is
    // false for an item from a channel).
    private void Prepare(BaseItem item, Guid parent, DateTime created)
    {
        item.ParentId = parent;
        item.ChannelId = item is Series or Season ? Guid.Empty : ChannelId();
        item.DateCreated = created;
        item.DateModified = DateTime.UtcNow;
        item.Name = string.Empty;
    }

    // "Recently added" as this server first saw the title (FirstSeen).
    private async Task DateAsync(BaseItem item, BaseItem parent, DateTime date, CancellationToken ct)
    {
        if (item.DateCreated != date)
        {
            item.DateCreated = date;
            await _library.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        }
    }

    // A series or a season made by an earlier version in the channel: taken
    // out of it, so that Jellyfin counts its episodes.
    private async Task OutOfChannelAsync(BaseItem item, BaseItem parent, CancellationToken ct)
    {
        if (item.ChannelId.Equals(Guid.Empty))
        {
            return;
        }

        item.ChannelId = Guid.Empty;
        await _library.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
    }

    private string SeasonName(int season) => season == 0
        ? new LibraryOptions().SeasonZeroDisplayName
        : string.Format(System.Globalization.CultureInfo.CurrentCulture, _localization.GetLocalizedString("NameSeasonNumber"), season);

    private Guid ChannelId() => _library.GetNewItemId("Channel " + PairedChannel.ChannelName, typeof(Channel));

    // A series' IDs: TMDB's, TheTVDB's (Jellyfin's TMDB provider finds a
    // series by it too), IMDb's.
    private static void SetIds(Series series, WantedSeries w)
    {
        if (w.Local is not null)
        {
            return;
        }

        if (w.Tmdb > 0)
        {
            series.SetProviderId(MetadataProvider.Tmdb, Wanted.Number(w.Tmdb));
        }

        if (w.Tvdb > 0)
        {
            series.SetProviderId(MetadataProvider.Tvdb, Wanted.Number(w.Tvdb));
        }

        if (w.Ids.TryGetValue("imdb", out var imdb))
        {
            series.SetProviderId(MetadataProvider.Imdb, imdb);
        }
    }

    // A series' IDs where the stand-in lacks one the catalogs now give
    // (a tvdb series now known to TMDB, say): added, never removed
    // (Jellyfin's metadata adds its own).
    private async Task KeepIdsAsync(Series series, BaseItem parent, WantedSeries w, CancellationToken ct)
    {
        var before = string.Join(',', series.ProviderIds.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
        if (w.Tmdb > 0 && string.IsNullOrEmpty(series.GetProviderId(MetadataProvider.Tmdb)))
        {
            series.SetProviderId(MetadataProvider.Tmdb, Wanted.Number(w.Tmdb));
        }

        if (w.Tvdb > 0 && string.IsNullOrEmpty(series.GetProviderId(MetadataProvider.Tvdb)))
        {
            series.SetProviderId(MetadataProvider.Tvdb, Wanted.Number(w.Tvdb));
        }

        if (before != string.Join(',', series.ProviderIds.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)))
        {
            await _library.UpdateItemAsync(series, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        }
    }

    // An episode's IDs: TMDB's, TheTVDB's and IMDb's of it.
    private static void SetIds(BaseItem item, Dictionary<string, string> ids)
    {
        if (ids.TryGetValue("tmdb", out var tmdb))
        {
            item.SetProviderId(MetadataProvider.Tmdb, tmdb);
        }

        if (ids.TryGetValue("tvdb", out var tvdb))
        {
            item.SetProviderId(MetadataProvider.Tvdb, tvdb);
        }

        if (ids.TryGetValue("imdb", out var imdb))
        {
            item.SetProviderId(MetadataProvider.Imdb, imdb);
        }
    }

    // A film's or an episode's description from its holder's copies, at
    // every pass (one saved right after an item was made was lost once):
    // one source per copy, best first, each with its streams; the item's
    // length, size, container, picture size and 3D from the best; its
    // chapters and skip markers; its day; a local title's description.
    private async Task DescribeAsync(BaseItem item, BaseItem parent, Playable copy, DateTime date, LocalTitle? local, CancellationToken ct)
    {
        if (copy.Chosen is not { } holder)
        {
            return;
        }

        var best = holder.Best;
        var options = _library.GetLibraryOptions(item);
        var hasSegments = false;
        try
        {
            hasSegments = await PairedSegments.SaveAsync(_segments, item, options, StandInStreams.Segments(best), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogInformation(e, "MSFP: the skip markers of {Path} couldn't be kept", item.Path);
        }

        var sources = Sources(item, holder, hasSegments);
        _streams.SaveMediaStreams(item.Id, sources[0].MediaStreams.ToList(), CancellationToken.None);
        SaveSources(item, sources);
        SaveChapters(item, best);
        try
        {
            await _previews.RegisterAsync(item, best).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogInformation(e, "MSFP: the previews of {Path} couldn't be registered", item.Path);
        }

        var changed = false;
        void Set<T>(T now, T was, Action<T> set)
        {
            if (!EqualityComparer<T>.Default.Equals(now, was))
            {
                set(now);
                changed = true;
            }
        }

        Set<long?>(StandInStreams.DurationMs(best) is > 0 and var ms ? ms * TimeSpan.TicksPerMillisecond : item.RunTimeTicks, item.RunTimeTicks, v => item.RunTimeTicks = v);
        Set<long?>(best.Size, item.Size, v => item.Size = v);
        Set(StandInStreams.ItemContainer(best.MediaType), item.Container, v => item.Container = v);
        Set(StandInStreams.Bitrate(best), item.TotalBitrate, v => item.TotalBitrate = v);
        Set(best.Video?.Width ?? 0, item.Width, v => item.Width = v);
        Set(best.Video?.Height ?? 0, item.Height, v => item.Height = v);
        if (item is Video video)
        {
            Set(StandInStreams.ThreeD(best.ThreeD), video.Video3DFormat, v => video.Video3DFormat = v);
        }

        Set(date, item.DateCreated, v => item.DateCreated = v);
        if (changed)
        {
            await _library.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        }

        await DescribeLocalAsync(item, parent, local, ct).ConfigureAwait(false);
    }

    // A local title's description, the owner's (MSFP video section 3.10):
    // its name and year, its image fetched once (re-encoded by the engine),
    // the item locked so that Jellyfin never looks it up elsewhere.
    private async Task DescribeLocalAsync(BaseItem item, BaseItem parent, LocalTitle? local, CancellationToken ct)
    {
        if (local is null)
        {
            return;
        }

        int? year = local.Year is >= 1800 and <= 9999 ? local.Year : null;
        if (item.Name != local.Name || item.ProductionYear != year || !item.IsLocked)
        {
            item.Name = local.Name;
            item.ProductionYear = year;
            item.IsLocked = true;
            await _library.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        }

        if (!local.Image || item.HasImage(ImageType.Primary, 0) || _host.Engine is not { } engine)
        {
            return;
        }

        try
        {
            var (image, format) = await engine.TitleImageAsync(local.Peer, local.Key, ct).ConfigureAwait(false);
            using var stream = new MemoryStream(image);
            await _providers.SaveImage(item, stream, "image/" + format, ImageType.Primary, null, ct).ConfigureAwait(false);
            await _library.UpdateItemAsync(item, parent, ItemUpdateType.ImageUpdate, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Asked again at a next pass.
            _log.LogInformation("MSFP: the image of a paired server's local title couldn't be fetched: {Message}", e.Message);
        }
    }

    // The holder's copies as the stand-in's sources, best first, each named
    // by its label.
    private List<MediaSourceInfo> Sources(BaseItem item, Holder holder, bool hasSegments)
    {
        var labels = StandInStreams.Labels(holder.Copies);
        var sources = new List<MediaSourceInfo>();
        for (var i = 0; i < holder.Copies.Count; i++)
        {
            var c = holder.Copies[i];
            var id = PairedSources.SourceIdOf(item, holder.Peer, c.Id, i == 0);
            var built = StandInStreams.Of(c, Three, s => SubtitlePath(item, id, s.Id));
            foreach (var st in built.Streams)
            {
                StandInStreams.Localize(st, _localization.GetLocalizedString, l => _localization.GetLanguageDisplayName(l));
            }

            sources.Add(PairedSources.SourceOf(item, id, labels[i], c, built.Streams, hasSegments));
        }

        return sources;
    }

    /// <summary>
    /// A source for one play of a holder's copy (<see cref="RemotePlays"/>):
    /// as the stand-in's saved one for that copy (its label, streams and
    /// length), with the ID given, the subtitle tracks beside the file read
    /// where <paramref name="external"/> says; and the copy's track IDs by
    /// the Index Jellyfin shows them at.
    /// </summary>
    internal (MediaSourceInfo Source, Dictionary<int, string> Tracks) SourceFor(BaseItem item, Holder holder, global::Msfp.Protocol.Copy copy, string id, Func<global::Msfp.Protocol.CopySubtitle, string> external)
    {
        var labels = StandInStreams.Labels(holder.Copies);
        var at = Math.Max(0, holder.Copies.ToList().FindIndex(c => c.Id == copy.Id));
        var built = StandInStreams.Of(copy, Three, external);
        foreach (var st in built.Streams)
        {
            StandInStreams.Localize(st, _localization.GetLocalizedString, l => _localization.GetLanguageDisplayName(l));
        }

        var source = PairedSources.SourceOf(item, id, labels[Math.Min(at, labels.Count - 1)], copy, built.Streams, StandInStreams.Segments(copy).Count > 0);
        return (source, built.Tracks.ToDictionary(kv => kv.Value, kv => kv.Key));
    }

    /// <summary>The copy's track IDs by the Index the stand-in's saved source shows them at.</summary>
    internal static Dictionary<int, string> TracksOf(global::Msfp.Protocol.Copy copy) =>
        StandInStreams.Of(copy, _ => null, _ => string.Empty).Tracks.ToDictionary(kv => kv.Value, kv => kv.Key);

    // Where Jellyfin reads a subtitle track beside a copy's file: the
    // plugin's route on its own loopback, which fetches it from the paired
    // server during a playback (migration phase 6).
    private string SubtitlePath(BaseItem item, string sourceId, string track)
    {
        var net = _server.GetConfiguration<MediaBrowser.Common.Net.NetworkConfiguration>("network");
        var baseUrl = (net.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length > 0 && !baseUrl.StartsWith('/'))
        {
            baseUrl = "/" + baseUrl;
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"http://127.0.0.1:{net.InternalHttpPort}{baseUrl}/Msfp/Subtitle/{item.Id:N}/{sourceId}/{track}");
    }

    // The sources, saved where Jellyfin keeps a channel item's.
    private static void SaveSources(BaseItem item, List<MediaSourceInfo> sources)
    {
        var dir = item.GetInternalMetadataPath();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "channelmediasourceinfos.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sources, Jellyfin.Extensions.Json.JsonDefaults.Options);
        if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes))
        {
            File.WriteAllBytes(file, bytes);
        }
    }

    // The best copy's chapters, as a local file's (no images), when they changed.
    private void SaveChapters(BaseItem item, global::Msfp.Protocol.Copy best)
    {
        var name = _localization.GetLocalizedString("ChapterNameValue");
        var wanted = StandInStreams.Chapters(best, n => string.Format(System.Globalization.CultureInfo.CurrentCulture, name, n));
        var had = _chapters.GetChapters(item.Id);
        if (had.Count == wanted.Count && had.Zip(wanted).All(p => p.First.StartPositionTicks == p.Second.StartPositionTicks && p.First.Name == p.Second.Name))
        {
            return;
        }

        _chapters.SaveChapters(item, wanted);
    }

    // A language tag of the catalog (BCP 47) as Jellyfin writes a stream's ("fra").
    private string? Three(string tag)
    {
        var two = tag.Split('-')[0];
        return Identification.Languages.Terminology(tag) is { Length: > 0 } three ? three
            : two.Length == 3 ? two.ToLowerInvariant()
            : _localization.FindLanguageInfo(two)?.ThreeLetterISOLanguageName;
    }

    private void Remove(IEnumerable<BaseItem> items)
    {
        foreach (var item in items)
        {
            _library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, false);
        }
    }

    // Stand-ins gone at the end of a pass, most often because this server
    // now holds the title: what users did with them (watched, position,
    // favourite) goes to the local items sharing their user data keys.
    // Jellyfin keeps a deleted item's user data, detached, but reattaches
    // it only to a new item or within one folder; the local copy is in
    // another library and was usually added before. Its own merge picks,
    // per user, the latest played.
    private async Task RemoveAsync(IEnumerable<BaseItem> items, CancellationToken ct)
    {
        var gone = items.ToList();
        if (gone.Count == 0)
        {
            return;
        }

        var keys = gone.SelectMany(i => i.GetUserDataKeys()).ToHashSet(StringComparer.Ordinal);
        var kinds = gone.Select(i => i.GetBaseItemKind()).Distinct().ToArray();
        Remove(gone);
        foreach (var local in _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = kinds,
            Recursive = true,
            IsVirtualItem = false,
        }))
        {
            // On the item Jellyfin keeps in memory, which its answers read
            // (a list gives copies). Then saved again per user as Jellyfin
            // saves user data, for its own cache and its clients' updates.
            if (!PairedSources.IsStandIn(local) && local.GetUserDataKeys().Any(keys.Contains)
                && _library.GetItemById(local.Id) is { } held)
            {
                await held.ReattachUserDataAsync(ct).ConfigureAwait(false);
                foreach (var userId in (held.UserData ?? []).Select(d => d.UserId).Distinct())
                {
                    if (_users.GetUserById(userId) is { } user && _userData.GetUserData(user, held) is { } data)
                    {
                        _userData.SaveUserData(user, held, data, UserDataSaveReason.Import, ct);
                    }
                }
            }
        }
    }

    // New stand-ins get Jellyfin's metadata at once, names included (never
    // local titles: they are the owner's).
    private void Refresh(List<Guid> items)
    {
        foreach (var id in items)
        {
            _providers.QueueRefresh(id, new MetadataRefreshOptions(new DirectoryService(_fs))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
            }, RefreshPriority.Normal);
        }

        if (items.Count > 0)
        {
            _log.LogInformation("MSFP: {Count} titles of paired servers getting their metadata", items.Count);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.Ordinal);

    // Which libraries the plugin made, in its data folder: one its owner
    // removed stays removed.
    private sealed class MadeLibraries
    {
        public bool Films { get; set; }

        public bool Series { get; set; }
    }

    private static string MadePath() => Path.Combine(Plugin.Instance!.DataFolderPath, "paired", "libraries.json");

    private static MadeLibraries Made()
    {
        try
        {
            return File.Exists(MadePath()) ? JsonSerializer.Deserialize<MadeLibraries>(File.ReadAllBytes(MadePath())) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static void SaveMade(MadeLibraries made)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MadePath())!);
        File.WriteAllBytes(MadePath(), JsonSerializer.SerializeToUtf8Bytes(made));
    }
}

/// <summary>The paired servers' titles here, for the page.</summary>
public sealed class StandInStatus
{
    /// <summary>"ready", "making" (until its first scan), "removed" (by its owner), or "" before the first pass.</summary>
    public string Films { get; set; } = string.Empty;

    public string Series { get; set; } = string.Empty;

    public string FilmsName { get; set; } = string.Empty;

    public string SeriesName { get; set; } = string.Empty;

    public int FilmCount { get; set; }

    public int SeriesCount { get; set; }

    public int EpisodeCount { get; set; }

    /// <summary>The extras shown under them (trailers, featurettes, themes when let in…).</summary>
    public int ExtraCount { get; set; }

    public DateTimeOffset? Kept { get; set; }
}
