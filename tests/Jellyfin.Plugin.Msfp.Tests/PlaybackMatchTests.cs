using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Msfp.Viewing;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>Which of a title's playbacks a session's report is about (RemotePlays.Match).</summary>
public class PlaybackMatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);

    private static List<(string StreamId, string? SessionId, DateTimeOffset Opened)> Lives(params (string Stream, string? Session, int Second)[] l)
    {
        var list = new List<(string, string?, DateTimeOffset)>();
        foreach (var (stream, session, second) in l)
        {
            list.Add((stream, session, T0.AddSeconds(second)));
        }

        return list;
    }

    [Fact]
    public void ByTheLiveStreamTheSessionNames() =>
        Assert.Equal(1, RemotePlays.Match(Lives(("msfp-a", "s1", 0), ("msfp-b", null, 5)), "0123abcd_msfp-b", "s2", false));

    [Fact]
    public void ByTheSessionItWasBoundTo() =>
        Assert.Equal(0, RemotePlays.Match(Lives(("msfp-a", "s1", 0), ("msfp-b", "s2", 5)), null, "s1", false));

    [Fact]
    public void TheTitlesOnlyPlayback() =>
        Assert.Equal(0, RemotePlays.Match(Lives(("msfp-a", null, 0)), null, "s9", false));

    [Fact]
    public void TwoUnnamedReportsAreNotGuessed() =>
        Assert.Equal(-1, RemotePlays.Match(Lives(("msfp-a", null, 0), ("msfp-b", null, 5)), null, "s1", false));

    [Fact]
    public void StartingTakesTheNewestNoSessionHas()
    {
        // Two clients naming no live stream, each opening then starting:
        // the second start gets the second playback, the first being bound.
        Assert.Equal(1, RemotePlays.Match(Lives(("msfp-a", "s1", 0), ("msfp-b", null, 5)), null, "s2", true));
        Assert.Equal(2, RemotePlays.Match(Lives(("msfp-a", null, 0), ("msfp-b", "s1", 5), ("msfp-c", null, 9)), null, "s3", true));
    }

    [Fact]
    public void NothingForNoPlayback() =>
        Assert.Equal(-1, RemotePlays.Match(Lives(), "x_msfp-a", "s1", true));
}
