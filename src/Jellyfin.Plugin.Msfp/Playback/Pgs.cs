using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// PGS subtitles as MSFP serves them (video sections 6.2 and 6.4): a
/// track's segments grouped into display sets (a presentation segment up
/// to the next one), cut into 2-minute parts by the presentation time of
/// each set, each part starting 10 seconds early; and, on the viewer's
/// side, parts joined back into one track, each set once.
/// </summary>
internal static class Pgs
{
    private const int Stretch = 120;
    private const int Early = 10;
    private const long Clock = 90_000;

    /// <summary>A display set: its presentation time (90 kHz) and its bytes.</summary>
    internal readonly record struct DisplaySet(long Pts, byte[] Bytes);

    /// <summary>
    /// The track's display sets, in order; segments before the first
    /// presentation segment are dropped. Null when the bytes aren't PGS
    /// segments up to their exact end.
    /// </summary>
    public static List<DisplaySet>? Sets(ReadOnlySpan<byte> data)
    {
        var sets = new List<DisplaySet>();
        var start = -1;
        long pts = 0;
        var at = 0;
        while (at < data.Length)
        {
            if (data.Length - at < 13 || data[at] != (byte)'P' || data[at + 1] != (byte)'G')
            {
                return null;
            }

            var size = (data[at + 11] << 8) | data[at + 12];
            if (data.Length - at - 13 < size)
            {
                return null;
            }

            if (data[at + 10] == 0x16)
            {
                if (start >= 0)
                {
                    sets.Add(new DisplaySet(pts, data[start..at].ToArray()));
                }

                start = at;
                pts = ((long)data[at + 2] << 24) | ((long)data[at + 3] << 16) | ((long)data[at + 4] << 8) | data[at + 5];
            }

            at += 13 + size;
        }

        if (start >= 0)
        {
            sets.Add(new DisplaySet(pts, data[start..].ToArray()));
        }

        return sets;
    }

    /// <summary>Part n: the sets shown from n × 120 − 10 seconds to (n + 1) × 120; empty when none.</summary>
    public static byte[] Part(IReadOnlyList<DisplaySet> sets, int part)
    {
        var from = Math.Max(0, ((long)part * Stretch - Early) * Clock);
        var to = (long)(part + 1) * Stretch * Clock;
        using var output = new MemoryStream();
        foreach (var s in sets.Where(s => s.Pts >= from && s.Pts < to))
        {
            output.Write(s.Bytes);
        }

        return output.ToArray();
    }

    /// <summary>Parts joined into one track, a set the parts share (the 10 seconds) once.</summary>
    public static byte[] Join(IEnumerable<byte[]> parts)
    {
        using var output = new MemoryStream();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            foreach (var set in Sets(part) ?? [])
            {
                if (seen.Add(Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(set.Bytes))))
                {
                    output.Write(set.Bytes);
                }
            }
        }

        return output.ToArray();
    }
}
