using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Entities;
using Msfp.Protocol;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// A copy's facts (MSFP video sections 3.2 to 3.6) as the media streams of a
/// stand-in's source, written as Jellyfin's own reading of the same file
/// writes them (checked against local files on Jellyfin 12.2, migration
/// spike 0a and phase 5), so that its decision engine judges a peer's copy
/// as it judges a local file: ffprobe's codec and profile names, the level
/// as the level_idc, Jellyfin's subtitle codec names, colour names from
/// H.273, three-letter languages, the dispositions as flags.
///
/// Indexes as Jellyfin numbers a file's: the subtitle files beside it
/// first (0, 1…), then the file's streams in the file's order, each at the
/// external count plus its index in the file (MSFP's a&lt;n&gt; and
/// s&lt;n&gt; are those indexes), a Data stream where the file has one the
/// copy doesn't list (Jellyfin's ffmpeg maps a stream by its place among
/// the file's: no gaps). <see cref="Built.Tracks"/> maps the copy's track
/// IDs ("v" for the picture) to these indexes.
/// </summary>
internal static class StandInStreams
{
    /// <summary>A stand-in source's streams, and the copy's track IDs to their indexes.</summary>
    internal sealed record Built(List<MediaStream> Streams, Dictionary<string, int> Tracks);

    // ffmpeg's colour names (av_color_*_name) by H.273 code point.
    private static readonly Dictionary<int, string> PrimariesNames = new()
    {
        [1] = "bt709", [4] = "bt470m", [5] = "bt470bg", [6] = "smpte170m", [7] = "smpte240m", [8] = "film",
        [9] = "bt2020", [10] = "smpte428", [11] = "smpte431", [12] = "smpte432", [22] = "ebu3213",
    };

    private static readonly Dictionary<int, string> TransferNames = new()
    {
        [1] = "bt709", [4] = "gamma22", [5] = "gamma28", [6] = "smpte170m", [7] = "smpte240m", [8] = "linear",
        [9] = "log100", [10] = "log316", [11] = "iec61966-2-4", [12] = "bt1361e", [13] = "iec61966-2-1",
        [14] = "bt2020-10", [15] = "bt2020-12", [16] = "smpte2084", [17] = "smpte428", [18] = "arib-std-b67",
    };

    private static readonly Dictionary<int, string> MatrixNames = new()
    {
        [0] = "gbr", [1] = "bt709", [4] = "fcc", [5] = "bt470bg", [6] = "smpte170m", [7] = "smpte240m",
        [8] = "ycgco", [9] = "bt2020nc", [10] = "bt2020c", [11] = "smpte2085", [12] = "chroma-derived-nc",
        [13] = "chroma-derived-c", [14] = "ictcp", [15] = "ipt-c2", [16] = "ycgco-re", [17] = "ycgco-ro",
    };

    // ffmpeg's channel layout for a count (av_channel_layout_default), as Jellyfin shows it.
    private static readonly Dictionary<int, string> Layouts = new()
    {
        [1] = "mono", [2] = "stereo", [3] = "2.1", [4] = "4.0", [5] = "5.0", [6] = "5.1", [7] = "6.1", [8] = "7.1",
    };

    /// <summary>
    /// A copy's media type as Jellyfin names a local file's container
    /// (mkv, mp4, ts, webm, avi…); null for one it has no name for.
    /// </summary>
    internal static string? Container(string mediaType)
    {
        var slash = mediaType.IndexOf('/', StringComparison.Ordinal);
        return (slash < 0 ? mediaType : mediaType[(slash + 1)..]) switch
        {
            "mp4" => "mp4",
            "quicktime" => "mov",
            "3gpp" => "3gp",
            "3gpp2" => "3g2",
            "matroska" or "x-matroska" => "mkv",
            "webm" => "webm",
            "mp2t" or "vnd.dlna.mpeg-tts" => "ts",
            "avi" or "x-msvideo" => "avi",
            "mpeg" => "mpeg",
            "ogg" => "ogg",
            "x-flv" => "flv",
            "flac" => "flac",
            "wave" or "wav" => "wav",
            "aiff" => "aiff",
            "aac" => "aac",
            _ => null,
        };
    }

