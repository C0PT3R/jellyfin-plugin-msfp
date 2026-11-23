using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Msfp;
using Msfp.Describe;
using Msfp.Protocol;
using Video = MediaBrowser.Controller.Entities.Video;

using ByteRange = Msfp.ByteRange;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// The owning side of a peer's playback here (the engine's IPlayback and
/// IMedia): a peer's viewer plays one of this server's films, episodes or
/// extras. The engine has checked the call, the pairing, its limits and the
/// copy, and decided what is served (<see cref="StartRequest.Served"/>);
/// this server makes exactly that or refuses it (MSFP playback section 2.7):
/// the file itself from disk, or a remux, a transcode or the baseline made
/// by Jellyfin's own transcoder (<see cref="Deliveries"/>), checked against
/// the order, with a master playlist of its own. A copy's text subtitles are
/// served apart as WebVTT (Jellyfin's subtitle encoder), its PGS as
/// pictures; ASS, fonts, DVD and DVB subtitles aren't served apart this
/// release.
/// </summary>
public sealed partial class JellyfinPlayback : IPlayback, IMedia, IDisposable
{
    private readonly ILibraryManager _library;
    private readonly ISubtitleEncoder _subtitles;
    private readonly CopyKeeper _copies;
    private readonly Deliveries _deliveries;
    private readonly TranscodeLimit _limit;
    private readonly Previews _previews;
    private readonly IMediaEncoder _encoder;
    private readonly PeerSessions _peerSessions;
    private readonly ILogger<JellyfinPlayback> _log;
    private readonly ConcurrentDictionary<string, Live> _live = new(StringComparer.Ordinal);

    internal JellyfinPlayback(ILibraryManager library, ISubtitleEncoder subtitles, CopyKeeper copies, Deliveries deliveries,
        ISessionManager sessions, ITrickplayManager trickplay, IMediaEncoder encoder, ILogger<JellyfinPlayback> log)
    {
        _library = library;
        _subtitles = subtitles;
        _copies = copies;
        _deliveries = deliveries;
        _encoder = encoder;
        _peerSessions = new PeerSessions(sessions, log);
        _previews = new Previews(trickplay, library);
        _limit = new TranscodeLimit(() => Plugin.Instance?.Configuration.TranscodesAtOnce ?? 2, () => OwnTranscodes(sessions.Sessions));
        _log = log;
        _deliveries.Ended += OnDeliveryEnded;
    }

    /// <summary>The peers' playbacks here now, for the operator's page.</summary>
    public int Playing => _live.Count;

    /// <summary>Transcodes now, the server's own users' and peers', and the limit.</summary>
    public (int Own, int Peers, int Most) Transcodes => _limit.Now();

    /// <summary>The engine, to stop a playback from here; set by EngineHost.</summary>
    internal Func<MsfpEngine?>? Engine { get; set; }

    /// <summary>
    /// What this server makes, for the engine (MsfpConfig.Encoders): h264
    /// always, hevc and av1 when Jellyfin's settings allow encoding them,
    /// the audio codecs Jellyfin's ffmpeg encodes; the range sdr only
    /// (keep refused everywhere this release).
    /// </summary>
    public global::Msfp.Encoders Encoders()
    {
        var now = _deliveries.Encoders();
        return new global::Msfp.Encoders
        {
            Video = now.Video.Where(v => v != Names.BaselineVideoCodec).ToList(),
            Audio = now.Audio.Where(a => a != Names.BaselineAudioCodec).ToList(),
        };
    }

    // The server's own users' transcodes: their sessions playing with the
    // video re-encoded (peers' deliveries, devices msfp-…, are counted by
    // the limit itself).
    internal static int OwnTranscodes(IEnumerable<SessionInfo> sessions) =>
        sessions.Count(s => s.NowPlayingItem is not null && s.TranscodingInfo is { IsVideoDirect: false }
            && s.DeviceId?.StartsWith("msfp-", StringComparison.Ordinal) != true);

    /// <inheritdoc />
    public void Dispose() => _deliveries.Ended -= OnDeliveryEnded;

    /// <inheritdoc />
    // The tracks, previews and the rest are the copy's facts in the
    // catalog; here what isn't served apart: ASS, DVD and DVB subtitles
    // (and so no fonts) this release (decision 6).
    public ValueTask<CopyInfo> DescribeAsync(string copy, CancellationToken ct)
    {
        var item = Item(copy);
        var info = new CopyInfo();
        if (_copies.VersionOf(item.Id) is { } v)
        {
            var not = v.Copy.SubtitleTracks.Where(s => !ServedApart(s.Format)).Select(s => s.Id).ToList();
            if (not.Count > 0)
            {
                info.NotApart = not;
            }
        }

        return ValueTask.FromResult(info);
    }

