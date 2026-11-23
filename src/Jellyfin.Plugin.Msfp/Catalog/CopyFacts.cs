using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Entities;
using Msfp.Describe;
using Msfp.Protocol;
using DescribeStream = Msfp.Describe.Stream;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>
/// A version's file as an MSFP copy (video sections 3.2 to 3.6): the facts
/// from the file's headers (msfp-dotnet's Describe, the counterpart of
/// msfp-go's that Overfin uses), the subtitle files Jellyfin lists beside
/// it, and the map from the copy's track IDs to Jellyfin's stream indexes
/// that deliveries name tracks by. Until a file is read, the copy Jellyfin's
/// data describes (<see cref="Minimal"/>). The copy's ID, etag, size,
/// segments and previews are the caller's.
/// </summary>
internal static partial class CopyFacts
{
    /// <summary>
    /// The subtitle formats of a file beside a video the owner side serves
    /// apart: text as WebVTT (Jellyfin's subtitle encoder), PGS as PGS. ASS
    /// (and its fonts), DVD and DVB pictures aren't served apart this
    /// release: such a file is left out of the copy (it would never reach
    /// the viewer, as it isn't in the file's bytes either).
    /// </summary>
    internal static readonly HashSet<string> ExternalApart = new(StringComparer.Ordinal)
    {
        "subrip", "webvtt", "ttml", "hdmv_pgs_subtitle",
    };

    /// <summary>A Jellyfin subtitle codec as the registry's token; "" for one that isn't a token.</summary>
    internal static string SubtitleFormat(string? codec)
    {
        var c = (codec ?? string.Empty).Trim().ToLowerInvariant();
        c = c switch
        {
            "srt" or "text" => "subrip",
            "vtt" => "webvtt",
            "pgssub" or "pgs" or "sup" => "hdmv_pgs_subtitle",
            "dvdsub" or "vobsub" or "sub" => "dvd_subtitle",
            "dvbsub" => "dvb_subtitle",
            "ssa" => "ssa",
            _ => c,
        };
        return Names.IsToken(c) ? c : string.Empty;
    }

    /// <summary>The subtitle files beside a version, in Jellyfin's order, those served apart only.</summary>
    internal static List<MediaStream> Externals(IEnumerable<MediaStream> streams) =>
        streams.Where(s => s.IsExternal && s.Type == MediaStreamType.Subtitle && ExternalApart.Contains(SubtitleFormat(s.Codec)))
            .OrderBy(s => s.Index)
            .ToList();

    /// <summary>
    /// The copy a probe describes, with the subtitle files Jellyfin lists
    /// beside it, and the map of its track IDs (v, a&lt;n&gt;, s&lt;n&gt;)
    /// to Jellyfin's stream indexes. ID and etag left empty; the chapters'
    /// generic names dropped.
    /// </summary>
    public static (Copy Copy, Dictionary<string, int> Tracks) Shape(Probe probe, IReadOnlyList<MediaStream> jellyfin)
    {
        var ext = Externals(jellyfin);
        var copy = Shaper.Shape(probe, ext.Select(s => new External(s.Language ?? string.Empty, SubtitleFormat(s.Codec))
        {
            Default = s.IsDefault,
            Forced = s.IsForced,
            HearingImpaired = s.IsHearingImpaired,
        }).ToList());
        copy = copy with { Chapters = CleanChapters(copy.Chapters) };
        return (copy, Tracks(probe, copy, jellyfin, ext));
    }

    /// <summary>
    /// The map of a copy's track IDs to Jellyfin's stream indexes. Jellyfin
    /// numbers the subtitle files first, then the file's streams (the
    /// attachments left out), so the two are paired by kind, in order: the
    /// n-th audio stream of the file is Jellyfin's n-th audio stream; a kind
    /// whose counts differ isn't mapped (its tracks can't be asked of
    /// Jellyfin). The files beside it are paired in Jellyfin's order.
    /// </summary>
    internal static Dictionary<string, int> Tracks(Probe probe, Copy copy, IReadOnlyList<MediaStream> jellyfin, IReadOnlyList<MediaStream> externals)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var embedded = jellyfin.Where(s => !s.IsExternal).OrderBy(s => s.Index).ToList();
        var listed = copy.AudioTracks.Select(a => a.Id).Concat(copy.SubtitleTracks.Select(s => s.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var (kind, file) in new (MediaStreamType, List<DescribeStream>)[]
        {
            (MediaStreamType.Video, probe.Streams.Where(s => s.CodecType == "video" && s.Disposition.AttachedPic == 0 && s.Disposition.StillImage == 0).ToList()),
            (MediaStreamType.Audio, probe.Streams.Where(s => s.CodecType == "audio").ToList()),
            (MediaStreamType.Subtitle, probe.Streams.Where(s => s.CodecType == "subtitle").ToList()),
        })
        {
            var theirs = embedded.Where(s => s.Type == kind).ToList();
            if (file.Count != theirs.Count)
            {
                continue;
            }

            for (var i = 0; i < file.Count; i++)
            {
                var id = kind switch
                {
                    MediaStreamType.Video => i == 0 && copy.Video is not null ? "v" : string.Empty,
                    MediaStreamType.Audio => "a" + file[i].Index.ToString(CultureInfo.InvariantCulture),
                    _ => "s" + file[i].Index.ToString(CultureInfo.InvariantCulture),
                };
                if (id == "v" || listed.Contains(id))
                {
                    map[id] = theirs[i].Index;
                }
            }
        }

        var n = 0;
        foreach (var t in copy.SubtitleTracks.Where(t => t.External))
        {
            if (n < externals.Count)
            {
                map[t.Id] = externals[n++].Index;
            }
        }

        return map;
    }

