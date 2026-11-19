using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Msfp.Identification;

/// <summary>
/// Matching: which TMDB title a film or a series of the libraries is, from
/// what its files say. An ID written in its folder or file name comes
/// first; otherwise TMDB is searched by its name and year, and its first
/// candidates weighed against the files. The best is taken; when it isn't
/// clearly the one, it's said why (<see cref="Result.Sure"/>,
/// <see cref="Result.Reasons"/>), for an admin to check. Ported from
/// Overfin's identify/match.go.
/// </summary>
internal static class Matcher
{
    // How many of TMDB's search results are looked at closely (their pages
    // fetched).
    private const int CandidatesWeighed = 5;

    // The scores: a name that is one of the title's counts most; then the
    // year, a film's length or a series' seasons, its language among the
    // audio's, and how known it is. A candidate is sure from SureScore with
    // SureMargin over the next.
    private const int NameScore = 50, NameInScore = 25;
    private const int YearScore = 20, YearNearScore = 15;
    private const int YearOffScore = -20;
    private const int FitScore = 15, MisfitScore = -25;
    private const int LanguageScore = 10;
    private const int KnownMost = 5;
    private const int SureScore = 60, SureMargin = 15;

    /// <summary>Finds which TMDB title the files are.</summary>
    /// <exception cref="ArgumentException">The files are neither a film nor a series.</exception>
    public static async Task<Result> MatchAsync(ITmdbSource src, Files f, CancellationToken ct = default)
    {
        if (f.Kind != "movie" && f.Kind != "series")
        {
            throw new ArgumentException($"identify: not a film or a series: \"{f.Kind}\"", nameof(f));
        }

        var n = Names.Parse(f.Folder);
        if (f.Kind == "movie")
        {
            var file = Names.Parse(f.File);
            if (n.Name.Length == 0)
            {
                n = n with { Name = file.Name, Year = file.Year };
            }

            n = n with { Tmdb = Or(n.Tmdb, file.Tmdb), Imdb = Or(n.Imdb, file.Imdb), Tvdb = Or(n.Tvdb, file.Tvdb) };
        }

        var id = await ByIdAsync(src, f.Kind, n, ct).ConfigureAwait(false);
        if (id.Length > 0)
        {
            // Written by whoever named the files: taken, and checked like any
            // match; what contradicts it is said, never undone.
            var full = await PageAsync(src, f.Kind, id, ct).ConfigureAwait(false);
            if (full is not null)
            {
                var c = ToCandidate(full) with { Score = Score(n, f, full, 0) };
                var reasons = Doubts.Checks(ToLibraryTitle(f, full), n);
                return new Result { Tmdb = id, Sure = reasons.Count == 0, Reasons = reasons, Candidates = [c] };
            }

            // TMDB doesn't know that ID (as this kind): searched for instead.
        }

        return await SearchAsync(src, n, f, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The TMDB ID the names carry, directly or through IMDb's or TVDB's; ""
    /// without one.
    /// </summary>
    private static async Task<string> ByIdAsync(ITmdbSource src, string kind, Named n, CancellationToken ct)
    {
        if (n.Tmdb.Length > 0)
        {
            return n.Tmdb;
        }

        foreach (var (source, id) in new[] { ("imdb", n.Imdb), ("tvdb", n.Tvdb) })
        {
            if (id.Length == 0)
            {
                continue;
            }

            var found = await src.FindAsync(kind, source, id, ct).ConfigureAwait(false);
            if (found > 0)
            {
                return found.ToString(CultureInfo.InvariantCulture);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Looks for the title by its name: with its year, then without when
    /// nothing came; its first candidates weighed, the best taken.
    /// </summary>
    private static async Task<Result> SearchAsync(ITmdbSource src, Named n, Files f, CancellationToken ct)
    {
        if (n.Name.Length == 0)
        {
            return new Result { Reasons = [new Reason("no-name", "Its folder and file say no name to search for.")] };
        }

        var found = await src.SearchKindAsync(f.Kind, n.Name, n.Year, ct).ConfigureAwait(false);
        if (found.Count == 0 && n.Year > 0)
        {
            found = await src.SearchKindAsync(f.Kind, n.Name, 0, ct).ConfigureAwait(false);
        }

        var nothing = new Result { Reasons = [new Reason("no-tmdb", $"TMDB found nothing named “{n.Name}”.")] };
        var candidates = new List<Candidate>();
        for (var i = 0; i < Math.Min(found.Count, CandidatesWeighed); i++)
        {
            var hit = found[i];
            var full = await PageAsync(src, f.Kind, hit.Id.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            if (full is null)
            {
                continue;
            }

            candidates.Add(ToCandidate(full) with { Score = Score(n, f, full, i) + Known(hit.Votes) });
        }

        if (candidates.Count == 0)
        {
            return nothing;
        }

        // The best first; equals keep TMDB's order (OrderByDescending is stable).
        candidates = [.. candidates.OrderByDescending(c => c.Score)];
        var best = candidates[0];
        var sure = best.Score >= SureScore && (candidates.Count == 1 || best.Score - candidates[1].Score >= SureMargin);
        IReadOnlyList<Reason> reasons = [];
        if (!sure && best.Score < SureScore)
        {
            reasons = [new Reason("unsure", $"No title on TMDB clearly fits “{n.Name}”; the closest is taken.")];
        }
        else if (!sure)
        {
            var next = candidates[1];
            reasons = [new Reason("several", $"Several titles on TMDB fit: “{best.Name}” ({Doubts.YearText(best.Year)}) and “{next.Name}” ({Doubts.YearText(next.Year)}).")];
        }

        return new Result { Tmdb = best.Tmdb, Sure = sure, Reasons = reasons, Candidates = candidates };
    }

    /// <summary>Weighs a candidate (TMDB's rank-th result) against the names and files.</summary>
    private static int Score(Named n, Files f, TmdbTitle full, int rank)
    {
        var s = 0;
        var lt = ToLibraryTitle(f, full);
        var want = Doubts.Simple(n.Name);
        if (want.Length > 0)
        {
            if (Doubts.ExactName(want, lt))
            {
                s += NameScore;
            }
            else if (Doubts.Matches(n.Name, lt))
            {
                s += NameInScore;
            }
        }

        if (n.Year > 0 && full.Year > 0)
        {
            s += Math.Abs(n.Year - full.Year) switch
            {
                0 => YearScore,
                1 => YearNearScore,
                _ => YearOffScore,
            };
        }

        // A film's length, a series' seasons: as Identification's doubts
        // judge them.
        if (f.Kind == "movie" && f.RuntimeMs > 0 && full.Runtime > 0)
        {
            s += Doubts.RuntimeDoubt(lt) is null ? FitScore : MisfitScore;
        }

        if (f.Kind == "series" && f.Episodes.Count > 0 && full.Seasons.Count > 0)
        {
            s += Doubts.Seasons(lt) is null ? FitScore : MisfitScore;
        }

        // Dubbed releases are many: the original language among the audio's
        // counts for it, its absence not against it.
        if (full.Language.Length > 0 && f.Audio.Any(a => Languages.Same(a, full.Language)))
        {
            s += LanguageScore;
        }

        // TMDB's order, for equals: its first results a little ahead.
        return s + Math.Max(0, 3 - rank);
    }

    /// <summary>
    /// A little more for titles many people voted on: a spin-off or a TV
    /// special rarely has as many as the work itself.
    /// </summary>
    internal static int Known(int votes) => Math.Min(KnownMost, (int)Math.Log10(Math.Max(votes, 0) + 1.0));

    /// <summary>A title's page on TMDB; null when there's none.</summary>
    private static async Task<TmdbTitle?> PageAsync(ITmdbSource src, string kind, string id, CancellationToken ct)
    {
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return null;
        }

        return await src.TitleAsync(kind, n, ct).ConfigureAwait(false);
    }

    private static Candidate ToCandidate(TmdbTitle full) =>
        new(full.Id.ToString(CultureInfo.InvariantCulture), full.Name, full.Year, full.OriginalName, full.Language, 0);

    /// <summary>The files taken for full, as Identification's doubts read them.</summary>
    internal static LibraryTitle ToLibraryTitle(Files f, TmdbTitle full) => new()
    {
        Kind = f.Kind,
        FolderName = f.Folder,
        FileName = f.File,
        RuntimeMs = f.RuntimeMs,
        Tmdb = full.Id.ToString(CultureInfo.InvariantCulture),
        TmdbName = full.Name,
        TmdbYear = full.Year,
        Episodes = f.Episodes,
        Details = new TitleDetails
        {
            Runtime = full.Runtime,
            OriginalName = full.OriginalName,
            Language = full.Language,
            Names = full.Names,
            Seasons = full.Seasons,
        },
    };

    private static string Or(string a, string b) => a.Length > 0 ? a : b;
}
