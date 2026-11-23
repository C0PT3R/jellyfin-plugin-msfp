using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// What a Jellyfin ffmpeg command line says it makes: read, never changed
/// or run by the plugin (the arguments are Jellyfin's own). Ported from the
/// companion plugin's checked deliveries (0.9.0), with what MSFP's baseline
/// asks besides: the H.264 profile and level, the pixel format, the
/// deinterlacing, the audio's sample rate, the sample entry written in
/// fragmented MP4, and a burned subtitle.
/// </summary>
internal sealed partial class Made
{
    /// <summary>The video codecs a peer may ask, and Jellyfin's software encoder of each.</summary>
    public static readonly Dictionary<string, string> VideoEncoders = new(StringComparer.Ordinal)
    {
        ["h264"] = "libx264",
        ["hevc"] = "libx265",
        ["av1"] = "libsvtav1",
    };

    /// <summary>The audio codecs a peer may ask, and Jellyfin's encoders of each.</summary>
    public static readonly Dictionary<string, string[]> AudioEncoders = new(StringComparer.Ordinal)
    {
        ["aac"] = ["libfdk_aac", "aac"],
        ["ac3"] = ["ac3"],
        ["eac3"] = ["eac3"],
        ["mp3"] = ["libmp3lame"],
        ["opus"] = ["libopus"],
        ["flac"] = ["flac"],
    };

    public bool VideoCopied { get; init; }

    /// <summary>The codec converted to (a token); null when copied or unknown.</summary>
    public string? VideoCodec { get; init; }

    public int? VideoBitrate { get; init; }

    /// <summary>The height the scale filter caps the picture at; null when not read.</summary>
    public int? ScaleHeight { get; init; }

    /// <summary>A cap of the width alone (Jellyfin dropped the height).</summary>
    public int? ScaleWidthOnly { get; init; }

    public bool ToneMapped { get; init; }

    /// <summary>The encoder's profile (high, main…); null when none is set.</summary>
    public string? Profile { get; init; }

    /// <summary>The encoder's level as Jellyfin writes it (41); null when none is set.</summary>
    public int? Level { get; init; }

    /// <summary>The last pixel format the filters end on (yuv420p…); null when none.</summary>
    public string? PixelFormat { get; init; }

    public bool Deinterlaced { get; init; }

    /// <summary>The video's sample entry (hvc1, dvh1, avc1…); null when none is set.</summary>
    public string? VideoTag { get; init; }

    /// <summary>A subtitle burned into the picture.</summary>
    public bool Burned { get; init; }

    public bool AudioCopied { get; init; }

    public string? AudioCodec { get; init; }

    public int? AudioChannels { get; init; }

    /// <summary>The audio's sample rate when set (-ar); null when the source's is kept.</summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>The audio stream's map in the file (0:n), as ffmpeg counts the file's streams.</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>"ts" or "mp4".</summary>
    public string? Segments { get; init; }

    [GeneratedRegex(@"-codec:v:0 (\S+)")]
    private static partial Regex VCodec();

    [GeneratedRegex(@"-codec:a:0 (\S+)")]
    private static partial Regex ACodec();

    [GeneratedRegex(@"(?:-maxrate|-b:v) (\d{1,10})\b")]
    private static partial Regex VBitrate();

    [GeneratedRegex(@" -ac (\d{1,2})\b")]
    private static partial Regex AChannels();

    [GeneratedRegex(@" -ar (\d{4,6})\b")]
    private static partial Regex ARate();

    [GeneratedRegex(@"-map 0:(\d{1,5})\b")]
    private static partial Regex Maps();

    [GeneratedRegex(@"-hls_segment_type (\S+)")]
    private static partial Regex SegType();

    [GeneratedRegex(@"-profile:v(?::0)? (\S+)")]
    private static partial Regex VProfile();

    [GeneratedRegex(@" -level(?::v)?(?::0)? (\d{1,3})\b")]
    private static partial Regex VLevel();

