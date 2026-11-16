using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Msfp.Protocol;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>A library of this server, as its media server knows it.</summary>
/// <param name="Id">The media server's own ID: never sent to peers.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">One of the kinds of MSFP section 9.3, or "".</param>
public sealed record LibraryInfo(string Id, string Name, string Kind);

/// <summary>One title in one library of this server.</summary>
/// <param name="Library">The media server's library ID.</param>
/// <param name="Key">The title key (section 11).</param>
/// <param name="Kind">movie, series, season or episode.</param>
/// <param name="Audio">Its copies' audio languages, as tags.</param>
/// <param name="Subtitles">Its copies' subtitle languages, as tags.</param>
public sealed record CatalogEntry(string Library, string Key, string Kind, IReadOnlyList<string> Audio, IReadOnlyList<string> Subtitles);

/// <summary>Where this server's titles come from: Jellyfin, or a test's list.</summary>
public interface ICatalogSource
{
    /// <summary>The server's libraries.</summary>
    IReadOnlyList<LibraryInfo> Libraries();

    /// <summary>Every title of every library, keyed by TMDB (section 11).</summary>
    IEnumerable<CatalogEntry> Entries();
}

/// <summary>A title answer entry of section 9.4.</summary>
public sealed record TitleOut(string Key, string Kind, string Library, IReadOnlyList<string> Audio, IReadOnlyList<string> Subtitles);

/// <summary>A page of a titles answer.</summary>
public sealed record TitlesPage(IReadOnlyList<TitleOut> Titles, IReadOnlyList<string> Gone, string? Next, bool Changes, long Seq);

/// <summary>
/// This server's catalog and its change log (MSFP sections 9.3, 9.4 and
/// 12): the titles of every library, kept up to date by comparing a fresh
/// reading with the last one. Each comparison that finds changes gives
/// them one new seq. Removed titles are kept as tombstones for 30 days.
/// Thread-safe; saved to one JSON file.
/// </summary>
public sealed class OwnCatalog
{
    /// <summary>The most titles in a page.</summary>
    public const int PageSize = 5000;

    /// <summary>How long removed titles are remembered for change readings.</summary>
    public static readonly TimeSpan KeepLogFor = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly object _lock = new();
    private readonly ICatalogSource _source;
    private readonly TimeProvider _clock;
    private readonly string? _path;
    private State _state;
    private IReadOnlyList<LibraryInfo> _libraries = Array.Empty<LibraryInfo>();

    public OwnCatalog(ICatalogSource source, TimeProvider clock, string? path)
    {
        _source = source;
        _clock = clock;
        _path = path;
        _state = path != null && File.Exists(path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(path), Options) ?? new State()
            : new State();
        if (string.IsNullOrEmpty(_state.IdSecret))
        {
            _state.IdSecret = Encodings.Base64Url(RandomNumberGenerator.GetBytes(32));
        }
    }

    /// <summary>The current change number.</summary>
    public long Seq
    {
        get
        {
            lock (_lock)
            {
                return _state.Seq;
            }
        }
    }

