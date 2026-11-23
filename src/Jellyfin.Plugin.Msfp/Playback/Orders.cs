using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;
using Msfp;
using Msfp.Protocol;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// What a peer's play makes of a copy, as an order of Jellyfin's
/// transcoder (Overfin's executor is the reference: internal/delivery's
/// Plan): the engine has decided what is served (<see cref="StartRequest.Served"/>);
/// here that becomes Jellyfin's IDs and closed values, or a refusal of what
/// this server doesn't make, before anything starts. Nothing of the request
/// but registry tokens, track IDs and numbers reaches the order.
/// </summary>
internal static class Orders
{
    /// <summary>The baseline's level (MSFP playback section 2.7): H.264 High at most 4.1.</summary>
    public const string BaselineLevel = "41";

    /// <summary>The most macroblocks a frame has at H.264 level 4.1 (MaxFS).</summary>
    public const int Level41Macroblocks = 8192;

    /// <summary>The baseline's highest audio sample rate.</summary>
    public const int BaselineSampleRate = 48_000;

    /// <summary>
    /// The order of a remux, a transcode or the baseline of copy (as the
    /// catalog sends it) of version, its tracks mapped to Jellyfin's
    /// indexes, Jellyfin's streams of the version, and what this Jellyfin
    /// makes now. Throws <see cref="DeliveryRefusedException"/>.
    /// </summary>
    public static DeliveryOrder Plan(StartRequest r, Copy c, IReadOnlyDictionary<string, int> tracks, Guid version, IReadOnlyList<MediaStream> streams, EncodersNow enc)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(c);
        var served = r.Served;
        var baseline = r.Delivery.Length == 0;
        var t = served.Transcode ?? new TranscodeTargets();
        if (t.Range == Names.RangeKeep)
        {
            // Never on the CPU; on a GPU not this release (decision 5).
            throw new DeliveryRefusedException("range-keep");
        }

        var picture = c.Video is not null || c.MediaType.StartsWith("video/", StringComparison.Ordinal);
        var videoOut = picture ? served.Video : string.Empty;
        if (picture && c.Video is null)
        {
            // A picture whose facts aren't read yet: converted, never said
            // to be within the baseline or copied by a transcode.
            if (r.Delivery == global::Msfp.Protocol.Playback.DeliveryTranscode && videoOut == Names.Copy)
            {
                throw new DeliveryRefusedException("not-read");
            }

            if (r.Delivery != global::Msfp.Protocol.Playback.DeliveryRemux)
            {
                videoOut = videoOut is { Length: > 0 } v && v != Names.Copy ? v : Names.BaselineVideoCodec;
            }
        }

        if (t.BurnSubtitle.Length > 0 && !enc.Burn)
        {
            // Burned subtitles only where the operator turned them on
            // (Settings; off by default).
            throw new DeliveryRefusedException("burn-off");
        }

