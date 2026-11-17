using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Federation;

/// <summary>A peer's state (MSFP section 7.3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PeerState>))]
public enum PeerState
{
    /// <summary>The peer used our code; our operator hasn't accepted.</summary>
    Ours,

    /// <summary>We used the peer's code; its operator hasn't accepted.</summary>
    Theirs,

    /// <summary>Both sides accepted.</summary>
    Paired,

    /// <summary>The peer answered, signed, that it doesn't know us.</summary>
    Gone,
}

/// <summary>What this server keeps of a peer (MSFP section 7.2, as far as pairing goes).</summary>
public sealed class Peer
{
    /// <summary>Its public key, base64url: its identity.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Where to call it, as it was given.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Its cleaned name.</summary>
    public string Name { get; set; } = string.Empty;

    public PeerState State { get; set; }

    /// <summary>Whether it was paired once: a gone peer comes back to paired only then.</summary>
    public bool WasPaired { get; set; }

    /// <summary>
    /// The pins of its latest signed hello, request or answer (section 3.1):
    /// the certificates a checked call accepts beside trusted ones.
    /// </summary>
    public List<string> Pins { get; set; } = new();

    /// <summary>This server's libraries offered to it (media server IDs).</summary>
    public List<string> Offered { get; set; } = new();

    /// <summary>The catalog seq at which its offer last changed.</summary>
    public long OffersSeq { get; set; }

    /// <summary>Whether this server reads every library the peer offers.</summary>
    public bool WantAll { get; set; } = true;

    /// <summary>The peer's libraries read, when not all (the peer's IDs).</summary>
    public List<string> Wanted { get; set; } = new();

    public DateTimeOffset Added { get; set; }

    public DateTimeOffset? LastHeard { get; set; }
}

/// <summary>A live pairing code this server made: its secret, found by its hash.</summary>
public sealed class StoredCode
{
    /// <summary>SHA-256 of the secret, base64url.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>The secret, base64url: the proof is checked against it.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Unix seconds.</summary>
    public long Expires { get; set; }
}

/// <summary>Everything the store holds.</summary>
public sealed class StoreData
{
    public List<Peer> Peers { get; set; } = new();

    public List<StoredCode> Codes { get; set; } = new();

    /// <summary>SHA-256 (base64url) of keys whose pairing this server ended.</summary>
    public List<string> Forgotten { get; set; } = new();

    public Peer? Find(string key) => Peers.FirstOrDefault(p => p.Key == key);

    public bool HasLiveCode(DateTimeOffset now) => Codes.Any(c => c.Expires > now.ToUnixTimeSeconds());
}

/// <summary>
/// The plugin's own storage for peers and codes: one JSON file in the
/// plugin's data folder, replaced whole on each change, so each change is
/// one transaction. Thread-safe.
/// </summary>
public sealed class PeerStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string? _path;
    private StoreData _data;

    /// <summary>A store in a file; null keeps it in memory only (tests).</summary>
    public PeerStore(string? path)
    {
        _path = path;
        _data = path != null && File.Exists(path)
            ? JsonSerializer.Deserialize<StoreData>(File.ReadAllBytes(path), Options) ?? new StoreData()
            : new StoreData();

        // Pairings stored before the address rules of 2026-11-16 keep their
        // address, normalized once (section 5.2, "Stored pairings").
        foreach (var peer in _data.Peers)
        {
            peer.Address = global::Msfp.Address.Normalize(peer.Address);
        }
    }

    /// <summary>The SHA-256 of a key, as the forgotten list holds it.</summary>
    public static string KeyHash(byte[] key) => Encodings.Base64Url(SHA256.HashData(key));

    /// <summary>Reads under the lock.</summary>
    public T Read<T>(Func<StoreData, T> read)
    {
        lock (_lock)
        {
            return read(_data);
        }
    }

    /// <summary>
    /// Changes the data and saves it, all or nothing: when the change or the
    /// save fails, the data is as before.
    /// </summary>
    public T Change<T>(Func<StoreData, T> change)
    {
        lock (_lock)
        {
            var copy = JsonSerializer.Deserialize<StoreData>(JsonSerializer.SerializeToUtf8Bytes(_data, Options), Options)!;
            var result = change(copy);
            Save(copy);
            _data = copy;
            return result;
        }
    }

    private void Save(StoreData data)
    {
        if (_path == null)
        {
            return;
        }

        var tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(data, Options));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>The server's key, kept as its 32-byte seed in a file only its owner reads.</summary>
public static class KeyFile
{
    /// <summary>Loads the key from the file, or makes one and stores it on first start.</summary>
    public static ServerKey LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            return ServerKey.FromSeed(File.ReadAllBytes(path));
        }

        var key = ServerKey.Generate();
        var tmp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var f = new FileStream(tmp, options))
        {
            f.Write(key.ExportSeed());
        }

        File.Move(tmp, path, overwrite: false);
        return key;
    }
}
