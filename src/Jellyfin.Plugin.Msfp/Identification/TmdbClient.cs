using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Msfp.Identification;

/// <summary>
/// TMDB's API (v3), as identification asks it. Answers are kept on disk,
/// one file per request, for <see cref="KeptFor"/> at most (TMDB's terms
/// forbid keeping its data longer than six months); what TMDB doesn't know
/// for <see cref="NotFoundKeptFor"/>. Requests are paced: at most 20 a
/// second and 4 at once, over all clients. The key is sent and never
/// written anywhere: not in the cache's names or files, not in errors.
/// </summary>
internal sealed partial class TmdbClient : ITmdbSource, IDisposable
{
    /// <summary>How long an answer is kept.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(150);

    /// <summary>How long a not-found answer is kept.</summary>
    public static readonly TimeSpan NotFoundKeptFor = TimeSpan.FromDays(7);

    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const int Retries = 3;
    private const int MaxNames = 60;

    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    // The pace, shared by every client: one request every 50 ms at most
    // (20 a second), 4 at once.
    private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(50);
    private static readonly SemaphoreSlim AtOnce = new(4, 4);
    private static readonly Lock PaceLock = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static TimeSpan _nextSlot = TimeSpan.Zero;

    private readonly HttpClient _http;
    private readonly string _key;
    private readonly bool _bearer;
    private readonly string _cacheFolder;
    private readonly string _language;
    private readonly TimeProvider _time;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    /// <summary>A client of TMDB.</summary>
    /// <param name="apiKey">A v3 API key, or a v4 read access token (sent as a Bearer token).</param>
    /// <param name="cacheFolder">Where answers are kept; made when needed.</param>
    /// <param name="language">The language of names and texts asked for.</param>
    /// <param name="handler">For tests: what sends the requests; a SocketsHttpHandler without cookies otherwise.</param>
    /// <param name="time">For tests: the clock the cache's ages are measured by.</param>
    public TmdbClient(string apiKey, string cacheFolder, string language = "en-US", HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        _key = apiKey.Trim();
        // A v4 token is a JWT: long, in dot-separated parts. A v3 key is 32
        // hexadecimal digits.
        _bearer = _key.Length > 40 && _key.Contains('.', StringComparison.Ordinal);
        _cacheFolder = cacheFolder;
        _language = language;
        _time = time ?? TimeProvider.System;
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TmdbFound>> SearchKindAsync(string kind, string query, int year, CancellationToken ct = default)
    {
        var k = KindPath(kind);
        if (k.Length == 0 || string.IsNullOrWhiteSpace(query) || year < 0 || year > 9999)
        {
            return [];
        }

        var q = new Dictionary<string, string> { ["query"] = query, ["include_adult"] = "false", ["language"] = _language };
        if (year > 0)
        {
            q[kind == "movie" ? "year" : "first_air_date_year"] = year.ToString(CultureInfo.InvariantCulture);
        }

        var raw = await GetAsync<RawSearch>("/search/" + k, q, ct).ConfigureAwait(false);
        var output = new List<TmdbFound>();
        foreach (var r in raw?.Results ?? [])
        {
            var name = Plain(kind == "movie" ? r.Title : r.Name);
            if (ValidId(r.Id) && name.Length > 0)
            {
                output.Add(new TmdbFound(r.Id, kind, name, YearOf(kind == "movie" ? r.ReleaseDate : r.FirstAirDate), Math.Max(r.VoteCount ?? 0, 0)));
            }
        }

        return output;
    }

    /// <inheritdoc />
    public async Task<TmdbTitle?> TitleAsync(string kind, int tmdbId, CancellationToken ct = default)
    {
        var k = KindPath(kind);
        if (k.Length == 0 || !ValidId(tmdbId))
        {
            return null;
        }

        var q = new Dictionary<string, string>
        {
            ["append_to_response"] = "alternative_titles,translations,external_ids",
            ["language"] = _language,
        };
        var raw = await GetAsync<RawTitle>($"/{k}/{tmdbId.ToString(CultureInfo.InvariantCulture)}", q, ct).ConfigureAwait(false);
        var movie = kind == "movie";
        var name = Plain(movie ? raw?.Title : raw?.Name);
        if (raw is null || raw.Id != tmdbId || name.Length == 0)
        {
            return null;
        }

        var original = Plain(movie ? raw.OriginalTitle : raw.OriginalName);
        var runtime = movie ? raw.Runtime ?? 0 : raw.EpisodeRunTime?.FirstOrDefault() ?? 0;

        // Its other names, each once: alternative titles, then translations.
        var seen = new HashSet<string>(StringComparer.Ordinal) { name, original };
        var names = new List<string>();
        void Add(string? n)
        {
            n = Plain(n);
            if (n.Length > 0 && names.Count < MaxNames && seen.Add(n))
            {
                names.Add(n);
            }
        }

        foreach (var a in (raw.AlternativeTitles?.Titles ?? []).Concat(raw.AlternativeTitles?.Results ?? []))
        {
            Add(a.Title);
        }

        foreach (var tr in raw.Translations?.Translations ?? [])
        {
            Add(tr.Data?.Title);
            Add(tr.Data?.Name);
        }

        var seasons = new List<SeasonInfo>();
        foreach (var s in raw.Seasons ?? [])
        {
            var count = s.EpisodeCount ?? 0;
            if (s.SeasonNumber is >= 0 and <= 1000 && count is >= 0 and <= 10_000)
            {
                seasons.Add(new SeasonInfo(s.SeasonNumber.Value, Plain(s.Name, 120), count));
            }
        }

        var imdb = raw.ExternalIds?.ImdbId ?? string.Empty;
        var tvdb = raw.ExternalIds?.TvdbId ?? 0;
        return new TmdbTitle
        {
            Id = tmdbId,
            Kind = kind,
            Name = name,
            Year = YearOf(movie ? raw.ReleaseDate : raw.FirstAirDate),
            OriginalName = original,
            Language = IsoLanguage().IsMatch(raw.OriginalLanguage ?? string.Empty) ? raw.OriginalLanguage! : string.Empty,
            Runtime = runtime is > 0 and < 10_000 ? runtime : 0,
            Votes = Math.Max(raw.VoteCount ?? 0, 0),
            Names = names,
            Seasons = seasons,
            Imdb = ImdbId().IsMatch(imdb) ? imdb : string.Empty,
            Tvdb = !movie && tvdb is > 0 and < 1_000_000_000 ? tvdb : 0,
        };
    }

    /// <inheritdoc />
    public async Task<int> FindAsync(string kind, string source, string id, CancellationToken ct = default)
    {
        if (kind != "movie" && kind != "series")
        {
            return 0;
        }

        var raw = await FindRawAsync(source, id, ct).ConfigureAwait(false);
        var results = (kind == "movie" ? raw?.MovieResults : raw?.TvResults) ?? [];
        return results.Count > 0 && ValidId(results[0].Id) ? results[0].Id : 0;
    }

    /// <inheritdoc />
    public async Task<TmdbEpisodeFound?> FindEpisodeAsync(string source, string id, CancellationToken ct = default)
    {
        var raw = await FindRawAsync(source, id, ct).ConfigureAwait(false);
        foreach (var e in raw?.TvEpisodeResults ?? [])
        {
            if (ValidId(e.Id) && ValidId(e.ShowId) && e.SeasonNumber is >= 0 and <= 1000 && e.EpisodeNumber is >= 0 and <= 100_000)
            {
                return new TmdbEpisodeFound(e.ShowId, e.SeasonNumber, e.EpisodeNumber, e.Id);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TmdbEpisode>?> SeasonAsync(int seriesId, int season, CancellationToken ct = default)
    {
        if (!ValidId(seriesId) || season < 0 || season > 1000)
        {
            return null;
        }

        var path = $"/tv/{seriesId.ToString(CultureInfo.InvariantCulture)}/season/{season.ToString(CultureInfo.InvariantCulture)}";
        var raw = await GetAsync<RawSeason>(path, new Dictionary<string, string> { ["language"] = _language }, ct).ConfigureAwait(false);
        if (raw is null)
        {
            return null;
        }

        var output = new List<TmdbEpisode>();
        foreach (var e in raw.Episodes ?? [])
        {
            if (ValidId(e.Id) && e.EpisodeNumber is >= 0 and <= 100_000)
            {
                output.Add(new TmdbEpisode(e.EpisodeNumber, e.Id, Plain(e.Name, 300)));
            }
        }

        return output;
    }

    /// <summary>Removes the kept answers past their time.</summary>
    public void PruneCache()
    {
        if (!Directory.Exists(_cacheFolder))
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var file in Directory.EnumerateFiles(_cacheFolder, "*.json"))
        {
            if (ReadEntry(file) is not { } entry || Expired(entry, now))
            {
                TryDelete(file);
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<RawFind?> FindRawAsync(string source, string id, CancellationToken ct)
    {
        var param = source switch { "imdb" => "imdb_id", "tvdb" => "tvdb_id", _ => string.Empty };
        if (param.Length == 0 || !ExternalId().IsMatch(id))
        {
            return null;
        }

        return await GetAsync<RawFind>("/find/" + id, new Dictionary<string, string> { ["external_source"] = param }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// TMDB's answer at path, from the cache while it's fresh, read as T;
    /// null when TMDB doesn't know it (404).
    /// </summary>
    private async Task<T?> GetAsync<T>(string path, Dictionary<string, string> query, CancellationToken ct)
        where T : class
    {
        var body = await BodyAsync(path, query, ct).ConfigureAwait(false);
        if (body is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body);
        }
        catch (JsonException)
        {
            throw new TmdbException($"TMDB's answer to {path} isn't readable.");
        }
    }

    private async Task<string?> BodyAsync(string path, Dictionary<string, string> query, CancellationToken ct)
    {
        // The key is never in the query here: it's added when sending.
        var queryText = string.Join('&', query.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));
        var cacheFile = Path.Combine(_cacheFolder, Hash(path + "?" + queryText) + ".json");
        var now = _time.GetUtcNow();
        if (now - _lastPrune > TimeSpan.FromDays(1))
        {
            _lastPrune = now;
            PruneCache();
        }

        if (ReadEntry(cacheFile) is { } kept && !Expired(kept, now))
        {
            return kept.Found ? kept.Body : null;
        }

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path + "?" + queryText + (_bearer ? string.Empty : "&api_key=" + Uri.EscapeDataString(_key)));
            if (_bearer)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            await PaceAsync(ct).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            finally
            {
                AtOnce.Release();
            }

            using (response)
            {
                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        WriteEntry(cacheFile, new CacheEntry { At = _time.GetUtcNow(), Found = true, Body = body });
                        return body;
                    case HttpStatusCode.NotFound:
                        WriteEntry(cacheFile, new CacheEntry { At = _time.GetUtcNow(), Found = false });
                        return null;
                    case HttpStatusCode.TooManyRequests when attempt < Retries:
                        await Task.Delay(RetryAfter(response), ct).ConfigureAwait(false);
                        continue;
                    default:
                        throw new TmdbException($"TMDB answered {(int)response.StatusCode} to {path}.");
                }
            }
        }
    }

    /// <summary>Waits for a turn: a free place among 4, and 50 ms since the last request began.</summary>
    private static async Task PaceAsync(CancellationToken ct)
    {
        await AtOnce.WaitAsync(ct).ConfigureAwait(false);
        TimeSpan wait;
        lock (PaceLock)
        {
            var now = Clock.Elapsed;
            var slot = _nextSlot > now ? _nextSlot : now;
            _nextSlot = slot + Spacing;
            wait = slot - now;
        }

        if (wait > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch
            {
                AtOnce.Release();
                throw;
            }
        }
    }

    private TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var after = response.Headers.RetryAfter;
        var wait = after?.Delta ?? (after?.Date is { } date ? date - _time.GetUtcNow() : DefaultRetryAfter);
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }

    private static bool Expired(CacheEntry entry, DateTimeOffset now) =>
        now - entry.At >= (entry.Found ? KeptFor : NotFoundKeptFor) || entry.At > now + TimeSpan.FromDays(1);

    private static CacheEntry? ReadEntry(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void WriteEntry(string file, CacheEntry entry)
    {
        // A failure to keep it only costs a request next time.
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_cacheFolder);
            File.WriteAllText(temp, JsonSerializer.Serialize(entry));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next pruning.
        }
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    /// <summary>TMDB's word for a kind in paths; "" for another kind.</summary>
    private static string KindPath(string kind) => kind switch { "movie" => "movie", "series" => "tv", _ => string.Empty };

    private static bool ValidId(int id) => id > 0;

    /// <summary>The year of a YYYY-MM-DD date; 0 when there's none.</summary>
    private static int YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y > 1800 && y < 3000 ? y : 0;

    /// <summary>
    /// Text as TMDB gives it, made plain: control and bidirectional
    /// characters dropped, runs of white space made one space, cut to max
    /// characters.
    /// </summary>
    private static string Plain(string? s, int max = 300)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        var b = new StringBuilder(s.Length);
        var space = false;
        foreach (var r in s.EnumerateRunes())
        {
            if (r == Rune.ReplacementChar || IsBidiControl(r.Value))
            {
                continue;
            }

            if (Rune.IsControl(r) || Rune.IsWhiteSpace(r))
            {
                space = true;
                continue;
            }

            if (b.Length + r.Utf16SequenceLength + (space && b.Length > 0 ? 1 : 0) > max)
            {
                break;
            }

            if (space && b.Length > 0)
            {
                b.Append(' ');
            }

            space = false;
            b.Append(r.ToString());
        }

        return b.ToString();
    }

    private static bool IsBidiControl(int c) => c is 0x061C or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);