    /// <summary>
    /// The copy Jellyfin's data describes, for a file not read yet or whose
    /// read failed: its duration, media type (the file's first bytes when
    /// known, else Jellyfin's container), picture size and codec, and audio
    /// tracks with their languages and channels, without codecs strings, so
    /// that it is never played as it is (the baseline plays it). Its track
    /// IDs are the file's indexes as Jellyfin's suggest them. ID, etag and
    /// size are the caller's.
    /// </summary>
    public static (Copy Copy, Dictionary<string, int> Tracks) Minimal(string? sniffed, string? container, long? runTimeTicks, IReadOnlyList<MediaStream> jellyfin)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var embedded = jellyfin.Where(s => !s.IsExternal).OrderBy(s => s.Index).ToList();
        var externals = jellyfin.Count(s => s.IsExternal);

        // Jellyfin numbers the files beside a video first, then the file's
        // own streams in its order (the attachments left out): an index
        // less the files beside is the file's own, as near as Jellyfin
        // tells it.
        var offset = embedded.Count > 0 && embedded.Min(s => s.Index) >= externals ? externals : 0;
        VideoStream? video = null;
        var picture = false;
        if (embedded.FirstOrDefault(s => s.Type == MediaStreamType.Video) is { Width: > 0 and <= Copies.MaxVideoSide, Height: > 0 and <= Copies.MaxVideoSide } v)
        {
            picture = true;
            var codec = (v.Codec ?? string.Empty).ToLowerInvariant();
            if (Names.IsToken(codec))
            {
                video = new VideoStream(codec, v.Width!.Value, v.Height!.Value) { Interlaced = v.IsInterlaced };
                map["v"] = v.Index;
            }
        }

        // Streams not read yet: a film's or an episode's file is a video.
        if (embedded.Count == 0)
        {
            picture = true;
        }

        var audio = new List<CopyAudio>();
        foreach (var s in embedded.Where(s => s.Type == MediaStreamType.Audio))
        {
            var codec = (s.Codec ?? string.Empty).ToLowerInvariant();
            codec = codec == "dca" ? "dts" : codec;
            var index = s.Index - offset;
            if (!Names.IsToken(codec) || index < 0 || audio.Count == Copies.MaxAudioTracks)
            {
                continue;
            }

            var id = "a" + index.ToString(CultureInfo.InvariantCulture);
            audio.Add(new CopyAudio(id, codec)
            {
                Language = Languages.TrySentTag(s.Language ?? string.Empty, out var tag) ? tag : null,
                Channels = s.Channels is >= 1 and <= 64 ? s.Channels : null,
                Default = s.IsDefault,
                Forced = s.IsForced,
            });
            map[id] = s.Index;
        }

        var type = sniffed is { Length: > 0 } ? MediaTypes.CanonicalType(sniffed) : MediaTypes.ContainerType(container ?? string.Empty);
        var ms = runTimeTicks is > 0 ? runTimeTicks.Value / TimeSpan.TicksPerMillisecond : 0;
        var copy = new Copy(string.Empty, ms <= Copies.MaxDurationMs ? ms : 0, MediaTypes.AudioOrVideo(type, picture), 0, string.Empty, audio)
        {
            Video = video,
        };
        return (copy, map);
    }

    [GeneratedRegex(@"^\s*(chapter|chapitre|cap[ií]tulo|capitolo|kapitel|hoofdstuk|rozdział|глава|ch\.?)\s*[0-9]{1,4}\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericRe();

    [GeneratedRegex(@"^\s*[0-9]{1,4}\s*$|^\s*[0-9]{1,2}:[0-9]{2}(:[0-9]{2})?([.,][0-9]{1,3})?\s*$")]
    private static partial Regex NumberRe();

    /// <summary>
    /// A chapter's name worth sending: Describe's cleaning (plain text, no
    /// file name, address or site) plus none of the generated names ("Chapter
    /// 3", "Chapitre 03", "12", "00:05:00.000") that muxers and Jellyfin
    /// write when the file has none.
    /// </summary>
    internal static bool GenericName(string name) => GenericRe().IsMatch(name) || NumberRe().IsMatch(name);

    /// <summary>The chapters, their generic names dropped (the chapter kept, nameless).</summary>
    internal static List<CopyChapter> CleanChapters(IReadOnlyList<CopyChapter> chapters) =>
        chapters.Select(c => c.Name is { } n && (GenericName(n) || Shaper.LooksLikeLocation(n)) ? c with { Name = null } : c).ToList();
}
