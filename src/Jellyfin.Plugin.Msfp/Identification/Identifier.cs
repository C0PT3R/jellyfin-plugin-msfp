using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Msfp.Federation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Msfp.Identification;

/// <summary>An episode file as Jellyfin has it: its numbers, and the IDs Jellyfin keeps of it.</summary>
internal sealed record EpisodeFile(int Season, int Episode, string Imdb, string Tvdb);

/// <summary>
/// Identifies the films and series of the libraries offered to paired
/// servers, against TMDB, as Overfin does: before their keys enter the
/// network. A title is matched once, and again when its files change; the
/// owner's choice is never undone. A series' episodes get their TMDB IDs
/// and places. Runs a minute after Jellyfin starts, a minute after the
/// libraries change, and every 6 hours.
/// </summary>
public sealed class Identifier : BackgroundService
{
    /// <summary>
    /// The plugin's own TMDB key, used when the owner sets none. Empty until
    /// one is registered for the plugin.
    /// </summary>
    internal const string BuiltInTmdbKey = "";

    private static readonly TimeSpan Every = TimeSpan.FromHours(6);
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _library;
    private readonly EngineHost _host;
    private readonly ILogger<Identifier> _log;
    private readonly SemaphoreSlim _poke = new(0, 1);
    private TmdbClient? _tmdb;
    private string _tmdbKey = string.Empty;

    public Identifier(ILibraryManager library, EngineHost host, ILogger<Identifier> log)
    {
        _library = library;
        _host = host;
        _log = log;
    }

    /// <summary>The identities found; shared with the catalog and the page.</summary>
    internal Identities Identities { get; } = Identities.InDataFolder();

    /// <summary>Whether a run is going on, for the page.</summary>
    internal bool Running { get; private set; }

    /// <summary>The key in use: the owner's, else the plugin's; "" for none.</summary>
    internal static string Key() =>
        Plugin.Instance?.Configuration.TmdbKey is { Length: > 0 } own ? own.Trim() : BuiltInTmdbKey;

    /// <summary>Asks for a run soon (the libraries or the offers changed).</summary>
    internal void Poke()
    {
        try
        {
            _poke.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            // A new TMDB key: a pass with it.
            plugin.ConfigurationChanged += (_, _) => Poke();
        }

        await Wait(Settle, stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Running = true;
                await RunAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "MSFP: identifying the libraries' titles failed; tried again later");
            }
            finally
            {
                Running = false;
            }

