using System;
using System.Linq;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Federation;

/// <summary>
/// Why the last reading of a peer's titles failed, as the operator's page
/// shows it: one of the engine's codes (<see cref="ReadFailure"/>), when it
/// ended, and for a signed refusal its MSFP problem type. Only the engine's
/// codes, never an address or anything the peer wrote.
/// </summary>
/// <param name="Failure">One of <see cref="ReadFailure.All"/>.</param>
/// <param name="Refusal">For <see cref="ReadFailure.Refused"/>, the refusal's problem type; null otherwise.</param>
/// <param name="At">When the reading ended.</param>
public sealed record ReadFailed(string Failure, string? Refusal, DateTimeOffset At)
{
    /// <summary>
    /// A peer's last reading for the page (<see cref="PeerView.Reading"/>):
    /// null when it read, none ended since Jellyfin started, or the code is
    /// one this build doesn't know (shown as nothing rather than as raw words).
    /// </summary>
    public static ReadFailed? Of(LastReading? r) =>
        r is { Failure: { } f } && ReadFailure.All.Contains(f, StringComparer.Ordinal)
            ? new ReadFailed(f, f == ReadFailure.Refused ? r.Refusal : null, r.At)
            : null;
}
