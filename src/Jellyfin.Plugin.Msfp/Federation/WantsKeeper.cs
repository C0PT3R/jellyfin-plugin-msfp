using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Federation;

/// <summary>
/// Which of a peer's libraries this server reads, beyond what the engine
/// keeps: by default every library the peer offers, those it offers later
/// too; or only those the operator picked. For a peer that reads all, the
/// libraries it offers are asked once paired (every minute while it offers
/// none), and again every 30 minutes,
/// and the engine is told to read them all. The operator's picks are kept
/// in the plugin's data folder (wants.json: the peers that read only some).
/// </summary>
public sealed class WantsKeeper : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    private readonly EngineHost _host;
    private readonly ILogger<WantsKeeper> _log;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _applied = new(StringComparer.Ordinal);
    private HashSet<string>? _some;

    public WantsKeeper(EngineHost host, ILogger<WantsKeeper> log)
    {
        _host = host;
        _log = log;
    }

    private static string? FilePath => Plugin.Instance is { } p ? Path.Combine(p.DataFolderPath, "wants.json") : null;

    /// <summary>Whether this server reads every library the peer offers.</summary>
    public bool WantsAll(string peer)
    {
        lock (_lock)
        {
            return !Some().Contains(peer);
        }
    }

    /// <summary>Notes the operator's choice for a peer: all, or only those picked.</summary>
    public void Set(string peer, bool all)
    {
        lock (_lock)
        {
            var some = Some();
            if (all ? some.Remove(peer) : some.Add(peer))
            {
                Save(some);
            }
        }

        _applied[peer] = DateTimeOffset.UtcNow;
    }

    /// <summary>Forgets a peer whose pairing ended.</summary>
    public void Forget(string peer)
    {
        Set(peer, true);
        _applied.TryRemove(peer, out _);
    }

    /// <summary>Applies "all" for a peer now, in the background (it was just paired).</summary>
    public void Apply(string peer)
    {
        _applied.TryRemove(peer, out _);
        _ = Task.Run(() => ApplyAsync(peer, CancellationToken.None));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                if (_host.Engine is { } engine)
                {
                    foreach (var p in await engine.PeersAsync(stoppingToken).ConfigureAwait(false))
                    {
                        if (p.State == PeerState.Paired && (!_applied.TryGetValue(p.Id, out var at) || DateTimeOffset.UtcNow - at > Every))
                        {
                            await ApplyAsync(p.Id, stoppingToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "MSFP: the libraries to read couldn't be checked");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task ApplyAsync(string peer, CancellationToken ct)
    {
        if (_host.Engine is not { } engine || !WantsAll(peer))
        {
            return;
        }

        _applied[peer] = DateTimeOffset.UtcNow;
        try
        {
            var theirs = await engine.TheirLibrariesAsync(peer, ct).ConfigureAwait(false);
            var all = theirs.Where(l => !l.Gone).Select(l => l.Id).Order(StringComparer.Ordinal).ToList();
            var now = theirs.Where(l => l.Wanted && !l.Gone).Select(l => l.Id).Order(StringComparer.Ordinal).ToList();
            if (all.Count == 0)
            {
                // Nothing offered yet (a pairing just made): asked again
                // in a minute, not in 30.
                _applied.TryRemove(peer, out _);
                return;
            }

            if (!all.SequenceEqual(now))
            {
                await engine.SetWantsAsync(peer, all, ct).ConfigureAwait(false);
            }
        }
        catch (MsfpException e)
        {
            _log.LogInformation("MSFP: the libraries {Peer} offers couldn't be read: {Code}", peer, e.Code);
        }
    }

    private HashSet<string> Some()
    {
        if (_some is null)
        {
            _some = new HashSet<string>(StringComparer.Ordinal);
            if (FilePath is { } path && File.Exists(path))
            {
                try
                {
                    _some.UnionWith(JsonSerializer.Deserialize<List<string>>(File.ReadAllBytes(path)) ?? []);
                }
                catch (JsonException e)
                {
                    _log.LogWarning(e, "MSFP: wants.json couldn't be read; every peer's libraries are read");
                }
            }
        }

        return _some;
    }

    private static void Save(HashSet<string> some)
    {
        if (FilePath is not { } path)
        {
            return;
        }

        File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(some.Order(StringComparer.Ordinal).ToList()));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
