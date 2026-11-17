using System.Globalization;
using System.Text.RegularExpressions;

namespace Msfp;

/// <summary>
/// Title keys (MSFP section 11): films, series, seasons and episodes named
/// by TMDB IDs, in canonical form.
/// </summary>
public static partial class TitleKey
{
    public const string Movie = "movie";
    public const string Series = "series";
    public const string Season = "season";
    public const string Episode = "episode";

    /// <summary>The largest TMDB ID of a film or series.</summary>
    public const long MaxId = 9_999_999_999;

    // Canonical forms only: no leading zeros (except a lone 0 where 0 is allowed).
    [GeneratedRegex(@"^movie/tmdb/([1-9][0-9]{0,9})$", RegexOptions.CultureInvariant)]
    private static partial Regex MoviePattern();

    [GeneratedRegex(@"^series/tmdb/([1-9][0-9]{0,9})(/s(0|[1-9][0-9]{0,3})(/e(0|[1-9][0-9]{0,4}))?|/e([1-9][0-9]{0,9}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex SeriesPattern();

    /// <summary>The kind a key names, when it is a canonical title key.</summary>
    public static bool TryParse(string? key, out string kind)
    {
        kind = string.Empty;
        if (key == null || key.Length > 64)
        {
            return false;
        }

        if (MoviePattern().IsMatch(key))
        {
            kind = Movie;
            return true;
        }

        var m = SeriesPattern().Match(key);
        if (!m.Success)
        {
            return false;
        }

        // Every ID below the regex's 10 digits is within 9,999,999,999.
        kind = m.Groups[6].Success ? Episode
            : m.Groups[5].Success ? Episode
            : m.Groups[3].Success ? Season
            : Series;
        return true;
    }

    /// <summary>A TMDB ID as a provider gives it, made canonical; null when it isn't one.</summary>
    public static string? CanonicalId(string? id, long max = MaxId)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 10)
        {
            return null;
        }

        foreach (var c in id)
        {
            if (c < '0' || c > '9')
            {
                return null;
            }
        }

        var n = long.Parse(id, CultureInfo.InvariantCulture);
        return n is > 0 && n <= max ? n.ToString(CultureInfo.InvariantCulture) : null;
    }

    public static string ForMovie(string id) => $"movie/tmdb/{id}";

    public static string ForSeries(string id) => $"series/tmdb/{id}";

    public static string ForSeason(string id, int season) => string.Create(CultureInfo.InvariantCulture, $"series/tmdb/{id}/s{season}");

    public static string ForEpisode(string id, int season, int episode) => string.Create(CultureInfo.InvariantCulture, $"series/tmdb/{id}/s{season}/e{episode}");

    public static string ForEpisodeId(string id, string episodeId) => $"series/tmdb/{id}/e{episodeId}";
}
