using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.Msfp.Identification;

/// <summary>
/// Which films and series of the libraries may not be the TMDB title
/// they're taken for (Jellyfin matched them wrong), and why. Each reason is
/// a sign, not a proof: an admin looks, and fixes or confirms. Ported from
/// Overfin's identify/identify.go.
/// </summary>
internal static class Doubts
{
    // Folded are letters with accents, and what they fold to (Overfin's
    // store.Fold).
    private static readonly Dictionary<char, string> Folded = new()
    {
        ['à'] = "a", ['á'] = "a", ['â'] = "a", ['ã'] = "a", ['ä'] = "a", ['å'] = "a", ['ā'] = "a", ['ç'] = "c",
        ['è'] = "e", ['é'] = "e", ['ê'] = "e", ['ë'] = "e", ['ē'] = "e", ['ì'] = "i", ['í'] = "i", ['î'] = "i", ['ï'] = "i", ['ī'] = "i",
        ['ñ'] = "n", ['ò'] = "o", ['ó'] = "o", ['ô'] = "o", ['õ'] = "o", ['ö'] = "o", ['ø'] = "o", ['ō'] = "o",
        ['ù'] = "u", ['ú'] = "u", ['û'] = "u", ['ü'] = "u", ['ū'] = "u", ['ý'] = "y", ['ÿ'] = "y",
        ['æ'] = "ae", ['œ'] = "oe", ['ß'] = "ss",
    };

    /// <summary>
    /// Why t may not be the TMDB title it's taken for; none when nothing
    /// suggests it, when an admin chose or confirmed it, or when its folder
    /// names its TMDB ID.
    /// </summary>
    public static IReadOnlyList<Reason> Reasons(LibraryTitle t)
    {
        if (t.Overridden)
        {
            return [];
        }

        if (t.Tmdb.Length == 0)
        {
            return [new Reason("no-tmdb", "Not matched to a TMDB title.")];
        }

        var named = Names.Parse(t.FolderName);
        if (named.Name.Length == 0 && t.Kind == "movie")
        {
            named = Names.Parse(t.FileName);
        }

        var tags = TagsOf(t);
        if (tags.Count > 0)
        {
            if (tags.Contains(t.Tmdb))
            {
                return [];
            }

            return [new Reason("tmdb-tag", $"Its folder or file says TMDB {tags[0]}; it's taken for TMDB {t.Tmdb}.")];
        }

        return Checks(t, named);
    }

    /// <summary>
    /// The doubts the files raise about the TMDB title they're taken for:
    /// its name, its year, a series' seasons, a film's length.
    /// </summary>
    public static IReadOnlyList<Reason> Checks(LibraryTitle t, Named named)
    {
        var output = new List<Reason>();
        if (t.TmdbName.Length > 0 && named.Name.Length > 0 && !Matches(named.Name, t))
        {
            var text = $"Named “{named.Name}” here; TMDB calls it “{t.TmdbName}”";
            var original = t.Details.OriginalName;
            if (original.Length > 0 && original != t.TmdbName)
            {
                text += $" (originally “{original}”)";
            }

            output.Add(new Reason("name", text + "."));
        }

        if (named.Year > 0 && t.TmdbYear > 0 && Math.Abs(named.Year - t.TmdbYear) > 1)
        {
            output.Add(new Reason("year", $"From {named.Year} here; TMDB says {t.TmdbYear}."));
        }

        if (Seasons(t) is { } seasons)
        {
            output.Add(seasons);
        }

        if (RuntimeDoubt(t) is { } runtime)
        {
            output.Add(runtime);
        }

        return output;
    }

    /// <summary>
    /// Compares a film's length with TMDB's: half again as long, or less
    /// than two thirds, isn't another cut of it (an extended edition runs
    /// about a third longer). Null when it fits or can't be judged.
    /// </summary>
    public static Reason? RuntimeDoubt(LibraryTitle t)
    {
        if (t.Kind != "movie" || t.RuntimeMs <= 0 || t.Details.Runtime <= 0)
        {
            return null;
        }

        int here = (int)(t.RuntimeMs / 60_000), there = t.Details.Runtime;
        if (here == 0)
        {
            return null; // under a minute: not a length to judge by
        }

        if (Math.Abs(here - there) > 15 && (2 * here > 3 * there || 3 * here < 2 * there))
        {
            return new Reason("runtime", $"{here} minutes here; TMDB says {there}.");
        }

        return null;
    }

