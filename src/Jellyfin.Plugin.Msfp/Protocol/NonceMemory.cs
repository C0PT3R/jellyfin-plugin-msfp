using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Msfp.Protocol;

/// <summary>Which of the two memories a request's nonce goes in.</summary>
public enum NonceRoom
{
    /// <summary>Known peers, in any state.</summary>
    Peers,

    /// <summary>Strangers heard on pair while a code is live.</summary>
    Strangers,
}

/// <summary>
/// The nonce memory of MSFP section 6.5: every accepted (key, nonce text)
/// kept at least 10 minutes, in two memories with their own caps. It lives
/// in memory only; requests signed before it began are refused (step 4).
/// Thread-safe.
/// </summary>
public sealed class NonceMemory
{
    /// <summary>How long an accepted nonce is kept: twice the 5-minute window.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    /// <summary>How often old entries are cleared, unless a memory is full.</summary>
    public static readonly TimeSpan ClearEvery = TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private readonly Store _peers;
    private readonly Store _strangers;
    private DateTimeOffset _lastClear;

    public NonceMemory(TimeProvider clock, int peerCapacity = 100_000, int strangerCapacity = 1_000)
    {
        _clock = clock;
        Began = clock.GetUtcNow();
        _lastClear = Began;
        _peers = new Store(peerCapacity);
        _strangers = new Store(strangerCapacity);
    }

    /// <summary>When the memory began: requests dated earlier are refused.</summary>
    public DateTimeOffset Began { get; }

    /// <summary>How many entries both memories hold, for tests.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _peers.Expiry.Count + _strangers.Expiry.Count;
            }
        }
    }

    /// <summary>
    /// Step 9: remembers (key, nonce) in a memory when it is in neither and
    /// that memory has room. False when it was seen, or there is no room.
    /// </summary>
    public bool TryRemember(string key, string nonce, NonceRoom room)
    {
        var now = _clock.GetUtcNow();
        var entry = key + " " + nonce;
        lock (_lock)
        {
            if (now - _lastClear >= ClearEvery)
            {
                Clear(now);
            }

            // A nonce seen in either memory is refused, even when it has
            // expired there but wasn't cleared yet: it costs nothing.
            if (_peers.Expiry.ContainsKey(entry) || _strangers.Expiry.ContainsKey(entry))
            {
                return false;
            }

            var store = room == NonceRoom.Peers ? _peers : _strangers;
            if (store.Expiry.Count >= store.Capacity)
            {
                Clear(now);
                if (store.Expiry.Count >= store.Capacity)
                {
                    return false;
                }
            }

            var expires = now + KeepFor;
            store.Expiry.Add(entry, expires);
            store.Order.Enqueue((entry, expires));
            return true;
        }
    }

    private void Clear(DateTimeOffset now)
    {
        _lastClear = now;
        _peers.Clear(now);
        _strangers.Clear(now);
    }

    private sealed class Store
    {
        public Store(int capacity)
        {
            Capacity = capacity;
        }

        public int Capacity { get; }

        public Dictionary<string, DateTimeOffset> Expiry { get; } = new(StringComparer.Ordinal);

        // Entries in the order they expire: each is kept for the same time.
        public Queue<(string Entry, DateTimeOffset Expires)> Order { get; } = new();

        public void Clear(DateTimeOffset now)
        {
            while (Order.Count > 0 && Order.Peek().Expires <= now)
            {
                Expiry.Remove(Order.Dequeue().Entry);
            }
        }
    }
}
