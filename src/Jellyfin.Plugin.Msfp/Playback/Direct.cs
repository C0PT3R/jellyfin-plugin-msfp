using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;
using Msfp;
using Picture = Msfp.Picture;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>What a copy's file is, for choosing how to serve it.</summary>
internal sealed record FileFacts
{
    /// <summary>Its container, as MSFP names them; "" for one MSFP doesn't list.</summary>
    public string Container { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    public MediaStream? Video { get; init; }

    public Picture? Picture { get; init; }

    public IReadOnlyList<MediaStream> AudioTracks { get; init; } = [];

    /// <summary>The audio track a player takes by default.</summary>
    public MediaStream? Audio { get; init; }

    /// <summary>Its bitrate, from its size and duration; 0 when unknown.</summary>
    public int BitrateKbps { get; init; }
}

/// <summary>
/// Whether a viewer's device plays a file as it is (MSFP video section
/// 5.1): its container, its video codec, the audio the engine chose (or the
/// default one: with direct play the viewer's player picks the track, MSFP
/// media section 1), its HDR, its height and its bitrate all within what
/// the device said, and the cap in effect. Nothing said about containers or
/// video codecs: never.
/// </summary>
internal static class Direct
{
    public static bool Plays(FileFacts file, StartRequest request)
    {
        var caps = request.Capabilities;
        if (file.Container.Length == 0 || file.Video?.Codec is not { Length: > 0 } video
            || caps.Containers?.Contains(file.Container, StringComparer.Ordinal) != true
            || caps.VideoCodecs?.Contains(video.ToLowerInvariant(), StringComparer.Ordinal) != true)
        {
            return false;
        }

        var audio = file.Audio;
        if (request.AudioTrack.StartsWith('a') && int.TryParse(request.AudioTrack.AsSpan(1), out var index))
        {
            audio = file.AudioTracks.FirstOrDefault(a => a.Index == index) ?? audio;
        }

        // Nothing said about audio codecs: AAC only.
        var audioCodecs = caps.AudioCodecs is { Count: > 0 } ac ? ac : ["aac"];
        if (audio?.Codec is { } codec && !audioCodecs.Contains(codec.ToLowerInvariant(), StringComparer.Ordinal))
        {
            return false;
        }

        if (caps.MaxAudioChannels > 0 && audio?.Channels > caps.MaxAudioChannels)
        {
            return false;
        }

        if (file.Picture is { } p)
        {
            // Standard range only unless the device said which HDR it plays;
            // Dolby Vision with an HDR10 or HLG base plays as that base.
            var hdr = p.Hdr ?? [];
            if (hdr.Count > 0 && !hdr.Any(h => caps.Hdr?.Contains(h, StringComparer.Ordinal) == true))
            {
                return false;
            }

            if (caps.MaxHeight > 0 && global::Msfp.Protocol.Video.StepHeight(p.Height) > caps.MaxHeight)
            {
                return false;
            }
        }

        var cap = new[] { request.MaxBitrateKbps, caps.MaxBitrateKbps }.Where(c => c > 0).DefaultIfEmpty(0).Min();
        return cap == 0 || file.BitrateKbps == 0 || file.BitrateKbps <= cap;
    }
}
