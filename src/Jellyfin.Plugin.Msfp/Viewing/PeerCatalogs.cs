using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>A library a peer offers, as this server keeps it (section 9.3).</summary>
public sealed record PeerLibrary(string Id, string Name, string Kind);

/// <summary>A peer's title, as this server keeps it (section 9.4).</summary>
public sealed record PeerTitle(string Kind, string Library, List<string> Audio, List<string> Subtitles);

/// <summary>What this server knows of one peer's catalog.</summary>
public sealed class PeerCatalog
{
    /// <summary>The libraries the peer offers this server.</summary>
    public List<PeerLibrary> Libraries { get; set; } = new();

    /// <summary>The libraries the last reading asked for, sorted.</summary>
    public List<string> Requested { get; set; } = new();

    /// <summary>The seq of the last reading's first page; 0: read the whole list next.</summary>
    public long Seq { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>How the last reading went, for the operator.</summary>
    public string LastResult { get; set; } = string.Empty;

    public SortedDictionary<string, PeerTitle> Titles { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Peers' catalogs, one JSON file each in a folder (or in memory). Thread-safe.</summary>
public sealed class PeerCatalogStore
{
    private static readonly JsonSerializerOptions Options = new();

    private readonly string? _folder;
    private readonly object _lock = new();
    private readonly Dictionary<string, PeerCatalog> _memory = new(StringComparer.Ordinal);

    public PeerCatalogStore(string? folder)
    {
        _folder = folder;
        if (folder != null)
        {
            Directory.CreateDirectory(folder);
        }
    }

    public PeerCatalog Get(string peerKey)
    {
        lock (_lock)
        {
            if (_memory.TryGetValue(peerKey, out var c))
            {
                return c;
            }

            var path = PathOf(peerKey);
            c = path != null && File.Exists(path)
                ? JsonSerializer.Deserialize<PeerCatalog>(File.ReadAllBytes(path), Options) ?? new PeerCatalog()
                : new PeerCatalog();
            _memory[peerKey] = c;
            return c;
        }
    }

    public void Put(string peerKey, PeerCatalog catalog)
    {
        lock (_lock)
        {
            _memory[peerKey] = catalog;
            if (PathOf(peerKey) is { } path)
            {
                File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(catalog, Options));
                File.Move(path + ".tmp", path, overwrite: true);
            }
        }
    }

    public void Delete(string peerKey)
    {
        lock (_lock)
        {
            _memory.Remove(peerKey);
            if (PathOf(peerKey) is { } path && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Deletes a catalog by its file name.</summary>
    public void DeleteFileName(string fileName)
    {
        lock (_lock)
        {
            foreach (var key in _memory.Keys.Where(k => FileName(k) == fileName).ToList())
            {
                _memory.Remove(key);
            }

            if (_folder != null && Path.Combine(_folder, fileName + ".json") is var path && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>The peers that have a catalog, by file name.</summary>
    public IEnumerable<string> FileNames()
    {
        lock (_lock)
        {
            var names = _memory.Keys.Select(FileName);
            return _folder == null ? names.ToList() : names.Concat(Directory.GetFiles(_folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()).Distinct().ToList();
        }
    }

    /// <summary>A file name from a key: SHA-256, base64url, so any key is safe.</summary>
    public static string FileName(string peerKey) => Encodings.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(peerKey)));

    private string? PathOf(string peerKey) => _folder == null ? null : Path.Combine(_folder, FileName(peerKey) + ".json");
}

/// <summary>
/// Reads paired peers' libraries and titles (MSFP sections 9.3 and 9.4)
/// through checked calls (section 3.1), and keeps what section 9.4 says a
/// caller keeps. A whole reading first; changes after, while the libraries
/// asked for stay the same.
/// </summary>
public sealed class CatalogReader
{
    /// <summary>How often a peer is read.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    /// <summary>How long one reading may take.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    public const int MaxLibraries = 200;
    public const int MaxGoneKey = 128;

    /// <summary>The most titles kept of one peer (section 14: readers cap what they keep).</summary>
    public const int MaxTitles = 500_000;

    private readonly FederationService _federation;
    private readonly PeerCatalogStore _store;
    private readonly TimeProvider _clock;

    public CatalogReader(FederationService federation, PeerCatalogStore store, TimeProvider clock)
    {
        _federation = federation;
        _store = store;
        _clock = clock;

        // A peer's catalog goes with the peer, at once: a new pairing with
        // the same key must not start from the old one's titles and seq.
        federation.PeerRemoved += key => _store.Delete(key);
    }

    public PeerCatalogStore Store => _store;

    /// <summary>Reads the paired peers whose last reading is older than 30 minutes; drops catalogs of peers gone.</summary>
    public async Task ReadDueAsync(CancellationToken ct)
    {
        var peers = _federation.Peers();
        var known = peers.Select(p => PeerCatalogStore.FileName(p.Key)).ToHashSet(StringComparer.Ordinal);
        foreach (var file in _store.FileNames().Where(f => !known.Contains(f)).ToList())
        {
            // The catalog of a peer that was deleted.
            _store.DeleteFileName(file);
        }

        foreach (var p in peers.Where(p => p.State == PeerState.Paired))
        {
            var c = _store.Get(p.Key);
            if (c.ReadAt is not { } at || _clock.GetUtcNow() - at >= Every)
            {
                await ReadAsync(p.Key, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One reading of a peer: libraries, then titles. Returns what happened, for the operator.</summary>
    public async Task<string> ReadAsync(string peerKey, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Budget);
        var peer = _federation.Peers().FirstOrDefault(p => p.Key == peerKey);
        if (peer == null || peer.State != PeerState.Paired)
        {
            return "Not paired.";
        }

        var old = _store.Get(peerKey);
        string result;
        PeerCatalog? fresh;
        try
        {
            (result, fresh) = await ReadCoreAsync(peer, old, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            (result, fresh) = ("The reading took too long.", null);
        }

        // A failed reading keeps what was read before, and waits its turn.
        var kept = fresh ?? Clone(old);
        kept.ReadAt = _clock.GetUtcNow();
        kept.LastResult = result;
        _store.Put(peerKey, kept);
        return result;
    }

    private async Task<(string, PeerCatalog?)> ReadCoreAsync(Peer peer, PeerCatalog old, CancellationToken ct)
    {
        var libs = await _federation.CallCheckedAsync(peer.Key, FederationService.LibrariesPath, Encoding.UTF8.GetBytes("{}"), ct).ConfigureAwait(false);
        if (Refused(libs, out var why))
        {
            await AfterRefusalAsync(peer, libs, ct).ConfigureAwait(false);
            return (why, null);
        }

        var root = libs.Json!.Value;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ("The libraries answer isn't a JSON object.", null);
        }

        // A top-level member of the wrong type fails the answer (section 9,
        // corrected 2026-11-17); limits, which the plugin doesn't read
        // yet, are the one exception.
        var badLibs = WrongType(root, "libraries", JsonValueKind.Array) ?? WrongType(root, "name", JsonValueKind.String);
        if (badLibs != null)
        {
            return ($"The libraries answer's {badLibs} isn't of its type.", null);
        }

        if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            _federation.Rename(peer.Key, n.GetString());
        }

        var libraries = ReadLibraries(root);
        var offered = libraries.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var requested = (peer.WantAll ? offered : peer.Wanted.Where(offered.Contains)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (requested.Count == 0)
        {
            return ("Nothing to read: no library is both offered and wanted.", new PeerCatalog { Libraries = libraries });
        }

        // Changes only when the same libraries are asked for (SPEC-GAPS.md, 9.4).
        var since = old.Requested.SequenceEqual(requested) ? old.Seq : 0;
        var asked = requested.ToHashSet(StringComparer.Ordinal);
        var titles = new SortedDictionary<string, PeerTitle>(StringComparer.Ordinal);
        var gone = new HashSet<string>(StringComparer.Ordinal);
        bool? changes = null;
        long seq = 0;
        var after = string.Empty;
        while (true)
        {
            var body = Body(requested, after, since);
            var page = await _federation.CallCheckedAsync(peer.Key, FederationService.TitlesPath, body, ct).ConfigureAwait(false);
            if (Refused(page, out why))
            {
                await AfterRefusalAsync(peer, page, ct).ConfigureAwait(false);
                return (why, null);
            }

            var json = page.Json!.Value;
            if (json.ValueKind != JsonValueKind.Object)
            {
                return ("A titles answer isn't a JSON object.", null);
            }

            // Top-level members of the wrong type fail the answer (section
            // 9.4, corrected 2026-11-17): skipping one could end a reading
            // early or mix its kinds, and titles the peer holds be dropped.
            var wrong = WrongType(json, "titles", JsonValueKind.Array) ?? WrongType(json, "gone", JsonValueKind.Array)
                ?? WrongType(json, "next", JsonValueKind.String) ?? WrongType(json, "changes", JsonValueKind.True, JsonValueKind.False);
            if (wrong != null)
            {
                return ($"The peer's titles answer has a {wrong} that isn't of its type.", null);
            }

            var isChanges = json.TryGetProperty("changes", out var ch) && ch.ValueKind == JsonValueKind.True;
            if (changes == null)
            {
                changes = isChanges;

                // seq, when there, is an integer in its one form (section
                // 2.1): another form makes the answer a bad one. A seq that
                // isn't positive is no seq (the whole list next time).
                seq = 0;
                if (json.TryGetProperty("seq", out var sq) && sq.ValueKind != JsonValueKind.Null)
                {
                    if (!StrictJson.TryGetInteger(sq, out var s))
                    {
                        return ("The peer's titles answer has a seq that isn't an integer.", null);
                    }

                    seq = Math.Max(s, 0);
                }
            }
            else if (changes != isChanges)
            {
                return ("The peer mixed whole and changes pages in one reading.", null);
            }

            if (json.TryGetProperty("titles", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in list.EnumerateArray())
                {
                    if (ReadTitle(t, asked) is { } kept)
                    {
                        titles[kept.Key] = kept.Title;
                        gone.Remove(kept.Key);
                    }
                }
            }

            if (isChanges && json.TryGetProperty("gone", out var g) && g.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in g.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { } k && Encoding.UTF8.GetByteCount(k) <= MaxGoneKey)
                    {
                        gone.Add(k);
                        titles.Remove(k);
                    }
                }
            }

            if (titles.Count > MaxTitles)
            {
                return ($"The peer offers more than {MaxTitles} titles; the reading stopped.", null);
            }

            var next = json.TryGetProperty("next", out var nx) && nx.ValueKind == JsonValueKind.String ? nx.GetString() : null;
            if (string.IsNullOrEmpty(next) || string.CompareOrdinal(next, after) <= 0)
            {
                break;
            }

            after = next;
        }

        var fresh = new PeerCatalog { Libraries = libraries, Requested = requested, Seq = seq, ReadAt = _clock.GetUtcNow() };
        if (changes == true)
        {
            foreach (var (k, t) in old.Titles)
            {
                if (!gone.Contains(k) && asked.Contains(t.Library))
                {
                    fresh.Titles[k] = t;
                }
            }
        }

        foreach (var (k, t) in titles)
        {
            fresh.Titles[k] = t;
        }

        return (changes == true
            ? $"Read the changes: {titles.Count} new or changed, {gone.Count} gone; {fresh.Titles.Count} titles."
            : $"Read the whole list: {fresh.Titles.Count} titles.", fresh);
    }

    private static bool Refused(CallResult r, out string why)
    {
        why = r.Outcome switch
        {
            CallOutcome.Ok => string.Empty,
            CallOutcome.Refused => $"The peer refused ({r.Status}).",
            CallOutcome.CertificateRefused => "The peer's certificate is neither trusted nor named.",
            CallOutcome.Held => "The peer asked to wait (too many calls); the reading waits its turn.",
            _ => "The peer couldn't be reached, or its answer wasn't signed.",
        };
        return r.Outcome != CallOutcome.Ok || r.Json == null;
    }

    /// <summary>A signed 404 on libraries or titles: a hello at once (section 9.2).</summary>
    private async Task AfterRefusalAsync(Peer peer, CallResult r, CancellationToken ct)
    {
        if (r.Outcome == CallOutcome.Refused && r.Status == 404)
        {
            await _federation.HelloAsync(peer.Key, ct).ConfigureAwait(false);
        }
    }

    private static byte[] Body(List<string> requested, string after, long since)
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("libraries");
            foreach (var l in requested)
            {
                w.WriteStringValue(l);
            }

            w.WriteEndArray();
            w.WriteString("after", after);
            if (since > 0)
            {
                w.WriteNumber("since", since);
            }

            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    /// <summary>The libraries as section 9.3 says a caller keeps them.</summary>
    internal static List<PeerLibrary> ReadLibraries(JsonElement root)
    {
        var kept = new List<PeerLibrary>();
        if (!root.TryGetProperty("libraries", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return kept;
        }

        foreach (var e in list.EnumerateArray())
        {
            if (kept.Count == MaxLibraries)
            {
                break;
            }

            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idField) || idField.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            // 1 to 64 characters of A-Z a-z 0-9 _ - (section 9.3); of two
            // libraries with the same ID, the first.
            var id = idField.GetString()!;
            if (!IsLibraryId(id) || kept.Any(l => l.Id == id))
            {
                continue;
            }

            var name = e.TryGetProperty("name", out var nf) && nf.ValueKind == JsonValueKind.String ? nf.GetString() : null;
            var kind = e.TryGetProperty("kind", out var kf) && kf.ValueKind == JsonValueKind.String ? kf.GetString() : null;
            kind = kind is "movies" or "tvshows" or "music" or "musicvideos" or "homevideos" or "boxsets" or "books" or "photos" or "mixed" ? kind : string.Empty;
            kept.Add(new PeerLibrary(id, Names.Clean(name, "?"), kind));
        }

        return kept;
    }

    /// <summary>
    /// The member's name when it is there, not null, and of none of the
    /// kinds given; null otherwise (absent and null are fine).
    /// </summary>
    internal static string? WrongType(JsonElement obj, string name, params JsonValueKind[] kinds)
    {
        return obj.TryGetProperty(name, out var e) && e.ValueKind != JsonValueKind.Null && Array.IndexOf(kinds, e.ValueKind) < 0 ? name : null;
    }

    /// <summary>A library ID in section 9.3's form.</summary>
    internal static bool IsLibraryId(string id) =>
        id.Length is >= 1 and <= 64 && id.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    /// <summary>A title as section 9.4 says a caller keeps it; null to skip it.</summary>
    internal static (string Key, PeerTitle Title)? ReadTitle(JsonElement t, HashSet<string> asked)
    {
        if (t.ValueKind != JsonValueKind.Object
            || !t.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String
            || !t.TryGetProperty("kind", out var kd) || kd.ValueKind != JsonValueKind.String
            || !t.TryGetProperty("library", out var lib) || lib.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var key = k.GetString()!;
        if (!TitleKey.TryParse(key, out var kind) || kind != kd.GetString() || !asked.Contains(lib.GetString()!))
        {
            return null;
        }

        // Series and seasons carry no languages (section 9.4): whatever a
        // peer sends for them is dropped.
        var languages = kind is TitleKey.Movie or TitleKey.Episode;
        return (key, new PeerTitle(kind, lib.GetString()!, languages ? Tags(t, "audio") : new List<string>(), languages ? Tags(t, "subtitles") : new List<string>()));
    }

    private static List<string> Tags(JsonElement t, string name)
    {
        if (!t.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return Languages.Clean(list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null));
    }

    private static PeerCatalog Clone(PeerCatalog c) => new()
    {
        Libraries = c.Libraries.ToList(),
        Requested = c.Requested.ToList(),
        Seq = c.Seq,
        ReadAt = c.ReadAt,
        LastResult = c.LastResult,
        Titles = new SortedDictionary<string, PeerTitle>(c.Titles, StringComparer.Ordinal),
    };
}
