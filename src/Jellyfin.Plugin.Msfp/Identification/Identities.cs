using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Msfp.Identification;

/// <summary>An episode as TMDB has it: its ID and its place in TMDB's numbering.</summary>
internal sealed record EpisodeIdentity(int EpisodeId, int Season, int Episode);

/// <summary>
/// What the plugin found a film or a series of the libraries to be: the
/// TMDB title matched, whether it's clearly the one, why not, the candidates
/// weighed, and the owner's choice, which always wins. A series also keeps
/// its episodes, by the files' numbers.
/// </summary>
internal sealed class TitleIdentity
{
    public string Kind { get; set; } = string.Empty;

    /// <summary>Jellyfin's name of it, for the page.</summary>
    public string Name { get; set; } = string.Empty;

    public string Folder { get; set; } = string.Empty;

    public string File { get; set; } = string.Empty;

    /// <summary>What the match was made from; another value means its files changed, and it's matched again.</summary>
    public string Inputs { get; set; } = string.Empty;

    /// <summary>The TMDB title matched; "" for none.</summary>
    public string Tmdb { get; set; } = string.Empty;

    public bool Sure { get; set; }

    public List<Reason> Reasons { get; set; } = [];

    public List<Candidate> Candidates { get; set; } = [];

    /// <summary>The TMDB title the owner chose; "" when they didn't.</summary>
    public string Chosen { get; set; } = string.Empty;

    /// <summary>The chosen title's name and year on TMDB, for the page.</summary>
    public string ChosenName { get; set; } = string.Empty;

    /// <summary>The TMDB title's IMDb and TheTVDB IDs, as TMDB gives them.</summary>
    public string Imdb { get; set; } = string.Empty;

    public string Tvdb { get; set; } = string.Empty;

    /// <summary>A series' episodes by the files' numbers ("1/5"): their TMDB ID and place.</summary>
    public Dictionary<string, EpisodeIdentity> Episodes { get; set; } = [];

    /// <summary>The TMDB series the episodes were mapped for, and the files they were mapped from.</summary>
    public long MappedFor { get; set; }

    public string EpisodeFiles { get; set; } = string.Empty;

    /// <summary>When it was matched last.</summary>
    public DateTimeOffset Checked { get; set; }

    /// <summary>
    /// The TMDB title it is, for sharing: the owner's choice, else the
    /// match when it's clearly the one; null when neither.
    /// </summary>
    [JsonIgnore]
    public long? Taken => Chosen.Length > 0 ? Number(Chosen) : Sure ? Number(Tmdb) : null;

    /// <summary>Waiting for the owner: a match that isn't clearly the one, or none.</summary>
    [JsonIgnore]
    public bool ToCheck => Chosen.Length == 0 && !Sure;

    public TitleIdentity Copy()
    {
        var c = (TitleIdentity)MemberwiseClone();
        c.Reasons = [.. Reasons];
        c.Candidates = [.. Candidates];
        c.Episodes = new Dictionary<string, EpisodeIdentity>(Episodes, StringComparer.Ordinal);
        return c;
    }

    /// <summary>Whether it says the same as another (as stored).</summary>
    public bool SameAs(TitleIdentity other) =>
        JsonSerializer.Serialize(this, Identities.Options) == JsonSerializer.Serialize(other, Identities.Options);

    public static string EpisodeKey(int season, int episode) => season.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + episode.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static long? Number(string id) => long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
}

/// <summary>What changed in the identities.</summary>
internal sealed class IdentitiesChangedEventArgs : EventArgs
{
    public static readonly IdentitiesChangedEventArgs Matched = new(false);

    public IdentitiesChangedEventArgs(bool chosen)
    {
        Chosen = chosen;
    }

    /// <summary>
    /// The owner's choice on the plugin's page: shared at once, not after
    /// the libraries settle as a pass's matches are.
    /// </summary>
    public bool Chosen { get; }
}

/// <summary>
/// The plugin's identities of the libraries' films and series, by
/// Jellyfin's item ID, in identities.json in its data folder (readable by
/// its owner only, as the engine's records).
/// </summary>
internal sealed class Identities
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly string? _path;
    private readonly object _lock = new();
    private Dictionary<string, TitleIdentity>? _all;

    /// <param name="path">The file; null keeps them in memory only (tests).</param>
    public Identities(string? path)
    {
        _path = path;
    }

    /// <summary>Changed: what the catalog reads may differ.</summary>
    public event EventHandler<IdentitiesChangedEventArgs>? Changed;

    public static Identities InDataFolder() =>
        new(Plugin.Instance is { } p ? Path.Combine(p.DataFolderPath, "identities.json") : null);

    public TitleIdentity? Of(Guid item)
    {
        lock (_lock)
        {
            return All().TryGetValue(item.ToString("N"), out var t) ? t : null;
        }
    }

    /// <summary>Every identity, by item ID (a copy).</summary>
    public IReadOnlyDictionary<string, TitleIdentity> Snapshot()
    {
        lock (_lock)
        {
            return new Dictionary<string, TitleIdentity>(All(), StringComparer.Ordinal);
        }
    }

    /// <param name="item">The film or series.</param>
    /// <param name="identity">Its identity.</param>
    /// <param name="chosen">The owner's choice (shared at once), not a pass's match.</param>
    public void Put(Guid item, TitleIdentity identity, bool chosen = false)
    {
        lock (_lock)
        {
            All()[item.ToString("N")] = identity;
            Save();
        }

        Changed?.Invoke(this, chosen ? new IdentitiesChangedEventArgs(true) : IdentitiesChangedEventArgs.Matched);
    }

    /// <summary>Forgets the items no longer in the libraries.</summary>
    public void KeepOnly(IReadOnlySet<string> items)
    {
        bool removed;
        lock (_lock)
        {
            var gone = All().Keys.Where(k => !items.Contains(k)).ToList();
            gone.ForEach(k => All().Remove(k));
            removed = gone.Count > 0;
            if (removed)
            {
                Save();
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, IdentitiesChangedEventArgs.Matched);
        }
    }

    private Dictionary<string, TitleIdentity> All()
    {
        if (_all is not null)
        {
            return _all;
        }

        _all = new Dictionary<string, TitleIdentity>(StringComparer.Ordinal);
        if (_path is not null && File.Exists(_path))
        {
            try
            {
                _all = JsonSerializer.Deserialize<Dictionary<string, TitleIdentity>>(File.ReadAllBytes(_path), Options) is { } read
                    ? new Dictionary<string, TitleIdentity>(read, StringComparer.Ordinal)
                    : _all;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // Unreadable: matched again, the owner's choices lost with it.
            }
        }

        return _all;
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_all, Options));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, _path, true);
    }
}
