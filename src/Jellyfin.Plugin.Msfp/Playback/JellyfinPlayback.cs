using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Msfp;
using Msfp.Protocol;
using Video = MediaBrowser.Controller.Entities.Video;

using ByteRange = Msfp.ByteRange;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// The owning side of a peer's playback here (the engine's IPlayback and
/// IMedia): a peer's viewer plays one of this server's films or episodes.
/// The engine has checked the call, the pairing, its limits and the copy;
/// this server describes the copy from Jellyfin's streams and serves its
/// file as it is (direct play, with byte ranges) when the viewer's device
/// plays it, and its text subtitles as WebVTT, by Jellyfin's subtitle
/// encoder. What the device can't play as it is plays as HLS, Jellyfin
/// remuxing or transcoding it (<see cref="Hls"/>).
/// </summary>
public sealed partial class JellyfinPlayback : IPlayback, IMedia
{
    private readonly ILibraryManager _library;
    private readonly ISubtitleEncoder _subtitles;
    private readonly ITranscodeManager _transcodes;
    private readonly Hls _hls;
    private readonly TranscodeLimit _limit;
    private readonly Previews _previews;
    private readonly ILogger<JellyfinPlayback> _log;
    private readonly ConcurrentDictionary<string, Live> _live = new(StringComparer.Ordinal);