    [GeneratedRegex(@"-tag:v(?::0)? (\S+)")]
    private static partial Regex VTag();

    [GeneratedRegex(@"format=([a-z0-9]+)")]
    private static partial Regex PixFmt();

    // The software scale filter's caps: min(W\,H*a) and min(W/a\,H).
    [GeneratedRegex(@"min\(\d+/a\\,(\d+)\)")]
    private static partial Regex ScaleH();

    [GeneratedRegex(@"scale=trunc\(min\(max\(iw\\,ih\*a\)\\,(\d+)\)/\d+\)\*\d+:trunc\(ow/a/2\)\*2")]
    private static partial Regex ScaleWOnly();

    // A subtitle burned in: the subtitles filter, or an overlay filter (of
    // a picture subtitle, its hardware forms, alphasrc's canvas) as a filter
    // of the graph, not an encoder's option (SVT-AV1's enable-overlays).
    [GeneratedRegex(@"(?:^|[\]\[,;""\s])(?:subtitles=|overlay(?:_[a-z0-9]+)?(?:=|[,;""\[]|$)|alphasrc)")]
    private static partial Regex Burn();

    [GeneratedRegex(@"^[0-9a-f]{32}$")]
    private static partial Regex Stem();

    /// <summary>Whether a name is one of Jellyfin's transcode outputs' (32 hex digits).</summary>
    public static bool IsOutputStem(string name) => Stem().IsMatch(name);

    private static int? Number(Match m) =>
        m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Reads a command line.</summary>
    public static Made Parse(string args)
    {
        var v = VCodec().Match(args).Groups[1].Value;
        var a = ACodec().Match(args).Groups[1].Value;
        var maps = Maps().Matches(args).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        var seg = SegType().Match(args).Groups[1].Value;
        var formats = PixFmt().Matches(args);
        var tag = VTag().Match(args);
        return new Made
        {
            VideoCopied = v == "copy",
            VideoCodec = v == "copy" ? null : VideoToken(v),
            VideoBitrate = Number(VBitrate().Match(args)),
            ScaleHeight = Number(ScaleH().Match(args)),
            ScaleWidthOnly = Number(ScaleWOnly().Match(args)),
            ToneMapped = args.Contains("tonemap", StringComparison.Ordinal),
            Profile = VProfile().Match(args) is { Success: true } p ? p.Groups[1].Value.Trim('"') : null,
            Level = Number(VLevel().Match(args)),
            PixelFormat = formats.Count > 0 ? formats[^1].Groups[1].Value : null,
            Deinterlaced = args.Contains("yadif", StringComparison.Ordinal) || args.Contains("bwdif", StringComparison.Ordinal),
            VideoTag = tag.Success ? tag.Groups[1].Value : null,
            Burned = Burn().IsMatch(args),
            AudioCopied = a == "copy",
            AudioCodec = a == "copy" ? null : AudioToken(a),
            AudioChannels = Number(AChannels().Match(args)),
            AudioSampleRate = Number(ARate().Match(args)),
            AudioStreamIndex = maps.Count > 1 ? maps[1] : null,
            Segments = seg switch { "mpegts" => "ts", "fmp4" => "mp4", _ => null },
        };
    }

    // An encoder's codec, as a token: Jellyfin's software encoder, or a
    // hardware one named after the codec (h264_vaapi, hevc_qsv…).
    private static string? VideoToken(string encoder)
    {
        foreach (var (token, sw) in VideoEncoders)
        {
            if (encoder == sw || encoder.StartsWith(token + "_", StringComparison.Ordinal))
            {
                return token;
            }
        }

        return encoder == "libaom-av1" ? "av1" : null;
    }

    private static string? AudioToken(string encoder)
    {
        foreach (var (token, encoders) in AudioEncoders)
        {
            if (encoders.Contains(encoder))
            {
                return token;
            }
        }

        return null;
    }

