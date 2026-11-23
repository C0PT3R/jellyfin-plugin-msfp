using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Msfp.Federation;

/// <summary>
/// A copy's picture as the plugin ranks copies and holders: its height
/// and HDR formats. The engine's own (Msfp.Picture) went with MSFP's
/// revision (copies carry facts); kept here with the same JSON and order,
/// until phase 2 (owner) and phase 5 (viewer) rank copies by their facts.
/// </summary>
public sealed record Picture
{
    /// <summary>The picture's height.</summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>Its HDR formats; null for none.</summary>
    [JsonPropertyName("hdr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Hdr { get; set; }

    /// <summary>Whether this picture is better than other: taller, else with more HDR formats.</summary>
    public bool Better(Picture other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Height != other.Height ? Height > other.Height : (Hdr?.Count ?? 0) > (other.Hdr?.Count ?? 0);
    }

    /// <summary>The best of pictures, each counted at its height's step and with its known HDR formats; null for none.</summary>
    public static Picture? Best(IEnumerable<Picture> pictures)
    {
        ArgumentNullException.ThrowIfNull(pictures);
        Picture? best = null;
        foreach (var p in pictures)
        {
            var hdr = global::Msfp.Protocol.Video.CleanHdr(p.Hdr ?? []);
            var w = new Picture { Height = global::Msfp.Protocol.Video.StepHeight(p.Height), Hdr = hdr.Count > 0 ? [.. hdr] : null };
            if (w.Height > 0 && (best is null || w.Better(best)))
            {
                best = w;
            }
        }

        return best;
    }
}
