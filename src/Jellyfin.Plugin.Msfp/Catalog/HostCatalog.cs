using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Msfp;
using Picture = Msfp.Picture;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>One title of one copy, as the media server holds it.</summary>
/// <param name="Library">The media server's library ID: never sent (the engine maps it to a public ID).</param>
/// <param name="Key">The title key (MSFP video section 4).</param>
/// <param name="Kind">movie, series, season or episode.</param>
/// <param name="Copy">The copy's handle (Jellyfin's item ID) for films and episodes; null for series and seasons.</param>
public sealed record CatalogEntry(string Library, string Key, string Kind, string? Copy)
{
    /// <summary>The copy's audio languages, as the media server's codes.</summary>
    public IReadOnlyList<string> Audio { get; init; } = [];

    /// <summary>The copy's subtitle languages, of the tracks it can serve (text ones).</summary>
    public IReadOnlyList<string> Subtitles { get; init; } = [];

    /// <summary>The copy's picture; null when unknown.</summary>
    public Picture? Picture { get; init; }

    /// <summary>The other episodes the copy holds (a file of several episodes).</summary>
    public IReadOnlyList<string> With { get; init; } = [];

    /// <summary>Other identifiers, by scheme (imdb, tvdb).</summary>
    public IReadOnlyDictionary<string, string> Ids { get; init; } = new Dictionary<string, string>();

    /// <summary>The copy's name, for versions.</summary>
    public string CopyName { get; init; } = string.Empty;
}

/// <summary>Where this server's titles come from: Jellyfin, or a test's list.</summary>
public interface ICatalogSource
{
    /// <summary>The server's libraries, by the media server's IDs.</summary>
    IReadOnlyList<Library> Libraries();

    /// <summary>Every title of every library, keyed by TMDB.</summary>
    IEnumerable<CatalogEntry> Entries();
}

/// <summary>
/// This server's catalog for the engine (its ICatalog): the source read
/// whole, its entries joined by key, kept until the source changes
/// (<see cref="Changed"/>). The engine keeps the change log itself, from
/// snapshots of these titles, in its store.
/// </summary>
public sealed class HostCatalog : ICatalog
{
    private readonly ICatalogSource _source;
    private readonly object _lock = new();
    private Snapshot? _snapshot;

    public HostCatalog(ICatalogSource source)
    {
        _source = source;
    }

    /// <summary>Forgets what was read: the next call reads the source again.</summary>
    public void Changed()
    {
        lock (_lock)
        {
            _snapshot = null;
        }
    }