    /// <summary>
    /// The opaque ID peers know a library by: never the media server's own
    /// (section 12), but the same each time, so a peer's wants stay valid.
    /// </summary>
    public string PeerId(string library)
    {
        Encodings.TryDecodeBase64Url(_state.IdSecret, out var secret);
        var mac = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(library));
        return "lib_" + Encodings.Base64Url(mac.AsSpan(0, 12));
    }

    /// <summary>The server's libraries, as of the last refresh.</summary>
    public IReadOnlyList<LibraryInfo> Libraries
    {
        get
        {
            lock (_lock)
            {
                return _libraries;
            }
        }
    }

    /// <summary>
    /// Reads the catalog again from the source and logs what changed.
    /// Returns how many (library, title) entries changed.
    /// </summary>
    public int Refresh()
    {
        var libraries = _source.Libraries();
        var fresh = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var e in _source.Entries())
        {
            var id = e.Library + "|" + e.Key;
            fresh[id] = fresh.TryGetValue(id, out var twin)
                ? twin with { Audio = Merge(twin.Audio, e.Audio), Subtitles = Merge(twin.Subtitles, e.Subtitles) }
                : new Entry(e.Library, e.Key, e.Kind, Languages.Clean(e.Audio.OrderBy(t => t, StringComparer.Ordinal)), Languages.Clean(e.Subtitles.OrderBy(t => t, StringComparer.Ordinal)), 0);
        }

        lock (_lock)
        {
            _libraries = libraries;
            var now = _clock.GetUtcNow();
            var seq = _state.Seq + 1;
            var changed = 0;
            foreach (var (id, e) in fresh)
            {
                if (_state.Entries.TryGetValue(id, out var old) && old.SameAs(e))
                {
                    continue;
                }

                _state.Entries[id] = e with { Seq = seq };
                _state.Gone.RemoveAll(g => g.Library == e.Library && g.Key == e.Key);
                changed++;
            }

            foreach (var id in _state.Entries.Keys.Where(k => !fresh.ContainsKey(k)).ToList())
            {
                var old = _state.Entries[id];
                _state.Entries.Remove(id);
                _state.Gone.Add(new Tombstone(old.Library, old.Key, seq, now));
                changed++;
            }

            // Tombstones go after 30 days; changes since before them can't
            // be told any more.
            foreach (var g in _state.Gone.Where(g => now - g.At > KeepLogFor).ToList())
            {
                _state.Gone.Remove(g);
                _state.LogStart = Math.Max(_state.LogStart, g.Seq);
            }

            if (changed > 0)
            {
                _state.Seq = seq;
            }

            Save();
            return changed;
        }
    }

    /// <summary>
    /// A new change number with nothing in it: when what is offered to a
    /// peer changes, readings since before it must be whole ones.
    /// </summary>
    public long Bump()
    {
        lock (_lock)
        {
            _state.Seq++;
            Save();
            return _state.Seq;
        }
    }

    /// <summary>The libraries offered, as section 9.3 sends them.</summary>
    public IReadOnlyList<LibraryInfo> Offered(IEnumerable<string> offered)
    {
        var set = offered.ToHashSet(StringComparer.Ordinal);
        return Libraries.Where(l => set.Contains(l.Id)).ToList();
    }

    /// <summary>
    /// A page of a titles answer (section 9.4) for one peer: the libraries
    /// it asked for (peer IDs), those offered to it (media server IDs), the
    /// page start, since, and the seq at which its offer last changed.
    /// </summary>
    public TitlesPage Page(IEnumerable<string> requested, IEnumerable<string> offered, string after, long since, long offersSeq, int pageSize = PageSize)
    {
        // The libraries both asked for and offered, by media server ID,
        // with the ID the peer knows each by.
        var asked = requested.ToHashSet(StringComparer.Ordinal);
        var libs = offered.Distinct(StringComparer.Ordinal)
            .Select(id => (Id: id, Peer: PeerId(id)))
            .Where(l => asked.Contains(l.Peer))
            .ToDictionary(l => l.Id, l => l.Peer, StringComparer.Ordinal);

        lock (_lock)
        {
            var seq = _state.Seq;
            var changes = since > 0 && since >= _state.LogStart && since <= seq && since >= offersSeq;

            // The titles the peer may see now: one per key.
            var view = new SortedDictionary<string, TitleOut>(StringComparer.Ordinal);
            var held = _state.Entries.Values.Where(e => libs.ContainsKey(e.Library));
            if (changes)
            {
                var touched = _state.Entries.Values.Where(e => e.Seq > since && libs.ContainsKey(e.Library)).Select(e => e.Key)
                    .Concat(_state.Gone.Where(g => g.Seq > since && libs.ContainsKey(g.Library)).Select(g => g.Key))
                    .Where(k => string.CompareOrdinal(k, after) > 0)
                    .ToHashSet(StringComparer.Ordinal);
                held = held.Where(e => touched.Contains(e.Key));
                Fill(view, held, libs);

                var keys = touched.OrderBy(k => k, StringComparer.Ordinal).ToList();
                var page = keys.Take(pageSize).ToList();
                var titles = page.Where(view.ContainsKey).Select(k => view[k]).ToList();
                var gone = page.Where(k => !view.ContainsKey(k)).ToList();
                var next = keys.Count > pageSize ? page[^1] : null;
                return new TitlesPage(titles, gone, next, true, seq);
            }

            Fill(view, held.Where(e => string.CompareOrdinal(e.Key, after) > 0), libs);
            var whole = view.Values.Take(pageSize).ToList();
            return new TitlesPage(whole, Array.Empty<string>(), whole.Count == pageSize ? whole[^1].Key : null, false, seq);
        }
    }

    private static void Fill(SortedDictionary<string, TitleOut> view, IEnumerable<Entry> entries, Dictionary<string, string> libs)
    {
        foreach (var e in entries)
        {
            var lib = libs[e.Library];
            if (view.TryGetValue(e.Key, out var t))
            {
                // In several libraries: the lowest ID, and the languages of all.
                view[e.Key] = t with
                {
                    Library = string.CompareOrdinal(lib, t.Library) < 0 ? lib : t.Library,
                    Audio = Merge(t.Audio, e.Audio),
                    Subtitles = Merge(t.Subtitles, e.Subtitles),
                };
            }
            else
            {
                view[e.Key] = new TitleOut(e.Key, e.Kind, lib, e.Audio, e.Subtitles);
            }
        }
    }

    private static List<string> Merge(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        Languages.Clean(a.Concat(b).OrderBy(t => t, StringComparer.Ordinal));

    private void Save()
    {
        if (_path == null)
        {
            return;
        }

        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_state, Options));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>One (library, title) entry and the seq it last changed at.</summary>
    public sealed record Entry(string Library, string Key, string Kind, IReadOnlyList<string> Audio, IReadOnlyList<string> Subtitles, long Seq)
    {
        public bool SameAs(Entry o) => Kind == o.Kind && Audio.SequenceEqual(o.Audio) && Subtitles.SequenceEqual(o.Subtitles);
    }

    /// <summary>A title that left a library, at a seq and a time.</summary>
    public sealed record Tombstone(string Library, string Key, long Seq, DateTimeOffset At);

    /// <summary>What is saved.</summary>
    public sealed class State
    {
        public long Seq { get; set; } = 1;

        /// <summary>Changes since a seq below this can't be told: whole lists only.</summary>
        public long LogStart { get; set; } = 1;

        public string IdSecret { get; set; } = string.Empty;

        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.Ordinal);

        public List<Tombstone> Gone { get; set; } = new();
    }
}
