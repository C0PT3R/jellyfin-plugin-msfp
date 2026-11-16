using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Msfp.Federation;

/// <summary>Cleaning server names before they are stored (MSFP section 9.1).</summary>
public static class Names
{
    /// <summary>The longest name, in Unicode code points.</summary>
    public const int MaxLength = 80;

    /// <summary>
    /// Runs of white space made one space, other control and format
    /// characters (Cc, Cf) removed, the ends trimmed, cut to 80 code points; an empty
    /// name becomes the fallback (the host of the peer's address).
    /// </summary>
    public static string Clean(string? name, string fallback)
    {
        var sb = new StringBuilder();
        var space = false;
        var count = 0;
        foreach (var rune in (name ?? string.Empty).EnumerateRunes())
        {
            // White space first: a tab or a line feed (both Cc) separates
            // words like a space does (SPEC-GAPS.md, section 9.1).
            if (Rune.IsWhiteSpace(rune))
            {
                space = sb.Length > 0;
                continue;
            }

            var cat = Rune.GetUnicodeCategory(rune);
            if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format)
            {
                continue;
            }

            if (space)
            {
                if (count == MaxLength - 1)
                {
                    break;
                }

                sb.Append(' ');
                count++;
                space = false;
            }

            if (count == MaxLength)
            {
                break;
            }

            sb.Append(rune.ToString());
            count++;
        }

        return sb.Length == 0 ? fallback : sb.ToString();
    }
}
