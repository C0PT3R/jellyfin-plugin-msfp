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

/// <summary>
/// A stand-in's streams, from what the catalogs said of its copies: the
/// best picture, each audio and subtitle language. Codecs aren't known
/// before play; the source opened at play gives the real streams.
/// </summary>
internal static class Streams
{
    public static List<MediaStream> Of(Playable copy, Func<string, string?> language)
    {
        var streams = new List<MediaStream>();
        if (copy.Picture is { } p)
        {
            streams.Add(new MediaStream
            {
                Index = streams.Count,
                Type = MediaStreamType.Video,
                Height = p.Height,
                Width = p.Height * 16 / 9,
                IsDefault = true,
            });
        }

        var audio = copy.Audio.Select(language).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (var l in audio.Count > 0 ? audio.Cast<string?>() : [null])
        {
            streams.Add(new MediaStream { Index = streams.Count, Type = MediaStreamType.Audio, Language = l, IsDefault = streams.All(s => s.Type != MediaStreamType.Audio) });
        }

        foreach (var l in copy.Subtitles.Select(language).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            // Served as WebVTT (MSFP video section 6).
            streams.Add(new MediaStream { Index = streams.Count, Type = MediaStreamType.Subtitle, Language = l, Codec = "webvtt" });
        }

        return streams;
    }
}