    /// <summary>
    /// A copy's media type as Jellyfin names a local item's container (the
    /// format ffprobe names for ISO files, "mov,mp4,m4a,3gp,3g2,mj2"; "mkv,webm"
    /// for WebM), where it differs from its source's (<see cref="Container"/>).
    /// </summary>
    internal static string? ItemContainer(string mediaType) => Container(mediaType) switch
    {
        "mp4" or "mov" or "3gp" or "3g2" => "mov,mp4,m4a,3gp,3g2,mj2",
        "webm" => "mkv,webm",
        var c => c,
    };

    // Whether a container is ISO BMFF: its streams carry sample entries
    // (codec tags), its tracks a language ("und" when none) and the picture
    // the default flag, as ffmpeg writes such a file.
    private static bool Iso(string? container) => container is "mp4" or "mov" or "3gp" or "3g2";

    /// <summary>
    /// The copy's streams as Jellyfin's reading of its file writes them.
    /// <paramref name="three"/> gives a language tag's three-letter code as
    /// Jellyfin writes it (null for none); <paramref name="external"/> the
    /// path a subtitle track beside the file is read at.
    /// </summary>
    internal static Built Of(Copy copy, Func<string, string?> three, Func<CopySubtitle, string> external)
    {
        var container = Container(copy.MediaType);
        var iso = Iso(container);
        var streams = new List<MediaStream>();
        var tracks = new Dictionary<string, int>(StringComparer.Ordinal);

        // Beside the file, first, in the copy's order.
        foreach (var s in copy.SubtitleTracks.Where(s => s.External))
        {
            var st = Subtitle(s, three);
            st.Index = streams.Count;
            st.IsExternal = true;
            st.Path = external(s);
            tracks[s.Id] = st.Index;
            streams.Add(st);
        }

        var offset = streams.Count;
        var inFile = new SortedDictionary<int, MediaStream>();
        var ids = new Dictionary<int, string>();
        foreach (var a in copy.AudioTracks)
        {
            if (global::Msfp.Protocol.Playback.TryTrackId(a.Id, 'a', out var n) && n < 4096 && !inFile.ContainsKey(n))
            {
                inFile[n] = Audio(a, three, iso);
                ids[n] = a.Id;
            }
        }

        foreach (var s in copy.SubtitleTracks.Where(s => !s.External))
        {
            if (global::Msfp.Protocol.Playback.TryTrackId(s.Id, 's', out var n) && n < 4096 && !inFile.ContainsKey(n))
            {
                inFile[n] = Subtitle(s, three);
                ids[n] = s.Id;
            }
        }

        // The picture has no ID: the first index no track takes.
        if (copy.Video is { } v)
        {
            var at = 0;
            while (inFile.ContainsKey(at))
            {
                at++;
            }

            inFile[at] = Video(v, container, iso);
            ids[at] = "v";
        }

        var last = inFile.Count == 0 ? -1 : inFile.Keys.Max();
        for (var n = 0; n <= last; n++)
        {
            var st = inFile.TryGetValue(n, out var known) ? known : new MediaStream { Type = MediaStreamType.Data };
            st.Index = offset + n;
            if (ids.TryGetValue(n, out var id))
            {
                tracks[id] = st.Index;
            }

            streams.Add(st);
        }

        return new Built(streams, tracks);
    }

    /// <summary>
    /// A stream's words in the server's language, as Jellyfin gives a local
    /// file's when it reads them (its language's name, "Default",
    /// "External", "Forced"…): players name tracks from them.
    /// </summary>
    internal static void Localize(MediaStream s, Func<string, string> word, Func<string, string?> languageName)
    {
        if (s.Type is not (MediaStreamType.Audio or MediaStreamType.Subtitle))
        {
            return;
        }

        if (!string.IsNullOrEmpty(s.Language))
        {
            s.LocalizedLanguage = languageName(s.Language);
        }

        s.LocalizedDefault = word("Default");
        s.LocalizedExternal = word("External");
        if (s.Type == MediaStreamType.Audio)
        {
            s.LocalizedOriginal = word("Original");
        }
        else
        {
            s.LocalizedUndefined = word("Undefined");
            s.LocalizedForced = word("Forced");
            s.LocalizedHearingImpaired = word("HearingImpaired");
        }
    }