    /// <summary>
    /// Compares a series' episodes with TMDB's: far more here than TMDB
    /// lists in all (half again, and a few), then says where: seasons TMDB
    /// doesn't list, or with more episodes than it lists. A few more (double
    /// episodes split in two) or seasons numbered otherwise (anime TMDB lists
    /// as one season) aren't signs. Specials aside. Null when no sign.
    /// </summary>
    public static Reason? Seasons(LibraryTitle t)
    {
        if (t.Kind != "series" || t.Details.Seasons.Count == 0)
        {
            return null;
        }

        var listed = new Dictionary<int, int>();
        int total = 0, have = 0;
        foreach (var s in t.Details.Seasons)
        {
            listed[s.Number] = s.EpisodeCount;
            if (s.Number > 0)
            {
                total += s.EpisodeCount;
            }
        }

        foreach (var (n, c) in t.Episodes)
        {
            if (n > 0)
            {
                have += c;
            }
        }

        if (2 * have <= (3 * total) + 4)
        {
            return null;
        }

        var notes = new List<string>();
        foreach (var n in t.Episodes.Keys.Order())
        {
            var here = t.Episodes[n];
            if (n == 0)
            {
                continue;
            }

            if (!listed.TryGetValue(n, out var count))
            {
                notes.Add($"season {n} isn't on TMDB");
            }
            else if (count > 0 && here > count)
            {
                notes.Add($"season {n} has {here} episodes here, {count} on TMDB");
            }
        }

        if (notes.Count == 0)
        {
            return null;
        }

        if (notes.Count > 3)
        {
            notes = [.. notes.Take(3), "…"];
        }

        var text = string.Join("; ", notes);
        return new Reason("seasons", char.ToUpperInvariant(text[0]) + text[1..] + ".");
    }

    /// <summary>
    /// Whether a name from the files is one of the title's names on TMDB (its
    /// name, its original name, its alternative titles and translations),
    /// letters and digits only, case and accents aside; or one contains the
    /// other, for subtitles left out on one side.
    /// </summary>
    /// <remarks>
    /// "At least 4 long" counts UTF-8 bytes, as Overfin's len() does: a
    /// name in Japanese of two characters is long enough.
    /// </remarks>
    public static bool Matches(string name, LibraryTitle t)
    {
        var want = Simple(name);
        if (want.Length == 0)
        {
            return true;
        }

        foreach (var n in AllNames(t))
        {
            var got = Simple(n);
            if (got.Length == 0)
            {
                continue;
            }

            if (got == want || (Bytes(got) >= 4 && Bytes(want) >= 4
                && (got.Contains(want, StringComparison.Ordinal) || want.Contains(got, StringComparison.Ordinal))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether name (already <see cref="Simple"/>) is exactly one of the title's names.</summary>
    public static bool ExactName(string name, LibraryTitle t) => AllNames(t).Any(x => Simple(x) == name);

    /// <summary>
    /// A name with only its letters and digits, lower case, without accents,
    /// "&amp;" read as "and".
    /// </summary>
    public static string Simple(string s)
    {
        s = Fold(s.Replace("&", " and ", StringComparison.Ordinal));
        var b = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            if (Rune.IsLetter(r) || Rune.IsDigit(r))
            {
                b.Append(r.ToString());
            }
        }

        return b.ToString();
    }

    /// <summary>A name in lower case, without accents ("Évasion" as "evasion").</summary>
    public static string Fold(string name)
    {
        var b = new StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (Folded.TryGetValue(c, out var f))
            {
                b.Append(f);
            }
            else
            {
                b.Append(c);
            }
        }

        return b.ToString();
    }

    private static IEnumerable<string> AllNames(LibraryTitle t) =>
        new[] { t.TmdbName, t.Details.OriginalName }.Concat(t.Details.Names);

    /// <summary>The TMDB IDs a title's folder and file name.</summary>
    private static List<string> TagsOf(LibraryTitle t)
    {
        var tags = new List<string>();
        foreach (var s in new[] { t.FolderName, t.FileName })
        {
            var tag = Names.Parse(s).Tmdb;
            if (tag.Length > 0)
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>A year as the reasons write it.</summary>
    internal static string YearText(int y) => y == 0 ? "year unknown" : y.ToString(CultureInfo.InvariantCulture);
}
