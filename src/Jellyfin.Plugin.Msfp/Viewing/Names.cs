using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>The two libraries' names, in the server's language (English for one not listed); a name the owner changes stays.</summary>
internal static class Names
{
    private static readonly Dictionary<string, (string Films, string Series)> ByLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ("Films from paired servers", "Series from paired servers"),
        ["fr"] = ("Films des serveurs associés", "Séries des serveurs associés"),
        ["de"] = ("Filme von gekoppelten Servern", "Serien von gekoppelten Servern"),
        ["es"] = ("Películas de servidores vinculados", "Series de servidores vinculados"),
    };

    public static string Of(StandIns.Shelf shelf, string? culture)
    {
        var language = (culture ?? string.Empty).Split('-', '_')[0];
        var names = ByLanguage.TryGetValue(language, out var n) ? n : ByLanguage["en"];
        return shelf == StandIns.Shelf.Films ? names.Films : names.Series;
    }
}
