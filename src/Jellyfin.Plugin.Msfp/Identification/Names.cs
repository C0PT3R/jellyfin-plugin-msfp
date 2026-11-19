using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Msfp.Identification;

/// <summary>
/// Reading folder and file names: what a title is called there, its year,
/// the IDs written in it. Ported from Overfin's identify/names.go.
/// </summary>
/// <remarks>
/// The patterns say [0-9] and [\t\n\f\r ] where Overfin's say \d and \s,
/// and \z for $: .NET's \d and \s match more than ASCII, and its $ also
/// matches before a final newline.
/// </remarks>
internal static partial class Names
{
    // releaseTags are words of a release's description, never of a title's
    // name: where a name without a year stops ("The.Candidate.1080p.WEB").
    // Languages and editions aren't here: "The French Connection",
    // "Extended" are names too.
    private static readonly HashSet<string> ReleaseTags =
    [
        "480p", "576p", "720p", "1080p", "1080i", "2160p", "4k", "uhd",
        "bluray", "blu-ray", "bdrip", "brrip", "bdremux", "remux", "web", "web-dl",
        "webdl", "webrip", "hdtv", "hdrip", "dvdrip", "dvdr", "dvd",
        "x264", "x265", "h264", "h265", "h.264", "h.265", "hevc", "avc",
        "av1", "xvid", "divx", "10bit", "hdr", "hdr10", "dv",
        "aac", "ac3", "dts", "truehd", "atmos", "flac", "ddp", "dd5",
        "multi", "vostfr", "truefrench", "subfrench", "vff", "vfq",
    ];

    [GeneratedRegex(@"[\[{(][\t\n\f\r ]*tmdb(?:id)?[\t\n\f\r ]*[-=:][\t\n\f\r ]*([0-9]{1,9})[\t\n\f\r ]*[\]})]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TmdbTag();

    [GeneratedRegex(@"[\[{(][\t\n\f\r ]*(?:imdb(?:id)?[\t\n\f\r ]*[-=:][\t\n\f\r ]*)?(tt[0-9]{5,10})[\t\n\f\r ]*[\]})]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImdbTag();

    [GeneratedRegex(@"[\[{(][\t\n\f\r ]*tvdb(?:id)?[\t\n\f\r ]*[-=:][\t\n\f\r ]*([0-9]{1,9})[\t\n\f\r ]*[\]})]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TvdbTag();

    [GeneratedRegex(@"[\[{][^\]}]*[\]}]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\.[A-Za-z0-9]{2,4}\z")]
    private static partial Regex Extension();

    [GeneratedRegex(@"(?:^|[\t\n\f\r ._(\[-])((?:19|20)[0-9]{2})(?:\z|[\t\n\f\r ._)\]-])")]
    private static partial Regex YearIn();

    // Group is a release group at the end of a scene name ("-SPARKS").
    [GeneratedRegex(@"-[A-Za-z0-9]{2,15}\z")]
    private static partial Regex Group();

    /// <summary>
    /// Reads a folder or file name such as "Heat (1995) [tmdbid-949]" or
    /// "The.Candidate.2023.1080p.WEB-GROUP.mkv".
    /// </summary>
    public static Named Parse(string s)
    {
        var tmdb = NumberId(TmdbTag(), s);
        var tvdb = NumberId(TvdbTag(), s);
        var imdbMatch = ImdbTag().Match(s);
        var imdb = imdbMatch.Success ? imdbMatch.Groups[1].Value.ToLowerInvariant() : string.Empty;

        s = Brackets().Replace(s, " ");
        s = Extension().Replace(s.Trim(), string.Empty);
        // The year: the last one with a name before it ("1917 (2019)" is
        // 1917, from 2019; "1917" alone is a name).
        var year = 0;
        var cut = s.Length;
        foreach (Match m in YearIn().Matches(s))
        {
            var at = m.Groups[1].Index;
            if (s[..at].Trim(' ', '.', '_', '(', '[', '-').Length > 0)
            {
                year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                cut = at;
            }
        }

        var words = Fields(s[..cut].Replace('.', ' ').Replace('_', ' '));
        if (year == 0)
        {
            // Without a year, the name stops at the release's description,
            // and a scene name's group goes.
            for (var i = 1; i < words.Count; i++)
            {
                if (IsReleaseTag(words[i]))
                {
                    words.RemoveRange(i, words.Count - i);
                    break;
                }
            }

            if (words.Count > 1)
            {
                words[^1] = Group().Replace(words[^1], string.Empty);
            }
        }

        var name = string.Join(' ', Fields(string.Join(' ', words))).Trim(' ', '-', '(', '[');
        return new Named(name, year, tmdb, imdb, tvdb);
    }

    /// <summary>Whether a word describes a release ("1080p", "WEB-DL", "x264-GROUP").</summary>
    private static bool IsReleaseTag(string w)
    {
        w = w.ToLowerInvariant().Trim('(', ')', '[', ']', '-');
        if (ReleaseTags.Contains(w))
        {
            return true;
        }

        var dash = w.IndexOf('-', StringComparison.Ordinal);
        return dash >= 0 && ReleaseTags.Contains(w[..dash]);
    }

    /// <summary>
    /// The number tag finds in s, as TMDB and TVDB write them ("0949" is
    /// 949, and 0 is none); "" without one.
    /// </summary>
    private static string NumberId(Regex tag, string s)
    {
        var m = tag.Match(s);
        if (!m.Success)
        {
            return string.Empty;
        }

        var id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return id > 0 ? id.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    /// <summary>The words of s, split at white space (Go's strings.Fields).</summary>
    private static List<string> Fields(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
}
