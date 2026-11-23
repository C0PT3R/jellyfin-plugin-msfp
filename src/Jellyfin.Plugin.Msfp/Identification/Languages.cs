using System.Collections.Generic;

namespace Jellyfin.Plugin.Msfp.Identification;

/// <summary>
/// Whether two language names are the same language, however they're
/// written: TMDB's ("fr"), a tag ("fr-CA") or a media file's (ISO 639-2,
/// "fre" or "fra"). Ported from Overfin's internal/lang (its codes only).
/// </summary>
internal static class Languages
{
    // Codes are the languages films and series come in: each one's
    // two-letter code (ISO 639-1, or TMDB's own for Cantonese) and its
    // three-letter ones (ISO 639-2, bibliographic and terminology, as files
    // write them).
    private static readonly Dictionary<string, string[]> Codes = new()
    {
        ["af"] = ["afr"],
        ["am"] = ["amh"],
        ["ar"] = ["ara"],
        ["az"] = ["aze"],
        ["be"] = ["bel"],
        ["bg"] = ["bul"],
        ["bn"] = ["ben"],
        ["bo"] = ["bod", "tib"],
        ["br"] = ["bre"],
        ["bs"] = ["bos"],
        ["ca"] = ["cat"],
        ["cn"] = ["yue"], // Cantonese: TMDB writes it cn
        ["co"] = ["cos"],
        ["cs"] = ["ces", "cze"],
        ["cy"] = ["cym", "wel"],
        ["da"] = ["dan"],
        ["de"] = ["deu", "ger"],
        ["el"] = ["ell", "gre"],
        ["en"] = ["eng"],
        ["eo"] = ["epo"],
        ["es"] = ["spa"],
        ["et"] = ["est"],
        ["eu"] = ["eus", "baq"],
        ["fa"] = ["fas", "per"],
        ["fi"] = ["fin"],
        ["fo"] = ["fao"],
        ["fr"] = ["fra", "fre"],
        ["ga"] = ["gle"],
        ["gd"] = ["gla"],
        ["gl"] = ["glg"],
        ["gu"] = ["guj"],
        ["ha"] = ["hau"],
        ["he"] = ["heb"],
        ["hi"] = ["hin"],
        ["hr"] = ["hrv"],
        ["ht"] = ["hat"],
        ["hu"] = ["hun"],
        ["hy"] = ["hye", "arm"],
        ["id"] = ["ind"],
        ["ig"] = ["ibo"],
        ["is"] = ["isl", "ice"],
        ["it"] = ["ita"],
        ["iu"] = ["iku"],
        ["ja"] = ["jpn"],
        ["jv"] = ["jav"],
        ["ka"] = ["kat", "geo"],
        ["kk"] = ["kaz"],
        ["km"] = ["khm"],
        ["kn"] = ["kan"],
        ["ko"] = ["kor"],
        ["ku"] = ["kur"],
        ["ky"] = ["kir"],
        ["la"] = ["lat"],
        ["lb"] = ["ltz"],
        ["lo"] = ["lao"],
        ["lt"] = ["lit"],
        ["lv"] = ["lav"],
        ["mi"] = ["mri", "mao"],
        ["mk"] = ["mkd", "mac"],
        ["ml"] = ["mal"],
        ["mn"] = ["mon"],
        ["mr"] = ["mar"],
        ["ms"] = ["msa", "may"],
        ["mt"] = ["mlt"],
        ["my"] = ["mya", "bur"],
        ["nb"] = ["nob"],
        ["ne"] = ["nep"],
        ["nl"] = ["nld", "dut"],
        ["nn"] = ["nno"],
        ["no"] = ["nor"],
        ["oc"] = ["oci"],
        ["pa"] = ["pan"],
        ["pl"] = ["pol"],
        ["ps"] = ["pus"],
        ["pt"] = ["por"],
        ["ro"] = ["ron", "rum"],
        ["ru"] = ["rus"],
        ["sa"] = ["san"],
        ["si"] = ["sin"],
        ["sk"] = ["slk", "slo"],
        ["sl"] = ["slv"],
        ["so"] = ["som"],
        ["sq"] = ["sqi", "alb"],
        ["sr"] = ["srp"],
        ["sv"] = ["swe"],
        ["sw"] = ["swa"],
        ["ta"] = ["tam"],
        ["te"] = ["tel"],
        ["tg"] = ["tgk"],
        ["th"] = ["tha"],
        ["tk"] = ["tuk"],
        ["tl"] = ["tgl", "fil"],
        ["tr"] = ["tur"],
        ["uk"] = ["ukr"],
        ["ur"] = ["urd"],
        ["uz"] = ["uzb"],
        ["vi"] = ["vie"],
        ["xh"] = ["xho"],
        ["yi"] = ["yid"],
        ["yo"] = ["yor"],
        ["zh"] = ["zho", "chi"],
        ["zu"] = ["zul"],
    };

    private static readonly Dictionary<string, string> ThreeToTwo = MakeThreeToTwo();

    /// <summary>Whether a and b are the same language; never for unknown ones.</summary>
    public static bool Same(string a, string b)
    {
        var x = Base(a);
        return x.Length > 0 && x == Base(b);
    }

    /// <summary>
    /// A language's two-letter code (ISO 639-1), from a two-letter code, a
    /// BCP 47 tag or a three-letter one; "" when it's none of the languages
    /// known here.
    /// </summary>
    public static string Base(string tag)
    {
        tag = tag.Trim().ToLowerInvariant();
        var cut = tag.IndexOfAny(['-', '_']);
        if (cut >= 0)
        {
            tag = tag[..cut];
        }

        return tag.Length switch
        {
            2 => Codes.ContainsKey(tag) ? tag : string.Empty,
            3 => ThreeToTwo.GetValueOrDefault(tag, string.Empty),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// A language's three-letter code as media files and Jellyfin write it
    /// (ISO 639-2, the bibliographic one where there are two: "fre", not
    /// "fra"); "" when it's none of the languages known here.
    /// </summary>
    public static string Three(string tag)
    {
        var two = Base(tag);
        return two.Length > 0 ? Codes[two][^1] : string.Empty;
    }

    /// <summary>
    /// A language's three-letter code as Jellyfin 12's reading of a file
    /// writes a stream's (ISO 639-2, the terminological one where there are
    /// two: "fra", "deu"); "" when it's none of the languages known here.
    /// </summary>
    public static string Terminology(string tag)
    {
        var two = Base(tag);
        return two.Length > 0 ? Codes[two][0] : string.Empty;
    }

    private static Dictionary<string, string> MakeThreeToTwo()
    {
        var map = new Dictionary<string, string>();
        foreach (var (two, threes) in Codes)
        {
            foreach (var three in threes)
            {
                map[three] = two;
            }
        }

        return map;
    }
}