    /// <summary>The picture as Jellyfin's reading of a file writes it.</summary>
    internal static MediaStream Video(VideoStream v, string? container, bool iso)
    {
        var facts = Rfc6381.Read(v.Codec, v.Codecs, v.BitDepth, v.Dv, out _);
        var depth = facts?.BitDepth ?? v.BitDepth;
        var s = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = v.Codec,
            Width = v.Width,
            Height = v.Height,
            BitDepth = depth,
            Profile = VideoProfile(v.Codec, v.Codecs, facts),
            Level = VideoLevel(v.Codec, facts),

            // Jellyfin's reading writes ffprobe's refs, 1 for every picture
            // (the SPS's max_num_ref_frames is never read): the same here,
            // so that its decisions are a local file's (migration phase 5).
            RefFrames = 1,
            PixelFormat = PixelFormat(v.Codec, facts, depth),
            IsInterlaced = v.Interlaced,
            IsDefault = iso,
            Language = iso ? "und" : null,
            AspectRatio = DisplayAspect(v.Width, v.Height, v.SampleAspect),
            IsAnamorphic = v.SampleAspect is { } sar && sar != "1:1",
            Rotation = v.Rotation is { } r and not 0 ? FfprobeRotation(r) : null,
        };

        if (Fps(v.Fps) is { } fps)
        {
            s.AverageFrameRate = fps;
            s.RealFrameRate = fps;
        }

        if (v.Colour is { } c)
        {
            s.ColorPrimaries = c.Primaries is { } p ? PrimariesNames.GetValueOrDefault(p) : null;
            s.ColorTransfer = c.Transfer is { } t ? TransferNames.GetValueOrDefault(t) : null;
            s.ColorSpace = c.Matrix is { } m ? MatrixNames.GetValueOrDefault(m) : null;
        }

        if (v.Dv is { } dv)
        {
            s.DvVersionMajor = 1;
            s.DvVersionMinor = 0;
            s.DvProfile = dv.Profile;
            s.DvLevel = dv.Level;
            s.RpuPresentFlag = 1;
            s.ElPresentFlag = dv.El == true ? 1 : 0;
            s.BlPresentFlag = 1;
            s.DvBlSignalCompatibilityId = dv.Compatibility;
        }

        if (v.Hdr.Contains("hdr10plus", StringComparer.Ordinal))
        {
            s.Hdr10PlusPresentFlag = true;
        }

        if (v.Codec == "h264")
        {
            // AVCC (length-prefixed NAL units) but in a transport stream.
            s.IsAVC = container != "ts";
            s.NalLengthSize = container == "ts" ? "0" : "4";
        }

        if (iso && v.Codecs is { Length: > 0 } codecs)
        {
            var entry = codecs.Split('.')[0];
            s.CodecTag = v.Dv is not null && Rfc6381.DolbyVisionEntry(entry) is { } dvEntry ? dvEntry : entry;
        }
        else if (container == "ts" && v.Codec == "hevc")
        {
            // The transport stream's registration descriptor, as ffprobe reads it.
            s.CodecTag = "HEVC";
        }

