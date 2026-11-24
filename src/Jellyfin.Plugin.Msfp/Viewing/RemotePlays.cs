using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Msfp;
using Msfp.Protocol;
using PlayRequest = Msfp.PlayRequest;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// This server's viewers playing paired servers' titles (MSFP playback,
/// viewer side; migration plan 4.2). A play is made at PlaybackInfo
/// (<see cref="HandOverFilter"/>): by default the copy read as a plain file
/// by byte ranges at the plugin's file route, behind the play's token,
/// which Jellyfin plays, remuxes or converts as it does a local file; the
/// MSFP playback (delivery file) starts at its first read, and again on the
/// same etag after it ended (a long pause). Else the paired server's HLS,
/// as Jellyfin's live stream. Where the viewer is goes to the paired server
/// from Jellyfin's playback events (in the copy: an episode among several
/// in one copy from its start); the playback stops when Jellyfin's session
/// stops, or after <see cref="Idle"/> with neither a report nor a read.
/// </summary>
public sealed class RemotePlays : IHostedService, IDisposable
{
    /// <summary>How long a play nobody reports on nor reads lives.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);

    /// <summary>How long a paired server answering busy at a first read is taken for busy.</summary>
    public static readonly TimeSpan BusyFor = TimeSpan.FromMinutes(2);

    private readonly EngineHost _host;
    private readonly StandIns _standIns;
    private readonly ISessionManager _sessions;
    private readonly IConfigurationManager _config;
    private readonly ILibraryManager _library;
    private readonly PeerPreviews _previews;
    private readonly ILogger<RemotePlays> _log;

    // Remembered: the paired servers that refused delivery file (until their
    // catalog is read again), and those that answered busy at a first read.
    private readonly ConcurrentDictionary<string, bool> _refusedFile = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _busyUntil = new(StringComparer.Ordinal);
    private Timer? _sweep;

    public RemotePlays(EngineHost host, StandIns standIns, ISessionManager sessions, IConfigurationManager config, ILibraryManager library, PeerPreviews previews, ILogger<RemotePlays> log)
    {
        _library = library;
        _previews = previews;
        _host = host;
        _standIns = standIns;
        _sessions = sessions;
        _config = config;
        _log = log;
    }

    /// <summary>The live plays' tokens.</summary>
    internal PlayTokens Tokens { get; } = new();

