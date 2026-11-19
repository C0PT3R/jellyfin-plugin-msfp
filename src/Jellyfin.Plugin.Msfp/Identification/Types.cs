using System.Collections.Generic;

namespace Jellyfin.Plugin.Msfp.Identification;

// The types identification works with, ported from Overfin's
// internal/identify, internal/store and internal/tmdb (the parts it uses).
// Kinds are "movie" and "series", as in Overfin.

/// <summary>
/// What a folder or a file name says: a title, its year, and the IDs it
/// carries ("[tmdbid-949]", "[imdbid-tt0113277]", "[tvdbid-81189]", as
/// Jellyfin, Radarr and Sonarr write them). Empty strings and 0 for none.
/// </summary>
internal readonly record struct Named(string Name = "", int Year = 0, string Tmdb = "", string Imdb = "", string Tvdb = "");

/// <summary>Why a title may be wrong, as a code and in words.</summary>
/// <param name="Code">no-tmdb, no-name, tmdb-tag, name, year, seasons, runtime, unsure, several.</param>
/// <param name="Text">The reason in words, for an admin.</param>
internal sealed record Reason(string Code, string Text);

/// <summary>What the libraries know of a film or a series, from its files alone.</summary>
internal sealed class Files
{
    /// <summary>movie or series.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>The last name of the folder it is in.</summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>The last name of its file (a film's).</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>A film's length; 0 when unknown.</summary>
    public long RuntimeMs { get; init; }

    /// <summary>The languages of its audio tracks, as files write them ("fre", "eng"…).</summary>
    public IReadOnlyList<string> Audio { get; init; } = [];

    /// <summary>A series' episodes counted by season.</summary>
    public IReadOnlyDictionary<int, int> Episodes { get; init; } = new Dictionary<int, int>();
}

/// <summary>A TMDB title weighed for a film or a series.</summary>
internal sealed record Candidate(string Tmdb, string Name, int Year, string OriginalName, string Language, int Score);

/// <summary>
/// What matching found: the TMDB title taken ("" for none), whether it's
/// clearly the one, why not when it isn't, and the candidates weighed, best
/// first.
/// </summary>
internal sealed class Result
{
    public string Tmdb { get; init; } = string.Empty;

    public bool Sure { get; init; }

    public IReadOnlyList<Reason> Reasons { get; init; } = [];

    public IReadOnlyList<Candidate> Candidates { get; init; } = [];
}

/// <summary>A season as TMDB lists it.</summary>
internal sealed record SeasonInfo(int Number, string Name, int EpisodeCount);

/// <summary>What TMDB says of the title a library title is taken for.</summary>
internal sealed class TitleDetails
{
    /// <summary>In minutes: a film's, or a series' usual episode's; 0 when TMDB doesn't say.</summary>
    public int Runtime { get; init; }

    public string OriginalName { get; init; } = string.Empty;

    /// <summary>Its original language (ISO 639-1).</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Its other names: alternative titles and translations.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    public IReadOnlyList<SeasonInfo> Seasons { get; init; } = [];
}

/// <summary>
/// A film or a series of the libraries and the TMDB title it is taken for,
/// as Identification's doubts read it.
/// </summary>
internal sealed class LibraryTitle
{
    public string Kind { get; init; } = string.Empty;

    public string FolderName { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public long RuntimeMs { get; init; }

    /// <summary>The TMDB ID it is taken for; "" for none.</summary>
    public string Tmdb { get; init; } = string.Empty;

    public string TmdbName { get; init; } = string.Empty;

    public int TmdbYear { get; init; }

    /// <summary>A series' episodes counted by season.</summary>
    public IReadOnlyDictionary<int, int> Episodes { get; init; } = new Dictionary<int, int>();

    /// <summary>Chosen or confirmed by an admin: no doubts are raised.</summary>
    public bool Overridden { get; init; }

    public TitleDetails Details { get; init; } = new();
}

/// <summary>A search result from TMDB.</summary>
internal sealed record TmdbFound(int Id, string Kind, string Name, int Year, int Votes);

/// <summary>A title's page on TMDB, what identification reads of it.</summary>
internal sealed class TmdbTitle
{
    public int Id { get; init; }

    public string Kind { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>A film's release year or a series' first; 0 when unknown.</summary>
    public int Year { get; init; }

    public string OriginalName { get; init; } = string.Empty;

    /// <summary>Its original language (ISO 639-1), or "".</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>In minutes: a film's, or a series' usual episode's; 0 when TMDB doesn't say.</summary>
    public int Runtime { get; init; }

    /// <summary>How many of TMDB's users rated it.</summary>
    public int Votes { get; init; }

    /// <summary>Its other names: alternative titles and translations, each once.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    public IReadOnlyList<SeasonInfo> Seasons { get; init; } = [];

    /// <summary>Its IMDb ID ("tt…"), or "".</summary>
    public string Imdb { get; init; } = string.Empty;

    /// <summary>A series' TheTVDB ID; 0 when unknown.</summary>
    public int Tvdb { get; init; }
}

/// <summary>An episode of a season, as TMDB numbers it.</summary>
internal sealed record TmdbEpisode(int Number, int Id, string Name);

/// <summary>An episode an IMDb or TVDB ID names on TMDB: its series, its place in TMDB's order, its own ID.</summary>
internal sealed record TmdbEpisodeFound(int SeriesId, int Season, int Number, int EpisodeId);
