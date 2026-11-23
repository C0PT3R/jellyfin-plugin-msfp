using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Configuration;
using Jellyfin.Plugin.Msfp.Playback;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Msfp;
using Msfp.Protocol;
using Msfp.Stores;

namespace Jellyfin.Plugin.Msfp.Federation;

/// <summary>
/// The MSFP engine inside Jellyfin: its records in a JSON-file store in the
/// plugin's data folder, opened when Jellyfin starts and opened again when
/// the operator saves the plugin's settings (a new address is a move, which
/// the engine announces). Federation is off while no address is set, or
/// while the engine refuses the settings (<see cref="Problem"/> says why).
/// </summary>
public sealed class EngineHost : IHostedService, IDisposable
{
    /// <summary>The parts this plugin speaks besides the core: no music (README).</summary>
    public static readonly IReadOnlyList<string> Parts = ["catalog", "playback", "media", "video", "groups"];

    private readonly HostCatalog _catalog;
    private readonly JellyfinPlayback _playback;
    private readonly IServerConfigurationManager _server;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _opening = new(1, 1);
    private JsonFileStore? _store;
    private MsfpEngine? _engine;
    private CancellationTokenSource? _running;

    // Set once Jellyfin stops the plugin: no engine opens after that (a
    // settings change still waiting for the lock would open one nothing
    // stops, beside the next start's).
    private volatile bool _stopped;

    public EngineHost(HostCatalog catalog, JellyfinPlayback playback, IServerConfigurationManager server, ILoggerFactory loggers)
    {
        _catalog = catalog;
        _playback = playback;
        _playback.Engine = () => Engine;
        _server = server;
        _loggers = loggers;
        _log = loggers.CreateLogger("Jellyfin.Plugin.Msfp");
    }

    /// <summary>The engine; null while federation is off.</summary>
    public MsfpEngine? Engine => Volatile.Read(ref _engine);

    /// <summary>The engine's records, peers' catalogs among them; null before the first start.</summary>
    public JsonFileStore? Store => Volatile.Read(ref _store);

    /// <summary>Why federation is off, for the operator; empty while it is on.</summary>
    public string Problem { get; private set; } = string.Empty;

    /// <summary>The engine's events, as they happen (a reading of a peer's catalog ended, a pairing ended, ...).</summary>
    public event Action<MsfpEvent>? Events;

    /// <summary>
    /// The last reading of each peer's titles since Jellyfin started, for
    /// the operator's page: when, how many titles, and the error's code when
    /// it failed (a peer on another draft of MSFP shows so). Memory only.
    /// </summary>
    public LastReads Reads { get; } = new();

    /// <summary>Federation's changes, counted, for the configuration page to follow.</summary>
    public Changes Changes { get; } = new();

    /// <summary>The prefix the routes are under: Jellyfin's base URL, "" without one.</summary>
    public string Prefix { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += Reopen;
        }

