using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Msfp.Playback;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class PgsTests
{
    // A segment: "PG", the presentation time (90 kHz), a decoding time,
    // the type, the payload's size and the payload.
    private static byte[] Segment(long seconds, byte type, byte[] payload)
    {
        var pts = seconds * 90_000;
        return [(byte)'P', (byte)'G', (byte)(pts >> 24), (byte)(pts >> 16), (byte)(pts >> 8), (byte)pts, 0, 0, 0, 0, type,
            (byte)(payload.Length >> 8), (byte)payload.Length, .. payload];
    }

    // A display set: a presentation segment, a window, a palette, an
    // object (a 2 × 1 bitmap) and the end.
    internal static byte[] Set(long seconds, byte objects = 1)
    {
        byte[] pcs = [0x07, 0x80, 0x04, 0x38, 0x10, 0, 1, 0x80, 0, 0, objects, .. (objects == 1 ? new byte[] { 0, 0, 0, 0, 0, 10, 0, 10 } : [])];
        using var output = new MemoryStream();
        output.Write(Segment(seconds, 0x16, pcs));
        output.Write(Segment(seconds, 0x17, [1, 0, 0, 10, 0, 10, 0, 2, 0, 1]));
        if (objects == 1)
        {
            output.Write(Segment(seconds, 0x14, [0, 0, 1, 235, 128, 128, 255]));
            output.Write(Segment(seconds, 0x15, [0, 0, 0, 0xC0, 0, 0, 8, 0, 2, 0, 1, 1, 1, 0, 0]));
        }

        output.Write(Segment(seconds, 0x80, []));
        return output.ToArray();
    }

    [Fact]
    public void ATrackIsCutIntoPartsAndJoinedBack()
    {
        // Shown at 5 s, cleared at 9 s; at 115 s (in part 0 and part 1's
        // early 10 seconds), cleared at 118 s; at 300 s (part 2).
        byte[] track = [.. Set(5), .. Set(9, 0), .. Set(115), .. Set(118, 0), .. Set(300), .. Set(303, 0)];
        var sets = Pgs.Sets(track)!;
        Assert.Equal(6, sets.Count);
        Assert.Equal(115L * 90_000, sets[2].Pts);

        var parts = Enumerable.Range(0, 4).Select(n => Pgs.Part(sets, n)).ToList();
        Assert.Equal(4, Pgs.Sets(parts[0])!.Count);
        Assert.Equal(2, Pgs.Sets(parts[1])!.Count); // 115 and 118 again
        Assert.Equal(2, Pgs.Sets(parts[2])!.Count);
        Assert.Empty(parts[3]);                        // a stretch with none: empty

        Assert.Equal(track, Pgs.Join(parts));
    }

    [Fact]
    public void NotPgsIsRefused()
    {
        Assert.Null(Pgs.Sets([(byte)'X', (byte)'G', 0, 0, 0, 0, 0, 0, 0, 0, 0x80, 0, 0]));
        Assert.Null(Pgs.Sets(Set(1)[..20]));
        Assert.Empty(Pgs.Sets([])!);
    }
}
