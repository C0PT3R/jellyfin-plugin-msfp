using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Msfp.Protocol;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// The master playlist of a delivery, written here (Jellyfin's is
/// unusable: a wrong BANDWIDTH, a fallback variant of its own, keys in its
/// URIs): one variant, <see cref="MediaPlaylist"/>, with what MSFP media
/// section 3.1 and playback section 2.7 ask of it: BANDWIDTH, CODECS,
/// RESOLUTION, VIDEO-RANGE, and SUPPLEMENTAL-CODECS when Dolby Vision is
/// kept. Overfin's master.go is the reference.
/// </summary>
internal static class HlsMaster
{
    /// <summary>The media playlist's name in the master.</summary>
    public const string MediaPlaylist = "v0.m3u8";

    /// <summary>The codecs strings of audio, by codec: those an encoder writes, and those of a copied track whose own isn't known.</summary>
    internal static readonly Dictionary<string, string> AudioCodecs = new(StringComparer.Ordinal)
    {
        ["aac"] = "mp4a.40.2", ["ac3"] = "ac-3", ["eac3"] = "ec-3", ["mp3"] = "mp4a.40.34", ["opus"] = "Opus", ["flac"] = "fLaC",
        ["alac"] = "alac", ["dts"] = "dtsc", ["truehd"] = "mlpa",
    };

    /// <summary>
    /// The master of a delivery made of copy c, its audio track audio, as
    /// ordered (o) and made (m); output, when read, is what the first
    /// output file holds (its video's and audio's codecs strings, by
    /// ffprobe), which wins over what is foreseen from the order.
    /// </summary>
    public static byte[] Of(Copy c, string audio, DeliveryOrder o, DeliveryMade? m, (string? Video, string? Audio) output = default)
    {
        var attrs = new List<string>();
        var (codecs, supplemental) = MasterCodecs(c, audio, o, m, output);
        attrs.Add("BANDWIDTH=" + Bandwidth(c, o, m).ToString(CultureInfo.InvariantCulture));
        if (codecs.Length > 0)
        {
            attrs.Add("CODECS=\"" + codecs + "\"");
        }

        var (w, h) = Resolution(c, o, m);
        if (w > 0 && h > 0)
        {
            attrs.Add("RESOLUTION=" + w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture));
        }

        if (o.Video is not null && VideoRange(c, o, m) is { } range)
        {
            attrs.Add("VIDEO-RANGE=" + range);
        }

        if (supplemental.Length > 0)
        {
            attrs.Add("SUPPLEMENTAL-CODECS=\"" + supplemental + "\"");
        }

        var version = o.Segments == "mp4" ? 7 : 3;
        var b = new StringBuilder();
        b.Append("#EXTM3U\n#EXT-X-VERSION:").Append(version.ToString(CultureInfo.InvariantCulture)).Append("\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        b.Append("#EXT-X-STREAM-INF:").Append(string.Join(',', attrs)).Append('\n').Append(MediaPlaylist).Append('\n');
        return Encoding.UTF8.GetBytes(b.ToString());
    }

    private static (string Codecs, string Supplemental) MasterCodecs(Copy c, string audio, DeliveryOrder o, DeliveryMade? m, (string? Video, string? Audio) output)
    {
        var list = new List<string>();
        var supplemental = string.Empty;
        if (o.Video is { } v)
        {
            if (v.Copy && c.Video is not null)
            {
                var (b, dv) = CopiedVideoCodecs(c.Video);
                if (b.Length > 0)
                {
                    list.Add(b);
                }

                if (o.Segments == "mp4")
                {
                    supplemental = dv;
                }
            }
            else if (!v.Copy)
            {
                var height = m is { Height: > 0 } ? m.Height : v.MaxHeight;
                if ((output.Video ?? MadeVideoCodecs(v.Codec!, height, v.Level)) is { Length: > 0 } s)
                {
                    list.Add(s);
                }
            }
        }

        if (o.Audio is { } a)
        {
            string? s;
            if (a.Copy)
            {
                var track = c.AudioTracks.FirstOrDefault(t => t.Id == audio);
                s = track?.Codecs is { Length: > 0 } known ? known : track is not null ? AudioCodecs.GetValueOrDefault(track.Codec) : null;
            }
            else
            {
                s = output.Audio ?? AudioCodecs.GetValueOrDefault(a.Codec!);
            }

            if (s is { Length: > 0 })
            {
                list.Add(s);
            }
        }

        return (string.Join(',', list), supplemental);
    }