            if (await _poke.WaitAsync(Every, stoppingToken).ConfigureAwait(false))
            {
                // Let the libraries settle after a change.
                await Wait(Settle, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The owner's choice for a title: a TMDB ID ("" to go back to the
    /// match), checked at TMDB and completed at once (its other IDs, a
    /// series' episodes). Never undone by a later match.
    /// </summary>
    /// <returns>false when TMDB has no such title.</returns>
    internal async Task<bool> ChooseAsync(Guid item, string tmdb, CancellationToken ct)
    {
        var src = Tmdb() ?? throw new InvalidOperationException("no TMDB key");
        var known = Identities.Of(item) ?? throw new KeyNotFoundException();
        tmdb = tmdb.Trim();
        TmdbTitle? page = null;
        if (tmdb.Length > 0 && (!int.TryParse(tmdb, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0
            || (page = await src.TitleAsync(known.Kind, id, ct).ConfigureAwait(false)) is null))
        {
            return false;
        }

        var t = known.Copy();
        t.Chosen = page is not null ? page.Id.ToString(CultureInfo.InvariantCulture) : string.Empty;
        t.ChosenName = page is not null ? page.Name + (page.Year > 0 ? " (" + page.Year.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty) : string.Empty;
        var series = _library.GetItemById(item) as Series;
        await CompleteAsync(src, t, series is not null, series is not null ? EpisodeFiles(series) : [], true, ct).ConfigureAwait(false);
        Identities.Put(item, t);
        _log.LogInformation("MSFP: the owner chose TMDB {Kind} {Tmdb} for {Name}", t.Kind, t.Chosen.Length > 0 ? t.Chosen : "(the match)", t.Name);
        return true;
    }

    /// <summary>One pass: every film and series of the offered libraries, matched where needed.</summary>
    internal async Task RunAsync(CancellationToken ct)
    {
        var src = Tmdb();
        if (src is null || _host.Engine is not { } engine)
        {
            return;
        }

        var offered = new HashSet<Guid>();
        foreach (var peer in await engine.PeersAsync(ct).ConfigureAwait(false))
        {
            foreach (var lib in await engine.OffersAsync(peer.Id, ct).ConfigureAwait(false))
            {
                if (lib.Offered && Guid.TryParseExact(lib.Id, "N", out var id))
                {
                    offered.Add(id);
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var library in offered)
        {
            var items = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Recursive = true,
                IsVirtualItem = false,
                AncestorIds = [library],
            });
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(item.Id.ToString("N")))
                {
                    continue;
                }

                await IdentifyAsync(src, item, ct).ConfigureAwait(false);
            }
        }

        Identities.KeepOnly(seen);
    }

    // Matches the item when it has never been, or when its files changed;
    // maps a series' episodes when it's taken and they changed.
    private async Task IdentifyAsync(ITmdbSource src, BaseItem item, CancellationToken ct)
    {
        var episodes = item is Series series ? EpisodeFiles(series) : [];
        var files = FilesOf(item, episodes);
        var inputs = InputsOf(files);
        var known = Identities.Of(item.Id);
        var rematched = known is null || known.Inputs != inputs;
        TitleIdentity t;
        if (rematched)
        {
            var result = await Matcher.MatchAsync(src, files, ct).ConfigureAwait(false);
            t = new TitleIdentity
            {
                Kind = files.Kind,
                Name = item.Name ?? string.Empty,
                Folder = files.Folder,
                File = files.File,
                Inputs = inputs,
                Tmdb = result.Tmdb,
                Sure = result.Sure,
                Reasons = [.. result.Reasons],
                Candidates = [.. result.Candidates],
                Chosen = known?.Chosen ?? string.Empty,
                Checked = DateTimeOffset.UtcNow,
            };
            if (known is null || known.Tmdb != t.Tmdb || !known.Sure && t.Sure)
            {
                _log.LogInformation("MSFP: {Name} identified as TMDB {Kind} {Tmdb}{Doubt}", t.Name, t.Kind, t.Tmdb.Length > 0 ? t.Tmdb : "none", t.Sure ? string.Empty : " (to check)");
            }
        }
        else
        {
            t = known!.Copy();
            t.Name = item.Name ?? t.Name;
        }

        await CompleteAsync(src, t, item is Series, episodes, rematched, ct).ConfigureAwait(false);
        if (known is null || !known.SameAs(t))
        {
            Identities.Put(item.Id, t);
        }
    }

    /// <summary>
    /// The rest of a taken title: its IMDb and TheTVDB IDs from its TMDB page,
    /// and a series' episodes, mapped again when they changed or the title
    /// did. Nothing for a title not taken.
    /// </summary>
    internal static async Task CompleteAsync(ITmdbSource src, TitleIdentity t, bool series, IReadOnlyList<EpisodeFile> episodes, bool rematched, CancellationToken ct)
    {
        if (t.Taken is not { } taken)
        {
            t.Imdb = t.Tvdb = string.Empty;
            t.Episodes = [];
            return;
        }

        if (await src.TitleAsync(t.Kind, (int)taken, ct).ConfigureAwait(false) is { } page)
        {
            t.Imdb = page.Imdb;
            t.Tvdb = page.Tvdb > 0 ? page.Tvdb.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        if (series && (rematched || t.MappedFor != taken || !SameEpisodes(t, episodes)))
        {
            t.Episodes = await MapEpisodesAsync(src, (int)taken, episodes, ct).ConfigureAwait(false);
            t.MappedFor = taken;
            t.EpisodeFiles = EpisodesKey(episodes);
        }
    }

    // The same episode files as when mapped (those TMDB doesn't list were
    // looked for too: mapped again only when the files change).
    private static bool SameEpisodes(TitleIdentity t, IReadOnlyList<EpisodeFile> episodes) =>
        t.EpisodeFiles == EpisodesKey(episodes);

    internal static string EpisodesKey(IReadOnlyList<EpisodeFile> episodes) =>
        string.Join(',', episodes.Select(e => TitleIdentity.EpisodeKey(e.Season, e.Episode)));

    /// <summary>
    /// Each episode file's TMDB ID and place: through the episode's own IMDb
    /// or TheTVDB ID when Jellyfin keeps one (TMDB's lookup gives its series,
    /// season and number now, whatever the files' numbering), else at the
    /// files' numbers in TMDB's season list. An episode TMDB doesn't list is
    /// left out: it goes by its numbers (MSFP video section 4.1).
    /// </summary>
    internal static async Task<Dictionary<string, EpisodeIdentity>> MapEpisodesAsync(ITmdbSource src, int series, IReadOnlyList<EpisodeFile> episodes, CancellationToken ct)
    {
        var map = new Dictionary<string, EpisodeIdentity>(StringComparer.Ordinal);
        var seasons = new Dictionary<int, IReadOnlyList<TmdbEpisode>>();
        foreach (var e in episodes)
        {
            EpisodeIdentity? found = null;
            foreach (var (source, id) in new[] { ("tvdb", e.Tvdb), ("imdb", e.Imdb) })
            {
                if (id.Length > 0 && await src.FindEpisodeAsync(source, id, ct).ConfigureAwait(false) is { } f && f.SeriesId == series && f.EpisodeId > 0)
                {
                    found = new EpisodeIdentity(f.EpisodeId, f.Season, f.Number);
                    break;
                }
            }

            if (found is null)
            {
                if (!seasons.TryGetValue(e.Season, out var list))
                {
                    list = await src.SeasonAsync(series, e.Season, ct).ConfigureAwait(false) ?? [];
                    seasons[e.Season] = list;
                }

                if (list.FirstOrDefault(x => x.Number == e.Episode) is { Id: > 0 } listed)
                {
                    found = new EpisodeIdentity(listed.Id, e.Season, e.Episode);
                }
            }

            if (found is not null)
            {
                map[TitleIdentity.EpisodeKey(e.Season, e.Episode)] = found;
            }
        }

        return map;
    }

    private List<EpisodeFile> EpisodeFiles(Series series) =>
        _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            AncestorIds = [series.Id],
        })
        .OfType<Episode>()
        .Where(e => e.ParentIndexNumber is >= 0 and <= 9999 && e.IndexNumber is >= 0 and <= 99999)
        .SelectMany(e =>
        {
            // A file of several episodes: each of its numbers.
            var first = e.IndexNumber!.Value;
            var last = e.IndexNumberEnd is { } end && end > first && end - first < 16 ? end : first;
            return Enumerable.Range(first, last - first + 1).Select(n => new EpisodeFile(
                e.ParentIndexNumber!.Value, n,
                n == first ? e.GetProviderId(MetadataProvider.Imdb) ?? string.Empty : string.Empty,
                n == first ? e.GetProviderId(MetadataProvider.Tvdb) ?? string.Empty : string.Empty));
        })
        .DistinctBy(e => (e.Season, e.Episode))
        .OrderBy(e => e.Season).ThenBy(e => e.Episode)
        .ToList();

