using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// A paired server's extra as Jellyfin shows it (MSFP video section 3.9):
/// its type as Jellyfin's, and its name made from the type, and a number when
/// its title has several of that type: an extra carries no name of its own.
/// In the server's language, English for one not listed.
/// </summary>
internal static class ExtraNames
{
    // MSFP's tokens as Jellyfin's types; a token Jellyfin doesn't know is a plain extra.
    private static readonly Dictionary<string, ExtraType> Types = new(StringComparer.Ordinal)
    {
        ["trailer"] = ExtraType.Trailer,
        ["featurette"] = ExtraType.Featurette,
        ["behindthescenes"] = ExtraType.BehindTheScenes,
        ["deletedscene"] = ExtraType.DeletedScene,
        ["interview"] = ExtraType.Interview,
        ["scene"] = ExtraType.Scene,
        ["short"] = ExtraType.Short,
        ["clip"] = ExtraType.Clip,
        ["sample"] = ExtraType.Sample,
        ["themesong"] = ExtraType.ThemeSong,
        ["themevideo"] = ExtraType.ThemeVideo,
    };

    private static readonly Dictionary<string, Dictionary<ExtraType, string>> ByLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new()
        {
            [ExtraType.Trailer] = "Trailer", [ExtraType.Featurette] = "Featurette", [ExtraType.BehindTheScenes] = "Behind the scenes",
            [ExtraType.DeletedScene] = "Deleted scene", [ExtraType.Interview] = "Interview", [ExtraType.Scene] = "Scene",
            [ExtraType.Short] = "Short", [ExtraType.Clip] = "Clip", [ExtraType.Sample] = "Sample",
            [ExtraType.ThemeSong] = "Theme song", [ExtraType.ThemeVideo] = "Theme video", [ExtraType.Unknown] = "Extra",
        },
        ["fr"] = new()
        {
            [ExtraType.Trailer] = "Bande-annonce", [ExtraType.Featurette] = "Making-of", [ExtraType.BehindTheScenes] = "Coulisses",
            [ExtraType.DeletedScene] = "Scène coupée", [ExtraType.Interview] = "Entretien", [ExtraType.Scene] = "Scène",
            [ExtraType.Short] = "Court métrage", [ExtraType.Clip] = "Extrait", [ExtraType.Sample] = "Échantillon",
            [ExtraType.ThemeSong] = "Thème musical", [ExtraType.ThemeVideo] = "Vidéo thème", [ExtraType.Unknown] = "Bonus",
        },
        ["de"] = new()
        {
            [ExtraType.Trailer] = "Trailer", [ExtraType.Featurette] = "Featurette", [ExtraType.BehindTheScenes] = "Hinter den Kulissen",
            [ExtraType.DeletedScene] = "Entfallene Szene", [ExtraType.Interview] = "Interview", [ExtraType.Scene] = "Szene",
            [ExtraType.Short] = "Kurzfilm", [ExtraType.Clip] = "Clip", [ExtraType.Sample] = "Beispiel",
            [ExtraType.ThemeSong] = "Titelmusik", [ExtraType.ThemeVideo] = "Titelvideo", [ExtraType.Unknown] = "Extra",
        },
        ["es"] = new()
        {
            [ExtraType.Trailer] = "Tráiler", [ExtraType.Featurette] = "Reportaje", [ExtraType.BehindTheScenes] = "Detrás de las cámaras",
            [ExtraType.DeletedScene] = "Escena eliminada", [ExtraType.Interview] = "Entrevista", [ExtraType.Scene] = "Escena",
            [ExtraType.Short] = "Cortometraje", [ExtraType.Clip] = "Clip", [ExtraType.Sample] = "Muestra",
            [ExtraType.ThemeSong] = "Canción temática", [ExtraType.ThemeVideo] = "Vídeo temático", [ExtraType.Unknown] = "Extra",
        },
    };

    /// <summary>An MSFP extra type (a token) as Jellyfin's; Unknown for one it doesn't name.</summary>
    public static ExtraType TypeOf(string token) => Types.TryGetValue(token, out var t) ? t : ExtraType.Unknown;

    /// <summary>An extra's name: its type, then its number when it isn't 0 ("Trailer 2").</summary>
    public static string Of(string token, int number, string? culture)
    {
        var language = (culture ?? string.Empty).Split('-', '_')[0];
        var names = ByLanguage.TryGetValue(language, out var n) ? n : ByLanguage["en"];
        var name = names[TypeOf(token)];
        return number > 0 ? name + " " + number.ToString(CultureInfo.InvariantCulture) : name;
    }
}