    /// <summary>
    /// A copied picture's codecs string as segments carry it (parameter
    /// sets out of band: hvc1, avc1), and, for Dolby Vision with a
    /// compatible base (profile 8), its Dolby Vision entry and brand; Dolby
    /// Vision without one (profile 5) is its Dolby Vision entry itself.
    /// </summary>
    internal static (string Base, string Dv) CopiedVideoCodecs(VideoStream v)
    {
        var b = v.Codecs ?? string.Empty;
        var dot = b.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            var four = b[..dot];
            b = four switch
            {
                "hev1" => "hvc1" + b[dot..],
                "avc3" => "avc1" + b[dot..],
                _ => b,
            };
        }

        if (v.Dv is null || Rfc6381.Read(v.Codec, b, v.BitDepth, v.Dv, out _) is not { DolbyVision: { Length: > 0 } dv })
        {
            return (b, string.Empty);
        }

        var brand = v.Dv.Compatibility switch { 1 => "db1p", 2 => "db2g", 4 => "db4h", 6 => "db1p", _ => string.Empty };
        return brand.Length == 0 ? (dv, string.Empty) : (b, dv + "/" + brand);
    }

    /// <summary>
    /// A converted picture's codecs string, by its codec and height, when
    /// its output wasn't read: what Jellyfin's encoders write (8-bit, High
    /// or Main), at the level the height calls for (the baseline's 4.1 at
    /// most).
    /// </summary>
    internal static string MadeVideoCodecs(string codec, int height, string? level = null)
    {
        string Step(string a, string b, string c) => height <= 720 ? a : height <= 1080 ? b : c;
        return codec switch
        {
            "h264" when level == Orders.BaselineLevel => Step("avc1.64001f", "avc1.640029", "avc1.640029"),
            "h264" => Step("avc1.64001f", "avc1.640028", "avc1.640033"),
            "hevc" => Step("hvc1.1.6.L93.B0", "hvc1.1.6.L120.B0", "hvc1.1.6.L153.B0"),
            "av1" => Step("av01.0.05M.08", "av01.0.08M.08", "av01.0.13M.08"),
            _ => string.Empty,
        };
    }

    // The peak bits a second of what is served: the copy's own rate for a
    // stream copied, the cap made of one converted, with the audio's share.
    internal static long Bandwidth(Copy c, DeliveryOrder o, DeliveryMade? m)
    {
        long total = 0;
        var fileRate = c.DurationMs > 0 ? c.Size * 8 * 1000 / c.DurationMs : 0;
        var videoCopied = o.Video is { Copy: true };
        if (videoCopied || (o.Video is null && o.Audio is { Copy: true }))
        {
            total = fileRate;
        }

        if (o.Video is { Copy: false } v)
        {
            var rate = v.MaxBitrate;
            if (m?.Made.VideoBitrate is { } made && made > 0)
            {
                rate = Math.Min(rate, made);
            }

            total = rate;
        }

        if (o.Audio is { Copy: false } a)
        {
            total += a.Channels > 2 ? 384_000 : 192_000;
        }
        else if (o.Audio is not null && !videoCopied && o.Video is not null)
        {
            total += 640_000; // a copied track beside a converted picture
        }

        return total > 0 ? total : 1_000_000;
    }

    // The picture served: the copy's when copied, else what was made.
    internal static (int Width, int Height) Resolution(Copy c, DeliveryOrder o, DeliveryMade? m)
    {
        switch (o.Video)
        {
            case null:
                return (0, 0);
            case { Copy: true }:
                return c.Video is { } cv ? (cv.Width, cv.Height) : (0, 0);
        }

        if (m is { Width: > 0, Height: > 0 })
        {
            return (m.Width, m.Height);
        }

        if (c.Video is not { Width: > 0, Height: > 0 } src)
        {
            return (0, 0);
        }

        var h = Math.Min(o.Video.MaxHeight, src.Height);
        var w = (int)((h * (double)src.Width / src.Height / 2) + 0.5) * 2;
        return (w, h);
    }

    // The served picture's VIDEO-RANGE: PQ or HLG when the copy's range is
    // kept and is PQ or HLG (Dolby Vision with no base of another range:
    // PQ), else SDR.
    internal static string VideoRange(Copy c, DeliveryOrder o, DeliveryMade? m)
    {
        var v = o.Video!;
        var kept = v.Copy || (!v.ToneMap && m?.Made.ToneMapped != true);
        if (!kept || c.Video is not { } cv)
        {
            return "SDR";
        }

        switch (cv.Colour?.Transfer)
        {
            case VideoColour.TransferPq:
                return "PQ";
            case VideoColour.TransferHlg:
                return "HLG";
        }

        return cv.Dv is { Compatibility: 0 or 1 or 6 } && v.Copy ? "PQ" : "SDR";
    }
}
