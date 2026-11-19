using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Msfp.Identification;

/// <summary>
/// TMDB, as identification asks it. What TMDB doesn't know is an empty
/// answer (an empty list, null or 0), never an exception; other failures
/// throw.
/// </summary>
internal interface ITmdbSource
{
    /// <summary>
    /// Films ("movie") or series ("series") whose names match query, in
    /// TMDB's order of relevance (its first page): from year only, when year
    /// isn't 0 (a series' first year).
    /// </summary>
    Task<IReadOnlyList<TmdbFound>> SearchKindAsync(string kind, string query, int year, CancellationToken ct = default);

    /// <summary>A title's page; null when TMDB has no such title of that kind.</summary>
    Task<TmdbTitle?> TitleAsync(string kind, int tmdbId, CancellationToken ct = default);

    /// <summary>
    /// The TMDB ID of the film or series an IMDb ("imdb") or TVDB ("tvdb")
    /// ID names; 0 when none.
    /// </summary>
    Task<int> FindAsync(string kind, string source, string id, CancellationToken ct = default);

    /// <summary>The episode an IMDb or TVDB ID names; null when none.</summary>
    Task<TmdbEpisodeFound?> FindEpisodeAsync(string source, string id, CancellationToken ct = default);

    /// <summary>A season's episodes, in TMDB's numbering; null when TMDB has no such season.</summary>
    Task<IReadOnlyList<TmdbEpisode>?> SeasonAsync(int seriesId, int season, CancellationToken ct = default);
}