    [GeneratedRegex("^[a-z]{2}$")]
    private static partial Regex IsoLanguage();

    [GeneratedRegex("^tt[0-9]{5,10}$")]
    private static partial Regex ImdbId();

    [GeneratedRegex("^(tt)?[0-9]{1,12}$")]
    private static partial Regex ExternalId();

    private sealed class CacheEntry
    {
        [JsonPropertyName("at")]
        public DateTimeOffset At { get; set; }

        [JsonPropertyName("found")]
        public bool Found { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }

    private sealed class RawSearch
    {
        [JsonPropertyName("results")]
        public List<RawTitle>? Results { get; set; }
    }

    private sealed class RawTitle
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; set; }

        [JsonPropertyName("original_language")]
        public string? OriginalLanguage { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("vote_count")]
        public int? VoteCount { get; set; }

        [JsonPropertyName("runtime")]
        public int? Runtime { get; set; }

        [JsonPropertyName("episode_run_time")]
        public List<int>? EpisodeRunTime { get; set; }

        [JsonPropertyName("seasons")]
        public List<RawSeasonInfo>? Seasons { get; set; }

        // Films list their alternative titles under titles, series under
        // results.
        [JsonPropertyName("alternative_titles")]
        public RawAlternativeTitles? AlternativeTitles { get; set; }