    // What matching reads of an item: its folder and file names, a film's
    // length and audio languages, a series' episodes by season.
    private static Files FilesOf(BaseItem item, IReadOnlyList<EpisodeFile> episodes)
    {
        var path = item.Path ?? string.Empty;
        if (item is Series)
        {
            return new Files
            {
                Kind = "series",
                Folder = Path.GetFileName(path.TrimEnd('/', '\\')),
                Episodes = episodes.GroupBy(e => e.Season).ToDictionary(g => g.Key, g => g.Count()),
            };
        }

        return new Files
        {
            Kind = "movie",
            Folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty),
            File = Path.GetFileName(path),
            RuntimeMs = (item.RunTimeTicks ?? 0) / TimeSpan.TicksPerMillisecond,
            Audio = item.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(s.Language)).Select(s => s.Language!).Distinct().ToList(),
        };
    }

    internal static string InputsOf(Files f)
    {
        var text = string.Join('\n', f.Kind, f.Folder, f.File, (f.RuntimeMs / 60_000).ToString(CultureInfo.InvariantCulture),
            string.Join(',', f.Audio.Order(StringComparer.Ordinal)),
            string.Join(',', f.Episodes.OrderBy(e => e.Key).Select(e => e.Key.ToString(CultureInfo.InvariantCulture) + ":" + e.Value.ToString(CultureInfo.InvariantCulture))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    private TmdbClient? Tmdb()
    {
        var key = Key();
        if (key.Length == 0 || Plugin.Instance is not { } plugin)
        {
            return null;
        }

        if (_tmdb is null || _tmdbKey != key)
        {
            _tmdb?.Dispose();
            _tmdb = new TmdbClient(key, Path.Combine(plugin.DataFolderPath, "tmdb"));
            _tmdbKey = key;
        }

        return _tmdb;
    }

    private static async Task Wait(TimeSpan time, CancellationToken ct)
    {
        try
        {
            await Task.Delay(time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override void Dispose()
    {
        _tmdb?.Dispose();
        _poke.Dispose();
        base.Dispose();
    }
}
