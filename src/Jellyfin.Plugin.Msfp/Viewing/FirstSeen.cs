using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// When this server first saw each paired server's title, for "recently
/// added" (a stand-in's DateCreated), as Overfin keeps it: a title is new
/// from the day this server first saw it, so that a stand-in made again
/// (moved, gone and back) keeps its day; the titles of a paired server's
/// first reading count as old (what it held already isn't new here), and
/// get <see cref="Old"/>. Kept in the plugin's data folder, by stand-in
/// path; the first time, the days are taken from the stand-ins there are
/// (an update from a version without these records), and the paired
/// servers they came from count as read already. A title unseen for
/// <see cref="Forgotten"/> is forgotten.
/// </summary>
internal sealed class FirstSeen
{
    /// <summary>The day a title of a first reading gets: the Unix epoch, as Overfin's 0.</summary>
    public static readonly DateTime Old = DateTime.UnixEpoch;

    /// <summary>How long a title no paired server lists is remembered.</summary>
    public static readonly TimeSpan Forgotten = TimeSpan.FromDays(400);

    private Records _records = new();
    private bool _changed;

    /// <summary>Whether there were records (false before the first pass of a version that keeps them).</summary>
    public bool Existed { get; private set; }

    /// <summary>The paired servers whose titles were read here already.</summary>
    public IReadOnlyCollection<string> Peers => _records.Peers;

    public static string PathIn(string dataFolder) => Path.Combine(dataFolder, "paired", "first-seen.json");

    public static FirstSeen Load(string file)
    {
        var f = new FirstSeen();
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Records>(File.ReadAllBytes(file)) is { } r)
            {
                f._records = r;
                f.Existed = true;
            }
        }
        catch (JsonException)
        {
            // Unreadable: started again (the days are taken from the stand-ins).
        }

        return f;
    }

    /// <summary>Takes the day of a stand-in made before there were records, and its paired servers as read.</summary>
    public void Seed(string path, DateTime created)
    {
        if (_records.Titles.TryAdd(path, new Seen { First = created }))
        {
            _changed = true;
        }
    }

    /// <summary>Counts paired servers as read already: their next titles are new.</summary>
    public void Read(IEnumerable<string> peers)
    {
        foreach (var p in peers)
        {
            if (!_records.Peers.Contains(p))
            {
                _records.Peers.Add(p);
                _changed = true;
            }
        }
    }

    /// <summary>
    /// The day a stand-in shows as added: when this server first saw the
    /// title; now for a title first seen now from a paired server read
    /// before, <see cref="Old"/> for one of a first reading.
    /// </summary>
    public DateTime Of(string path, IEnumerable<string> holders, DateTime now)
    {
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_records.Titles.TryGetValue(path, out var seen))
        {
            if (seen.Last != day)
            {
                seen.Last = day;
                _changed = true;
            }

            return DateTime.SpecifyKind(seen.First, DateTimeKind.Utc);
        }

        var first = holders.Any(_records.Peers.Contains) ? now : Old;
        _records.Titles[path] = new Seen { First = first, Last = day };
        _changed = true;
        return first;
    }

    /// <summary>Forgets the titles unseen for <see cref="Forgotten"/>.</summary>
    public void Prune(DateTime now)
    {
        var before = (now - Forgotten).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var (path, seen) in _records.Titles.ToList())
        {
            if (seen.Last is { Length: > 0 } last && string.CompareOrdinal(last, before) < 0)
            {
                _records.Titles.Remove(path);
                _changed = true;
            }
        }
    }

    /// <summary>Writes the records when they changed.</summary>
    public void Save(string file)
    {
        if (!_changed && Existed)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_records));
        File.Move(tmp, file, true);
        _changed = false;
        Existed = true;
    }

    private sealed class Records
    {
        [JsonPropertyName("peers")]
        public HashSet<string> Peers { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("titles")]
        public Dictionary<string, Seen> Titles { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class Seen
    {
        [JsonPropertyName("first")]
        public DateTime First { get; set; }

        [JsonPropertyName("last")]
        public string? Last { get; set; }
    }
}
