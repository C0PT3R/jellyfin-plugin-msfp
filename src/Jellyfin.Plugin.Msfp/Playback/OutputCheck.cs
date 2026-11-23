using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Msfp.Protocol;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// What a delivery's first output file holds (its init segment in
/// fragmented MP4, else its first segment), as ffprobe reads it, checked
/// against the order: the command line said what Jellyfin meant to make,
/// this is what it made. A difference refuses the play; what passes gives
/// the master playlist its codecs strings.
/// </summary>
internal static class OutputCheck
{
    /// <summary>What was made otherwise than ordered, as closed codes.</summary>
    public static List<string> Differences(DeliveryOrder o, Copy made, Copy source, string audio)
    {
        var d = new List<string>();
        if (o.Video is { } v)
        {
            if (made.Video is not { } mv)
            {
                d.Add("out-no-video");
            }
            else if (v.Copy)
            {
                if (source.Video is { } sv && sv.Codec != mv.Codec)
                {
                    d.Add("out-video-codec");
                }
            }
            else
            {
                if (mv.Codec != v.Codec)
                {
                    d.Add("out-video-codec");
                }

                if (mv.Height > v.MaxHeight)
                {
                    d.Add("out-height");
                }

                if (v.PixelFormat == "yuv420p" && mv.BitDepth is { } depth && depth != 8)
                {
                    d.Add("out-bit-depth");
                }

                if (mv.Colour?.HighRange == true || mv.Dv is not null)
                {
                    d.Add("out-hdr");
                }

                if (v.Level == Orders.BaselineLevel && !AvcWithin(mv.Codecs, 41))
                {
                    d.Add("out-profile-level");
                }

                if (v.Deinterlace && mv.Interlaced)
                {
                    d.Add("out-interlaced");
                }
            }
        }

        if (o.Audio is { } a)
        {
            var src = source.AudioTracks.FirstOrDefault(t => t.Id == audio);
            var ma = made.AudioTracks.FirstOrDefault();
            if (ma is null)
            {
                d.Add("out-no-audio");
            }
            else if (a.Copy)
            {
                if (src is not null && src.Codec != ma.Codec)
                {
                    d.Add("out-audio-codec");
                }
            }
            else
            {
                if (ma.Codec != a.Codec)
                {
                    d.Add("out-audio-codec");
                }

                if (ma.Channels is { } ch && ch > a.Channels)
                {
                    d.Add("out-audio-channels");
                }

                if (a.MaxSampleRate > 0 && ma.SampleRate is { } rate && rate > a.MaxSampleRate)
                {
                    d.Add("out-sample-rate");
                }

                if (a.Codec == "aac" && ma.Codecs is { Length: > 0 } codecs && codecs != "mp4a.40.2")
                {
                    d.Add("out-aac-profile");
                }
            }
        }

        return d;
    }

    /// <summary>The codecs strings of what was made, as the master carries them (avc3 and hev1 as avc1 and hvc1, parameter sets out of band in segments).</summary>
    public static (string? Video, string? Audio) Codecs(DeliveryOrder o, Copy made)
    {
        string? video = null;
        if (o.Video is { Copy: false } && made.Video is { Codecs: { Length: > 0 } } mv)
        {
            video = HlsMaster.CopiedVideoCodecs(mv with { Dv = null }).Base;
        }

        var audio = o.Audio is { Copy: false } && made.AudioTracks.FirstOrDefault() is { Codecs: { Length: > 0 } c } ? c : null;
        return (video, audio);
    }

    /// <summary>An avc1/avc3 string of Constrained Baseline, Main or High at a level up to maxLevel.</summary>
    internal static bool AvcWithin(string? codecs, int maxLevel)
    {
        if (codecs is not { Length: 11 } || codecs[4] != '.' || codecs[..4] is not ("avc1" or "avc3")
            || !int.TryParse(codecs.AsSpan(5), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        var profile = b >> 16;
        var level = b & 0xff;
        return profile is 66 or 77 or 100 && level >= 10 && level <= maxLevel;
    }
}
