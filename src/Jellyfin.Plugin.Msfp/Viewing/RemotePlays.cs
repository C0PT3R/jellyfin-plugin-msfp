using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// This server's viewers playing paired servers' titles (MSFP playback,
/// viewer side). Pressing Play opens the stand-in's source: the plugin asks
/// the paired servers holding the title, best copy first, the next when one
/// is busy or refuses, and gives Jellyfin a source at its own relay route
/// (loopback only), which Jellyfin reads and converts for the device as it
/// does any file. Where the viewer is goes to the paired server from
/// Jellyfin's playback events; the playback stops when Jellyfin closes the
/// source, when the viewer stops, or after 3 minutes with neither a report
/// nor a read (a client that never says it stopped).
/// </summary>
public sealed class RemotePlays : IHostedService, IDisposable
{
    /// <summary>How long a playback nobody reports on nor reads lives.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);

    // Every value MSFP lists: the paired server sends its copy as it is
    // whenever it can (direct play there costs it the least), and Jellyfin
    // converts it here when the viewer's device needs it.
    private static readonly Capabilities Everything = new()
    {
        Containers = ["mp4", "m4v", "mov", "webm", "mkv", "ts"],
        AudioCodecs = ["aac", "mp3", "opus", "vorbis", "flac", "ac3", "eac3", "alac", "pcm_s16le"],
        VideoCodecs = ["h264", "hevc", "av1", "vp8", "vp9"],
        Hdr = ["hdr10", "hlg"],
        MaxAudioChannels = 8,
    };

    private readonly EngineHost _host;
    private readonly StandIns _standIns;
    private readonly ISessionManager _sessions;
    private readonly IConfigurationManager _config;
    private readonly IMediaEncoder _encoder;
    private readonly ILogger<RemotePlays> _log;
    private readonly ConcurrentDictionary<string, Live> _byToken = new(StringComparer.Ordinal);
    private Timer? _sweep;

    public RemotePlays(EngineHost host, StandIns standIns, ISessionManager sessions, IConfigurationManager config, IMediaEncoder encoder, ILogger<RemotePlays> log)
    {
        _encoder = encoder;
        _host = host;
        _standIns = standIns;
        _sessions = sessions;
        _config = config;
        _log = log;
    }

