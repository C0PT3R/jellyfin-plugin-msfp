using System.Collections.Generic;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// What a peer's device could play, as MSFP's draft before the revision
/// carried it (the engine's Capabilities, gone with proposal 17: the
/// viewer's server now names the delivery). Kept for
/// <see cref="Direct.Plays"/> and <see cref="Hls.Choose"/> until phase 3 of
/// the migration makes exactly the delivery asked; removed in phase 9.
/// </summary>
public sealed record Capabilities
{
    public List<string>? Containers { get; set; }

    public List<string>? AudioCodecs { get; set; }

    public List<string>? VideoCodecs { get; set; }

    public List<string>? Hdr { get; set; }

    public List<string>? Subtitles { get; set; }

    public int MaxAudioChannels { get; set; }

    public int MaxHeight { get; set; }

    public int MaxBitrateKbps { get; set; }
}

/// <summary>A play as the draft before the revision asked it of the owner: the device's capabilities, the audio track, the bitrate cap. Until phase 3 (see <see cref="Capabilities"/>).</summary>
public sealed record Asked
{
    public Capabilities Capabilities { get; init; } = new();

    public string AudioTrack { get; init; } = string.Empty;

    public int MaxBitrateKbps { get; init; }
}