    /// <summary>The libraries and titles now, read once until <see cref="Changed"/>.</summary>
    internal Snapshot Read()
    {
        lock (_lock)
        {
            return _snapshot ??= Snapshot.Of(_source.Libraries(), _source.Entries());
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Library>> LibrariesAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<Library>>(Read().Libraries.Select(l => l with { }).ToList());

    /// <inheritdoc />
    public ValueTask<TitlePage> TitlesAsync(IReadOnlyList<string> libraries, string after, int limit, CancellationToken ct)
    {
        var s = Read();
        var wanted = libraries.ToHashSet(StringComparer.Ordinal);
        var titles = new List<Title>();
        var more = false;
        var start = s.FirstAfter(after);
        for (var i = start; i < s.Keys.Count; i++)
        {
            var t = s.Titles[s.Keys[i]];
            var libs = t.Libraries!.Where(wanted.Contains).ToList();
            if (libs.Count == 0)
            {
                continue;
            }

            if (titles.Count == limit)
            {
                more = true;
                break;
            }

            titles.Add(t with
            {
                Libraries = libs,
                Audio = t.Audio?.ToList(),
                Subtitles = t.Subtitles?.ToList(),
                Pictures = t.Pictures?.ToList(),
                With = t.With?.ToList(),
                Ids = t.Ids is null ? null : new Dictionary<string, string>(t.Ids),
            });
        }

        return ValueTask.FromResult(new TitlePage(titles, more));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Copy>> CopiesAsync(string key, IReadOnlyList<string> libraries, CancellationToken ct)
    {
        var s = Read();
        IReadOnlyList<Copy> copies = s.Copies.TryGetValue(key, out var all)
            ? all.Where(c => libraries.Contains(c.Library, StringComparer.Ordinal)).Select(c => c with { Audio = c.Audio?.ToList() }).ToList()
            : [];
        return ValueTask.FromResult(copies);
    }

    /// <summary>The catalog as read once.</summary>
    internal sealed class Snapshot
    {
        public required IReadOnlyList<Library> Libraries { get; init; }

        /// <summary>Every key, in ascending byte order.</summary>
        public required IReadOnlyList<string> Keys { get; init; }

        public required IReadOnlyDictionary<string, VideoTitle> Titles { get; init; }

        public required IReadOnlyDictionary<string, List<Copy>> Copies { get; init; }

        /// <summary>The index of the first key strictly after this one.</summary>
        public int FirstAfter(string after)
        {
            if (after.Length == 0)
            {
                return 0;
            }

            int lo = 0, hi = Keys.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (string.CompareOrdinal(Keys[mid], after) <= 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        public static Snapshot Of(IReadOnlyList<Library> libraries, IEnumerable<CatalogEntry> entries)
        {
            var titles = new Dictionary<string, VideoTitle>(StringComparer.Ordinal);
            var copies = new Dictionary<string, List<Copy>>(StringComparer.Ordinal);
            var pictures = new Dictionary<string, Picture>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                if (!titles.TryGetValue(e.Key, out var t))
                {
                    t = new VideoTitle { Key = e.Key, Kind = e.Kind, Libraries = [] };
                    titles[e.Key] = t;
                }
                else if (t.Kind != e.Kind)
                {
                    continue;
                }

                if (!t.Libraries!.Contains(e.Library))
                {
                    t.Libraries.Add(e.Library);
                }

                t.Audio = Union(t.Audio, e.Audio);
                t.Subtitles = Union(t.Subtitles, e.Subtitles);
                t.With = Union(t.With, e.With);
                if (e.Picture is { } p)
                {
                    (t.Pictures ??= []).Add(p);
                }

                foreach (var (scheme, id) in e.Ids)
                {
                    (t.Ids ??= new Dictionary<string, string>(StringComparer.Ordinal)).TryAdd(scheme, id);
                }

                if (e.Copy is { } handle)
                {
                    if (e.Picture is { } cp)
                    {
                        pictures[handle] = cp;
                    }

                    var list = copies.TryGetValue(e.Key, out var l) ? l : copies[e.Key] = [];
                    if (!list.Any(c => c.Handle == handle && c.Library == e.Library))
                    {
                        list.Add(new Copy { Handle = handle, Name = e.CopyName, Audio = e.Audio.ToList(), Library = e.Library });
                    }
                }
            }

            // Best first, so that play without a version plays it (MSFP
            // video section 3.2).
            foreach (var list in copies.Values)
            {
                list.Sort((a, b) => Rank(pictures.GetValueOrDefault(b.Handle), pictures.GetValueOrDefault(a.Handle)));
            }

            return new Snapshot
            {
                Libraries = libraries,
                Keys = titles.Keys.Order(StringComparer.Ordinal).ToList(),
                Titles = titles,
                Copies = copies,
            };
        }

        // Orders two copies' pictures as Picture.Best ranks them: above 0
        // when a is the better.
        private static int Rank(Picture? a, Picture? b)
        {
            var sa = a is null ? null : Picture.Best([a]);
            var sb = b is null ? null : Picture.Best([b]);
            if (sa is null || sb is null)
            {
                return sa is null ? (sb is null ? 0 : -1) : 1;
            }

            return sa.Better(sb) ? 1 : sb.Better(sa) ? -1 : 0;
        }

        private static List<string>? Union(List<string>? have, IReadOnlyList<string> add)
        {
            if (add.Count == 0)
            {
                return have;
            }

            have ??= [];
            foreach (var a in add)
            {
                if (!have.Contains(a, StringComparer.OrdinalIgnoreCase))
                {
                    have.Add(a);
                }
            }

            return have;
        }
    }
}