    /// <summary>The size made of a converted picture: the height its scale caps it at, the width its shape gives (even numbers, as Jellyfin's filter rounds them); zeros when copied or not read.</summary>
    public (int Width, int Height) Size(Source s)
    {
        if (VideoCopied || s is not { Width: > 0, Height: > 0 })
        {
            return (0, 0);
        }

        var aspect = (double)s.Width!.Value / s.Height!.Value;
        int h;
        if (ScaleHeight is { } sh)
        {
            h = Math.Min(sh, s.Height.Value);
        }
        else if (ScaleWidthOnly is { } wo)
        {
            h = (int)(Math.Min(wo, s.Width.Value) / aspect);
        }
        else
        {
            return (0, 0);
        }

        h = h / 2 * 2;
        var w = (int)(h * aspect / 2) * 2;
        return (w, h);
    }

    /// <summary>
    /// What was made otherwise than ordered, as closed codes; a bitrate at
    /// or below the one asked is fine (Jellyfin caps it near the source's).
    /// </summary>
    public List<string> Differences(DeliveryOrder o, Source s)
    {
        var d = new List<string>();
        if (o.Video is { } v)
        {
            if (v.Copy != VideoCopied)
            {
                d.Add(v.Copy ? "video-converted" : "video-copied");
            }
            else if (!v.Copy)
            {
                if (VideoCodec != v.Codec)
                {
                    d.Add("video-codec");
                }

                var want = Math.Min(v.MaxHeight, s.Height ?? v.MaxHeight);
                var (_, h) = Size(s);
                if (h > 0 && h < want - 2)
                {
                    d.Add("height-cap");
                }
                else if (h > want || (h == 0 && s.Height > v.MaxHeight))
                {
                    // Not scaled at all while the picture is taller than asked.
                    d.Add("height-over");
                }

                if (VideoBitrate is { } b && b > v.MaxBitrate)
                {
                    d.Add("bitrate-over");
                }

                if ((s.Hdr || v.ToneMap) && v.ToneMap != ToneMapped)
                {
                    d.Add(ToneMapped ? "tone-mapped" : "not-tone-mapped");
                }

                if (v.Profile is { Length: > 0 } profile && !string.Equals(Profile, profile, StringComparison.OrdinalIgnoreCase))
                {
                    d.Add("video-profile");
                }

                if (v.Level is { Length: > 0 } level && (Level is not { } l || l > int.Parse(level, CultureInfo.InvariantCulture)))
                {
                    d.Add("video-level");
                }

                if (v.PixelFormat is { Length: > 0 } pf && PixelFormat != pf)
                {
                    d.Add("pixel-format");
                }

                if (v.Deinterlace && !Deinterlaced)
                {
                    d.Add("not-deinterlaced");
                }

                if (v.BurnIndex >= 0 != Burned)
                {
                    d.Add(Burned ? "burned" : "not-burned");
                }
            }

            if (o.Segments == "mp4" && v.Tag is { Length: > 0 } tag && VideoTag != tag)
            {
                // The sample entry HEVC (and Dolby Vision) is written with in
                // fragmented MP4 (MSFP media section 3.1).
                d.Add("sample-entry");
            }
        }

        if (o.Audio is { } a)
        {
            var map = AudioStreamIndex is { } m ? DeliveryRules.JellyfinIndex(s.Streams, m) : -1;
            if (map != a.Index)
            {
                d.Add("audio-stream");
            }
            else if (a.Copy != AudioCopied)
            {
                d.Add(a.Copy ? "audio-converted" : "audio-copied");
            }
            else if (!a.Copy && AudioCodec != a.Codec)
            {
                d.Add("audio-codec");
            }
            else if (!a.Copy && AudioChannels is { } ch && ch > a.Channels)
            {
                d.Add("audio-channels");
            }
            else if (!a.Copy && a.MaxSampleRate > 0 && (AudioSampleRate ?? s.AudioSampleRate ?? int.MaxValue) > a.MaxSampleRate)
            {
                d.Add("audio-sample-rate");
            }
        }

        if (Segments != o.Segments)
        {
            d.Add("segments");
        }

        return d;
    }
}