        [JsonPropertyName("translations")]
        public RawTranslations? Translations { get; set; }

        [JsonPropertyName("external_ids")]
        public RawExternalIds? ExternalIds { get; set; }
    }

    private sealed class RawSeasonInfo
    {
        [JsonPropertyName("season_number")]
        public int? SeasonNumber { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("episode_count")]
        public int? EpisodeCount { get; set; }
    }

    private sealed class RawAlternativeTitles
    {
        [JsonPropertyName("titles")]
        public List<RawAlternativeTitle>? Titles { get; set; }

        [JsonPropertyName("results")]
        public List<RawAlternativeTitle>? Results { get; set; }
    }

    private sealed class RawAlternativeTitle
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }

    private sealed class RawTranslations
    {
        [JsonPropertyName("translations")]
        public List<RawTranslation>? Translations { get; set; }
    }

    private sealed class RawTranslation
    {
        [JsonPropertyName("data")]
        public RawTranslationData? Data { get; set; }
    }

    private sealed class RawTranslationData
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class RawExternalIds
    {
        [JsonPropertyName("imdb_id")]
        public string? ImdbId { get; set; }

        [JsonPropertyName("tvdb_id")]
        public int? TvdbId { get; set; }
    }

    private sealed class RawFind
    {
        [JsonPropertyName("movie_results")]
        public List<RawId>? MovieResults { get; set; }

        [JsonPropertyName("tv_results")]
        public List<RawId>? TvResults { get; set; }

        [JsonPropertyName("tv_episode_results")]
        public List<RawEpisodeFound>? TvEpisodeResults { get; set; }
    }

    private sealed class RawId
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    private sealed class RawEpisodeFound
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("show_id")]
        public int ShowId { get; set; }

        [JsonPropertyName("season_number")]
        public int SeasonNumber { get; set; }

        [JsonPropertyName("episode_number")]
        public int EpisodeNumber { get; set; }
    }

    private sealed class RawSeason
    {
        [JsonPropertyName("episodes")]
        public List<RawEpisode>? Episodes { get; set; }
    }

    private sealed class RawEpisode
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("episode_number")]
        public int EpisodeNumber { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}

/// <summary>A failure to get an answer from TMDB (never what it doesn't know).</summary>
internal sealed class TmdbException : Exception
{
    public TmdbException(string message)
        : base(message)
    {
    }
}