    /// <summary>Whether a subtitle format is served apart this release: text formats as WebVTT, PGS as pictures.</summary>
    internal static bool ServedApart(string format) => format is "subrip" or "webvtt" or "mov_text" or "ttml" or "hdmv_pgs_subtitle";

    /// <inheritdoc />
    public async ValueTask<Started> StartAsync(StartRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = Item(request.Copy);
        if (request.AudioOnly || item is not Video video || string.IsNullOrEmpty(video.Path))
        {
            throw new MsfpException(MsfpError.NotPlayable);
        }

        if (_copies.VersionOf(item.Id) is not { } described || described.Copy.Several)
        {
            // Copies of several files aren't listed this release (decision 4).
            throw new MsfpException(MsfpError.NotPlayable);
        }

        var copy = described.Copy;
        var served = request.Served;
        var handle = Base64UrlText.Encode(RandomNumberGenerator.GetBytes(16));
        Live live;
        if (request.Delivery == global::Msfp.Protocol.Playback.DeliveryFile)
        {
            // The file itself, from disk: through the path checks, and still
            // the file the copy describes (its size and time as described:
            // its etag's), else refused and described again.
            if (_copies.OpenFile(video.Path) is not { } opened)
            {
                throw new MsfpException(MsfpError.NotPlayable);
            }

            using (opened.File)
            {
                if (opened.File.Size != described.Size || opened.File.ModifiedUnix != described.Modified || opened.File.Size != copy.Size)
                {
                    _copies.Wake();
                    _log.LogInformation("MSFP: {Peer} asked {Title}, whose file changed since it was described: refused until described again", request.Peer.Name, request.Title);
                    throw new MsfpException(MsfpError.DeliveryRefused);
                }

                var slot = request.Admit?.Invoke(served.Method);
                live = new Live(item.Id, request.Peer.Name, copy, described.Tracks)
                {
                    Files = opened.Files,
                    Real = opened.File.Real,
                    Size = opened.File.Size,
                    Modified = opened.File.Modified,
                    MimeType = copy.MediaType,
                    Slot = slot,
                    Method = served.Method,
                };
            }
        }
        else
        {
            live = await StartHlsAsync(request, video, copy, described.Tracks, ct).ConfigureAwait(false);
        }

        _live[handle] = live;
        live.PositionMs = request.StartMs;
        live.Session = await _peerSessions.StartAsync(handle, request.Peer.Name, item.Id, live.Method, request.StartMs).ConfigureAwait(false);
        _log.LogInformation("MSFP: {Peer} plays {Title} here, {Method} ({Purpose})", request.Peer.Name, request.Title, live.Method, request.Purpose);
        return new Started
        {
            Handle = handle,
            Method = live.Method,
            Protocol = live.Delivery is null ? global::Msfp.Protocol.Playback.ProtocolFile : global::Msfp.Protocol.Playback.ProtocolHls,
            PositionMs = request.StartMs,
            AudioRenditions = false,
        };
    }

