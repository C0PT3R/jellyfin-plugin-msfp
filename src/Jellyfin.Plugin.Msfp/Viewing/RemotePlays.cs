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
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
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

    /// <summary>Whether paired servers' titles are kept as stand-ins and play: not on this branch until phases 5 and 6 of the migration (see <see cref="OpenAsync"/>, StandIns.KeepAsync).</summary>
    internal static bool ViewingOn { get; set; }

    // Since MSFP's revision a play names its delivery (no capabilities):
    // the viewer's side chooses it from the copy's facts in phase 6 of the
    // migration (the hand-over at PlaybackInfo).

    private readonly EngineHost _host;
    private readonly StandIns _standIns;
    private readonly ISessionManager _sessions;
    private readonly IConfigurationManager _config;
    private readonly IMediaEncoder _encoder;
    private readonly ILocalizationManager _localization;
    private readonly PeerPreviews _previews;
    private readonly MediaBrowser.Controller.MediaSegments.IMediaSegmentManager _segments;
    private readonly ILogger<RemotePlays> _log;
    private readonly ConcurrentDictionary<string, Live> _byToken = new(StringComparer.Ordinal);
    private Timer? _sweep;

    public RemotePlays(EngineHost host, StandIns standIns, ISessionManager sessions, IConfigurationManager config, IMediaEncoder encoder, ILocalizationManager localization, MediaBrowser.Controller.Trickplay.ITrickplayManager trickplay, MediaBrowser.Controller.MediaSegments.IMediaSegmentManager segments, ILogger<RemotePlays> log)
    {
        _segments = segments;
        _previews = new PeerPreviews(trickplay, log);
        _localization = localization;
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
        _sessions.PlaybackStart += Started;
        _sessions.PlaybackStopped += Stopped;
        _host.Events += Event;
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        // Playbacks' PGS files left by a stop of Jellyfin.
        try
        {
            var playing = System.IO.Path.GetDirectoryName(Subtitles.PictureFolder("x"))!;
            if (System.IO.Directory.Exists(playing))
            {
                System.IO.Directory.Delete(playing, true);
            }
        }
        catch (System.IO.IOException)
        {
        }

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
    internal async Task<PairedLiveStream> OpenAsync(BaseItem item, (string Audio, string Subtitles) wishes, CancellationToken ct, AudioSwitch? audioSwitch = null)
    {
        // Playing paired servers' titles is off on this branch until phase 6
        // of the migration to MSFP's revised version 0 (the hand-over at
        // PlaybackInfo, from the copies' facts); the code below is the draft
        // before's, adapted only to compile.
        if (!ViewingOn)
        {
            throw new InvalidOperationException("Playing paired servers' titles comes back with the migration's phase 6.");
        }

        _ = wishes;
        var request = new PlayRequest { Title = string.Empty };
        var engine = _host.Engine ?? throw new InvalidOperationException("Federation is off on this server.");
        var key = item.Path![Wanted.PathOf(string.Empty).Length..];
        var copy = await _standIns.CopyOfAsync(key, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("No paired server holds this title now.");
        var busy = false;
        foreach (var holder in copy.Holders)
        {
            RemotePlayback playback;
            try
            {
                // A track picked during a playback: its ID on the server that
                // played it; on another, the same language.
                var asked = audioSwitch is not null && holder.Peer == audioSwitch.Peer ? request with { AudioTrack = audioSwitch.Track } : request;
                playback = await engine.PlayAsync(holder.Peer, asked with { Title = key }, ct).ConfigureAwait(false);
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
                live.VideoBitrateFilled = await ProbeAsync(source, ct).ConfigureAwait(false);
                NameAudio(source, playback);
                ListAllAudio(source, playback, live);
                live.DurationMs = (source.RunTimeTicks ?? 0) / TimeSpan.TicksPerMillisecond;
                await KeepRuntimeAsync(item, live.DurationMs, ct).ConfigureAwait(false);

                // PGS tracks are fetched before Jellyfin has the source: it
                // may burn one in from its first segment. One not ready in
                // 15 seconds is left out of this playback.
                var pictures = new List<string>(); // the copy's PGS tracks: phase 6
                var ready = await Subtitles.FetchPicturesAsync(engine, live, pictures, TimeSpan.FromSeconds(15), _log).ConfigureAwait(false);
                AddSubtitles(source, playback, token, ready);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "MSFP: {Key} from {Peer} couldn't be read; the next one holding it, if any", key, holder.Peer);
                await EndAsync(live, 0).ConfigureAwait(false);
                continue;
            }

            StandIns.Playing.AddOrUpdate(item.Id, 1, (_, n) => n + 1);
            StandIns.SavePlaying(item, source.MediaStreams.ToList());
            // The skip markers before Jellyfin hands the source over: clients ask for them as playing starts.
            try
            {
                // The copy's segments since MSFP's revision: saved at each
                // catalog reading in phase 5, not at play.
                IReadOnlyList<global::Msfp.Protocol.CopySegment> segments = [];
                await PairedSegments.SaveAsync(_segments, item, segments, ct).ConfigureAwait(false);
                source.HasSegments = segments.Count > 0;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogInformation(e, "MSFP: the skip markers of {Key} couldn't be kept", key);
            }

            _previews.Fetch(engine, item, playback);
            return new PairedLiveStream(live, source);
        }

        if (busy)
        {
            throw new PairedBusyException("The paired servers holding this title are busy now. Try again later.");
        }

        throw new InvalidOperationException("No paired server could play this title now.");
    }

    // A stand-in's runtime, from its copy's length: Jellyfin marks an item
    // without one as played at any stop (UserDataManager.UpdatePlayState),
    // and its TMDB provider gives none to films and episodes. Kept on the
    // item Jellyfin holds, from the first play on.
    private static async Task KeepRuntimeAsync(BaseItem item, long durationMs, CancellationToken ct)
    {
        if (durationMs <= 0)
        {
            return;
        }

        var ticks = durationMs * TimeSpan.TicksPerMillisecond;
        if (item.RunTimeTicks is { } had && Math.Abs(had - ticks) < 5 * TimeSpan.TicksPerSecond)
        {
            return;
        }

        item.RunTimeTicks = ticks;
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
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

    // A tag of the paired server's ("fr") as Jellyfin writes a stream's
    // language ("fre"), the localization's for a language not listed.
    private string Three(string tag) =>
        Identification.Languages.Three(tag) is { Length: > 0 } three ? three
        : _localization.FindLanguageInfo(tag.Split('-')[0])?.ThreeLetterISOLanguageName ?? tag;

    // A Jellyfin language code ("fre") as a tag MSFP sends ("fr"); "" for none.
    private string Tag(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var info = _localization.FindLanguageInfo(code.Trim());
        var tag = info?.TwoLetterISOLanguageName is { Length: 2 } two ? two : code.Trim();
        return tag.Length <= 12 && tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? tag : string.Empty;
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
    // True when the video's bitrate was filled in (VideoBitrate).
    private async Task<bool> ProbeAsync(MediaSourceInfo source, CancellationToken ct)
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
        return VideoBitrate(source);
    }

    // A video track without a bitrate (a Matroska file without statistics)
    // gets the file's share left after its audio: Jellyfin guesses one from
    // the picture's width for an opened live stream otherwise (8 Mbit/s for
    // 720p, MediaSourceManager.AddMediaInfo), which a local file never
    // gets, and which then caps the player's quality choices and makes
    // Jellyfin convert for a viewer limited below it.
    // The answer to the client then shows none, as for the local file
    // (AudioSwitchFilter): Jellyfin's own decisions keep the share.
    internal static bool VideoBitrate(MediaSourceInfo source)
    {
        var video = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        if (video is null || video.BitRate is > 0 || source.Bitrate is not { } total || total <= 0)
        {
            return false;
        }

        var audio = source.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).Sum(s => (long)(s.BitRate ?? 0));
        var left = total - audio;
        video.BitRate = (int)Math.Clamp(left > 0 ? left : total, 1, int.MaxValue);
        return true;
    }

    // A converted stream carries one audio track and no language: the one
    // the play answer says it chose.
    private void NameAudio(MediaSourceInfo source, RemotePlayback p)
    {
        // The play answer names no track since MSFP's revision: the copy
        // does (phase 6 of the migration names the stream from it; the
        // hand-over then removes this, plan 4.2).
    }

    // A converted stream carries the chosen audio track only: the copy's
    // others are listed too, so that players offer them. Each track keeps
    // an Index from its place among the copy's (the same for every
    // playback of that copy), and the one playing comes first in the list,
    // after the video: Jellyfin maps a stream to ffmpeg by its place in the
    // list, not its Index. Picking another one plays the copy again with
    // it (AudioSwitchFilter).
    private void ListAllAudio(MediaSourceInfo source, RemotePlayback p, Live live)
    {
        // The copy's tracks come from the catalog since MSFP's revision:
        // phase 5 lists them on the stand-in before any play; this goes in
        // phase 6 (plan 4.2).
    }

    /// <summary>A playback by Jellyfin's ID of its live stream (the provider's prefix, then ours), null when none.</summary>
    internal Live? ByLiveStreamId(string liveStreamId) =>
        _byToken.Values.FirstOrDefault(l => SameStream(liveStreamId, l.StreamId));

    // The paired server's subtitles, text (WebVTT) and pictures (PGS), as
    // external tracks Jellyfin reads whole at the relay (Subtitles.cs), in
    // place of the subtitle tracks found inside a file played as it is
    // (the same tracks, checked by the engine).
    private void AddSubtitles(MediaSourceInfo source, RemotePlayback p, string token, IReadOnlySet<string> picturesReady)
    {
        // Since MSFP's revision the subtitles are the copy's tracks (in the
        // file) and the answer's served ones (RemotePlayback.Subtitles):
        // phase 6 of the migration adds them (plan 4.2). None until then.
    }

    // A track's words in the server's language, as Jellyfin's own reading
    // of a file gives them (ProbeResultNormalizer): its language's name,
    // "Default", "Forced", "External"...
    private MediaStream Localize(MediaStream s)
    {
        if (!string.IsNullOrEmpty(s.Language))
        {
            s.LocalizedLanguage = _localization.GetLanguageDisplayName(s.Language);
        }

        s.LocalizedDefault = _localization.GetLocalizedString("Default");
        s.LocalizedExternal = _localization.GetLocalizedString("External");
        if (s.Type == MediaStreamType.Audio)
        {
            s.LocalizedOriginal = _localization.GetLocalizedString("Original");
        }
        else
        {
            s.LocalizedUndefined = _localization.GetLocalizedString("Undefined");
            s.LocalizedForced = _localization.GetLocalizedString("Forced");
            s.LocalizedHearingImpaired = _localization.GetLocalizedString("HearingImpaired");
        }

        return s;
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
    private void Started(object? sender, PlaybackProgressEventArgs e) => Report(e, starting: true);

    private void Progress(object? sender, PlaybackProgressEventArgs e) => Report(e, starting: false);

    private void Report(PlaybackProgressEventArgs e, bool starting)
    {
        if (Of(e, starting) is { } live && _host.Engine is { } engine)
        {
            live.Touched = DateTimeOffset.UtcNow;
            live.PositionMs = (e.PlaybackPositionTicks ?? 0) / TimeSpan.TicksPerMillisecond;
            engine.Progress(live.Playback.Id, live.PositionMs, e.IsPaused);
        }
    }

    // A playback the paired server's side ended (its operator, expiry, the
    // pairing ended or suspended, its library no longer offered): forgotten
    // here at once, or a session still on the title would keep it.
    private void Event(MsfpEvent e)
    {
        if (e is RemotePlaybackEnded ended && _byToken.Values.FirstOrDefault(l => l.Playback.Id == ended.Playback) is { } live)
        {
            _log.LogInformation("MSFP: a playback of {Key} ended on the paired server's side ({Reason})", live.Playback.Title, ended.Reason);
            _ = EndAsync(live, live.PositionMs);
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
    private Live? Of(PlaybackProgressEventArgs e, bool starting = false)
    {
        if (e.Item is not { } item || !PairedSources.IsStandIn(item))
        {
            return null;
        }

        return LiveOf(item.Id, e.Session?.PlayState?.LiveStreamId, e.Session?.Id, starting);
    }

    // A stand-in's playback for a session: by the live stream it names,
    // else the one it was bound to, else the title's only one, else, as a
    // session starts playing, the newest one no session has: some clients
    // (Kodi's add-on) don't name the live stream, and two of them may play
    // the same title. The session is bound to the playback found.
    private Live? LiveOf(Guid item, string? liveStreamId, string? sessionId, bool starting = false)
    {
        var lives = _byToken.Values.Where(l => l.Item.Id == item).ToList();
        var at = Match(lives.Select(l => (l.StreamId, l.SessionId, l.Opened)).ToList(), liveStreamId, sessionId, starting);
        if (at < 0)
        {
            return null;
        }

        var live = lives[at];
        if (sessionId is not null)
        {
            live.SessionId ??= sessionId;
        }

        return live;
    }

    /// <summary>
    /// <see cref="LiveOf"/>'s choice among a title's playbacks (their live
    /// stream, session and opening): the index of the one a session's
    /// report is about, -1 for none.
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

    // Every minute. A playback a Jellyfin session is still on is kept and
    // reported to the paired server, paused or not: some clients report
    // seldom while paused (Kodi's add-on: every 250 seconds), past the
    // paired server's 2 minutes without news (MSFP playback 6.3) and this
    // server's Idle. Jellyfin ends a session's playback itself when the
    // client is gone. Others, neither reported on nor read for Idle: stopped.
    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        var kept = new HashSet<Live>();
        foreach (var session in _sessions.Sessions)
        {
            if (session.NowPlayingItem is not { } item || LiveOf(item.Id, session.PlayState?.LiveStreamId, session.Id) is not { } live
                || _host.Engine is not { } engine || !kept.Add(live))
            {
                continue;
            }

            live.Touched = now;
            if (session.PlayState?.PositionTicks is { } ticks)
            {
                live.PositionMs = ticks / TimeSpan.TicksPerMillisecond;
            }

            engine.Progress(live.Playback.Id, live.PositionMs, session.PlayState?.IsPaused ?? false);
        }

        foreach (var live in _byToken.Values.Where(l => !kept.Contains(l)).ToList())
        {
            if (now - live.Touched > Idle)
            {
                _log.LogInformation("MSFP: a playback of {Key} neither reported on nor read for {Idle}: stopped", live.Playback.Title, Idle);
                _ = EndAsync(live, live.PositionMs);
            }
        }
    }

    /// <summary>Stops a playback on its paired server, once.</summary>
    internal async Task EndAsync(Live live, long positionMs)
    {
        if (!_byToken.TryRemove(live.Token, out _))
        {
            return;
        }

        Subtitles.ForgetPictures(live.Token);
        if (StandIns.Playing.AddOrUpdate(live.Item.Id, 0, (_, n) => n - 1) <= 0)
        {
            StandIns.Playing.TryRemove(live.Item.Id, out _);

            // Its saved source back to what the catalogs say, at the next pass.
            _standIns.Soon();
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

        /// <summary>Whether the video's bitrate was filled in, the file stating none (<see cref="VideoBitrate"/>).</summary>
        public bool VideoBitrateFilled { get; set; }

        /// <summary>The Jellyfin session playing it, once known (<see cref="LiveOf"/>).</summary>
        public string? SessionId { get; set; }

        /// <summary>When it was opened: the newest goes to a session starting without naming its live stream.</summary>
        public DateTimeOffset Opened { get; } = DateTimeOffset.UtcNow;

        public long PositionMs { get; set; }

        /// <summary>The copy's length, for its subtitle parts.</summary>
        public long DurationMs { get; set; }

        public DateTimeOffset Touched { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>The subtitle tracks read whole, by track.</summary>
        public ConcurrentDictionary<string, Lazy<Task<byte[]?>>> Subtitles { get; } = new(StringComparer.Ordinal);

        /// <summary>A converted stream's audio tracks by the Index Jellyfin shows; empty otherwise.</summary>
        public Dictionary<int, global::Msfp.Protocol.CopyAudio> AudioByIndex { get; } = new();
    }

    /// <summary>
    /// An audio track picked: during a playback, on that server its ID,
    /// elsewhere its language; before Play, its language only (no peer).
    /// </summary>
    internal sealed record AudioSwitch(string Peer, string Track, string Language);
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
