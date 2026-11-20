using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// Paired servers' playbacks here in Jellyfin's own dashboard: each one a
/// session of its own, with no user (a paired server's viewers are never
/// known here), the paired server's name as the device, "MSFP" as the
/// client, and the device ID msfp-&lt;handle&gt; that Jellyfin's HLS for the
/// playback already uses, so that a remux's or a transcode's report
/// shows there too. Start, progress and stop are told as a client tells
/// them; no one's watched state is touched. A failure here never stops a
/// playback.
/// </summary>
internal sealed class PeerSessions(ISessionManager sessions, ILogger log)
{
    public const string Client = "MSFP";

    /// <summary>The playback's session, started; null when it couldn't be.</summary>
    public async Task<string?> StartAsync(string handle, string peerName, Guid item, string method, long positionMs)
    {
        try
        {
            var session = await sessions.LogSessionActivity(Client, Version(), "msfp-" + handle, peerName, string.Empty, null!).ConfigureAwait(false);
            await sessions.OnPlaybackStart(new PlaybackStartInfo
            {
                SessionId = session.Id,
                ItemId = item,
                MediaSourceId = item.ToString("N"),
                PlaySessionId = handle,
                PositionTicks = TimeSpan.FromMilliseconds(positionMs).Ticks,
                PlayMethod = MethodOf(method),
                CanSeek = true,
            }).ConfigureAwait(false);
            return session.Id;
        }
        catch (Exception e)
        {
            log.LogInformation(e, "MSFP: a paired server's playback couldn't be shown in Jellyfin's sessions");
            return null;
        }
    }

    public async Task ProgressAsync(string? session, string handle, Guid item, string method, long positionMs, bool paused)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await sessions.OnPlaybackProgress(new PlaybackProgressInfo
            {
                SessionId = session,
                ItemId = item,
                MediaSourceId = item.ToString("N"),
                PlaySessionId = handle,
                PositionTicks = TimeSpan.FromMilliseconds(positionMs).Ticks,
                IsPaused = paused,
                PlayMethod = MethodOf(method),
                CanSeek = true,
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.LogDebug(e, "MSFP: a paired server's progress couldn't be shown");
        }
    }

    public async Task StopAsync(string? session, string handle, Guid item, long positionMs)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            await sessions.OnPlaybackStopped(new PlaybackStopInfo
            {
                SessionId = session,
                ItemId = item,
                MediaSourceId = item.ToString("N"),
                PlaySessionId = handle,
                PositionTicks = TimeSpan.FromMilliseconds(positionMs).Ticks,
            }).ConfigureAwait(false);
            await sessions.ReportSessionEnded(session).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.LogDebug(e, "MSFP: a paired server's stop couldn't be shown");
        }
    }

    private static PlayMethod MethodOf(string method) => method switch
    {
        "direct" => PlayMethod.DirectPlay,
        "remux" => PlayMethod.DirectStream,
        _ => PlayMethod.Transcode,
    };

    private static string Version() => typeof(PeerSessions).Assembly.GetName().Version?.ToString(3) ?? "0";
}
