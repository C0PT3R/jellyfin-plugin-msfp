using Jellyfin.Plugin.Msfp.Viewing;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class SubtitlesTests
{
    [Fact]
    public void PartsAreJoinedEachCueOnce()
    {
        // Part 1 starts 10 seconds early: the cue at 1:55 comes in both.
        var p0 = "WEBVTT\n\n00:00:05.000 --> 00:00:07.000\nHello\n\n00:01:55.000 --> 00:02:02.000\nAcross &amp; both\n";
        var p1 = "WEBVTT\n\n00:01:55.000 --> 00:02:02.000\nAcross &amp; both\n\n00:02:10.000 --> 00:02:12.000\nLater\nTwo lines\n";
        var p2 = "WEBVTT\n";
        Assert.Equal(
            "WEBVTT\n\n00:00:05.000 --> 00:00:07.000\nHello\n\n00:01:55.000 --> 00:02:02.000\nAcross &amp; both\n\n00:02:10.000 --> 00:02:12.000\nLater\nTwo lines\n",
            Subtitles.Join([p0, p1, p2]));
    }
}
