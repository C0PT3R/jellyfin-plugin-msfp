using System;
using System.Collections.Concurrent;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Federation;

/// <summary>A reading of a peer's titles: when it ended, the titles read, and the error's code when it failed ("" when it didn't).</summary>
public sealed record LastRead(DateTimeOffset At, int Titles, string Error);

/// <summary>
/// The last reading of each peer's titles, from the engine's events, for
/// the operator's page. Memory only, so nothing outlives Jellyfin; an error
/// is kept as its code alone (never a message, an address or a body).
/// </summary>
public sealed class LastReads
{
    private readonly ConcurrentDictionary<string, LastRead> _reads = new(StringComparer.Ordinal);

    /// <summary>The peer's last reading; null when none ended since Jellyfin started.</summary>
    public LastRead? Of(string peer) => _reads.TryGetValue(peer, out var r) ? r : null;

    /// <summary>Keeps a reading's end; a pairing's end forgets the peer.</summary>
    public void Note(MsfpEvent e)
    {
        switch (e)
        {
            case CatalogReadEnded r:
                _reads[r.Peer] = new LastRead(DateTimeOffset.UtcNow, r.Titles, CodeOf(r.Error));
                break;
            case PeerEnded p:
                _reads.TryRemove(p.Peer, out _);
                break;
            case PeerGone g:
                _reads.TryRemove(g.Peer, out _);
                break;
        }
    }

    /// <summary>An error as the page names it: the MSFP error's code, else a family of failure; "" for none.</summary>
    internal static string CodeOf(Exception? e) => e switch
    {
        null => string.Empty,
        MsfpException m => m.Code.ToString(),
        OperationCanceledException => "Timeout",
        System.Net.Http.HttpRequestException => "Unreachable",
        System.Text.Json.JsonException => "Unreadable",
        _ => "Failed",
    };
}
