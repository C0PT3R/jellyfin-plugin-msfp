using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Msfp;
using Msfp.Protocol;
using PlayRequest = Msfp.PlayRequest;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>How a play of a paired server's copy reaches Jellyfin (migration plan 4.2).</summary>
internal enum Way
{
    /// <summary>The copy as it is, read by byte ranges at the plugin's file route (MSFP delivery file).</summary>
    File,

    /// <summary>The paired server's HLS (a remux or a conversion of its own), as Jellyfin's live stream.</summary>
    Hls,
}

/// <summary>
/// One play of a stand-in, made at PlaybackInfo for one Jellyfin user, one
/// stand-in, one media source and the paired server chosen to hold it
/// (<see cref="HandOver"/>). Its secret (256 random bits) is in the source's
/// path, and opens the copy's bytes there; the item ID alone opens nothing.
/// A play not read within <see cref="PlayTokens.Lapse"/> lapses; once read it
/// belongs to one MSFP playback (started again on the same etag after a long
/// pause), until Jellyfin's session stops it or nothing reads or reports on
/// it for <see cref="RemotePlays.Idle"/>. Never logged.
/// </summary>
internal sealed class PlayToken
{
    /// <summary>The secret: 43 base64url characters.</summary>
    public required string Secret { get; init; }

    /// <summary>The media source's ID in Jellyfin for this play (a GUID's form, which Jellyfin's HLS wants): not secret.</summary>
    public required string Handle { get; init; }

    public required Guid User { get; init; }

    public required Guid Item { get; init; }

    /// <summary>The paired server chosen, its key for the title and the copy played.</summary>
    public required string Peer { get; init; }

    public required string Key { get; init; }

    public required Copy Copy { get; init; }

    public required Way Way { get; init; }

    /// <summary>For the HLS way, what the paired server is asked.</summary>
    public PlayRequest? Asked { get; init; }

    /// <summary>The copy's track IDs ("v", "a1", "s2"…) by the Index Jellyfin shows them at.</summary>
    public IReadOnlyDictionary<int, string> Tracks { get; init; } = new Dictionary<int, string>();

    public DateTimeOffset Made { get; init; }

    /// <summary>The source Jellyfin is given for it, its paths the loopback ones (cloned for each request).</summary>
    public MediaBrowser.Model.Dto.MediaSourceInfo? Source { get; set; }

    /// <summary>The first read (the file, a subtitle track), null before.</summary>
    public DateTimeOffset? FirstRead { get; set; }

    /// <summary>The MSFP playback, once started; null before, and after it ended.</summary>
    public RemotePlayback? Playback { get; set; }

    /// <summary>The MSFP playback that ended last, to start again on the same etag.</summary>
    public RemotePlayback? Last { get; set; }

    /// <summary>The last read or report: what keeps it from going idle.</summary>
    public DateTimeOffset Touched { get; set; }

    /// <summary>Where the viewer is, in the title (not the copy: <see cref="StartMs"/>).</summary>
    public long PositionMs { get; set; }

    /// <summary>The Jellyfin session playing it, once known.</summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// The ID the client is shown for the play's source: the stand-in's
    /// saved source for the copy (so that the client finds that source's
    /// trickplay and version), null for a copy the stand-in doesn't show
    /// (another holder's): the handle then.
    /// </summary>
    public string? Shown { get; set; }

    /// <summary>The device the play was made for (Jellyfin's device ID), null when not known.</summary>
    public string? Device { get; set; }

    /// <summary>Jellyfin's ID of the live stream (the HLS way).</summary>
    public string StreamId { get; set; } = string.Empty;

    /// <summary>Starting the playback: one at a time.</summary>
    public SemaphoreSlim Starting { get; } = new(1, 1);

    /// <summary>The subtitle tracks read whole, by track.</summary>
    public ConcurrentDictionary<string, Lazy<System.Threading.Tasks.Task<byte[]?>>> Subtitles { get; } = new(StringComparer.Ordinal);

    /// <summary>Where the title starts in the copy (an episode among several in one copy), ms.</summary>
    public long StartMs => StandInStreams.Span(Copy).StartMs;
}

