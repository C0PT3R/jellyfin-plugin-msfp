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
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
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
/// file: an msfp:// path from its key, the paired servers' channel, a
/// source of its own to open at play, the languages and picture the
/// catalog gave, and Jellyfin's own metadata from its TMDB ID.
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
        ILogger<StandIns> log)
    {
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
    /// A film's or an episode's copies, by its key, as the last pass found
    /// them: the paired servers holding it, best copy first; null when none.
    /// </summary>
    internal Playable? CopyOf(string key) => CopyOf(_wanted, key);

    internal static Playable? CopyOf(Wanted? w, string key)
    {
        if (w is null || !global::Msfp.Protocol.VideoKey.TryParse(key, out var k) || k.Scheme != "tmdb")
        {
            return null;
        }

        return k.Kind == "movie"
            ? w.Films.TryGetValue(k.Id, out var f) ? f.Copy : null
            : w.Series.TryGetValue(k.Id, out var s) && s.Episodes.TryGetValue(key, out var e) ? e.Copy : null;
    }

    /// <summary>
    /// <see cref="CopyOf"/> for a playback: the copies are taken from the
    /// catalogs as stored, as a pass takes them, when the last pass is
    /// behind: before the first one since Jellyfin started (2 minutes, or a
    /// catalog read), and when a server it found has stopped being paired
    /// (the next pass comes 30 seconds after; a server paired since may
    /// hold the title).
    /// </summary>
    internal async Task<Playable?> CopyOfAsync(string key, CancellationToken ct)
    {
        if (_host.Engine is not { } engine)
        {
            return CopyOf(key);
        }

        var cached = CopyOf(key);
        if (cached is not null)
        {
            var paired = (await engine.PeersAsync(ct).ConfigureAwait(false))
                .Where(p => p.State == PeerState.Paired).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            if (Fresh(cached, paired))
            {
                return cached;
            }
        }

        var wanted = Wanted.Of(await LivePeersAsync(engine, ct).ConfigureAwait(false), Held());
        lock (_lock)
        {
            _wanted ??= wanted;
        }

        return CopyOf(wanted, key);
    }

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
        // Off on this branch until phase 5 of the migration builds the
        // stand-ins from the copies' facts (MSFP's revised version 0): those
        // already made are left as they are.
        if (!RemotePlays.ViewingOn)
        {
            return;
        }

        if (Plugin.Instance is not { } plugin || !await _running.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            Wanted wanted;
            if (_host.Engine is { } engine)
            {
                wanted = Wanted.Of(await LivePeersAsync(engine, ct).ConfigureAwait(false), Held());
            }
            else if (_host.Problem.Length > 0)
            {
                // Federation off: the libraries empty, kept.
                wanted = new Wanted();
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
            if (films.Folder is { } ff)
            {
                counts.FilmCount = await KeepFilmsAsync(ff, wanted, ct).ConfigureAwait(false);
            }

            if (series.Folder is { } sf)
            {
                (counts.SeriesCount, counts.EpisodeCount) = await KeepSeriesAsync(sf, wanted, ct).ConfigureAwait(false);
            }

            counts.Kept = DateTimeOffset.UtcNow;
            Status = counts;
        }
        finally
        {
            _running.Release();
        }
    }

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

    // What this server holds itself, by TMDB: the plugin's identity of a
    // title where there is one (the libraries offered), Jellyfin's ID
    // otherwise; a series' episodes by TMDB's ID where the identity mapped
    // them, by their numbers otherwise.
    private Held Held()
    {
        var held = new Held();
        var identities = _identifier.Identities;
        var seriesTmdb = new Dictionary<Guid, (long Tmdb, TitleIdentity? Identity)>();
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
            var tmdb = identity?.Taken ?? Catalog.JellyfinCatalogSource.TmdbId(item.GetProviderId(MetadataProvider.Tmdb));
            if (tmdb is not { } id)
            {
                continue;
            }

            if (item is Movie)
            {
                held.Films.Add(id);
            }
            else
            {
                seriesTmdb[item.Id] = (id, identity);
            }
        }

        foreach (var item in _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
        }))
        {
            if (item is not Episode e || PairedSources.IsStandIn(e) || !seriesTmdb.TryGetValue(e.SeriesId, out var s)
                || e.ParentIndexNumber is not { } season || e.IndexNumber is not { } first)
            {
                continue;
            }

            var hs = held.SeriesOf(s.Tmdb);
            var last = e.IndexNumberEnd is { } end && end > first && end - first < 16 ? end : first;
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

        return held;
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

    private async Task<int> KeepFilmsAsync(Folder library, Wanted wanted, CancellationToken ct)
    {
        var existing = Existing(library);
        var created = new List<Guid>();
        foreach (var f in wanted.Films.Values)
        {
            var path = Wanted.PathOf(f.Copy.Key);
            if (!existing.Remove(path, out var item))
            {
                var movie = new Movie { Id = _library.GetNewItemId(path, typeof(Movie)), Path = path };
                Prepare(movie, library.Id);
                movie.SetProviderId(MetadataProvider.Tmdb, Wanted.Number(f.Tmdb));
                SetIds(movie, f.Ids);
                _library.CreateItem(movie, library);
                await movie.ReattachUserDataAsync(ct).ConfigureAwait(false);
                created.Add(movie.Id);
                item = movie;
            }
            else if (string.IsNullOrEmpty(item.Name))
            {
                // Its metadata never came (TMDB unreachable, ...): asked again.
                created.Add(item.Id);
            }

            SaveStreams(item, f.Copy);
        }

        await RemoveAsync(existing.Values, ct).ConfigureAwait(false);
        Refresh(created);
        return wanted.Films.Count;
    }

    private async Task<(int Series, int Episodes)> KeepSeriesAsync(Folder library, Wanted wanted, CancellationToken ct)
    {
        var existing = Existing(library);
        var created = new List<Guid>();
        var episodes = 0;
        foreach (var w in wanted.Series.Values)
        {
            var seriesPath = Wanted.PathOf(Wanted.SeriesKey(w.Tmdb));
            if (!existing.Remove(seriesPath, out var seriesItem) || seriesItem is not Series series)
            {
                series = new Series { Id = _library.GetNewItemId(seriesPath, typeof(Series)), Path = seriesPath };
                Prepare(series, library.Id);
                series.SetProviderId(MetadataProvider.Tmdb, Wanted.Number(w.Tmdb));
                SetIds(series, w.Ids);
                _library.CreateItem(series, library);
                created.Add(series.Id);
            }
            else if (string.IsNullOrEmpty(series.Name))
            {
                created.Add(series.Id);
            }

            await OutOfChannelAsync(series, library, ct).ConfigureAwait(false);

            var seasons = new Dictionary<int, Season>();
            foreach (var e in w.Episodes.Values)
            {
                if (!seasons.TryGetValue(e.Season, out var season))
                {
                    var seasonPath = Wanted.PathOf(Wanted.SeasonKey(w.Tmdb, e.Season));
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
                        Prepare(s, series.Id);
                        // Named as Jellyfin names its own (else by its path's end, "s1").
                        s.Name = SeasonName(e.Season);
                        _library.CreateItem(s, series);
                        created.Add(s.Id);
                    }
                    else if (s.Name != SeasonName(e.Season) && s.Name == Path.GetFileName(seasonPath))
                    {
                        s.Name = SeasonName(e.Season);
                        await _library.UpdateItemAsync(s, series, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                    }

                    await OutOfChannelAsync(s, series, ct).ConfigureAwait(false);
                    season = s;
                    seasons[e.Season] = season;
                }

                var path = Wanted.PathOf(e.Copy.Key);
                existing.Remove(path, out var item);
                if (item is Episode moved && (moved.ParentIndexNumber != e.Season || moved.IndexNumber != e.Episode))
                {
                    // TMDB moved it: made again at its new place, its user data kept.
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
                    Prepare(episode, season.Id);
                    if (e.EpisodeId > 0)
                    {
                        episode.SetProviderId(MetadataProvider.Tmdb, Wanted.Number(e.EpisodeId));
                    }

                    _library.CreateItem(episode, season);
                    await episode.ReattachUserDataAsync(ct).ConfigureAwait(false);
                    created.Add(episode.Id);
                    item = episode;
                }
                else if (string.IsNullOrEmpty(item.Name))
                {
                    created.Add(item.Id);
                }

                SaveStreams(item, e.Copy);
                episodes++;
            }
        }

        // Episodes first, then their seasons and series.
        await RemoveAsync(existing.Values.OrderBy(i => i is Episode ? 0 : i is Season ? 1 : 2), ct).ConfigureAwait(false);
        Refresh(created);
        return (wanted.Series.Count, episodes);
    }

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
    private void Prepare(BaseItem item, Guid parent)
    {
        var now = DateTime.UtcNow;
        item.ParentId = parent;
        item.ChannelId = item is Series or Season ? Guid.Empty : ChannelId();
        item.DateCreated = now;
        item.DateModified = now;
        item.Name = string.Empty;
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

    private static void SetIds(BaseItem item, Dictionary<string, string> ids)
    {
        if (ids.TryGetValue("imdb", out var imdb))
        {
            item.SetProviderId(MetadataProvider.Imdb, imdb);
        }

        if (ids.TryGetValue("tvdb", out var tvdb))
        {
            item.SetProviderId(MetadataProvider.Tvdb, tvdb);
        }
    }

    // The languages and picture the catalogs gave, as the item's streams,
    // and its own source (saved where Jellyfin keeps a channel item's), at
    // every pass: one saved right after an item was made was lost once.
    private void SaveStreams(BaseItem item, Playable copy)
    {
        // Playing now: its saved source is the playback's (SavePlaying).
        if (Playing.ContainsKey(item.Id))
        {
            return;
        }

        var streams = Streams.Of(copy, Three);
        _streams.SaveMediaStreams(item.Id, streams, CancellationToken.None);
        SaveSource(item, streams);
    }

    /// <summary>The stand-ins playing now: the passes leave their saved source alone.</summary>
    internal static System.Collections.Concurrent.ConcurrentDictionary<Guid, int> Playing { get; } = new();

    /// <summary>
    /// A stand-in's saved source while it plays: the playback's streams, its
    /// subtitle tracks at the relay among them. Jellyfin's subtitle route
    /// reads the item's saved source, not the live one.
    /// </summary>
    internal static void SavePlaying(BaseItem item, List<MediaStream> streams) => SaveSource(item, streams);

    private static void SaveSource(BaseItem item, List<MediaStream> streams)
    {
        var dir = item.GetInternalMetadataPath();
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "channelmediasourceinfos.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { PairedSources.SourceOf(item, streams) }, Jellyfin.Extensions.Json.JsonDefaults.Options);
        if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes))
        {
            File.WriteAllBytes(file, bytes);
        }
    }

    // A language tag of the catalog (BCP 47) as Jellyfin's three-letter code.
    private string? Three(string tag)
    {
        var two = tag.Split('-')[0];
        return Identification.Languages.Three(tag) is { Length: > 0 } three ? three
            : _localization.FindLanguageInfo(two)?.ThreeLetterISOLanguageName ?? (two.Length == 3 ? two : null);
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

    // New stand-ins get Jellyfin's metadata at once, names included.
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

    public DateTimeOffset? Kept { get; set; }
}