        return s;
    }

    /// <summary>An audio track as Jellyfin's reading of a file writes it.</summary>
    internal static MediaStream Audio(CopyAudio a, Func<string, string?> three, bool iso)
    {
        var s = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Codec = a.Codec,
            Channels = a.Channels,
            ChannelLayout = a.Channels is { } n ? Layouts.GetValueOrDefault(n) : null,
            SampleRate = a.SampleRate,
            BitDepth = a.BitDepth,
            Profile = AudioProfile(a),
            Language = a.Language is { Length: > 0 } l ? three(l) : null,
            IsDefault = a.Default,
            IsForced = a.Forced,
            IsOriginal = a.Original,
        };
        if (s.Language is null && iso)
        {
            s.Language = "und";
        }

        if (iso && a.Codecs is { Length: > 0 } codecs)
        {
            s.CodecTag = codecs.Split('.')[0];
        }

        return s;
    }

    /// <summary>A subtitle track as Jellyfin's reading of a file writes it.</summary>
    internal static MediaStream Subtitle(CopySubtitle t, Func<string, string?> three)
    {
        var codec = SubtitleCodec(t.Format);
        return new MediaStream
        {
            Type = MediaStreamType.Subtitle,
            Codec = codec,
            Language = t.Language is { Length: > 0 } l ? three(l) : null,
            IsDefault = t.Default,
            IsForced = t.Forced,
            IsHearingImpaired = t.HearingImpaired,

            // As Jellyfin marks a file's text and PGS tracks: deliverable apart.
            SupportsExternalStream = codec is not null && (TextFormats.Contains(codec) || codec == "PGSSUB"),
        };
    }

    // The text subtitle formats of the registry, as Jellyfin names them.
    private static readonly HashSet<string> TextFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "subrip", "srt", "ass", "ssa", "webvtt", "vtt", "mov_text", "ttml", "text", "sami", "smi", "microdvd", "subviewer", "subviewer1", "mpl2", "jacosub", "realtext", "pjs", "stl", "vplayer",
    };

    /// <summary>A subtitle format of the registry as Jellyfin names it (its picture formats in capitals).</summary>
    internal static string? SubtitleCodec(string format) => format switch
    {
        "" => null,
        "hdmv_pgs_subtitle" => "PGSSUB",
        "dvd_subtitle" => "DVDSUB",
        "dvb_subtitle" => "DVBSUB",
        "dvb_teletext" => "DVBTXT",
        _ => format,
    };

    /// <summary>The profile ffprobe names for a picture, from its codecs string; null when it names none.</summary>
    internal static string? VideoProfile(string codec, string? codecs, CodecsFacts? facts)
    {
        if (facts?.Profile is not { } p)
        {
            return null;
        }

        switch (codec)
        {
            case "h264":
                // The constraint flags: constraint_set1 makes Constrained
                // Baseline; constraint_set3 the Intra profiles.
                var flags = codecs is { Length: >= 11 } && int.TryParse(codecs.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var f) ? f : 0;
                var intra = (flags & 0x10) != 0;
                return p switch
                {
                    66 => (flags & 0x40) != 0 ? "Constrained Baseline" : "Baseline",
                    77 => "Main",
                    88 => "Extended",
                    100 => "High",
                    110 => intra ? "High 10 Intra" : "High 10",
                    122 => intra ? "High 4:2:2 Intra" : "High 4:2:2",
                    244 => intra ? "High 4:4:4 Intra" : "High 4:4:4 Predictive",
                    44 => "CAVLC 4:4:4",
                    118 => "Multiview High",
                    128 => "Stereo High",
                    _ => null,
                };
            case "hevc":
                return p switch
                {
                    1 => "Main",
                    2 => "Main 10",
                    3 => "Main Still Picture",
                    4 => "Rext",
                    6 => "Multiview Main",
                    9 => "SCC",
                    _ => null,
                };
            case "av1":
                return p switch { 0 => "Main", 1 => "High", 2 => "Professional", _ => null };
            case "vp9":
                return p is >= 0 and <= 3 ? "Profile " + p.ToString(CultureInfo.InvariantCulture) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The level as Jellyfin writes ffprobe's: H.264's and HEVC's level_idc
    /// (the codecs string's), 0 for AV1, -99 for VP9; null for others.
    /// </summary>
    internal static double? VideoLevel(string codec, CodecsFacts? facts) => codec switch
    {
        "h264" or "hevc" => facts?.Level,
        "av1" => 0,
        "vp9" => -99,
        _ => null,
    };

    /// <summary>The pixel format ffprobe names, from the chroma the codecs string tells and the bit depth (4:2:0 when it tells none).</summary>
    internal static string PixelFormat(string codec, CodecsFacts? facts, int? depth)
    {
        var chroma = facts?.Chroma;
        if (chroma is null && codec == "h264" && facts?.Profile is { } hp)
        {
            chroma = hp switch { 122 => "4:2:2", 244 or 44 => "4:4:4", _ => "4:2:0" };
        }
        else if (chroma is null && codec == "hevc" && facts?.Profile is 4 && facts.Constraints is { } bytes)
        {
            // RExt: general_max_422chroma (byte 1, bit 0), general_max_420chroma (byte 2, bit 7).
            var parts = bytes.Split('.');
            var b1 = parts.Length > 0 && int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var x) ? x : 0;
            var b2 = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var y) ? y : 0;
            chroma = (b2 & 0x80) != 0 ? "4:2:0" : (b1 & 0x01) != 0 ? "4:2:2" : "4:4:4";
        }

        var name = (chroma ?? "4:2:0") switch
        {
            "4:2:2" => "yuv422p",
            "4:4:4" => "yuv444p",
            "4:0:0" => "gray",
            _ => "yuv420p",
        };
        return depth is > 8 ? name + depth.Value.ToString(CultureInfo.InvariantCulture) + "le" : name;
    }

    /// <summary>The profile ffprobe names for an audio track: AAC's object type, DTS's flavour, the object-based formats.</summary>
    internal static string? AudioProfile(CopyAudio a)
    {
        var codecs = a.Codecs ?? string.Empty;
        switch (a.Codec)
        {
            case "aac":
                return codecs switch
                {
                    "mp4a.40.1" or "mp4a.66" => "Main",
                    "mp4a.40.2" or "mp4a.67" => "LC",
                    "mp4a.40.3" or "mp4a.68" => "SSR",
                    "mp4a.40.4" => "LTP",
                    "mp4a.40.5" => "HE-AAC",
                    "mp4a.40.29" => "HE-AACv2",
                    "mp4a.40.23" => "LD",
                    "mp4a.40.39" => "ELD",
                    "mp4a.40.42" => "xHE-AAC",
                    _ => null,
                };
            case "dts":
                if (a.Objects == "dts-x")
                {
                    return "DTS-HD MA + DTS:X";
                }

                return codecs switch
                {
                    "dtsc" => "DTS",
                    "dtsh" or "dtsl" => "DTS-HD MA",
                    "dtse" => "DTS Express",
                    _ => null,
                };
            case "eac3" when a.Objects == "dolby-atmos":
                return "Dolby Digital Plus + Dolby Atmos";
            case "truehd" when a.Objects == "dolby-atmos":
                return "Dolby TrueHD + Dolby Atmos";
            default:
                return null;
        }
    }

    /// <summary>An MSFP rotation (clockwise) as ffprobe's (counterclockwise, -180 to 180): 90 is -90, 270 is 90.</summary>
    internal static int FfprobeRotation(int clockwise) => (clockwise % 360) switch
    {
        90 => -90,
        180 => 180,
        270 => 90,
        _ => 0,
    };

    /// <summary>The display aspect ratio ffprobe writes: width × sample width : height × sample height, reduced.</summary>
    internal static string DisplayAspect(int width, int height, string? sampleAspect)
    {
        long sw = 1, sh = 1;
        if (sampleAspect?.Split(':') is [var a, var b] && long.TryParse(a, out var x) && long.TryParse(b, out var y) && x > 0 && y > 0)
        {
            sw = x;
            sh = y;
        }

        var w = width * sw;
        var h = height * sh;
        var g = (long)System.Numerics.BigInteger.GreatestCommonDivisor(w, h);
        return g > 0 ? string.Create(CultureInfo.InvariantCulture, $"{w / g}:{h / g}") : string.Empty;
    }

    /// <summary>A frame rate "num/den" as Jellyfin keeps it; null for none.</summary>
    internal static float? Fps(string? fps) =>
        fps?.Split('/') is [var n, var d] && double.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var a)
        && double.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out var b) && b > 0
            ? (float)(a / b)
            : null;

    /// <summary>
    /// Where a copy's title lies in it: the whole copy, or for an episode in
    /// a copy holding several the span its startMs and endMs give (MSFP
    /// video sections 4.2 and 5.2; an end left out is the copy's end).
    /// </summary>
    internal static (long StartMs, long EndMs) Span(Copy copy)
    {
        var end = copy.DurationMs;
        var start = Math.Clamp(copy.StartMs ?? 0, 0, Math.Max(end, 0));
        if (copy.EndMs is { } e && e > start && e <= end)
        {
            end = e;
        }

        return (start, end);
    }

    /// <summary>Whether a copy's title is only part of it (<see cref="Span"/>).</summary>
    internal static bool Spanned(Copy copy) => Span(copy) is var (s, e) && (s > 0 || e < copy.DurationMs);

    /// <summary>The title's length in a copy, its span's (<see cref="Span"/>).</summary>
    internal static long DurationMs(Copy copy) => Span(copy) is var (s, e) ? Math.Max(e - s, 0) : 0;

    /// <summary>
    /// A copy's chapters as Jellyfin keeps a local file's: a chapter without a name named as Jellyfin names one (<paramref name="unnamed"/> of its number from 1), no images.
    /// For a title that is a span of its copy, the chapters within it, from
    /// its start (the one it starts in, at 0).
    /// </summary>
    internal static List<ChapterInfo> Chapters(Copy copy, Func<int, string> unnamed)
    {
        var (start, end) = Span(copy);
        var named = copy.Chapters.Select((c, i) => (c.StartMs, Name: string.IsNullOrWhiteSpace(c.Name) ? unnamed(i + 1) : c.Name!)).ToList();
        if (Spanned(copy))
        {
            var before = named.LastOrDefault(c => c.StartMs <= start);
            named = named.Where(c => c.StartMs > start && c.StartMs < end).Select(c => (c.StartMs - start, c.Name)).ToList();
            if (before.Name is not null)
            {
                named.Insert(0, (0, before.Name));
            }
        }

        return named.Select(c => new ChapterInfo { StartPositionTicks = c.StartMs * TimeSpan.TicksPerMillisecond, Name = c.Name }).ToList();
    }

    /// <summary>A copy's skip markers within its title's span, from its start (<see cref="Span"/>).</summary>
    internal static List<CopySegment> Segments(Copy copy)
    {
        if (!Spanned(copy))
        {
            return copy.Segments.ToList();
        }

        var (start, end) = Span(copy);
        return copy.Segments
            .Select(m => new CopySegment(m.Kind, Math.Max(m.StartMs, start) - start, Math.Min(m.EndMs, end) - start))
            .Where(m => m.EndMs > m.StartMs).ToList();
    }

    /// <summary>The overall bitrate of a copy, bits per second: size × 8 / duration; null without a duration.</summary>
    internal static int? Bitrate(Copy copy) =>
        copy.DurationMs > 0 ? (int)Math.Min(int.MaxValue, copy.Size * 8 * 1000 / copy.DurationMs) : null;

    /// <summary>
    /// The labels of a holder's copies, in their order: the height the
    /// picture counts as, its dynamic range, the edition, 3D, the codec only
    /// where two copies would read the same without it, and a number where
    /// they still do.
    /// </summary>
    internal static List<string> Labels(IReadOnlyList<Copy> copies)
    {
        var bases = copies.Select(c => Label(c, codec: false)).ToList();
        var labels = new List<string>();
        for (var i = 0; i < copies.Count; i++)
        {
            var same = bases.Count(b => b == bases[i]) > 1;
            labels.Add(same ? Label(copies[i], codec: true) : bases[i]);
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < labels.Count; i++)
        {
            var n = seen[labels[i]] = seen.GetValueOrDefault(labels[i]) + 1;
            if (n > 1 || labels.Count(l => l == labels[i]) > 1)
            {
                labels[i] = labels[i] + " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        return labels;
    }

    private static string Label(Copy c, bool codec)
    {
        var parts = new List<string>();
        if (c.Video is { } v)
        {
            var counted = (long)v.Width * 9 > (long)v.Height * 16 ? (long)v.Width * 9 / 16 : v.Height;
            parts.Add(counted.ToString(CultureInfo.InvariantCulture) + (v.Interlaced ? "i" : "p"));
            if (Range(v) is { } range)
            {
                parts.Add(range);
            }
        }

        if (c.Edition is { } edition)
        {
            parts.Add(Edition(edition));
        }

        if (c.ThreeD is not null)
        {
            parts.Add("3D");
        }

        if (codec && c.Video is { } cv)
        {
            parts.Add(CodecName(cv.Codec));
        }

        return parts.Count > 0 ? string.Join(' ', parts) : c.Video is null ? "Audio" : "Video";
    }

    // A picture's dynamic range as players name it; null for SDR.
    private static string? Range(VideoStream v) =>
        v.Dv is not null ? "Dolby Vision"
        : v.Hdr.Contains("hdr10plus", StringComparer.Ordinal) ? "HDR10+"
        : v.Colour?.Transfer == VideoColour.TransferPq ? "HDR10"
        : v.Colour?.Transfer == VideoColour.TransferHlg ? "HLG"
        : null;

    private static string Edition(string token) => token switch
    {
        "theatrical" => "Theatrical",
        "directors" => "Director's Cut",
        "extended" => "Extended",
        "unrated" => "Unrated",
        "remastered" => "Remastered",
        "special" => "Special Edition",
        "imax" => "IMAX",
        "uncut" => "Uncut",
        "final" => "Final Cut",
        _ => char.ToUpperInvariant(token[0]) + token[1..].Replace('-', ' '),
    };

    private static string CodecName(string codec) => codec switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vvc" => "VVC",
        "mpeg2video" => "MPEG-2",
        "mpeg4" => "MPEG-4",
        _ => codec.ToUpperInvariant(),
    };

    /// <summary>The 3D layout of a copy as Jellyfin's; null for 2D or a layout it has no name for.</summary>
    internal static Video3DFormat? ThreeD(string? threeD) => threeD switch
    {
        // As Jellyfin's reading of a file's stereo side data names them.
        "sbs" or "sbs-rl" => Video3DFormat.FullSideBySide,
        "tab" or "tab-rl" => Video3DFormat.FullTopAndBottom,
        "mvc" => Video3DFormat.MVC,
        _ => null,
    };
}