        var audio = r.AudioTrack;
        var videoConverted = picture && videoOut.Length > 0 && videoOut != Names.Copy;
        var audioConverted = audio.Length > 0 && served.Audio != Names.Copy;
        var jvideo = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video && !s.IsExternal);
        var o = new DeliveryOrder
        {
            ItemId = version.ToString("N"),
            SourceId = version.ToString("N"),
            StartTicks = Math.Clamp(r.StartMs, 0, Math.Max(c.DurationMs, 0)) * TimeSpan.TicksPerMillisecond,
        };

        if (picture)
        {
            var v = new DeliveryVideo { Copy = !videoConverted };
            if (videoConverted)
            {
                if (!enc.Video.Contains(videoOut))
                {
                    throw new DeliveryRefusedException("video-codec-not-made");
                }

                v.Codec = videoOut;
                var height = c.Video?.Height ?? jvideo?.Height ?? 1080;
                var width = c.Video?.Width ?? jvideo?.Width ?? 0;
                if (t.MaxHeight > 0)
                {
                    height = Math.Min(height, t.MaxHeight);
                }

                if (baseline)
                {
                    // Within level 4.1: 1080 lines at most, and a frame of
                    // at most 8,192 macroblocks (a wide picture is lower).
                    height = BaselineHeight(width, Math.Min(height, 1080), c.Video?.Height ?? jvideo?.Height ?? 0);
                    v.Profile = "high";
                    v.Level = BaselineLevel;
                    v.Deinterlace = c.Video?.Interlaced == true || jvideo?.IsInterlaced == true;
                }

                v.MaxHeight = height;
                var kbps = t.MaxBitrateKbps > 0 ? t.MaxBitrateKbps : DefaultKbps(height);
                if (r.MaxBitrateKbps > 0)
                {
                    kbps = Math.Min(kbps, Math.Max(r.MaxBitrateKbps - AudioKbps(served, t), 100));
                }

                v.MaxBitrate = kbps * 1000;
                // An HDR picture made SDR: tone mapped (range sdr, the only
                // one made this release).
                v.ToneMap = c.Video is { } cv && (cv.Colour?.HighRange == true || cv.Dv is not null);
                v.PixelFormat = enc.Hardware ? null : "yuv420p";
                if (t.BurnSubtitle.Length > 0)
                {
                    if (!tracks.TryGetValue(t.BurnSubtitle, out var burn))
                    {
                        throw new DeliveryRefusedException("track-not-mapped");
                    }

                    v.BurnIndex = burn;
                }
            }
            else if (t.BurnSubtitle.Length > 0)
            {
                throw new DeliveryRefusedException("burn-with-copy");
            }

            o.Video = v;
        }

        if (audio.Length > 0)
        {
            if (!tracks.TryGetValue(audio, out var index))
            {
                throw new DeliveryRefusedException("track-not-mapped");
            }

            var a = new DeliveryAudio { Index = index, Copy = !audioConverted };
            if (audioConverted)
            {
                if (!enc.Audio.Contains(served.Audio))
                {
                    throw new DeliveryRefusedException("audio-codec-not-made");
                }

                a.Codec = served.Audio;
                a.Channels = t.AudioChannels > 0 ? t.AudioChannels : Names.BaselineChannels;
                if (c.AudioTracks.FirstOrDefault(x => x.Id == audio) is { Channels: > 0 } track)
                {
                    a.Channels = Math.Min(a.Channels, track.Channels.Value);
                }

                if (baseline)
                {
                    a.MaxSampleRate = BaselineSampleRate;
                }
            }

            o.Audio = a;
        }

        o.Segments = SegmentsFor(r, c, o, audio);
        if (o.Video is { Copy: true } copied && o.Segments == "mp4")
        {
            copied.Tag = SampleEntry(c.Video);
        }
        else if (o.Video is { Copy: false, Codec: "hevc" } hevc && o.Segments == "mp4")
        {
            hevc.Tag = "hvc1";
        }

        return o;
    }

    /// <summary>
    /// The sample entry a copied picture is written with in fragmented MP4
    /// (MSFP media section 3.1, msfp-spec 104f093): HEVC hvc1; Dolby Vision
    /// with a compatible base (profile 8) keeps hvc1; with none (profile 5)
    /// dvh1. Jellyfin writes hvc1 for profile 8 and dvh1 for every other
    /// profile: one with a compatible base but another profile (7), and
    /// Dolby Vision on another codec, are refused in fragmented MP4 (MPEG-TS
    /// still made). Null for a codec with nothing to check.
    /// </summary>
    internal static string? SampleEntry(VideoStream? v)
    {
        if (v is null)
        {
            return null;
        }

        if (v.Dv is { } dv)
        {
            if (v.Codec != "hevc")
            {
                throw new DeliveryRefusedException("dv-sample-entry");
            }

            return dv switch
            {
                { Compatibility: 0 } => "dvh1",
                { Profile: 8 } => "hvc1",
                _ => throw new DeliveryRefusedException("dv-sample-entry"),
            };
        }

        return v.Codec == "hevc" ? "hvc1" : null;
    }

    /// <summary>
    /// The segment format: a remux's as its media type says; a transcode's
    /// or the baseline's, MPEG-TS for H.264 (or no picture) with what TS
    /// carries, fragmented MP4 for the rest; refused when neither carries
    /// what is served.
    /// </summary>
    internal static string SegmentsFor(StartRequest r, Copy c, DeliveryOrder o, string audio)
    {
        var codecs = new List<string>();
        if (o.Video is { } v)
        {
            if (!v.Copy)
            {
                codecs.Add(v.Codec!);
            }
            else if (c.Video is not null)
            {
                codecs.Add(c.Video.Codec);
            }
        }

        if (o.Audio is { } a)
        {
            if (!a.Copy)
            {
                codecs.Add(a.Codec!);
            }
            else if (c.AudioTracks.FirstOrDefault(x => x.Id == audio) is { } track)
            {
                codecs.Add(track.Codec);
            }
        }

        bool Carries(string sub) => codecs.All(x => Encoders.DefaultSegments[sub].Contains(x));
        var asked = r.Served.Segments;
        switch (asked)
        {
            case "mp2t":
                return Carries("mp2t") ? "ts" : throw new DeliveryRefusedException("segments-cannot-carry");
            case "mp4":
                return Carries("mp4") ? "mp4" : throw new DeliveryRefusedException("segments-cannot-carry");
        }

        if (Carries("mp2t") && (o.Video is null || codecs.Count > 0 && codecs[0] == Names.BaselineVideoCodec))
        {
            return "ts";
        }

        return Carries("mp4") ? "mp4" : throw new DeliveryRefusedException("segments-cannot-carry");
    }

    /// <summary>
    /// The baseline's height of a picture width × sourceHeight scaled to at
    /// most cap lines: the tallest even height whose frame keeps within
    /// level 4.1's 8,192 macroblocks.
    /// </summary>
    internal static int BaselineHeight(int width, int cap, int sourceHeight)
    {
        var h = Math.Max(cap / 2 * 2, 2);
        if (width <= 0 || sourceHeight <= 0)
        {
            return h;
        }

        var aspect = (double)width / sourceHeight;
        while (h > 144)
        {
            var w = (int)Math.Round(h * aspect / 2) * 2;
            if (((w + 15) / 16) * ((h + 15) / 16) <= Level41Macroblocks)
            {
                break;
            }

            h -= 2;
        }

        return h;
    }

    /// <summary>A converted picture's bitrate when none is asked: what its height needs, within Jellyfin's table of sizes by bitrate.</summary>
    internal static int DefaultKbps(int height) => height switch
    {
        <= 480 => 4_000,
        <= 720 => 8_000,
        <= 1080 => 16_000,
        <= 1440 => 30_000,
        _ => 50_000,
    };

    // The share of a cap the audio takes: converted, what Jellyfin gives
    // it; copied, a generous guess.
    private static int AudioKbps(Served s, TranscodeTargets t)
    {
        if (s.Audio == Names.Copy || s.Audio.Length == 0)
        {
            return 640;
        }

        return t.AudioChannels > 2 ? 384 : 192;
    }

    /// <summary>The source an order reads, from Jellyfin's streams of the version.</summary>
    public static Source SourceOf(IReadOnlyList<MediaStream> streams, int audioIndex)
    {
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video && !s.IsExternal);
        var audio = audioIndex >= 0 ? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && !s.IsExternal && s.Index == audioIndex) : null;
        if (audioIndex >= 0 && audio is null)
        {
            throw new DeliveryRefusedException("no-such-audio");
        }

        return new Source(video?.Width, video?.Height, video?.VideoRange == VideoRange.HDR, audio?.Channels,
            video?.Codec?.ToLowerInvariant(), audio?.Codec?.ToLowerInvariant(), streams.Select(x => (x.Index, x.IsExternal)).ToList())
        {
            AudioSampleRate = audio?.SampleRate,
        };
    }
}
