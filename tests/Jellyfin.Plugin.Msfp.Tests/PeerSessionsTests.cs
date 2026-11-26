using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Playback;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// A paired server's playback in Jellyfin's own dashboard: a session of
// its own with no user, the paired server's name as the device, "MSFP"
// as the client, msfp-<handle> as the device ID; start, progress and stop
// told as a client tells them, the method as Jellyfin names it. Jellyfin
// failing never fails the playback; without a session, nothing is told.
public class PeerSessionsTests
{
    // Sessions records the calls made of Jellyfin's session manager.
    public class Sessions : DispatchProxy
    {
        public List<(string Name, object?[] Args)> Calls { get; } = [];

        public bool Fail { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add((method!.Name, args ?? []));
            if (Fail)
            {
                return method.ReturnType == typeof(Task) ? Task.FromException(new InvalidOperationException("down")) : throw new InvalidOperationException("down");
            }

            if (method.Name == nameof(ISessionManager.LogSessionActivity))
            {
                return Task.FromResult(new SessionInfo(null!, NullLogger.Instance) { Id = "s1" });
            }

            return method.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }

    private static (PeerSessions, Sessions) Make()
    {
        var proxy = DispatchProxy.Create<ISessionManager, Sessions>();
        return (new PeerSessions(proxy, NullLogger.Instance), (Sessions)(object)proxy);
    }

    [Fact]
    public async Task StartProgressStop()
    {
        var (p, s) = Make();
        var item = Guid.NewGuid();
        var session = await p.StartAsync("h1", "Bob's server", item, "remux", 90_000);
        Assert.Equal("s1", session);
        var log = s.Calls[0];
        Assert.Equal(nameof(ISessionManager.LogSessionActivity), log.Name);
        Assert.Equal(PeerSessions.Client, log.Args[0]);
        Assert.Equal("msfp-h1", log.Args[2]);
        Assert.Equal("Bob's server", log.Args[3]);
        Assert.Null(log.Args[5]); // no user
        var start = Assert.IsType<PlaybackStartInfo>(s.Calls[1].Args[0]);
        Assert.Equal(("s1", item, "h1", PlayMethod.DirectStream, TimeSpan.FromSeconds(90).Ticks),
            (start.SessionId, start.ItemId, start.PlaySessionId, start.PlayMethod, start.PositionTicks));
        Assert.Equal(item.ToString("N"), start.MediaSourceId);

        await p.ProgressAsync(session, "h1", item, "transcode", 120_000, paused: true);
        var progress = Assert.IsType<PlaybackProgressInfo>(s.Calls[2].Args[0]);
        Assert.True(progress.IsPaused);
        Assert.Equal(PlayMethod.Transcode, progress.PlayMethod);

        await p.StopAsync(session, "h1", item, 130_000);
        Assert.Equal(TimeSpan.FromSeconds(130).Ticks, Assert.IsType<PlaybackStopInfo>(s.Calls[3].Args[0]).PositionTicks);
        Assert.Equal((nameof(ISessionManager.ReportSessionEnded), "s1"), (s.Calls[4].Name, s.Calls[4].Args[0]));

        await p.StartAsync("h2", "Bob's server", item, "direct", 0);
        Assert.Equal(PlayMethod.DirectPlay, Assert.IsType<PlaybackStartInfo>(s.Calls.Last(c => c.Name == nameof(ISessionManager.OnPlaybackStart)).Args[0]).PlayMethod);
    }

    [Fact]
    public async Task JellyfinFailingNeverFailsThePlayback()
    {
        var (p, s) = Make();
        s.Fail = true;
        Assert.Null(await p.StartAsync("h1", "Bob", Guid.NewGuid(), "direct", 0));
        await p.ProgressAsync("s1", "h1", Guid.NewGuid(), "direct", 0, false);
        await p.StopAsync("s1", "h1", Guid.NewGuid(), 0);
        s.Calls.Clear();
        await p.ProgressAsync(null, "h1", Guid.NewGuid(), "direct", 0, false);
        await p.StopAsync(null, "h1", Guid.NewGuid(), 0);
        Assert.Empty(s.Calls);
    }
}
