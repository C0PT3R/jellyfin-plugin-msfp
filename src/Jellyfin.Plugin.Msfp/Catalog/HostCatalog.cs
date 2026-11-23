using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Msfp;
using Copy = Msfp.Protocol.Copy;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>One title as one library holds it, with that library's copies of it.</summary>
/// <param name="Library">The media server's library ID: never sent (the engine maps it to a public ID).</param>
/// <param name="Key">The title key (MSFP video section 4).</param>
/// <param name="Kind">movie, series, season, episode or extra.</param>
public sealed record CatalogEntry(string Library, string Key, string Kind)
{
    /// <summary>A film's, an episode's or an extra's copies in this library, in the owner's order (its default version first); their IDs are Jellyfin's item IDs (handles, never sent).</summary>
    public IReadOnlyList<Copy> Copies { get; init; } = [];

    /// <summary>
    /// For an episode keyed by its TMDB ID: its place in TMDB's numbering
    /// (MSFP video section 4.1); null leaves the episode out.
    /// </summary>
    public Place? Place { get; init; }

    /// <summary>The other episodes the copy holds (a file of several episodes).</summary>
    public IReadOnlyList<string> With { get; init; } = [];

    /// <summary>Other identifiers, by scheme (imdb, tvdb, tmdb), where MSFP takes them (video section 4.3).</summary>
    public IReadOnlyDictionary<string, string> Ids { get; init; } = new Dictionary<string, string>();

    /// <summary>An extra's type, a token (trailer, featurette…).</summary>
    public string? ExtraType { get; init; }

    /// <summary>A local film's or series' name.</summary>
    public string? Name { get; init; }

    /// <summary>A local film's or series' year; 0 for none.</summary>
    public int Year { get; init; }

    /// <summary>A local film or series whose image is shared.</summary>
    public bool Image { get; init; }

    /// <summary>The item whose image a local title shares.</summary>
    public Guid? ImageItem { get; init; }
}

/// <summary>Where this server's titles come from: Jellyfin, or a test's list.</summary>
public interface ICatalogSource
{
    /// <summary>The server's libraries, by the media server's IDs.</summary>
    IReadOnlyList<Library> Libraries();

    /// <summary>Every title of every library.</summary>
    IEnumerable<CatalogEntry> Entries();

    /// <summary>A local title's image (a JPEG within MSFP's bounds), from its item; null for none.</summary>
    ValueTask<byte[]?> ImageAsync(Guid item, CancellationToken ct);
}

/// <summary>
/// This server's catalog for the engine (its ICatalog): the source read
/// whole, its entries joined by key, kept until the source changes
/// (<see cref="Changed"/>). The engine keeps the change log itself, from
/// snapshots of these titles, in its store. A title's copies leave only
/// to the libraries that hold them: a peer offered one library never sees
/// another's copies of the same title.
/// </summary>
public sealed class HostCatalog : ICatalog, IImages
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

            List<Copy>? copies = null;
            if (t.Copies is { } all)
            {
                copies = all.Where(c => s.CopyLibraries.TryGetValue((t.Key, c.Id), out var held) && held.Any(wanted.Contains)).ToList();
                if (copies.Count == 0)
                {
                    continue;
                }
            }

            if (titles.Count == limit)
            {
                more = true;
                break;
            }

            titles.Add(t with
            {
                Libraries = libs,
                Copies = copies,
                With = t.With?.ToList(),
                Ids = t.Ids is null ? null : new Dictionary<string, string>(t.Ids),
            });
        }

        return ValueTask.FromResult(new TitlePage(titles, more));
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> ImageAsync(string key, CancellationToken ct)
    {
        if (!Read().Images.TryGetValue(key, out var item) || await _source.ImageAsync(item, ct).ConfigureAwait(false) is not { Length: > 0 } image)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        return image;
    }

    /// <summary>The catalog as read once.</summary>
    internal sealed class Snapshot
    {
        public required IReadOnlyList<Library> Libraries { get; init; }

        /// <summary>Every key, in ascending byte order.</summary>
        public required IReadOnlyList<string> Keys { get; init; }

        public required IReadOnlyDictionary<string, VideoTitle> Titles { get; init; }

        /// <summary>The libraries holding each title's copy, by (key, copy ID).</summary>
        public required IReadOnlyDictionary<(string Key, string Copy), HashSet<string>> CopyLibraries { get; init; }

        /// <summary>The item whose image each local title shares, by key.</summary>
        public required IReadOnlyDictionary<string, Guid> Images { get; init; }

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
            var held = new Dictionary<(string, string), HashSet<string>>();
            var images = new Dictionary<string, Guid>(StringComparer.Ordinal);
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

                t.With = Union(t.With, e.With);
                t.Place ??= e.Place;
                t.ExtraType ??= e.ExtraType;
                if (e.Name is { Length: > 0 } && t.Name is null)
                {
                    (t.Name, t.Year, t.Image) = (e.Name, e.Year, e.Image);
                    if (e.ImageItem is { } item)
                    {
                        images[e.Key] = item;
                    }
                }

                foreach (var (scheme, id) in e.Ids)
                {
                    (t.Ids ??= new Dictionary<string, string>(StringComparer.Ordinal)).TryAdd(scheme, id);
                }

                // The copies in the order the libraries list them, each
                // once, each with the libraries holding it (the owner's
                // order: Jellyfin's default version first).
                foreach (var c in e.Copies)
                {
                    if (!held.TryGetValue((e.Key, c.Id), out var libs))
                    {
                        held[(e.Key, c.Id)] = libs = new HashSet<string>(StringComparer.Ordinal);
                        (t.Copies ??= []).Add(c);
                    }

                    libs.Add(e.Library);
                }
            }

            foreach (var t in titles.Values)
            {
                if (t.Copies is { Count: > CopyKeeper.MaxCopies } many)
                {
                    t.Copies = many.Take(CopyKeeper.MaxCopies).ToList();
                }
            }

            return new Snapshot
            {
                Libraries = libraries,
                Keys = titles.Keys.Order(StringComparer.Ordinal).ToList(),
                Titles = titles,
                CopyLibraries = held,
                Images = images,
            };
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