    // A remux, a transcode or the baseline: planned (a refusal of what
    // isn't made here, before anything starts), counted (the server's
    // transcodes, then the pool's), made by Jellyfin and checked, its
    // first output read and checked, its master written.
    private async Task<Live> StartHlsAsync(StartRequest request, Video video, Copy copy, IReadOnlyDictionary<string, int> tracks, CancellationToken ct)
    {
        var served = request.Served;
        var streams = video.GetMediaStreams();
        DeliveryOrder order;
        Source source;
        try
        {
            order = Orders.Plan(request, copy, tracks, video.Id, streams, _deliveries.Encoders());
            source = Orders.SourceOf(streams, order.Audio?.Index ?? -1);
        }
        catch (DeliveryRefusedException e)
        {
            _log.LogInformation("MSFP: {Peer} asked {Title} as {Delivery}: refused ({Why})", request.Peer.Name, request.Title, DeliveryName(request), e.Code);
            throw e.ToMsfp();
        }

        var transcode = served.Transcodes;
        if (transcode && !_limit.TryTake())
        {
            _log.LogInformation("MSFP: {Peer} asked {Title} as {Delivery}: no transcode left on this server", request.Peer.Name, request.Title, DeliveryName(request));
            throw new MsfpException(MsfpError.ServerBusy);
        }

        ISlot? slot = null;
        try
        {
            slot = request.Admit?.Invoke(served.Method);
            var made = await _deliveries.StartAsync(order, source, ct).ConfigureAwait(false);
            try
            {
                var output = await ReadOutputAsync(made, order, copy, request.AudioTrack, ct).ConfigureAwait(false);
                var names = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
                var media = System.Text.Encoding.UTF8.GetBytes(Hls.Media(made.Playlist, names));
                return new Live(video.Id, request.Peer.Name, copy, tracks)
                {
                    Delivery = made.Id,
                    Master = HlsMaster.Of(copy, request.AudioTrack, order, made, output),
                    MediaPlaylist = media,
                    Names = names,
                    Transcode = transcode,
                    Slot = slot,
                    Method = served.Method,
                };
            }
            catch
            {
                await _deliveries.StopAsync(made.Id).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception e)
        {
            slot?.Release();
            if (transcode)
            {
                _limit.Give();
            }

            switch (e)
            {
                case DeliveryRefusedException r:
                    _log.LogInformation("MSFP: {Peer} asked {Title} as {Delivery}: refused ({Why})", request.Peer.Name, request.Title, DeliveryName(request), r.Code);
                    throw r.ToMsfp();
                case MsfpException { Code: MsfpError.NotFound }:
                    throw new MsfpException(MsfpError.DeliveryRefused);
            }

            throw;
        }
    }

    private static string DeliveryName(StartRequest r) => r.Delivery.Length > 0 ? r.Delivery : "the baseline";

    // The first output file of a delivery, read by ffprobe (bounded, its
    // path never leaving): checked against the order, its codecs strings
    // for the master. When it can't be read, what the order foresees is
    // used.
    private async Task<(string? Video, string? Audio)> ReadOutputAsync(DeliveryMade made, DeliveryOrder order, Copy copy, string audio, CancellationToken ct)
    {
        if (_deliveries.FirstOutput(made.Id) is not { } first)
        {
            _log.LogDebug("MSFP delivery {Id}: no output file to read", made.Id);
            return default;
        }

        Copy outCopy;
        try
        {
            var prober = new Prober
            {
                FFprobe = _encoder.ProbePath ?? string.Empty,
                Timeout = TimeSpan.FromSeconds(15),
                MaxOutput = 1 << 20,
                ProbeSize = 8 << 20,
                AnalyzeDuration = TimeSpan.FromSeconds(5),
                Nice = 10,
            };
            (outCopy, _) = await prober.DescribeAsync(first, null, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is DescribeException or IOException or InvalidOperationException or ProtocolException)
        {
            _log.LogDebug("MSFP delivery {Id}: its output couldn't be read ({Kind})", made.Id, e.GetType().Name);
            return default;
        }

        var diff = OutputCheck.Differences(order, outCopy, copy, audio);
        if (diff.Count > 0)
        {
            throw new DeliveryRefusedException("output-differs:" + string.Join(',', diff));
        }

        return OutputCheck.Codecs(order, outCopy);
    }

    /// <inheritdoc />
    // Since MSFP's revision a report carries no pause: Jellyfin's dashboard
    // shows the playback as playing. A download's late report (watched
    // offline) has no playback left here.
    public ValueTask ProgressAsync(Report report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Late)
        {
            return ValueTask.CompletedTask;
        }

        if (!_live.TryGetValue(report.Handle, out var live))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var positionMs = report.HasPosition ? report.PositionMs : live.PositionMs;
        live.PositionMs = positionMs;
        _ = _peerSessions.ProgressAsync(live.Session, report.Handle, live.Item, live.Method, positionMs, false);
        if (live.Delivery is { } id)
        {
            // Keeps Jellyfin's transcode alive while the viewer is paused.
            _deliveries.Ping(id);
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

        if (live.Delivery is { } id)
        {
            await _deliveries.StopAsync(id).ConfigureAwait(false);
        }
    }

    // A delivery its watchdog stopped: its MSFP playback ends too.
    private void OnDeliveryEnded(string id)
    {
        var found = _live.FirstOrDefault(p => p.Value.Delivery == id);
        if (found.Value is null)
        {
            return;
        }

        if (Engine?.Invoke() is { } engine && engine.Playbacks().FirstOrDefault(p => p.Handle == found.Key) is { } playback)
        {
            engine.StopPlayback(playback.Id, EndReason.Expired);
        }
        else
        {
            _ = StopAsync(found.Key, EndReason.Expired, CancellationToken.None).AsTask();
        }
    }

    /// <inheritdoc />
    public async ValueTask ServeAsync(string handle, MediaPath path, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(output);
        if (path.OfCopy)
        {
            // A sheet under a previews ticket: the handle is the copy's.
            if (path.Kind != MediaKind.Sheet)
            {
                throw new MsfpException(MsfpError.NotFound);
            }

            await SheetAsync(Item(handle), path.Sheet, output, ct).ConfigureAwait(false);
            return;
        }

        if (!_live.TryGetValue(handle, out var live) || path.Entry != 0)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        switch (path.Kind)
        {
            case MediaKind.File when live.Delivery is null:
                await ServeFileAsync(live, range, output, ct).ConfigureAwait(false);
                return;
            case MediaKind.Master when live.Master is { } master:
                output.Start(200, global::Msfp.Protocol.Playback.HlsType, master.Length, string.Empty);
                await output.WriteAsync(master, ct).ConfigureAwait(false);
                return;
            case MediaKind.Playlist when live.MediaPlaylist is { } media && path.Name == HlsMaster.MediaPlaylist:
                output.Start(200, global::Msfp.Protocol.Playback.HlsType, media.Length, string.Empty);
                await output.WriteAsync(media, ct).ConfigureAwait(false);
                return;
            case MediaKind.Segment when live.Delivery is { } id && live.Names.TryGetValue(path.Name, out var url):
                await SegmentAsync(id, url, range, output, ct).ConfigureAwait(false);
                return;
            case MediaKind.Sheet:
                await SheetAsync(_library.GetItemById(live.Item) ?? throw new MsfpException(MsfpError.NotFound), path.Sheet, output, ct).ConfigureAwait(false);
                return;
            case MediaKind.Subtitles:
                var track = await WebVttAsync(live, path.Track, ct).ConfigureAwait(false);
                byte[] part;
                if (path.Whole)
                {
                    part = track;
                }
                else if (!WebVtt.TryPart(track, path.Part, out part))
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

    // The file's bytes from the range the engine resolved (First to Last,
    // within the file; null for the whole): the file opened again through
    // the path checks, and only while it is the one the playback started
    // with (its real path, size and time). The engine writes the status,
    // the length, Content-Range and the ETag.
    private static async Task ServeFileAsync(Live live, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        using var f = live.Files?.Open(live.Real);
        if (f is null || f.Real != live.Real || f.Size != live.Size || f.Modified != live.Modified)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var first = range?.First ?? 0;
        var last = range is { Last: >= 0 } r ? r.Last : f.Size - 1;
        if (first < 0 || first > last || last >= f.Size)
        {
            throw new MsfpException(MsfpError.RangeNotSatisfiable);
        }

        output.Start(range is null ? 200 : 206, live.MimeType, last - first + 1, string.Empty);
        var buffer = new byte[256 * 1024];
        for (var at = first; at <= last;)
        {
            var read = await RandomAccess.ReadAsync(f.Handle, buffer.AsMemory(0, (int)Math.Min(buffer.Length, last - at + 1)), at, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            at += read;
        }
    }

    // A segment of a delivery, or the range of it asked, as Jellyfin answers it.
    private async Task SegmentAsync(string id, string url, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        using var res = await _deliveries.GetAsync(id, url, range, ct).ConfigureAwait(false);
        switch (res.StatusCode)
        {
            case HttpStatusCode.OK or HttpStatusCode.PartialContent:
                break;
            case HttpStatusCode.RequestedRangeNotSatisfiable:
                throw new MsfpException(MsfpError.RangeNotSatisfiable);
            case HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests:
                throw new MsfpException(MsfpError.ServerBusy);
            default:
                throw new MsfpException(MsfpError.NotFound);
        }

        var type = url.Contains(".mp4", StringComparison.Ordinal) ? "video/mp4" : "video/mp2t";
        output.Start((int)res.StatusCode, type, res.Content.Headers.ContentLength ?? -1, res.Content.Headers.ContentRange?.ToString() ?? string.Empty);
        var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            var buffer = new byte[128 * 1024];
            int n;
            while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task SheetAsync(BaseItem item, int sheet, IResponseSink output, CancellationToken ct)
    {
        var bytes = await _previews.SheetAsync(item, sheet, ct).ConfigureAwait(false);
        output.Start(200, "image/jpeg", bytes.Length, string.Empty);
        await output.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    // Jellyfin's index of one of the copy's subtitle tracks, of a format
    // served apart; NotFound otherwise.
    private static (int Index, CopySubtitle Track) SubtitleOf(Live live, string track)
    {
        if (live.Copy.SubtitleTracks.FirstOrDefault(s => s.Id == track) is not { } t || !ServedApart(t.Format) || !live.Tracks.TryGetValue(track, out var index))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        return (index, t);
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

        var (index, t) = SubtitleOf(live, track);
        if (t.Format != "hdmv_pgs_subtitle" || _library.GetItemById(live.Item) is not Video item
            || item.GetMediaStreams().FirstOrDefault(s => s.Index == index && s.Type == MediaStreamType.Subtitle && s.IsPgsSubtitleStream) is not { } stream)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        byte[] data;
        if (stream.IsExternal && stream.Path is { Length: > 0 } sup)
        {
            using var opened = _copies.OpenFile(sup)?.File ?? throw new MsfpException(MsfpError.NotFound);
            data = new byte[(int)Math.Min(opened.Size, 64 << 20)];
            var n = await RandomAccess.ReadAsync(opened.Handle, data, 0, ct).ConfigureAwait(false);
            data = data.AsSpan(0, n).ToArray();
        }
        else
        {
            using var opened = _copies.OpenFile(item.Path)?.File ?? throw new MsfpException(MsfpError.NotFound);
            var psi = new System.Diagnostics.ProcessStartInfo(_encoder.EncoderPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
            };
            // Jellyfin numbers external subtitle files first: the file's own
            // stream number counts the streams inside it only.
            var inFile = item.GetMediaStreams().Count(s => !s.IsExternal && s.Index < index);
            foreach (var a in new[] { "-v", "error", "-copyts", "-i", opened.Real, "-map", "0:" + inFile.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:s", "copy", "-f", "sup", "pipe:1" })
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

    // A text subtitle track of the copy as WebVTT, made once per playback
    // by Jellyfin's subtitle encoder.
    private async Task<byte[]> WebVttAsync(Live live, string track, CancellationToken ct)
    {
        if (live.Subtitles.TryGetValue(track, out var done))
        {
            return done;
        }

        var (index, t) = SubtitleOf(live, track);
        if (t.Format == "hdmv_pgs_subtitle" || _library.GetItemById(live.Item) is not { } item
            || !item.GetMediaStreams().Any(s => s.Index == index && s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream))
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

    private sealed class Live(Guid item, string peer, Copy copy, IReadOnlyDictionary<string, int> tracks)
    {
        public Guid Item { get; } = item;

        public string Peer { get; } = peer;

        /// <summary>The copy as the catalog sent it when the playback started.</summary>
        public Copy Copy { get; } = copy;

        /// <summary>Its track IDs mapped to Jellyfin's stream indexes.</summary>
        public IReadOnlyDictionary<string, int> Tracks { get; } = tracks;

        /// <summary>A transcode, counted by the server-wide limit.</summary>
        public bool Transcode { get; init; }

        /// <summary>The engine's count of this playback; null outside the engine.</summary>
        public ISlot? Slot { get; init; }

        /// <summary>For the file: the library's folders it was opened under, its real path, size and time (never logged nor sent).</summary>
        public LibraryFiles? Files { get; init; }

        public string Real { get; init; } = string.Empty;

        public long Size { get; init; }

        public DateTime Modified { get; init; }

        public string MimeType { get; init; } = "application/octet-stream";

        /// <summary>For HLS: the delivery's ID; null for the file.</summary>
        public string? Delivery { get; init; }

        public byte[]? Master { get; init; }

        public byte[]? MediaPlaylist { get; init; }

        /// <summary>The segment names the media playlist gives, with Jellyfin's URL of each.</summary>
        public ConcurrentDictionary<string, string> Names { get; init; } = new(StringComparer.Ordinal);

        public long PositionMs { get; set; }

        /// <summary>direct, remux or transcode.</summary>
        public string Method { get; init; } = string.Empty;

        /// <summary>Its session in Jellyfin's dashboard (PeerSessions); null when none.</summary>
        public string? Session { get; set; }

        /// <summary>The PGS tracks read, by track.</summary>
        public ConcurrentDictionary<string, List<Pgs.DisplaySet>> Pictures { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, byte[]> Subtitles { get; } = new(StringComparer.Ordinal);
    }
}
