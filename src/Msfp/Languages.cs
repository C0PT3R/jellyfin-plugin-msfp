using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Msfp;

/// <summary>Language tags in titles (MSFP section 9.4).</summary>
public static class Languages
{
    /// <summary>The most tags in one list.</summary>
    public const int MaxTags = 12;

    // ISO 639-2/B codes that Jellyfin may hold, as their 639-2/T twins.
    private static readonly Dictionary<string, string> Bibliographic = new(StringComparer.Ordinal)
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho", ["cze"] = "ces",
        ["dut"] = "nld", ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu", ["gre"] = "ell", ["ice"] = "isl",
        ["mac"] = "mkd", ["mao"] = "mri", ["may"] = "msa", ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk",
        ["tib"] = "bod", ["wel"] = "cym",
    };

    private static readonly Lazy<Dictionary<string, string>> TwoLetter = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (c.TwoLetterISOLanguageName.Length == 2 && c.ThreeLetterISOLanguageName.Length == 3)
            {
                map.TryAdd(c.ThreeLetterISOLanguageName, c.TwoLetterISOLanguageName);
            }
        }

        return map;
    });

    /// <summary>Whether a text is a tag as section 9.4 keeps it: 2 to 12 of A–Z a–z 0–9 -.</summary>
    public static bool IsTag(string? tag)
    {
        if (tag == null || tag.Length < 2 || tag.Length > 12)
        {
            return false;
        }

        foreach (var c in tag)
        {
            if (!(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A language as Jellyfin keeps it on a stream (ISO 639-2, or a tag)
    /// as a tag: the two-letter code where there is one, lowercase; null for
    /// none, "und" and anything that isn't a tag.
    /// </summary>
    public static string? FromJellyfin(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var l = language.Trim().ToLowerInvariant();
        if (l is "und" or "unk" or "mul" or "zxx" or "mis" || !IsTag(l))
        {
            return null;
        }

        if (l.Length == 3)
        {
            if (Bibliographic.TryGetValue(l, out var t))
            {
                l = t;
            }

            if (TwoLetter.Value.TryGetValue(l, out var two))
            {
                return two;
            }
        }

        return l;
    }

    /// <summary>Valid tags, each once (ignoring case), at most 12, in order.</summary>
    public static List<string> Clean(IEnumerable<string?> tags)
    {
        var kept = new List<string>();
        foreach (var t in tags)
        {
            if (kept.Count == MaxTags)
            {
                break;
            }

            if (IsTag(t) && !kept.Contains(t!, StringComparer.OrdinalIgnoreCase))
            {
                kept.Add(t!);
            }
        }

        return kept;
    }
}