        await OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopped = true;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= Reopen;
        }

        // The background work stops at once, lock or not: Jellyfin gives
        // its services a bounded time to stop, and an engine left running
        // here keeps working beside the next start's.
        CancelRunning();
        var locked = false;
        try
        {
            locked = await _opening.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            if (locked)
            {
                _opening.Release();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopped = true;
        CancelRunning();
        Interlocked.Exchange(ref _engine, null)?.Dispose();
        _running?.Dispose();
        _store?.Dispose();
        _opening.Dispose();
    }

    private void CancelRunning()
    {
        try
        {
            _running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Reopen(object? sender, BasePluginConfiguration e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await OpenAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "MSFP: the engine couldn't be opened again");
            }
        });
    }

    /// <summary>Opens the engine with the settings saved now, closing the one before.</summary>
    public async Task OpenAsync(CancellationToken ct)
    {
        await _opening.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CloseAsync().ConfigureAwait(false);
            if (_stopped)
            {
                return;
            }

            var plugin = Plugin.Instance ?? throw new InvalidOperationException("the MSFP plugin isn't loaded");
            var config = plugin.Configuration;
            Prefix = PrefixOf(_server.GetNetworkConfiguration().BaseUrl);
            if (_store is null)
            {
                var dir = Path.Combine(plugin.DataFolderPath, "msfp");
                _store = JsonFileStore.Open(dir);
            }

            var address = (config.Address ?? string.Empty).Trim().TrimEnd('/');
            if (address.Length == 0)
            {
                Problem = "Set this server's address, and save: federation is off until then.";
                return;
            }

            try
            {
                var engine = await MsfpEngine.OpenAsync(Settings(config, address, _store), ct).ConfigureAwait(false);
                if (_stopped)
                {
                    // Stopped while it opened: StopAsync didn't wait for it.
                    engine.Dispose();
                    return;
                }

                _running = new CancellationTokenSource();
                var token = _running.Token;
                _ = Task.Run(() => RunAsync(engine, token), CancellationToken.None);
                Volatile.Write(ref _engine, engine);
                Problem = string.Empty;
                _log.LogInformation("MSFP: federation on at {Address}{Prefix}", address, Prefix);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Problem = "Federation is off: " + Describe(e);
                _log.LogWarning(e, "MSFP: federation is off");
            }
        }
        finally
        {
            _opening.Release();
            Changes.Bump();
        }
    }

    private MsfpConfig Settings(PluginConfiguration config, string address, JsonFileStore store)
    {
        var name = (config.ServerName ?? string.Empty).Trim();
        var pins = (config.CertificatePins ?? string.Empty)
            .Split((char[])['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Pins.Valid(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new MsfpConfig
        {
            Address = address,
            Store = store,
            Name = name.Length > 0 ? () => name : null,
            Prefix = Prefix,
            Discovery = config.Discovery,
            Pins = pins.Count > 0 ? pins : null,
            AllowLoopback = config.AllowLoopback,
            Notifier = new LogNotifier(_loggers.CreateLogger("Jellyfin.Plugin.Msfp.Events"), e =>
            {
                Reads.Note(e);
                Changes.Bump();
                Events?.Invoke(e);
            }),
            Logger = _loggers.CreateLogger("Jellyfin.Plugin.Msfp.Engine"),
            Parts = Parts,
            Catalog = _catalog,
            Playback = _playback,
            Media = _playback,
            // What Jellyfin's transcoder makes for peers: h264 always, hevc
            // and av1 as Jellyfin's settings allow, the range sdr only.
            Encoders = _playback.Encoders(),
            // Paired servers' preview sheets, re-encoded as they're relayed.
            SheetEncoder = new Viewing.SheetEncoder(),
        };
    }

    // The engine's background work: moves, scheduled hellos, readings of
    // peers' catalogs, keys' time.
    private async Task RunAsync(MsfpEngine engine, CancellationToken ct)
    {
        try
        {
            await engine.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _log.LogError(e, "MSFP: the engine's background work stopped");
        }
    }

    private async Task CloseAsync()
    {
        var engine = Interlocked.Exchange(ref _engine, null);
        if (Interlocked.Exchange(ref _running, null) is { } running)
        {
            await running.CancelAsync().ConfigureAwait(false);
            running.Dispose();
        }

        engine?.Dispose();
    }

    /// <summary>
    /// Jellyfin's base URL as the engine's prefix: "/jellyfin" stays, "" for
    /// none; one the engine can't take (MSFP core section 11: segments of
    /// A–Z a–z 0–9 . _ ~ -) is kept anyway, and the engine refuses it.
    /// </summary>
    internal static string PrefixOf(string? baseUrl)
    {
        var b = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (b.Length == 0)
        {
            return string.Empty;
        }

        return b.StartsWith('/') ? b : "/" + b;
    }

    private static string Describe(Exception e) => e switch
    {
        MsfpException { Code: MsfpError.InvalidAddress } => "the address isn't https://host[:port] with no path (MSFP core section 5.2).",
        MsfpException { Code: MsfpError.PlainHttp } => "the address must be https:// (MSFP core section 3.1).",
        _ => e.Message,
    };

    /// <summary>The engine's events, in Jellyfin's log, then to the plugin's parts that follow them.</summary>
    private sealed class LogNotifier(ILogger log, Action<MsfpEvent> then) : INotifier
    {
        public void Notify(MsfpEvent e)
        {
            log.LogInformation("MSFP: {Event}", e.ToString());
            try
            {
                then(e);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "MSFP: an event's follower failed");
            }
        }
    }
}