    /// <summary>The playbacks live now.</summary>
    public int Count => _byToken.Count;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackProgress += Progress;
        _sessions.PlaybackStart += Progress;
        _sessions.PlaybackStopped += Stopped;
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackProgress -= Progress;
        _sessions.PlaybackStart -= Progress;
        _sessions.PlaybackStopped -= Stopped;
        _sweep?.Change(Timeout.Infinite, Timeout.Infinite);
        foreach (var live in _byToken.Values.ToList())
        {
            await EndAsync(live, -1).ConfigureAwait(false);
        }
    }

    public void Dispose() => _sweep?.Dispose();

    /// <summary>
    /// Starts a playback of a stand-in on one of the paired servers holding
    /// it, best copy first, and gives the source Jellyfin reads.
    /// </summary>
    /// <exception cref="InvalidOperationException">No paired server could play it now; the message says why.</exception>
    internal async Task<PairedLiveStream> OpenAsync(BaseItem item, CancellationToken ct)
    {
        var engine = _host.Engine ?? throw new InvalidOperationException("Federation is off on this server.");
        var key = item.Path![Wanted.PathOf(string.Empty).Length..];
        var copy = _standIns.CopyOf(key) ?? throw new InvalidOperationException("No paired server holds this title now.");
        var busy = false;
        foreach (var holder in copy.Holders)
        {
            RemotePlayback playback;
            try
            {
                playback = await engine.PlayAsync(holder.Peer, new PlayRequest { Title = key, Capabilities = Everything }, ct).ConfigureAwait(false);
            }
            catch (MsfpException e)
            {
                busy |= e.Code is MsfpError.StreamLimitReached or MsfpError.ServerBusy or MsfpError.BackingOff;
                _log.LogInformation("MSFP: {Peer} didn't play {Key}: {Code}; the next one holding it, if any", holder.Peer, key, e.Code);
                continue;
            }

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var live = new Live(this, token, playback, item);
            _byToken[token] = live;
            _log.LogInformation("MSFP: playing {Key} from {Peer} ({Method}, {Protocol})", key, holder.Peer, playback.Method, playback.Protocol);
            var source = SourceOf(item, playback, token);
            try
            {
                await ProbeAsync(source, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "MSFP: {Key} from {Peer} couldn't be read; the next one holding it, if any", key, holder.Peer);
                await EndAsync(live, 0).ConfigureAwait(false);
                continue;
            }

            return new PairedLiveStream(live, source);
        }

        throw new InvalidOperationException(busy
            ? "The paired servers holding this title are busy now. Try again later."
            : "No paired server could play this title now.");
    }

    /// <summary>A playback by its relay token, null when none: each read keeps it from going idle.</summary>
    internal Live? ByToken(string token)
    {
        if (!_byToken.TryGetValue(token, out var live))
        {
            return null;
        }

        live.Touched = DateTimeOffset.UtcNow;
        return live;
    }

    // The source Jellyfin reads: the relay route on its own loopback, probed
    // by Jellyfin when opened (the real streams, codecs included).
    private MediaSourceInfo SourceOf(BaseItem item, RemotePlayback p, string token)
    {
        var net = _config.GetConfiguration<MediaBrowser.Common.Net.NetworkConfiguration>("network");
        var baseUrl = (net.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length > 0 && !baseUrl.StartsWith('/'))
        {
            baseUrl = "/" + baseUrl;
        }

        var file = p.Protocol == "file";
        return new MediaSourceInfo
        {
            Id = item.Id.ToString("N"),
            Name = "From a paired server",
            Protocol = MediaProtocol.Http,
            Path = $"http://127.0.0.1:{net.InternalHttpPort}{baseUrl}/Msfp/Relay/{token}/" + (file ? "file" : "master.m3u8"),
            IsRemote = true,
            RequiresOpening = false,
            RequiresClosing = true,
            SupportsProbing = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            Container = file ? ContainerOf(p.MimeType) : "hls",
            IsInfiniteStream = false,
            RunTimeTicks = p.DurationMs > 0 ? TimeSpan.FromMilliseconds(p.DurationMs).Ticks : item.RunTimeTicks,
            Type = MediaSourceType.Default,
            MediaStreams = [],
        };
    }

    // Its streams, read here through the relay: Jellyfin's own probe of a
    // live stream keeps one video and one audio track only (made for live
    // TV), and every audio language counts here.
    private async Task ProbeAsync(MediaSourceInfo source, CancellationToken ct)
    {
        var info = await _encoder.GetMediaInfo(new MediaInfoRequest
        {
            MediaSource = source,
            MediaType = MediaBrowser.Model.Dlna.DlnaProfileType.Video,
            ExtractChapters = false,
        }, ct).ConfigureAwait(false);
        source.MediaStreams = info.MediaStreams;
        source.Container = info.Container ?? source.Container;
        source.Bitrate = info.Bitrate;
        source.RunTimeTicks = info.RunTimeTicks ?? source.RunTimeTicks;
        source.SupportsProbing = false;
    }

    private static string? ContainerOf(string mime) => mime switch
    {
        "video/mp4" => "mp4",
        "video/x-matroska" or "video/matroska" => "mkv",
        "video/webm" => "webm",
        "video/quicktime" => "mov",
        "video/mp2t" => "ts",
        _ => null,
    };

    // Where the viewer is, from Jellyfin's reports on a stand-in.
    private void Progress(object? sender, PlaybackProgressEventArgs e)
    {
        if (Of(e) is { } live && _host.Engine is { } engine)
        {
            live.Touched = DateTimeOffset.UtcNow;
            live.PositionMs = (e.PlaybackPositionTicks ?? 0) / TimeSpan.TicksPerMillisecond;
            engine.Progress(live.Playback.Id, live.PositionMs, e.IsPaused);
        }
    }

    private void Stopped(object? sender, PlaybackStopEventArgs e)
    {
        if (Of(e) is { } live)
        {
            live.PositionMs = (e.PlaybackPositionTicks ?? live.PositionMs * TimeSpan.TicksPerMillisecond) / TimeSpan.TicksPerMillisecond;
            _ = EndAsync(live, live.PositionMs);
        }
    }

    // The playback a report is about: by the live stream the session plays,
    // else the one playback of that stand-in.
    private Live? Of(PlaybackProgressEventArgs e)
    {
        if (e.Item is not { } item || !PairedSources.IsStandIn(item))
        {
            return null;
        }

        var stream = e.Session?.PlayState?.LiveStreamId;
        var lives = _byToken.Values.Where(l => l.Item.Id == item.Id).ToList();
        return lives.FirstOrDefault(l => stream is not null && l.StreamId == stream) ?? (lives.Count == 1 ? lives[0] : null);
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var live in _byToken.Values.Where(l => now - l.Touched > Idle).ToList())
        {
            _log.LogInformation("MSFP: a playback of {Key} neither reported on nor read for {Idle}: stopped", live.Playback.Title, Idle);
            _ = EndAsync(live, live.PositionMs);
        }
    }

    /// <summary>Stops a playback on its paired server, once.</summary>
    internal async Task EndAsync(Live live, long positionMs)
    {
        if (!_byToken.TryRemove(live.Token, out _))
        {
            return;
        }

        try
        {
            if (_host.Engine is { } engine)
            {
                await engine.StopAsync(live.Playback.Id, positionMs).ConfigureAwait(false);
            }
        }
        catch (MsfpException)
        {
            // Ended already on the paired server's side.
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "MSFP: stopping a playback on a paired server failed");
        }
    }

    /// <summary>A playback on a paired server, as Jellyfin reads it.</summary>
    internal sealed class Live(RemotePlays owner, string token, RemotePlayback playback, BaseItem item)
    {
        public RemotePlays Owner { get; } = owner;

        public string Token { get; } = token;

        public RemotePlayback Playback { get; } = playback;

        public BaseItem Item { get; } = item;

        public string StreamId { get; set; } = string.Empty;

        public long PositionMs { get; set; }

        public DateTimeOffset Touched { get; set; } = DateTimeOffset.UtcNow;
    }
}

/// <summary>A playback on a paired server as Jellyfin's live stream: closing it stops the playback.</summary>
public sealed class PairedLiveStream : ILiveStream
{
    private readonly RemotePlays.Live _live;

    internal PairedLiveStream(RemotePlays.Live live, MediaSourceInfo source)
    {
        _live = live;
        MediaSource = source;
        UniqueId = "msfp-" + Guid.NewGuid().ToString("N");
        source.LiveStreamId = UniqueId;
        live.StreamId = UniqueId;
    }

    /// <inheritdoc />
    public int ConsumerCount { get; set; }

    /// <inheritdoc />
    public string OriginalStreamId { get; set; } = string.Empty;

    /// <inheritdoc />
    public string TunerHostId => "msfp";

    /// <inheritdoc />
    public bool EnableStreamSharing => false;

    /// <inheritdoc />
    public MediaSourceInfo MediaSource { get; set; }

    /// <inheritdoc />
    public string UniqueId { get; }

    /// <inheritdoc />
    public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task Close() => _live.Owner.EndAsync(_live, _live.PositionMs);

    /// <inheritdoc />
    public System.IO.Stream GetStream() => throw new NotSupportedException();

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