/// <summary>
/// The live plays' tokens: kept by the SHA-256 of their secret, compared in
/// constant time, and by their handle.
/// </summary>
internal sealed class PlayTokens
{
    /// <summary>How long a play waits for its first read.</summary>
    public static readonly TimeSpan Lapse = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, PlayToken> _bySecret = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PlayToken> _byHandle = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now;

    public PlayTokens(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public DateTimeOffset Now => _now();

    /// <summary>The plays live now (lapsed ones left out).</summary>
    public IReadOnlyList<PlayToken> All => _bySecret.Values.Where(Live).ToList();

    /// <summary>A new play: its secret random, its handle a new GUID's.</summary>
    public PlayToken Mint(Guid user, Guid item, string peer, string key, Copy copy, Way way, PlayRequest? asked = null, IReadOnlyDictionary<int, string>? tracks = null)
    {
        var now = _now();
        var t = new PlayToken
        {
            Secret = Base64Url(RandomNumberGenerator.GetBytes(32)),
            Handle = Guid.NewGuid().ToString("N"),
            User = user,
            Item = item,
            Peer = peer,
            Key = key,
            Copy = copy,
            Way = way,
            Asked = asked,
            Tracks = tracks ?? new Dictionary<int, string>(),
            Made = now,
            Touched = now,
        };
        _bySecret[Hash(t.Secret)] = t;
        _byHandle[t.Handle] = t;
        return t;
    }

    /// <summary>A play by its secret, null when none is live: the secret compared in constant time.</summary>
    public PlayToken? Find(string? secret)
    {
        if (secret is not { Length: 43 } || !_bySecret.TryGetValue(Hash(secret), out var t)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(secret), Encoding.ASCII.GetBytes(t.Secret)))
        {
            return null;
        }

        return Live(t) ? t : null;
    }

    /// <summary>
    /// A play by its handle, for a request of a Jellyfin user: null when none
    /// is live, or it is another user's or another item's.
    /// </summary>
    public PlayToken? OfHandle(string? handle, Guid user, Guid item) =>
        handle is { Length: 32 } && _byHandle.TryGetValue(handle, out var t) && Live(t) && t.User == user && t.Item == item ? t : null;

    /// <summary>
    /// A play by the source ID its client was shown (<see cref="PlayToken.Shown"/>),
    /// for a request of a Jellyfin user from a device: the newest live one of
    /// that user, item and device (any of the user's devices for a request
    /// that names none, as one authenticated by a key in its URL); null when none.
    /// </summary>
    public PlayToken? OfShown(string? shown, Guid user, Guid item, string? device) =>
        string.IsNullOrEmpty(shown) ? null
            : _bySecret.Values.Where(t => Live(t) && t.User == user && t.Item == item && (string.IsNullOrEmpty(device) || (t.Device ?? string.Empty) == device)
                && string.Equals(t.Shown, shown, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.Made).FirstOrDefault();

    /// <summary>A play by its handle, whoever's: for Jellyfin's own reads (no user).</summary>
    public PlayToken? OfHandle(string? handle) =>
        handle is { Length: 32 } && _byHandle.TryGetValue(handle, out var t) && Live(t) ? t : null;

    public void Drop(PlayToken t)
    {
        _bySecret.TryRemove(Hash(t.Secret), out _);
        _byHandle.TryRemove(t.Handle, out _);
    }

    /// <summary>The plays not read within <see cref="Lapse"/>, dropped.</summary>
    public void Sweep()
    {
        foreach (var t in _bySecret.Values)
        {
            _ = Live(t);
        }
    }

    /// <summary>How many plays hold a stream on a paired server: those reading, and those waiting for their first read.</summary>
    public int Holding(string peer) => _bySecret.Values.Count(t => t.Peer == peer && Live(t) && (t.Playback is not null || t.FirstRead is null));

    private bool Live(PlayToken t)
    {
        if (t.FirstRead is null && _now() - t.Made > Lapse)
        {
            Drop(t);
            return false;
        }

        return true;
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(secret)));

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