    public JellyfinPlayback(ILibraryManager library, ISubtitleEncoder subtitles, ITranscodeManager transcodes,
        IServerConfigurationManager server, IAuthenticationManager auth, ISessionManager sessions, ITrickplayManager trickplay,
        MediaBrowser.Controller.MediaEncoding.IMediaEncoder encoder, MediaBrowser.Controller.MediaSegments.IMediaSegmentManager segments, ILogger<JellyfinPlayback> log)
    {
        _segments = segments;
        _encoder = encoder;
        _peerSessions = new PeerSessions(sessions, log);

        // Joins left by a stop of Jellyfin mid-playback.
        try
        {
            var joined = Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "joined");
            if (Directory.Exists(joined))
            {
                Directory.Delete(joined, true);
            }
        }
        catch (IOException)
        {
        }
        _library = library;
        _subtitles = subtitles;
        _transcodes = transcodes;
        _hls = new Hls(server, auth);
        _previews = new Previews(trickplay, library);
        _limit = new TranscodeLimit(() => Plugin.Instance?.Configuration.TranscodesAtOnce ?? 2, () => OwnTranscodes(sessions.Sessions));
        _log = log;
    }

    private readonly MediaBrowser.Controller.MediaEncoding.IMediaEncoder _encoder;
    private readonly PeerSessions _peerSessions;
    private readonly MediaBrowser.Controller.MediaSegments.IMediaSegmentManager _segments;

    /// <summary>The peers' playbacks here now, for the operator's page.</summary>
    public int Playing => _live.Count;

    /// <summary>Transcodes now, the server's own users' and peers', and the limit.</summary>
    public (int Own, int Peers, int Most) Transcodes => _limit.Now();

    // The server's own users' transcodes: their sessions playing with the
    // video re-encoded (peers' HLS, asked as devices msfp-<handle>, are
    // counted by the limit itself).
    internal static int OwnTranscodes(IEnumerable<SessionInfo> sessions) =>
        sessions.Count(s => s.NowPlayingItem is not null && s.TranscodingInfo is { IsVideoDirect: false }
            && s.DeviceId?.StartsWith("msfp-", StringComparison.Ordinal) != true);

    /// <inheritdoc />
    // Since MSFP's revision the tracks and previews are the copy's facts in
    // the catalog (phase 2 of the migration); what stays here is what can't
    // be served apart and the ASS fonts (phase 3).
    public ValueTask<CopyInfo> DescribeAsync(string copy, CancellationToken ct)
    {
        _ = Item(copy);
        return ValueTask.FromResult(new CopyInfo());
    }

    /// <inheritdoc />
    public async ValueTask<Started> StartAsync(StartRequest request, CancellationToken ct)
    {
        var item = Item(request.Copy);
        if (request.AudioOnly || item is not Video video || string.IsNullOrEmpty(video.Path) || !File.Exists(video.Path))
        {
            throw new MsfpException(MsfpError.NotPlayable);
        }

        var streams = item.GetMediaStreams();
        var file = FileInfoOf(video, streams);
        var audio = file.Audio;
        if (request.AudioTrack.StartsWith('a') && int.TryParse(request.AudioTrack.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            audio = file.AudioTracks.FirstOrDefault(a => a.Index == index) ?? audio;
        }

        // Since MSFP's revision the engine decides the delivery (Served)
        // and the host makes exactly that or refuses it. Until phase 3 of
        // the migration ports the companion plugin's checked deliveries,
        // only the plain file is made; remux, transcode, the baseline and
        // copies of several files (Joined) are refused.
        if (request.Delivery != global::Msfp.Protocol.Playback.DeliveryFile || Joined.Parts(video).Count > 1)
        {
            _log.LogInformation("MSFP: {Peer} asked {Title} as {Delivery}: not made until the deliveries are back (migration phase 3)", request.Peer.Name, request.Title, request.Delivery);
            throw new MsfpException(MsfpError.DeliveryRefused);
        }

        var handle = Base64UrlText.Encode(RandomNumberGenerator.GetBytes(16));
        request.Admit?.Invoke("direct");
        var live = new Live(item.Id, video.Path, request.Peer.Name, null, false);
        var started = new Started { Handle = handle, Method = "direct", Protocol = "file" };
        _live[handle] = live;
        live.Method = started.Method;
        live.Session = await _peerSessions.StartAsync(handle, request.Peer.Name, item.Id, started.Method, request.StartMs).ConfigureAwait(false);
        _log.LogInformation("MSFP: {Peer} plays {Title} here, {Method}", request.Peer.Name, request.Title, started.Method);
        // Segments and the stream facts are the catalog's copy since MSFP's
        // revision (phase 2).
        return started with { PositionMs = request.StartMs };
    }

    // The copy's skip markers, as Jellyfin keeps them (its own providers', or
    // a plugin's), in MSFP's kinds (video section 5.3); none when it has none.
    internal async Task<IReadOnlyList<CopySegment>?> SegmentsOfAsync(BaseItem item, CancellationToken ct)
    {
        try
        {
            var found = await _segments.GetSegmentsAsync(item, null, _library.GetLibraryOptions(item)).ConfigureAwait(false);
            var markers = found
                .Select(s => (Kind: Viewing.PairedSegments.KindOf(s.Type), s.StartTicks, s.EndTicks))
                .Where(s => s.Kind is not null && s.StartTicks >= 0)
                .Select(s => new CopySegment(s.Kind!, s.StartTicks / TimeSpan.TicksPerMillisecond, Math.Max(s.EndTicks, 0) / TimeSpan.TicksPerMillisecond))
                .ToList();
            return markers.Count > 0 ? markers : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Markers are optional (MSFP video section 5.3): played without.
            _log.LogWarning(e, "MSFP: the skip markers of {Title} couldn't be read", item.Name);
            return null;
        }
    }

    /// <inheritdoc />
    // Since MSFP's revision a report carries no pause: Jellyfin's dashboard
    // shows the playback as playing.
    public ValueTask ProgressAsync(Report report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        var handle = report.Handle;
        if (!_live.TryGetValue(handle, out var live))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var positionMs = report.HasPosition ? report.PositionMs : live.PositionMs;
        const bool paused = false;

        live.PositionMs = positionMs;
        live.Paused = paused;
        _ = _peerSessions.ProgressAsync(live.Session, handle, live.Item, live.Method, positionMs, paused);
        if (live.Master is not null)
        {
            // Keeps Jellyfin's transcode alive while the viewer is paused.
            try
            {
                _transcodes.PingTranscodingJob(handle, paused);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(string handle, EndReason reason, CancellationToken ct)
    {
        if (!_live.TryRemove(handle, out var live))
        {
            return;
        }

        _log.LogInformation("MSFP: {Peer}'s playback here ended ({Reason})", live.Peer, reason);
        await _peerSessions.StopAsync(live.Session, handle, live.Item, live.PositionMs).ConfigureAwait(false);
        if (live.Transcode)
        {
            _limit.Give();
        }

        live.Joined?.Dispose();
        if (live.Master is not null)
        {
            try
            {
                await _transcodes.KillTranscodingJobs("msfp-" + handle, handle, _ => true).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "MSFP: a peer's transcode couldn't be stopped");
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask ServeAsync(string handle, MediaPath path, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        if (!_live.TryGetValue(handle, out var live))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        if (live.Joined is { } joined)
        {
            await ServeJoinedAsync(joined, path, range, output, ct).ConfigureAwait(false);
            return;
        }

        switch (path.Kind)
        {
            case MediaKind.Master when live.Master is not null:
                await PlaylistAsync(live, live.Master, true, output, ct).ConfigureAwait(false);
                return;
            case MediaKind.Playlist when live.Master is not null && live.Names.TryGetValue(path.Name, out var playlist):
                await PlaylistAsync(live, playlist, false, output, ct).ConfigureAwait(false);
                return;
            case MediaKind.Segment when live.Master is not null && live.Names.TryGetValue(path.Name, out var segment):
                await _hls.SegmentAsync(live.Item, segment, range, output, ct).ConfigureAwait(false);
                return;
            case MediaKind.File when live.Master is null:
                await FileServing.ServeAsync(live.Path, range, MimeOf(live.Path), output, ct).ConfigureAwait(false);
                return;
            case MediaKind.Sheet:
                var sheet = await _previews.SheetAsync(_library.GetItemById(live.Item) ?? throw new MsfpException(MsfpError.NotFound), path.Sheet, ct).ConfigureAwait(false);
                output.Start(200, "image/jpeg", sheet.Length, string.Empty);
                await output.WriteAsync(sheet, ct).ConfigureAwait(false);
                return;
            case MediaKind.Subtitles:
                var track = await WebVttAsync(live, path.Track, ct).ConfigureAwait(false);
                if (!WebVtt.TryPart(track, path.Part, out var part))
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                output.Start(200, "text/vtt; charset=utf-8", part.Length, string.Empty);
                await output.WriteAsync(part, ct).ConfigureAwait(false);
                return;
            case MediaKind.Pictures:
                var sets = await PgsAsync(live, path.Track, ct).ConfigureAwait(false);
                var bytes = Pgs.Part(sets, path.Part);
                output.Start(200, "application/octet-stream", bytes.Length, string.Empty);
                if (bytes.Length > 0)
                {
                    await output.WriteAsync(bytes, ct).ConfigureAwait(false);
                }

                return;
            default:
                throw new MsfpException(MsfpError.NotFound);
        }
    }

    // A PGS track of the copy, its display sets read once per playback:
    // an external .sup as it is, an embedded one copied out by Jellyfin's
    // ffmpeg.
    private async Task<List<Pgs.DisplaySet>> PgsAsync(Live live, string track, CancellationToken ct)
    {
        if (live.Pictures.TryGetValue(track, out var done))
        {
            return done;
        }

        if (!track.StartsWith('s') || !int.TryParse(track.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || _library.GetItemById(live.Item) is not { } item
            || PgsSubtitles(item.GetMediaStreams()).FirstOrDefault(s => s.Index == index) is not { } stream)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        byte[] data;
        if (stream.IsExternal && stream.Path is { Length: > 0 } sup)
        {
            data = await File.ReadAllBytesAsync(sup, ct).ConfigureAwait(false);
        }
        else
        {
            var psi = new System.Diagnostics.ProcessStartInfo(_encoder.EncoderPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
            };
            // Jellyfin numbers external subtitle files first: the file's own
            // stream number counts the streams inside it only.
            var inFile = item.GetMediaStreams().Count(s => !s.IsExternal && s.Index < index);
            foreach (var a in new[] { "-v", "error", "-copyts", "-i", live.Path, "-map", "0:" + inFile.ToString(CultureInfo.InvariantCulture), "-c:s", "copy", "-f", "sup", "pipe:1" })
            {
                psi.ArgumentList.Add(a);
            }

            using var process = System.Diagnostics.Process.Start(psi) ?? throw new MsfpException(MsfpError.NotFound);
            using var ms = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            data = ms.ToArray();
        }

        var sets = Pgs.Sets(data) ?? throw new MsfpException(MsfpError.NotFound);
        live.Pictures[track] = sets;
        return sets;
    }

    private static IEnumerable<MediaStream> PgsSubtitles(IEnumerable<MediaStream> streams) =>
        streams.Where(s => s.Type == MediaStreamType.Subtitle && s.IsPgsSubtitleStream && s.Index is >= 0 and <= 999);

    [System.Text.RegularExpressions.GeneratedRegex(@"-codec:v:0 (\S+)")]
    private static partial System.Text.RegularExpressions.Regex VideoCodecArgument();

    /// <summary>The video encoder in Jellyfin's ffmpeg command ("copy" when copied); null when it names none.</summary>
    internal static string? VideoCodecOf(string arguments) =>
        VideoCodecArgument().Match(arguments) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The engine, to stop a playback from here; set by EngineHost.</summary>
    internal Func<MsfpEngine?>? Engine { get; set; }

    // A remux asked of Jellyfin copies the video, but Jellyfin may re-encode
    // it anyway (its own rules: a stream whose bitrate the file doesn't
    // say, among others). Watched in Jellyfin's report on the playback's
    // device while it starts: found re-encoding, it counts as a transcode
    // from then on (MSFP playback section 6.2), for the pairing and the
    // server; with none left, it's stopped.
    private void WatchRemux(string handle, string title) => _ = Task.Run(async () =>
    {
        for (var i = 0; i < 60 && _live.TryGetValue(handle, out var live); i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            // Jellyfin's ffmpeg for this playback (its play session is the
            // handle): the video copied, or encoded by another codec.
            string arguments;
            try
            {
                arguments = _transcodes.GetTranscodingJob(handle)?.Process?.StartInfo.Arguments ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var video = VideoCodecOf(arguments);
            if (video is null)
            {
                continue;
            }

            if (video == "copy")
            {
                return;
            }

            var counted = live.Slot?.Transcoding() != false && _limit.TryTake();
            if (counted)
            {
                live.Transcode = true;
                _log.LogInformation("MSFP: {Peer}'s remux of {Title} re-encodes the video: counted as a transcode", live.Peer, title);
                return;
            }

            _log.LogInformation("MSFP: {Peer}'s remux of {Title} re-encodes the video, and no transcode is left: stopped", live.Peer, title);
            if (Engine?.Invoke() is { } engine && engine.Playbacks().FirstOrDefault(p => p.Handle == handle) is { } playback)
            {
                engine.StopPlayback(playback.Id, EndReason.Operator);
            }

            return;
        }
    });

    // A copy of several files, joined here (Joined): its own playlists and segments.
    private static async Task ServeJoinedAsync(Joined joined, MediaPath path, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        switch (path.Kind)
        {
            case MediaKind.Master:
                var master = joined.Master();
                output.Start(200, "application/vnd.apple.mpegurl", master.Length, string.Empty);
                await output.WriteAsync(master, ct).ConfigureAwait(false);
                return;
            case MediaKind.Playlist when Joined.IsPlaylist(path.Name):
                var playlist = await joined.PlaylistAsync(ct).ConfigureAwait(false);
                output.Start(200, "application/vnd.apple.mpegurl", playlist.Length, string.Empty);
                await output.WriteAsync(playlist, ct).ConfigureAwait(false);
                return;
            case MediaKind.Segment when joined.SegmentPath(path.Name) is { } segment && File.Exists(segment):
                var type = segment.EndsWith(".ts", StringComparison.Ordinal) ? "video/mp2t" : "video/mp4";
                await FileServing.ServeAsync(segment, range, type, output, ct).ConfigureAwait(false);
                return;
            default:
                throw new MsfpException(MsfpError.NotFound);
        }
    }

    private async Task PlaylistAsync(Live live, string url, bool master, IResponseSink output, CancellationToken ct)
    {
        var body = await _hls.PlaylistAsync(live.Item, url, master, live.Names, ct).ConfigureAwait(false);
        output.Start(200, "application/vnd.apple.mpegurl", body.Length, string.Empty);
        await output.WriteAsync(body, ct).ConfigureAwait(false);
    }

    // A text subtitle track of the copy as WebVTT, made once per playback.
    private async Task<byte[]> WebVttAsync(Live live, string track, CancellationToken ct)
    {
        if (live.Subtitles.TryGetValue(track, out var done))
        {
            return done;
        }

        if (!track.StartsWith('s') || !int.TryParse(track.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || _library.GetItemById(live.Item) is not { } item
            || !TextSubtitles(item.GetMediaStreams()).Any(s => s.Index == index))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var stream = await _subtitles.GetSubtitles(item, item.Id.ToString("N"), index, "vtt", 0, 0, false, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            var bytes = ms.ToArray();
            live.Subtitles[track] = bytes;
            return bytes;
        }
    }

    private BaseItem Item(string copy)
    {
        if (!Guid.TryParseExact(copy, "N", out var id) || _library.GetItemById(id) is not { } item)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        return item;
    }

    private static IEnumerable<MediaStream> TextSubtitles(IEnumerable<MediaStream> streams) =>
        streams.Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream && s.Index is >= 0 and <= 999);

    // What the file is, for choosing direct play.
    internal static FileFacts FileInfoOf(Video video, IReadOnlyList<MediaStream> streams)
    {
        var container = ContainerOf(video.Path);
        var v = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        long kbps = 0;
        try
        {
            if (video.RunTimeTicks is > 0 && new FileInfo(video.Path).Length is var size and > 0)
            {
                kbps = size * 8 * TimeSpan.TicksPerSecond / video.RunTimeTicks.Value / 1000;
            }
        }
        catch (IOException)
        {
        }

        return new FileFacts
        {
            Container = container,
            MimeType = MimeOf(video.Path),
            Video = v,
            Picture = JellyfinCatalogSource.PictureOf(v),
            AudioTracks = audio,
            Audio = audio.FirstOrDefault(a => a.IsDefault) ?? audio.FirstOrDefault(),
            BitrateKbps = (int)Math.Min(kbps, int.MaxValue),
        };
    }

    /// <summary>A file's container, by its extension, as MSFP playback section 2.1 names them; "" for another.</summary>
    internal static string ContainerOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => "mp4",
        ".m4v" => "m4v",
        ".mov" => "mov",
        ".webm" => "webm",
        ".mkv" => "mkv",
        ".ts" or ".m2ts" => "ts",
        _ => string.Empty,
    };

    internal static string MimeOf(string path) => ContainerOf(path) switch
    {
        "mp4" or "m4v" => "video/mp4",
        "mov" => "video/quicktime",
        "webm" => "video/webm",
        "mkv" => "video/x-matroska",
        "ts" => "video/mp2t",
        _ => "application/octet-stream",
    };

    private static string? Lang(string? code) => string.IsNullOrWhiteSpace(code) ? null : code.Trim();

    // A track's label: its language's name in itself when known, else
    // Jellyfin's title of it.
    private static string Label(MediaStream s)
    {
        if (Lang(s.Language) is { } code && Languages.TrySentTag(code, out var tag))
        {
            try
            {
                var name = CultureInfo.GetCultureInfo(tag).NativeName;
                if (name.Length > 0 && !name.StartsWith("Unknown", StringComparison.Ordinal) && name != tag)
                {
                    return char.ToUpper(name[0], CultureInfo.InvariantCulture) + name[1..];
                }
            }
            catch (CultureNotFoundException)
            {
            }
        }

        return s.Title is { Length: > 0 } t ? t : s.DisplayTitle ?? "Track " + s.Index.ToString(CultureInfo.InvariantCulture);
    }

    private sealed class Live(Guid item, string path, string peer, string? master, bool transcode)
    {
        /// <summary>A transcode, counted by the server-wide limit (a remux found to re-encode becomes one).</summary>
        public bool Transcode { get; set; } = transcode;

        /// <summary>The engine's count of this playback, for a remux found to re-encode; null outside the engine.</summary>
        public ISlot? Slot { get; init; }

        /// <summary>Jellyfin's master playlist URL, for HLS; null for the file.</summary>
        public string? Master { get; } = master;

        /// <summary>The HLS names given in playlists, with Jellyfin's URL of each.</summary>
        public ConcurrentDictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

        public Guid Item { get; } = item;

        public string Path { get; } = path;

        public string Peer { get; } = peer;

        public long PositionMs { get; set; }

        public bool Paused { get; set; }

        /// <summary>direct, remux or transcode.</summary>
        public string Method { get; set; } = string.Empty;

        /// <summary>Its session in Jellyfin's dashboard (PeerSessions); null when none.</summary>
        public string? Session { get; set; }

        /// <summary>A copy of several files, joined here; null otherwise.</summary>
        public Joined? Joined { get; init; }

        /// <summary>The PGS tracks read, by track.</summary>
        public ConcurrentDictionary<string, List<Pgs.DisplaySet>> Pictures { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, byte[]> Subtitles { get; } = new(StringComparer.Ordinal);
    }
}