    /// <summary>The plays with an MSFP playback live now.</summary>
    public int Count => Tokens.All.Count(t => t.Playback is not null);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackProgress += Progress;
        _sessions.PlaybackStart += Started;
        _sessions.PlaybackStopped += Stopped;
        _host.Events += Event;
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackProgress -= Progress;
        _sessions.PlaybackStart -= Started;
        _sessions.PlaybackStopped -= Stopped;
        _host.Events -= Event;
        _sweep?.Change(Timeout.Infinite, Timeout.Infinite);
        foreach (var t in Tokens.All)
        {
            await EndAsync(t, drop: true).ConfigureAwait(false);
        }
    }

    public void Dispose() => _sweep?.Dispose();

    // ---- At PlaybackInfo (HandOverFilter) ----

    /// <summary>A stand-in's title: the paired servers holding it, best copy first; null when none.</summary>
    internal Task<Playable?> TitleAsync(BaseItem item, CancellationToken ct) => _standIns.CopyOfAsync(item.Path!, ct);

    /// <summary>The copy a client picked by one of the stand-in's saved sources' IDs; null for none.</summary>
    internal string? PickedCopy(BaseItem item, Playable title, string? sourceId)
    {
        if (string.IsNullOrEmpty(sourceId) || title.Chosen is not { } h)
        {
            return null;
        }

        for (var i = 0; i < h.Copies.Count; i++)
        {
            if (string.Equals(PairedSources.SourceIdOf(item, h.Peer, h.Copies[i].Id, i == 0), sourceId, StringComparison.OrdinalIgnoreCase))
            {
                return h.Copies[i].Id;
            }
        }

        return null;
    }

    /// <summary>The track IDs by the Index the client saw them at: the picked copy's of the stand-in's holder, else its best's.</summary>
    internal IReadOnlyDictionary<int, string> TracksOf(BaseItem item, Playable title, string? picked)
    {
        _ = item;
        if (title.Chosen is not { } h)
        {
            return new Dictionary<int, string>();
        }

        return StandIns.TracksOf(h.Copies.FirstOrDefault(c => c.Id == picked) ?? h.Best);
    }

    /// <summary>What is known of each paired server for a choice: a stream free, a refusal of delivery file, the pairing's cap.</summary>
    internal async Task<IReadOnlyDictionary<string, HandOver.PeerState>> PeerStatesAsync(CancellationToken ct)
    {
        var states = new Dictionary<string, HandOver.PeerState>(StringComparer.Ordinal);
        if (_host.Engine is not { } engine)
        {
            return states;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var p in await engine.PeersAsync(ct).ConfigureAwait(false))
        {
            states[p.Id] = State(p.Id, p.TheirLimits, Tokens.Holding(p.Id), _busyUntil.TryGetValue(p.Id, out var until) && until > now, _refusedFile.ContainsKey(p.Id));
        }

        return states;
    }

    /// <summary>A paired server's state for a choice, from its limits for us (null: not known), our plays holding a stream on it and what was remembered.</summary>
    internal static HandOver.PeerState State(string peer, Limits? theirs, int holding, bool busyLately, bool refusedFile)
    {
        _ = peer;
        var free = !busyLately && (theirs is null || holding < theirs.Streams);
        return new HandOver.PeerState(free, refusedFile, theirs?.BitrateKbps ?? 0);
    }

    /// <summary>A play of a choice, for a user: its token and the source Jellyfin is given.</summary>
    internal PlayToken Mint(Guid user, BaseItem item, HandOver.Choice choice)
    {
        var tracks = StandIns.TracksOf(choice.Copy);
        var play = Tokens.Mint(user, item.Id, choice.Holder.Peer, choice.Holder.Key, choice.Copy, choice.Way, choice.Asked, tracks);
        if (choice.Way == Way.File)
        {
            var (source, _) = _standIns.SourceFor(item, choice.Holder, choice.Copy, play.Handle, s => Loopback() + SubtitlePath(play, s));
            source.Path = Loopback() + FilePath(play);
            source.RequiresOpening = false;
            source.RequiresClosing = false;
            source.OpenToken = null;
            source.IsRemote = false;
            source.SupportsProbing = false;

            // A file: sorted before the stand-in's saved source of the same
            // ID (a client naming the ID it was shown, PairedSources).
            source.VideoType = VideoType.VideoFile;

            // Played as a file the client reads, unless the title is only
            // part of the copy: then Jellyfin reads it, from the episode's
            // start to its end (FilePath).
            source.SupportsDirectPlay = !StandInStreams.Spanned(choice.Copy);
            source.SupportsDirectStream = true;
            source.SupportsTranscoding = true;
            foreach (var s in source.MediaStreams.Where(s => s.IsExternal))
            {
                // Served apart as WebVTT, or PGS.
                if (s.Codec != "PGSSUB")
                {
                    s.Codec = "webvtt";
                }
            }

            play.Source = source;
        }
        else
        {
            // The HLS way: the copy's source under the play's handle, which
            // Jellyfin opens (OpenToken) within the PlaybackInfo request
            // (PairedSources gives it there only).
            var (source, _) = _standIns.SourceFor(item, choice.Holder, choice.Copy, play.Handle, s => Loopback() + SubtitlePath(play, s));
            play.Source = Openable(source, item, play.Handle);
        }

        return play;
    }

    /// <summary>A play's source of the HLS way: opened by Jellyfin (its open token names the stand-in and the play's handle), never read or probed as it is.</summary>
    internal static MediaSourceInfo Openable(MediaSourceInfo source, BaseItem item, string handle)
    {
        source.Id = handle;
        source.OpenToken = PairedSources.TokenOf(item, handle);
        source.RequiresOpening = true;
        source.RequiresClosing = true;
        source.SupportsDirectPlay = false;
        source.SupportsProbing = false;
        return source;
    }

    /// <summary>The ID of the stand-in's saved source for a choice's copy (the one its client is shown): the copy's when the stand-in's holder was chosen, else its first.</summary>
    internal string SavedSourceId(BaseItem item, Playable title, HandOver.Choice choice)
    {
        if (title.Chosen is { } h && h == choice.Holder)
        {
            var at = h.Copies.ToList().FindIndex(c => c.Id == choice.Copy.Id);
            if (at >= 0)
            {
                return PairedSources.SourceIdOf(item, h.Peer, choice.Copy.Id, at == 0);
            }
        }

        return item.Id.ToString("N");
    }

    /// <summary>A play's file at the plugin's route; for a title that is only part of its copy, a script Jellyfin's ffmpeg reads the span through.</summary>
    internal static string FilePath(PlayToken play) =>
        "/Msfp/File/" + play.Secret + (StandInStreams.Spanned(play.Copy) ? "/" + SpanName : string.Empty);

    /// <summary>The name of a span's script at the file route.</summary>
    internal const string SpanName = "span.ffconcat";

    /// <summary>
    /// An ffmpeg concat script of one file, the copy beside it at the route,
    /// from the title's start to its end: Jellyfin's ffmpeg reads it as the
    /// title alone, its times from 0 (MSFP video section 5.2).
    /// </summary>
    internal static string SpanScript(Copy copy)
    {
        var (start, end) = StandInStreams.Span(copy);
        var script = "ffconcat version 1.0\nfile 'f'\n";
        if (start > 0)
        {
            script += string.Create(System.Globalization.CultureInfo.InvariantCulture, $"inpoint {start / 1000.0:0.000}\n");
        }

        if (end < copy.DurationMs)
        {
            script += string.Create(System.Globalization.CultureInfo.InvariantCulture, $"outpoint {end / 1000.0:0.000}\n");
        }

        return script;
    }

    /// <summary>Where Jellyfin reads a play's subtitle track served apart.</summary>
    internal static string SubtitlePath(PlayToken play, CopySubtitle s) =>
        "/Msfp/Subtitle/" + play.Secret + "/" + s.Id + (s.Format == "pgs" ? ".sup" : ".vtt");

    /// <summary>This server's loopback base address, for Jellyfin's own reads.</summary>
    internal string Loopback()
    {
        var net = _config.GetConfiguration<MediaBrowser.Common.Net.NetworkConfiguration>("network");
        var baseUrl = (net.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length > 0 && !baseUrl.StartsWith('/'))
        {
            baseUrl = "/" + baseUrl;
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"http://127.0.0.1:{net.InternalHttpPort}{baseUrl}");
    }

    /// <summary>A play's source for a request (a copy: Jellyfin changes what it's given).</summary>
    internal static MediaSourceInfo Clone(MediaSourceInfo s)
    {
        var o = Jellyfin.Extensions.Json.JsonDefaults.Options;
        return JsonSerializer.Deserialize<MediaSourceInfo>(JsonSerializer.SerializeToUtf8Bytes(s, o), o)!;
    }

    // ---- The plain file (FileController) ----

    /// <summary>
    /// A play's MSFP playback for a read: the one live, else a new one
    /// (delivery file; after one that ended, on the same etag). Null, and
    /// the read fails, when the paired server refuses or is busy: that is
    /// remembered, so the next PlaybackInfo takes another way or server.
    /// </summary>
    internal async Task<RemotePlayback?> PlaybackAsync(PlayToken play, CancellationToken ct)
    {
        play.Touched = DateTimeOffset.UtcNow;
        play.FirstRead ??= play.Touched;
        if (play.Playback is { } live)
        {
            return live;
        }

        if (_host.Engine is not { } engine)
        {
            return null;
        }

        await play.Starting.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (play.Playback is { } started)
            {
                return started;
            }

            if (play.Last is { } last)
            {
                // After a long pause: the same copy, on the same etag, or
                // the copy changed (the engine reads the title again).
                try
                {
                    play.Playback = await engine.RestartAsync(last, ct).ConfigureAwait(false);
                    _log.LogInformation("MSFP: playing {Key} from {Peer} as a file again", play.Key, play.Peer);
                    return play.Playback;
                }
                catch (MsfpException e) when (e.Code == MsfpError.CopyChanged)
                {
                    _log.LogInformation("MSFP: {Key} changed on {Peer}: read again", play.Key, play.Peer);
                    await EndAsync(play, drop: true).ConfigureAwait(false);
                    return null;
                }
            }

            var request = new PlayRequest
            {
                Title = play.Key,
                Delivery = global::Msfp.Protocol.Playback.DeliveryFile,
                VersionId = play.Copy.Id,
                StartMs = InCopy(play, play.PositionMs),
            };
            play.Playback = await engine.PlayAsync(play.Peer, request, ct).ConfigureAwait(false);
            if (play.Playback.Etag != play.Copy.ETag && play.Copy.ETag.Length > 0)
            {
                // The copy changed on the paired server: its catalog is read
                // again (the engine), and this play ends.
                _log.LogInformation("MSFP: {Key} changed on {Peer}: read again", play.Key, play.Peer);
                await EndAsync(play, drop: true).ConfigureAwait(false);
                return null;
            }

            _log.LogInformation("MSFP: playing {Key} from {Peer} as a file", play.Key, play.Peer);
            Previews(engine, play);
            return play.Playback;
        }
        catch (MsfpException e)
        {
            Remember(play.Peer, e.Code);
            _log.LogInformation("MSFP: {Peer} didn't play {Key} as a file: {Code}", play.Peer, play.Key, e.Code);
            return null;
        }
        finally
        {
            play.Starting.Release();
        }
    }

    // A playback started: the copy's previews fetched in the background
    // when they are the stand-in's (its first source's copy, answer 8).
    private void Previews(MsfpEngine engine, PlayToken play)
    {
        if (_library.GetItemById(play.Item) is { Path: { } path } item
            && _standIns.CopyOf(path)?.Chosen is { } h && h.Peer == play.Peer && h.Best.Id == play.Copy.Id)
        {
            _previews.Fetch(engine, item, play.Peer, play.Key, play.Copy);
        }
    }

    /// <summary>A playback found ended at a read (a long pause): forgotten here, so the next read starts it again.</summary>
    internal void Ended(PlayToken play, RemotePlayback ended)
    {
        lock (play)
        {
            if (play.Playback?.Id == ended.Id)
            {
                play.Last = ended;
                play.Playback = null;
            }
        }
    }

    /// <summary>A play's MSFP playback for its subtitle tracks served apart: a file's started if need be (a first read), the HLS way's as it is.</summary>
    internal Task<RemotePlayback?> PlaybackForSubtitlesAsync(PlayToken play, CancellationToken ct) =>
        play.Way == Way.File ? PlaybackAsync(play, ct) : Task.FromResult(play.Playback);

    // A refusal remembered: delivery file refused, until the server's
    // catalog is read again; busy, for a while.
    private void Remember(string peer, MsfpError code)
    {
        switch (code)
        {
            case MsfpError.DeliveryRefused:
                _refusedFile[peer] = true;
                break;
            case MsfpError.StreamLimitReached or MsfpError.ServerBusy or MsfpError.BackingOff:
                _busyUntil[peer] = DateTimeOffset.UtcNow + BusyFor;
                break;
        }
    }

    // ---- The HLS way (PairedSources) ----

    /// <summary>
    /// Opens a play of the HLS way: the paired server asked what the choice
    /// says (a refused remux asked as a conversion, then as the baseline),
    /// and the source Jellyfin reads: the relay route on its own loopback,
    /// with the streams the paired server sends.
    /// </summary>
    /// <exception cref="InvalidOperationException">The paired server didn't play it; the message says why.</exception>
    internal async Task<PairedLiveStream> OpenAsync(BaseItem item, PlayToken play, CancellationToken ct)
    {
        var engine = _host.Engine ?? throw new InvalidOperationException("Federation is off on this server.");
        var asked = play.Asked ?? throw new InvalidOperationException("Not a play of the HLS way.");
        RemotePlayback? playback = null;
        while (playback is null)
        {
            try
            {
                playback = await engine.PlayAsync(play.Peer, asked, ct).ConfigureAwait(false);
            }
            catch (MsfpException e) when (e.Code == MsfpError.DeliveryRefused && HandOver.Lower(asked) is { } lower)
            {
                _log.LogInformation("MSFP: {Peer} refused {Delivery} of {Key}: asked again lower", play.Peer, asked.Delivery.Length > 0 ? asked.Delivery : "the baseline", play.Key);
                asked = lower;
            }
            catch (MsfpException e)
            {
                Remember(play.Peer, e.Code);
                Tokens.Drop(play);
                _log.LogInformation("MSFP: {Peer} didn't play {Key}: {Code}", play.Peer, play.Key, e.Code);
                if (e.Code is MsfpError.StreamLimitReached or MsfpError.ServerBusy or MsfpError.BackingOff)
                {
                    throw new PairedBusyException("The paired server holding this title is busy now. Try again later.");
                }

                throw new PairedRefusedException("The paired server couldn't play this title now.");
            }
        }

        play.Playback = playback;
        play.FirstRead = play.Touched = DateTimeOffset.UtcNow;
        Previews(engine, play);
        _log.LogInformation("MSFP: playing {Key} from {Peer} ({Method}, {Protocol})", play.Key, play.Peer, playback.Method, playback.Protocol);
        var source = HlsSource(item, play, playback, Loopback());
        return new PairedLiveStream(this, play, source);
    }

    /// <summary>
    /// The source of a play of the HLS way: the relay route, the copy's
    /// picture as sent (its own, or converted to H.264 within the cap) and
    /// the one audio track played, then the subtitle tracks served apart.
    /// </summary>
    internal static MediaSourceInfo HlsSource(BaseItem item, PlayToken play, RemotePlayback p, string loopback)
    {
        var copy = play.Copy;
        var converted = p.Method == global::Msfp.Protocol.Playback.MethodTranscode;
        var t = play.Asked?.Transcode;
        var streams = new List<MediaStream>();
        foreach (var s in copy.SubtitleTracks.Where(s => p.Subtitles.Any(x => x.Id == s.Id)))
        {
            var st = StandInStreams.Subtitle(s, _ => null);
            st.Index = streams.Count;
            st.IsExternal = true;
            st.Codec = s.Format == "pgs" ? "PGSSUB" : "webvtt";
            st.Path = loopback + SubtitlePath(play, s);
            streams.Add(st);
        }

        var at = streams.Count;
        if (copy.Video is { } v)
        {
            var video = StandInStreams.Video(v, "ts", false);
            if (converted && (t is null || t.VideoCodec != global::Msfp.Protocol.Names.Copy))
            {
                video = new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Profile = "High", Level = 41, BitDepth = 8, Width = v.Width, Height = v.Height, PixelFormat = "yuv420p", ColorTransfer = "bt709", ColorPrimaries = "bt709", ColorSpace = "bt709" };
                if (p.MaxBitrateKbps > 0)
                {
                    video.BitRate = p.MaxBitrateKbps * 1000;
                }
            }

            video.Index = at++;
            streams.Add(video);
        }

        if ((copy.AudioTracks.FirstOrDefault(a => a.Id == p.AudioTrack) ?? copy.AudioTracks.FirstOrDefault()) is { } audio)
        {
            var copied = !converted || (t is not null && t.AudioCodec == global::Msfp.Protocol.Names.Copy);
            var st = copied
                ? StandInStreams.Audio(audio, _ => null, false)
                : new MediaStream { Type = MediaStreamType.Audio, Codec = "aac", Profile = "LC", Channels = 2, ChannelLayout = "stereo", SampleRate = 48000 };
            st.Language = audio.Language is { Length: > 0 } l ? Identification.Languages.Three(l) : null;
            st.IsDefault = true;
            st.Index = at;
            streams.Add(st);
        }

        return new MediaSourceInfo
        {
            Id = item.Id.ToString("N"),
            Name = "From a paired server",
            Protocol = MediaProtocol.Http,
            Path = loopback + "/Msfp/Relay/" + play.Secret + "/master.m3u8",
            IsRemote = true,
            RequiresOpening = false,
            RequiresClosing = true,
            SupportsProbing = false,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            Container = "hls",
            IsInfiniteStream = false,
            RunTimeTicks = StandInStreams.DurationMs(copy) is > 0 and var ms ? ms * TimeSpan.TicksPerMillisecond : item.RunTimeTicks,
            Type = MediaSourceType.Default,
            MediaStreams = streams,
        };
    }

    /// <summary>A play of the HLS way by Jellyfin's ID of its live stream (the provider's prefix, then ours), null when none.</summary>
    internal PlayToken? ByLiveStreamId(string liveStreamId) =>
        Tokens.All.FirstOrDefault(t => t.Way == Way.Hls && SameStream(liveStreamId, t.StreamId));

    // ---- Where the viewer is ----

    private void Started(object? sender, PlaybackProgressEventArgs e) => Report(e, starting: true);

    private void Progress(object? sender, PlaybackProgressEventArgs e) => Report(e, starting: false);

    private void Report(PlaybackProgressEventArgs e, bool starting)
    {
        if (Of(e, starting) is { } play)
        {
            play.Touched = DateTimeOffset.UtcNow;
            play.PositionMs = (e.PlaybackPositionTicks ?? 0) / TimeSpan.TicksPerMillisecond;
            if (play.Playback is { } p && _host.Engine is { } engine)
            {
                engine.Progress(p.Id, InCopy(play, play.PositionMs), e.IsPaused);
            }
        }
    }

    /// <summary>A position in a play's title as a position in its copy (MSFP video section 5.2).</summary>
    internal static long InCopy(PlayToken play, long positionMs) => Math.Max(positionMs, 0) + play.StartMs;

    // A play the paired server's side ended (its operator, expiry, the
    // pairing ended or suspended): forgotten here; a file read after it
    // starts another. A peer's catalog read again: its refusal of delivery
    // file forgotten.
    private void Event(MsfpEvent e)
    {
        switch (e)
        {
            case RemotePlaybackEnded ended when Tokens.All.FirstOrDefault(t => t.Playback?.Id == ended.Playback) is { } play:
                _log.LogInformation("MSFP: a playback of {Key} ended on the paired server's side ({Reason})", play.Key, ended.Reason);
                if (play.Playback is { } gone)
                {
                    Ended(play, gone);
                }

                if (play.Way == Way.Hls || ended.Reason is EndReason.PairingEnded)
                {
                    Tokens.Drop(play);
                }

                break;
            case CatalogReadEnded { Error: null } read:
                _refusedFile.TryRemove(read.Peer, out _);
                break;
        }
    }

    private void Stopped(object? sender, PlaybackStopEventArgs e)
    {
        if (Of(e) is { } play)
        {
            play.PositionMs = (e.PlaybackPositionTicks ?? play.PositionMs * TimeSpan.TicksPerMillisecond) / TimeSpan.TicksPerMillisecond;

            // The play stays a while: a client may go on with it (another
            // track, a seek in a conversion), which starts a new playback.
            _ = EndAsync(play, drop: play.Way == Way.Hls);
        }
    }

    // The play a report is about: by the media source the session plays
    // (a file's handle), by its live stream, else as LiveOf finds it.
    private PlayToken? Of(PlaybackProgressEventArgs e, bool starting = false)
    {
        if (e.Item is not { } item || !PairedSources.IsStandIn(item))
        {
            return null;
        }

        var source = e.MediaSourceId ?? e.Session?.PlayState?.MediaSourceId;
        if ((Tokens.OfHandle(source) ?? Shown(source, e.Session, item.Id)) is { } byHandle && byHandle.Item == item.Id)
        {
            if (e.Session?.Id is { } sid)
            {
                byHandle.SessionId ??= sid;
            }

            return byHandle;
        }

        return LiveOf(item.Id, e.Session?.PlayState?.LiveStreamId, e.Session?.Id, starting);
    }

    // A play by the source ID its client was shown, for a session's user and device.
    private PlayToken? Shown(string? source, SessionInfo? session, Guid item) =>
        session is null ? null : Tokens.OfShown(source, session.UserId, item, session.DeviceId);

    private PlayToken? LiveOf(Guid item, string? liveStreamId, string? sessionId, bool starting = false)
    {
        var plays = Tokens.All.Where(t => t.Item == item && t.FirstRead is not null).ToList();
        var at = Match(plays.Select(t => (t.StreamId, t.SessionId, t.Made)).ToList(), liveStreamId, sessionId, starting);
        if (at < 0)
        {
            return null;
        }

        var play = plays[at];
        if (sessionId is not null)
        {
            play.SessionId ??= sessionId;
        }

        return play;
    }

    /// <summary>
    /// <see cref="LiveOf"/>'s choice among a title's plays (their live
    /// stream, session and making): the index of the one a session's report
    /// is about, -1 for none.
    /// </summary>
    internal static int Match(IReadOnlyList<(string StreamId, string? SessionId, DateTimeOffset Opened)> lives, string? liveStreamId, string? sessionId, bool starting)
    {
        for (var i = 0; i < lives.Count; i++)
        {
            if (SameStream(liveStreamId, lives[i].StreamId))
            {
                return i;
            }
        }

        for (var i = 0; sessionId is not null && i < lives.Count; i++)
        {
            if (lives[i].SessionId == sessionId)
            {
                return i;
            }
        }

        if (lives.Count == 1)
        {
            return 0;
        }

        var newest = -1;
        for (var i = 0; starting && i < lives.Count; i++)
        {
            if (lives[i].SessionId is null && (newest < 0 || lives[i].Opened > lives[newest].Opened))
            {
                newest = i;
            }
        }

        return newest;
    }

    /// <summary>
    /// Whether Jellyfin's ID of a live stream is ours: Jellyfin puts its
    /// provider's prefix and "_" before the ID the live stream gave
    /// ("{md5}_msfp-{guid}").
    /// </summary>
    internal static bool SameStream(string? jellyfinId, string ours) =>
        jellyfinId is { Length: > 0 } id && ours.Length > 0
        && (id == ours || id.EndsWith("_" + ours, StringComparison.Ordinal));

    // Every minute. A play a Jellyfin session is still on is kept and
    // reported to the paired server, paused or not: some clients report
    // seldom while paused, past the paired server's 2 minutes without news
    // (MSFP playback 6.3). Others, neither reported on nor read for Idle:
    // stopped and dropped; plays never read, dropped after their lapse.
    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        Tokens.Sweep();
        var kept = new HashSet<PlayToken>();
        foreach (var session in _sessions.Sessions)
        {
            if (session.NowPlayingItem is not { } item
                || ((Tokens.OfHandle(session.PlayState?.MediaSourceId) ?? Shown(session.PlayState?.MediaSourceId, session, item.Id)) is { } h && h.Item == item.Id ? h : LiveOf(item.Id, session.PlayState?.LiveStreamId, session.Id)) is not { } play
                || !kept.Add(play))
            {
                continue;
            }

            play.Touched = now;
            if (session.PlayState?.PositionTicks is { } ticks)
            {
                play.PositionMs = ticks / TimeSpan.TicksPerMillisecond;
            }

            if (play.Playback is { } p && _host.Engine is { } engine)
            {
                engine.Progress(p.Id, InCopy(play, play.PositionMs), session.PlayState?.IsPaused ?? false);
            }
        }

        foreach (var play in Tokens.All.Where(t => !kept.Contains(t) && t.FirstRead is not null))
        {
            if (now - play.Touched > Idle)
            {
                if (play.Playback is not null)
                {
                    _log.LogInformation("MSFP: a play of {Key} neither reported on nor read for {Idle}: stopped", play.Key, Idle);
                }

                _ = EndAsync(play, drop: true);
            }
        }
    }

    /// <summary>Stops a play's MSFP playback on its paired server, once; dropped, its token opens nothing more.</summary>
    internal async Task EndAsync(PlayToken play, bool drop)
    {
        if (drop)
        {
            Tokens.Drop(play);
        }

        RemotePlayback? playback;
        lock (play)
        {
            playback = play.Playback;
            play.Playback = null;
            if (playback is not null)
            {
                play.Last = playback;
            }
        }

        if (playback is null)
        {
            return;
        }

        try
        {
            if (_host.Engine is { } engine)
            {
                await engine.StopAsync(playback.Id, InCopy(play, play.PositionMs)).ConfigureAwait(false);
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

    /// <summary>A play's live stream (the HLS way) by its stand-in, for Jellyfin closing it.</summary>
    internal Task CloseAsync(PlayToken play) => EndAsync(play, drop: true);
}

/// <summary>A play of the HLS way as Jellyfin's live stream: closing it stops the playback.</summary>
public sealed class PairedLiveStream : ILiveStream
{
    private readonly RemotePlays _owner;
    private readonly PlayToken _play;

    internal PairedLiveStream(RemotePlays owner, PlayToken play, MediaSourceInfo source)
    {
        _owner = owner;
        _play = play;
        MediaSource = source;
        UniqueId = "msfp-" + Guid.NewGuid().ToString("N");
        source.LiveStreamId = UniqueId;
        play.StreamId = UniqueId;
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
    public Task Close() => _owner.CloseAsync(_play);

    /// <inheritdoc />
    public System.IO.Stream GetStream() => throw new NotSupportedException();

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
