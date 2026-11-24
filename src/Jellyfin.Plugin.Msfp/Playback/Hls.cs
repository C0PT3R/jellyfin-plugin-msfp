using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// Jellyfin's media playlists for a delivery, rewritten to the subset MSFP
/// media section 3.1 allows (init.&lt;ext&gt; and seg&lt;n&gt;.&lt;ext&gt;),
/// each name kept with Jellyfin's URL for it. The master playlist is
/// <see cref="HlsMaster"/>'s.
/// </summary>
internal static partial class Hls
{
    [GeneratedRegex(@"^hls1/[A-Za-z0-9_-]+/(-?[0-9]{1,9})\.(ts|mp4|m4s|aac|mp3)\?")]
    private static partial Regex SegmentUrl();

    [GeneratedRegex(@"URI=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex UriAttribute();

    // A media playlist: init.<ext> and seg<n>.<ext>, durations kept, titles not.
    internal static string Media(string text, ConcurrentDictionary<string, string> names)
    {
        string[] keep = ["#EXT-X-VERSION:", "#EXT-X-TARGETDURATION:", "#EXT-X-MEDIA-SEQUENCE:", "#EXT-X-PLAYLIST-TYPE:"];
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var m = UriAttribute().Match(line);
                var seg = m.Success ? SegmentUrl().Match(m.Groups[1].Value) : Match.Empty;
                if (!seg.Success)
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                var name = "init." + seg.Groups[2].Value;
                names[name] = m.Groups[1].Value;
                sb.Append("#EXT-X-MAP:URI=\"").Append(name).Append("\"\n");
            }
            else if (line.StartsWith('#'))
            {
                if (line.Contains("URI=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    sb.Append(line.AsSpan(0, line.IndexOf(',', StringComparison.Ordinal) is var c and >= 0 ? c : line.Length)).Append(",\n");
                }
                else if (keep.Any(k => line.StartsWith(k, StringComparison.Ordinal))
                    || line is "#EXT-X-INDEPENDENT-SEGMENTS" or "#EXT-X-DISCONTINUITY" or "#EXT-X-ENDLIST")
                {
                    sb.Append(line).Append('\n');
                }
            }
            else
            {
                var seg = SegmentUrl().Match(line);
                if (!seg.Success)
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                var name = "seg" + seg.Groups[1].Value + "." + seg.Groups[2].Value;
                names[name] = line;
                sb.Append(name).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> Lines(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || lines[0] != "#EXTM3U")
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        return lines.Skip(1).Where(l => l.Length > 0);
    }
}
