using Jellyfin.Plugin.Msfp.Playback;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class TranscodeLimitTests
{
    [Fact]
    public void PeersTakeWhatTheServersOwnUsersLeave()
    {
        var own = 0;
        var limit = new TranscodeLimit(() => 2, () => own);
        Assert.True(limit.TryTake());
        Assert.True(limit.TryTake());
        Assert.False(limit.TryTake());
        Assert.Equal((0, 2, 2), limit.Now());

        limit.Give();
        own = 1;
        // One peer's and one own user's: the limit is reached.
        Assert.False(limit.TryTake());
        own = 0;
        Assert.True(limit.TryTake());
    }

    [Fact]
    public void OwnUsersOverTheLimitLeavePeersNothing()
    {
        // The server's own users are never refused: they may be above it.
        var limit = new TranscodeLimit(() => 2, () => 3);
        Assert.False(limit.TryTake());
        Assert.Equal((3, 0, 2), limit.Now());
    }

    [Fact]
    public void ZeroGivesPeersNoTranscodeAndAChangeAppliesAtOnce()
    {
        var most = 0;
        var limit = new TranscodeLimit(() => most, () => 0);
        Assert.False(limit.TryTake());
        most = -1;
        Assert.False(limit.TryTake());
        Assert.Equal(0, limit.Now().Most);
        most = 1;
        Assert.True(limit.TryTake());
        Assert.False(limit.TryTake());
    }

    [Fact]
    public void GivingMoreThanTakenStaysAtZero()
    {
        var limit = new TranscodeLimit(() => 1, () => 0);
        limit.Give();
        Assert.True(limit.TryTake());
        Assert.False(limit.TryTake());
    }

    [Fact]
    public void OwnTranscodesAreSessionsPlayingWithTheVideoReencoded()
    {
        SessionInfo Session(string device, bool playing, TranscodingInfo? t) => new(null!, null!)
        {
            DeviceId = device,
            NowPlayingItem = playing ? new MediaBrowser.Model.Dto.BaseItemDto() : null,
            TranscodingInfo = t,
        };

        var sessions = new[]
        {
            Session("tv", true, new TranscodingInfo { IsVideoDirect = false }),
            Session("phone", true, new TranscodingInfo { IsVideoDirect = true }), // remux
            Session("browser", true, null), // direct play
            Session("old", false, new TranscodingInfo { IsVideoDirect = false }), // stopped
            Session("msfp-abc", true, new TranscodingInfo { IsVideoDirect = false }), // a peer's
            Session("laptop", true, new TranscodingInfo { IsVideoDirect = false }),
        };
        Assert.Equal(2, JellyfinPlayback.OwnTranscodes(sessions));
    }
}
