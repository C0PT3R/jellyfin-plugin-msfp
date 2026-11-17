using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.Msfp.Protocol;

/// <summary>A token bucket: a rate a second, and a burst it may spend at once.</summary>
public sealed class TokenBucket
{
    private readonly double _rate;
    private readonly double _burst;
    private double _tokens;
    private DateTimeOffset _at;

    public TokenBucket(double rate, double burst, DateTimeOffset now)
    {
        _rate = rate;
        _burst = burst;
        _tokens = burst;
        _at = now;
    }

    /// <summary>Whether the bucket is full again: nothing to remember.</summary>
    public bool IsFull(DateTimeOffset now) => Level(now) >= _burst;

    /// <summary>
    /// Takes one token, or says in how many whole seconds (at least 1) one
    /// will be there.
    /// </summary>
    public bool TryTake(DateTimeOffset now, out int retryAfter)
    {
        _tokens = Level(now);
        _at = now;
        if (_tokens >= 1)
        {
            _tokens -= 1;
            retryAfter = 0;
            return true;
        }

        retryAfter = Math.Clamp((int)Math.Ceiling((1 - _tokens) / _rate), 1, 3600);
        return false;
    }

    private double Level(DateTimeOffset now) => Math.Min(_burst, _tokens + (Math.Max(0, (now - _at).TotalSeconds) * _rate));
}

/// <summary>What the limits decide about one call.</summary>
public enum LimitVerdict
{
    /// <summary>The call goes on.</summary>
    Pass,

    /// <summary>The peer is over its own limit: a signed 429 rate-limited.</summary>
    PeerLimited,

    /// <summary>The server is full: a signed 503 server-busy.</summary>
    Busy,
}

/// <summary>
/// The limits of MSFP section 8.3: per network address before a signature
/// checks out, per peer and for all peers after, and on requests at once.
/// The numbers are Overfin's (core version 1, section 8.3), fixed. Requests
/// over a limit are refused at once: no queue.
/// </summary>
public sealed class CallLimits
{
    public const double AddressRate = 50, AddressBurst = 400;
    public const double PeerRate = 20, PeerBurst = 200;
    public const double AllRate = 200, AllBurst = 2000;
    public const int PeerAtOnce = 8, AllAtOnce = 64;

    /// <summary>How many network addresses are remembered at most.</summary>
    public const int MaxAddresses = 10_000;

    private readonly object _lock = new();
    private readonly Dictionary<string, TokenBucket> _addresses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenBucket> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _peerBusy = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private TokenBucket? _all;
    private int _allBusy;

    public CallLimits(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// A call from a network address, before any signature check: false
    /// when that address is over its limit (the unsigned 404). A call
    /// without a known address (tests) always passes.
    /// </summary>
    public bool AllowAddress(string? address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return true;
        }

        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (!_addresses.TryGetValue(address, out var bucket))
            {
                if (_addresses.Count >= MaxAddresses)
                {
                    // Full buckets hold nothing worth keeping.
                    foreach (var (k, b) in new List<KeyValuePair<string, TokenBucket>>(_addresses))
                    {
                        if (b.IsFull(now))
                        {
                            _addresses.Remove(k);
                        }
                    }

                    if (_addresses.Count >= MaxAddresses)
                    {
                        return false;
                    }
                }

                bucket = new TokenBucket(AddressRate, AddressBurst, now);
                _addresses[address] = bucket;
            }

            return bucket.TryTake(now, out _);
        }
    }

    /// <summary>
    /// A call from a known peer, once its signature checks out. On Pass,
    /// the slot is held until the returned handle is disposed.
    /// </summary>
    public LimitVerdict Enter(string peerKey, out int retryAfter, out IDisposable? slot)
    {
        slot = null;
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            _all ??= new TokenBucket(AllRate, AllBurst, now);
            if (!_peers.TryGetValue(peerKey, out var bucket))
            {
                bucket = new TokenBucket(PeerRate, PeerBurst, now);
                _peers[peerKey] = bucket;
            }

            _peerBusy.TryGetValue(peerKey, out var busy);
            if (busy >= PeerAtOnce)
            {
                retryAfter = 1;
                return LimitVerdict.PeerLimited;
            }

            if (_allBusy >= AllAtOnce)
            {
                retryAfter = 1;
                return LimitVerdict.Busy;
            }

            if (!bucket.TryTake(now, out retryAfter))
            {
                return LimitVerdict.PeerLimited;
            }

            if (!_all.TryTake(now, out retryAfter))
            {
                return LimitVerdict.Busy;
            }

            _peerBusy[peerKey] = busy + 1;
            _allBusy++;
            slot = new Slot(this, peerKey);
            return LimitVerdict.Pass;
        }
    }

    /// <summary>Forgets a peer's bucket (its pairing ended).</summary>
    public void Forget(string peerKey)
    {
        lock (_lock)
        {
            _peers.Remove(peerKey);
        }
    }

    private void Leave(string peerKey)
    {
        lock (_lock)
        {
            if (_peerBusy.TryGetValue(peerKey, out var busy))
            {
                if (busy <= 1)
                {
                    _peerBusy.Remove(peerKey);
                }
                else
                {
                    _peerBusy[peerKey] = busy - 1;
                }
            }

            _allBusy = Math.Max(0, _allBusy - 1);
        }
    }

    private sealed class Slot : IDisposable
    {
        private CallLimits? _owner;
        private readonly string _key;

        public Slot(CallLimits owner, string key)
        {
            _owner = owner;
            _key = key;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave(_key);
    }
}
