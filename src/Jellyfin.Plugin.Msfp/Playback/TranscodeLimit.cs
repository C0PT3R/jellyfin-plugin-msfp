using System;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// The server-wide limit on transcodes at once ("Transcodes at once on this
/// server", on the plugin's page): Jellyfin's own users' transcodes count,
/// and so do peers'; a peer's transcode is refused at the limit, and the
/// server's own users never are (the plugin only counts theirs). Direct
/// play and remux (the video copied) don't count.
/// </summary>
internal sealed class TranscodeLimit
{
    private readonly Func<int> _most;
    private readonly Func<int> _own;
    private readonly object _lock = new();
    private int _peers;

    /// <param name="most">The limit as set now; 0 or less: no transcode for peers.</param>
    /// <param name="own">The transcodes of the server's own users now.</param>
    public TranscodeLimit(Func<int> most, Func<int> own)
    {
        _most = most;
        _own = own;
    }

    /// <summary>Takes one transcode for a peer; false at the limit.</summary>
    public bool TryTake()
    {
        lock (_lock)
        {
            if (_peers + _own() >= _most())
            {
                return false;
            }

            _peers++;
            return true;
        }
    }

    /// <summary>Gives back one taken by <see cref="TryTake"/>.</summary>
    public void Give()
    {
        lock (_lock)
        {
            if (_peers > 0)
            {
                _peers--;
            }
        }
    }

    /// <summary>What is used now, for the operator's page.</summary>
    public (int Own, int Peers, int Most) Now()
    {
        lock (_lock)
        {
            return (_own(), _peers, Math.Max(_most(), 0));
        }
    }
}
