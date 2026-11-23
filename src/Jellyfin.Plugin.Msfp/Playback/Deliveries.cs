using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Msfp;

using ByteRange = Msfp.ByteRange;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// A delivery as the plugin orders it of Jellyfin: Jellyfin's IDs and
/// closed values only. Video null: no picture; Audio null: no audio.
/// </summary>
internal sealed class DeliveryOrder
{
    public string ItemId { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    /// <summary>"ts" (MPEG-TS) or "mp4" (fragmented MP4).</summary>
    public string Segments { get; set; } = string.Empty;

    /// <summary>Where the player starts: the segment Jellyfin makes first.</summary>
    public long StartTicks { get; set; }

    public DeliveryVideo? Video { get; set; }

    public DeliveryAudio? Audio { get; set; }
}

/// <summary>The picture: copied, or converted to a codec, a height, a bitrate, tone mapped or not, and the baseline's encoder options.</summary>
internal sealed class DeliveryVideo
{
    public bool Copy { get; set; }

    public string? Codec { get; set; }

    public int MaxHeight { get; set; }

    public int MaxBitrate { get; set; }

    /// <summary>An HDR picture mapped to SDR; false keeps HDR (a GPU path only, refused this release).</summary>
    public bool ToneMap { get; set; }

    /// <summary>The encoder's profile asked (the baseline's "high"); null for Jellyfin's own.</summary>
    public string? Profile { get; set; }

    /// <summary>The encoder's highest level asked (the baseline's "41"); null for Jellyfin's own.</summary>
    public string? Level { get; set; }

    /// <summary>The pixel format the picture must end on (yuv420p: 8-bit 4:2:0); null when not checked.</summary>
    public string? PixelFormat { get; set; }

    /// <summary>Deinterlaced (the baseline, of an interlaced picture).</summary>
    public bool Deinterlace { get; set; }

    /// <summary>Jellyfin's index of a subtitle stream burned into the picture; -1 for none.</summary>
    public int BurnIndex { get; set; } = -1;

    /// <summary>The sample entry a copied picture must be written with in fragmented MP4 (hvc1, dvh1); null when not checked.</summary>
    public string? Tag { get; set; }
}

/// <summary>The audio stream, by Jellyfin's index: copied, or converted to a codec, channels and a highest sample rate.</summary>
internal sealed class DeliveryAudio
{
    public int Index { get; set; } = -1;

    public bool Copy { get; set; }

    public string? Codec { get; set; }

    public int Channels { get; set; }

    /// <summary>The highest sample rate (the baseline's 48 kHz); 0 for any.</summary>
    public int MaxSampleRate { get; set; }
}

/// <summary>What this Jellyfin encodes now: registry tokens, and whether on a GPU, whether it tone maps.</summary>
internal sealed record EncodersNow(List<string> Video, List<string> Audio, bool Hardware, bool ToneMapping);

/// <summary>A delivery refused: a closed code; Busy for a delivery Jellyfin can't take now (503 server-busy), else 409 delivery-refused.</summary>
internal sealed class DeliveryRefusedException(string code, bool busy = false) : Exception(code)
{
    public string Code { get; } = code;

    public bool Busy { get; } = busy;

    /// <summary>The MSFP error a refusal answers.</summary>
    public MsfpException ToMsfp() => new(Busy ? MsfpError.ServerBusy : MsfpError.DeliveryRefused);
}

/// <summary>A delivery as the plugin keeps it while it runs, in memory only.</summary>
internal sealed class Delivery
{
    public required string Id { get; init; }

    public required string DeviceId { get; init; }

    public required DeliveryOrder Order { get; init; }

    public required Source Source { get; init; }

    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public long LastDownloadTicks { get; set; } = -1;

    /// <summary>The ffmpeg processes seen (a seek starts another), by identity.</summary>
    public HashSet<int> Seen { get; } = [];

    /// <summary>The output paths of its jobs, to delete what's left of them (never sent, never logged).</summary>
    public HashSet<string> Outputs { get; } = new(StringComparer.Ordinal);

    public bool Stopping { get; set; }
}

/// <summary>What a delivery reads of its version: its picture's and audio's facts as Jellyfin holds them.</summary>
/// <remarks>
/// Streams are the version's streams as Jellyfin numbers them, in its
/// order, and whether each is in another file (a subtitle file beside it).
/// </remarks>
internal sealed record Source(int? Width, int? Height, bool Hdr, int? AudioChannels, string? VideoCodec, string? AudioCodec, IReadOnlyList<(int Index, bool External)> Streams)
{
    public int? AudioSampleRate { get; init; }
}

/// <summary>A delivery started: its ID, what Jellyfin made, the size of the picture made, and Jellyfin's media playlist.</summary>
internal sealed record DeliveryMade(string Id, Made Made, int Width, int Height, string Playlist);

/// <summary>
/// Peers' remuxes and transcodes (ported from the companion plugin's
/// checked deliveries, 0.9.0): Jellyfin's own HLS, started for an order's
/// closed values through a request to Jellyfin itself (on its own loopback
/// address, with the plugin's API key in a header), its ffmpeg command line
/// read as soon as it runs and checked against the order (a difference
/// stops it and refuses), kept alive while the peer reports, and stopped
/// after two minutes without a segment read or a report. Each delivery is a
/// device of its own; its files are deleted by its jobs' paths when it
/// stops, and what an earlier run left is deleted at start.
/// </summary>
internal sealed class Deliveries : BackgroundService
{
    /// <summary>The name of the plugin's API key in Jellyfin's list of keys.</summary>
    public const string KeyName = "MSFP Federation";

    /// <summary>Seconds without a segment read or a report before a delivery is stopped.</summary>
    public const int WatchdogSeconds = 120;

    /// <summary>The most deliveries at once.</summary>
    public const int MaxDeliveries = 32;

    /// <summary>The prefix of its devices' IDs (counted apart from the server's own users' transcodes).</summary>
    public const string DevicePrefix = "msfp-delivery-";

    /// <summary>The most a media playlist may weigh.</summary>
    private const int MaxPlaylist = 4 << 20;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly ConcurrentDictionary<string, Delivery> _all = new(StringComparer.Ordinal);
    private readonly ITranscodeManager _transcodes;
    private readonly IServerConfigurationManager _server;
    private readonly IMediaEncoder _encoder;
    private readonly IAuthenticationManager _auth;
    private readonly ILogger<Deliveries> _log;
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private readonly object _stemsLock = new();
    private string? _key;

    public Deliveries(ITranscodeManager transcodes, IServerConfigurationManager server, IMediaEncoder encoder, IAuthenticationManager auth, ILogger<Deliveries> log)
    {
        _transcodes = transcodes;
        _server = server;
        _encoder = encoder;
        _auth = auth;
        _log = log;
    }

    /// <summary>A delivery its watchdog stopped (no segment read nor report), or one Jellyfin restarted otherwise than ordered.</summary>
    public event Action<string>? Ended;

    /// <summary>The deliveries running now.</summary>
    public int Count => _all.Count;

    private bool Hardware => _server.GetEncodingOptions().HardwareAccelerationType != HardwareAccelerationType.none;

    /// <summary>
    /// What this Jellyfin encodes now: h264 always; hevc and av1 when
    /// Jellyfin's settings allow encoding them and its ffmpeg has the
    /// encoder; the audio codecs its ffmpeg encodes.
    /// </summary>
    public EncodersNow Encoders()
    {
        var options = _server.GetEncodingOptions();
        var video = new List<string> { "h264" };
        if (options.AllowHevcEncoding && (_encoder.SupportsEncoder(Made.VideoEncoders["hevc"]) || Hardware))
        {
            video.Add("hevc");
        }

        if (options.AllowAv1Encoding && (_encoder.SupportsEncoder(Made.VideoEncoders["av1"]) || Hardware))
        {
            video.Add("av1");
        }

        var audio = new List<string>();
        foreach (var (token, encoders) in Made.AudioEncoders)
        {
            if (token == "aac" || encoders.Any(_encoder.SupportsEncoder))
            {
                audio.Add(token);
            }
        }

        var hardware = Hardware;
        // Jellyfin 12.2 tone maps on the GPU only with its setting on; on
        // the CPU it always does with tonemapx (IsSwTonemapAvailable reads
        // no setting), for a picture its own reading holds as HDR.
        var toneMapping = hardware ? options.EnableTonemapping : _encoder.SupportsFilter("tonemapx");
        return new EncodersNow(video, audio, hardware, toneMapping);
    }

    /// <summary>
    /// Starts a delivery: Jellyfin's media playlist for the order, the
    /// segment the player starts at asked (ffmpeg starts there), Jellyfin's
    /// command line read and checked; a difference stops it and refuses.
    /// </summary>
    public async Task<DeliveryMade> StartAsync(DeliveryOrder o, Source source, CancellationToken ct)
    {
        DeliveryRules.Validate(o);
        if (_all.Count >= MaxDeliveries)
        {
            throw new DeliveryRefusedException("busy", true);
        }

        Precheck(o, source);
        var token = await KeyAsync(ct).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        var d = new Delivery { Id = id, DeviceId = DevicePrefix + id[..12], Order = o, Source = source };
        var root = Base() + "Videos/" + o.ItemId + "/";
        var query = DeliveryRules.Query(o, d, source, Hardware);

        // Jellyfin's media playlist for these settings: its decisions run
        // here; nothing starts until a segment is asked.
        string playlist;
        using (var req = Request(root + "main.m3u8?" + query, token, null))
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            HttpResponseMessage res;
            try
            {
                res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                throw new DeliveryRefusedException("jellyfin-unreachable", true);
            }

            using (res)
            {
                if (!res.IsSuccessStatusCode)
                {
                    throw new DeliveryRefusedException("jellyfin-" + ((int)res.StatusCode).ToString(CultureInfo.InvariantCulture), res.StatusCode == HttpStatusCode.ServiceUnavailable);
                }

                playlist = await ReadSmall(res, MaxPlaylist, cts.Token).ConfigureAwait(false);
            }
        }

        if (!playlist.StartsWith("#EXTM3U", StringComparison.Ordinal) || !playlist.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal))
        {
            throw new DeliveryRefusedException("playlist-not-vod");
        }

        var segments = DeliveryRules.Segments(playlist);
        if (segments.Count == 0)
        {
            throw new DeliveryRefusedException("playlist-empty");
        }

        if (!_all.TryAdd(id, d))
        {
            throw new DeliveryRefusedException("busy", true);
        }

        try
        {
            // The segment the player starts at: ffmpeg starts there (and, in
            // fragmented MP4, writes the init segment too), so the player's
            // own first requests find their files and start nothing else.
            var first = (int)Math.Clamp(o.StartTicks / TimeSpan.TicksPerSecond / DeliveryRules.SegmentSeconds, 0, segments.Count - 1);
            using var segmentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            segmentCts.CancelAfter(TimeSpan.FromSeconds(90));
            var warm = Warm(root + segments[first], token, segmentCts.Token);

            // Jellyfin's command line, read as soon as ffmpeg runs: often,
            // since a remux of a short file may be over (and Jellyfin let
            // go of its process) within a fraction of a second.
            Made? made = null;
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until && made is null && !warm.IsCompleted)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
                made = Capture(d).FirstOrDefault();
            }

            made ??= Capture(d).FirstOrDefault();

            if (made is null)
            {
                throw new DeliveryRefusedException("not-started");
            }

            var diff = made.Differences(o, source);
            if (diff.Count > 0)
            {
                throw new DeliveryRefusedException("made-differs:" + string.Join(',', diff));
            }

            if (!await warm.ConfigureAwait(false))
            {
                throw new DeliveryRefusedException("first-segment-failed");
            }

            var (width, height) = made.Size(source);
            return new DeliveryMade(id, made, width, height, playlist);
        }
        catch
        {
            await StopAsync(id).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The first output file of a delivery's job (the init segment of
    /// fragmented MP4, else the first segment), for reading what it holds;
    /// null when there's none. Its path stays here: never sent nor logged.
    /// </summary>
    public string? FirstOutput(string id)
    {
        if (!_all.TryGetValue(id, out var d) || TranscodeFolder() is not { } folder)
        {
            return null;
        }

        List<string> outputs;
        lock (d.Outputs)
        {
            outputs = [.. d.Outputs];
        }

        foreach (var output in outputs)
        {
            var stem = Path.GetFileNameWithoutExtension(output);
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(output)), folder, StringComparison.Ordinal) || !Made.IsOutputStem(stem))
            {
                continue;
            }

            var init = Path.Combine(folder, stem + "-1.mp4");
            if (d.Order.Segments == "mp4" && File.Exists(init))
            {
                return init;
            }

            var files = Directory.EnumerateFiles(folder, stem + "*." + d.Order.Segments)
                .Where(f => !f.EndsWith("-1.mp4", StringComparison.Ordinal))
                .OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Count > 0)
            {
                return files[0];
            }
        }

        return null;
    }

    /// <summary>A delivery's report: its player is still there.</summary>
    public bool Ping(string id)
    {
        if (!_all.TryGetValue(id, out var d))
        {
            return false;
        }

        d.LastSeen = DateTime.UtcNow;
        try
        {
            if (_transcodes.GetTranscodingJob(id) is { HasExited: false })
            {
                _transcodes.PingTranscodingJob(id, false);
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
        }

        return true;
    }

    /// <summary>Stops a delivery: its ffmpeg, and its files deleted. False when there was none.</summary>
    public async Task<bool> StopAsync(string id)
    {
        if (!_all.TryRemove(id, out var d))
        {
            return false;
        }

        d.Stopping = true;
        Capture(d);
        try
        {
            await _transcodes.KillTranscodingJobs(d.DeviceId, d.Id, _ => true).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            _log.LogWarning("MSFP delivery {Id}: stopping its transcode failed", id);
        }

        DeleteOutputs(d);
        ForgetStems(d);
        return true;
    }

    /// <summary>Stops every delivery.</summary>
    public async Task<int> ClearAsync()
    {
        var n = 0;
        foreach (var id in _all.Keys.ToList())
        {
            if (await StopAsync(id).ConfigureAwait(false))
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>A playlist or a segment of a delivery, as Jellyfin answers it, from its URL relative to the version's /Videos/{id}/.</summary>
    public async Task<HttpResponseMessage> GetAsync(string id, string relative, ByteRange? range, CancellationToken ct)
    {
        if (!_all.TryGetValue(id, out var d))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        var token = await KeyAsync(ct).ConfigureAwait(false);
        using var req = Request(Base() + "Videos/" + d.Order.ItemId + "/" + relative, token, range);
        try
        {
            var res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            d.LastSeen = DateTime.UtcNow;
            return res;
        }
        catch (HttpRequestException)
        {
            throw new MsfpException(MsfpError.ServerBusy);
        }
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // What a run that ended without stopping its deliveries left in
        // Jellyfin's transcode folder (Jellyfin kills its own jobs when it
        // stops): the files of the stems it recorded.
        ClearLeftovers();
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await WatchAsync(++tick).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "MSFP deliveries: the watch failed once");
            }
        }
    }

    private async Task WatchAsync(int tick)
    {
        // Each ffmpeg Jellyfin starts for a delivery (a seek starts
        // another) is checked against its order, as the first was.
        foreach (var d in _all.Values)
        {
            foreach (var made in Capture(d))
            {
                var diff = made.Differences(d.Order, d.Source);
                if (diff.Count > 0)
                {
                    _log.LogWarning("MSFP delivery {Id}: Jellyfin restarted it otherwise than ordered ({Differences}): stopped", d.Id, string.Join(',', diff));
                    await StopAsync(d.Id).ConfigureAwait(false);
                    Ended?.Invoke(d.Id);
                }
            }
        }

        if (tick % 40 != 0)
        {
            return;
        }

        // Every 10 seconds: a segment read since counts as activity; two
        // minutes without it or a report stop the delivery. A live one keeps
        // Jellyfin's own kill timer from ending it (a player paused, its
        // buffer full, reads nothing for a while).
        foreach (var d in _all.Values)
        {
            TranscodingJob? job = null;
            try
            {
                job = _transcodes.GetTranscodingJob(d.Id);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
            }

            if (job is not null)
            {
                var pos = job.DownloadPositionTicks ?? -1;
                if (job.ActiveRequestCount > 0 || pos != d.LastDownloadTicks)
                {
                    d.LastDownloadTicks = pos;
                    d.LastSeen = DateTime.UtcNow;
                }
            }

            if ((DateTime.UtcNow - d.LastSeen).TotalSeconds >= WatchdogSeconds)
            {
                _log.LogInformation("MSFP delivery {Id}: no segment read nor report for {Seconds} s, stopped", d.Id, WatchdogSeconds);
                await StopAsync(d.Id).ConfigureAwait(false);
                Ended?.Invoke(d.Id);
            }
            else if (job is { HasExited: false })
            {
                try
                {
                    _transcodes.PingTranscodingJob(d.Id, false);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                }
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await ClearAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    // The ffmpeg processes Jellyfin started for a delivery since last
    // looked, as their command lines say.
    private List<Made> Capture(Delivery d)
    {
        var found = new List<Made>();
        TranscodingJob? job;
        try
        {
            job = _transcodes.GetTranscodingJob(d.Id);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return found;
        }

        if (job is null)
        {
            return found;
        }

        if (!string.IsNullOrEmpty(job.Path))
        {
            bool added;
            lock (d.Outputs)
            {
                added = d.Outputs.Add(job.Path);
            }

            if (added)
            {
                RecordStem(Path.GetFileNameWithoutExtension(job.Path));
            }
        }

        try
        {
            // Each process once, by the object itself: its ID is gone once
            // it ends, its command line isn't.
            if (job.Process is { } p && p.StartInfo.Arguments is { Length: > 0 } args)
            {
                lock (d.Seen)
                {
                    if (d.Seen.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p)))
                    {
                        found.Add(Made.Parse(args));
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The process ended meanwhile.
        }

        return found;
    }

    private string? TranscodeFolder()
    {
        try
        {
            return Path.GetFullPath(_server.GetTranscodePath()).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    // What Jellyfin leaves of a delivery's jobs once they're killed: the
    // files named after their output, in Jellyfin's transcode folder only.
    private void DeleteOutputs(Delivery d)
    {
        List<string> outputs;
        lock (d.Outputs)
        {
            outputs = [.. d.Outputs];
        }

        foreach (var output in outputs)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (TranscodeFolder() is not { } folder || !string.Equals(dir, folder, StringComparison.Ordinal))
            {
                continue;
            }

            DeleteStem(folder, Path.GetFileNameWithoutExtension(output));
        }
    }

    private void DeleteStem(string folder, string stem)
    {
        if (!Made.IsOutputStem(stem))
        {
            return;
        }

        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, stem + "*"))
            {
                File.Delete(f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("MSFP deliveries: some transcode files couldn't be deleted");
        }
    }

    // The stems of the deliveries' outputs (32 hex digits each, no path),
    // in the plugin's data folder, so that a run that ends without stopping
    // them has its files deleted at the next start.
    private static string? StemsFile() =>
        Plugin.Instance?.DataFolderPath is { Length: > 0 } dir ? Path.Combine(dir, "deliveries.json") : null;

    private void RecordStem(string stem) => EditStems(s => s.Add(stem));

    private void ForgetStems(Delivery d)
    {
        List<string> outputs;
        lock (d.Outputs)
        {
            outputs = [.. d.Outputs];
        }

        EditStems(s =>
        {
            foreach (var o in outputs)
            {
                s.Remove(Path.GetFileNameWithoutExtension(o));
            }
        });
    }

    private void EditStems(Action<HashSet<string>> edit)
    {
        if (StemsFile() is not { } file)
        {
            return;
        }

        lock (_stemsLock)
        {
            var stems = ReadStems(file);
            edit(stems);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(stems.Where(Made.IsOutputStem).OrderBy(x => x, StringComparer.Ordinal).ToList()));
                File.Move(file + ".tmp", file, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("MSFP deliveries: their record couldn't be written");
            }
        }
    }

    private static HashSet<string> ReadStems(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) is { } list)
            {
                return [.. list.Where(Made.IsOutputStem)];
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    private void ClearLeftovers()
    {
        if (StemsFile() is not { } file || TranscodeFolder() is not { } folder)
        {
            return;
        }

        lock (_stemsLock)
        {
            var stems = ReadStems(file);
            foreach (var stem in stems)
            {
                DeleteStem(folder, stem);
            }

            if (stems.Count > 0)
            {
                _log.LogInformation("MSFP deliveries: {Count} left by the last run cleared", stems.Count);
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void Precheck(DeliveryOrder o, Source s)
    {
        var now = Encoders();
        if (o.Video is { Copy: false } v)
        {
            if (!now.Video.Contains(v.Codec!))
            {
                throw new DeliveryRefusedException("no-encoder");
            }

            // On the CPU, Jellyfin always tone maps an HDR picture it
            // converts: keeping HDR needs a GPU path (refused this release).
            if (s.Hdr && !v.ToneMap)
            {
                throw new DeliveryRefusedException("hdr-kept-needs-gpu");
            }

            if (s.Hdr && v.ToneMap && !now.ToneMapping)
            {
                throw new DeliveryRefusedException("no-tone-mapping");
            }

            DeliveryRules.CheckRange(v, s);
        }

        if (o.Audio is { Copy: false } a && !now.Audio.Contains(a.Codec!))
        {
            throw new DeliveryRefusedException("no-encoder");
        }
    }

    // The plugin's API key, made the first time: Jellyfin's HLS is asked
    // with it, over loopback only, in a header (never in a URL).
    private async Task<string> KeyAsync(CancellationToken ct)
    {
        if (_key is { } k)
        {
            return k;
        }

        await _keyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_key is { } again)
            {
                return again;
            }

            var found = (await _auth.GetApiKeys().ConfigureAwait(false)).FirstOrDefault(a => a.AppName == KeyName && a.IsActive);
            if (found is null)
            {
                await _auth.CreateApiKey(KeyName).ConfigureAwait(false);
                found = (await _auth.GetApiKeys().ConfigureAwait(false)).FirstOrDefault(a => a.AppName == KeyName)
                    ?? throw new DeliveryRefusedException("no-key", true);
            }

            return _key = found.AccessToken;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    // A request of Jellyfin to itself, with the plugin's key.
    private static HttpRequestMessage Request(string url, string token, ByteRange? range)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"" + token + "\"");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("MSFP", null));
        if (range is not null)
        {
            req.Headers.TryAddWithoutValidation("Range", range.Suffix > 0
                ? "bytes=-" + range.Suffix.ToString(CultureInfo.InvariantCulture)
                : "bytes=" + range.First.ToString(CultureInfo.InvariantCulture) + "-" + (range.Last < 0 ? string.Empty : range.Last.ToString(CultureInfo.InvariantCulture)));
        }

        return req;
    }

    // Asks a segment, read through and dropped; whether it came.
    private static async Task<bool> Warm(string url, string token, CancellationToken ct)
    {
        try
        {
            using var req = Request(url, token, null);
            using var res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                return false;
            }

            await using var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await body.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return false;
        }
    }

    private static async Task<string> ReadSmall(HttpResponseMessage res, int max, CancellationToken ct)
    {
        await using var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > max)
            {
                throw new DeliveryRefusedException("playlist-too-large");
            }

            buffer.Write(chunk, 0, n);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Jellyfin's own address, as it listens on this machine.
    private string Base()
    {
        var net = _server.GetNetworkConfiguration();
        var prefix = string.IsNullOrEmpty(net.BaseUrl) ? string.Empty : "/" + net.BaseUrl.Trim('/');
        return $"http://127.0.0.1:{net.InternalHttpPort.ToString(CultureInfo.InvariantCulture)}{prefix}/";
    }
}

/// <summary>A delivery's rules that need nothing of Jellyfin: what an order may say, and the query it makes.</summary>
internal static class DeliveryRules
{
    /// <summary>The segments' length, in seconds.</summary>
    public const int SegmentSeconds = 6;

    internal static readonly string[] AllRanges =
        ["SDR", "HDR10", "HDR10Plus", "HLG", "DOVI", "DOVIWithHDR10", "DOVIWithHLG", "DOVIWithSDR", "DOVIWithHDR10Plus", "DOVIWithEL", "DOVIWithELHDR10Plus", "DOVIInvalid"];

    /// <summary>
    /// Tone mapping asked for a picture Jellyfin holds as SDR: Jellyfin
    /// decides from its own reading of the file (the stream's colour
    /// transfer), not from the copy's facts (an HDR found only in the
    /// frames), so it would only relabel the picture BT.709, never map it.
    /// Refused before ffmpeg starts.
    /// </summary>
    internal static void CheckRange(DeliveryVideo v, Source s)
    {
        if (v is { Copy: false, ToneMap: true } && !s.Hdr)
        {
            throw new DeliveryRefusedException("sdr-to-jellyfin");
        }
    }

    internal static void Validate(DeliveryOrder o)
    {
        if (!IsId(o.ItemId) || !IsId(o.SourceId) || o.Segments is not ("ts" or "mp4") || o.StartTicks < 0 || o.StartTicks > TimeSpan.FromDays(100).Ticks)
        {
            throw new DeliveryRefusedException("bad-order");
        }

        if (o.Video is { } v && !v.Copy && (v.Codec is null || !Made.VideoEncoders.ContainsKey(v.Codec) || v.MaxHeight is < 144 or > 4320 || v.MaxBitrate is < 100_000 or > 200_000_000
            || v.Profile is { } p && p is not ("high" or "main") || v.Level is { } l && l is not ("41" or "40" or "31") || v.PixelFormat is { } pf && pf != "yuv420p"
            || v.BurnIndex is < -1 or > 10_000))
        {
            throw new DeliveryRefusedException("bad-video");
        }

        if (o.Video is { Copy: true, Tag: { } tag } && tag is not ("hvc1" or "dvh1" or "avc1"))
        {
            throw new DeliveryRefusedException("bad-video");
        }

        if (o.Audio is { } a && (a.Index is < 0 or > 10_000 || (!a.Copy && (a.Codec is null || !Made.AudioEncoders.ContainsKey(a.Codec) || a.Channels is < 1 or > 8
            || a.MaxSampleRate is not (0 or 48_000)))))
        {
            throw new DeliveryRefusedException("bad-audio");
        }

        if (o.Video is null && o.Audio is null)
        {
            throw new DeliveryRefusedException("bad-order");
        }
    }

    private static bool IsId(string s) => s.Length == 32 && Guid.TryParseExact(s, "N", out var g) && g != Guid.Empty && s == g.ToString("N");

    // The query of Jellyfin's HLS for an order: closed values only.
    internal static string Query(DeliveryOrder o, Delivery d, Source s, bool hardware = false)
    {
        var q = new List<(string, string)>
        {
            ("MediaSourceId", o.SourceId),
            ("DeviceId", d.DeviceId),
            ("PlaySessionId", d.Id),
            ("SegmentContainer", o.Segments),
            ("SegmentLength", SegmentSeconds.ToString(CultureInfo.InvariantCulture)),
            ("MinSegments", "1"),
            ("RequireAvc", "false"),
            ("EnableAutoStreamCopy", "true"),
        };
        if (o.Video is { BurnIndex: >= 0 } burn)
        {
            q.Add(("SubtitleStreamIndex", burn.BurnIndex.ToString(CultureInfo.InvariantCulture)));
            q.Add(("SubtitleMethod", "Encode"));
        }
        else
        {
            q.Add(("SubtitleMethod", "Drop"));
        }

        if (o.Video is { } v)
        {
            if (v.Copy)
            {
                var codec = s.VideoCodec ?? "h264";
                q.Add(("VideoCodec", codec));
                q.Add(("AllowVideoStreamCopy", "true"));
                q.Add((codec + "-rangetype", string.Join(',', AllRanges)));
            }
            else
            {
                q.Add(("VideoCodec", v.Codec!));
                q.Add(("AllowVideoStreamCopy", "false"));
                var h = Math.Min(v.MaxHeight, s.Height ?? v.MaxHeight);
                q.Add(("MaxHeight", h.ToString(CultureInfo.InvariantCulture)));
                // Without a width, Jellyfin drops the height: the width this
                // height makes of the picture.
                if (s is { Width: > 0, Height: > 0 })
                {
                    var w = (int)Math.Round(h * (double)s.Width.Value / s.Height.Value / 2) * 2;
                    q.Add(("MaxWidth", w.ToString(CultureInfo.InvariantCulture)));
                }

                q.Add(("VideoBitrate", v.MaxBitrate.ToString(CultureInfo.InvariantCulture)));
                q.Add((v.Codec + "-rangetype", v.ToneMap ? "SDR" : "SDR,HDR10,HLG"));
                if (v.Profile is { Length: > 0 })
                {
                    q.Add((v.Codec + "-profile", v.Profile));
                }

                if (v.Level is { Length: > 0 })
                {
                    q.Add((v.Codec + "-level", v.Level));
                }

                if (v.Deinterlace)
                {
                    q.Add((v.Codec + "-deinterlace", "true"));
                }
            }
        }

        if (o.Audio is { } a)
        {
            q.Add(("AudioStreamIndex", a.Index.ToString(CultureInfo.InvariantCulture)));
            if (a.Copy)
            {
                q.Add(("AudioCodec", s.AudioCodec ?? "aac"));
                q.Add(("AllowAudioStreamCopy", "true"));
            }
            else
            {
                var ch = a.Channels.ToString(CultureInfo.InvariantCulture);
                q.Add(("AudioCodec", a.Codec!));
                q.Add(("AllowAudioStreamCopy", "false"));
                q.Add(("TranscodingMaxAudioChannels", ch));
                q.Add(("MaxAudioChannels", ch));
                q.Add(("AudioBitrate", (a.Channels > 2 ? 384_000 : 192_000).ToString(CultureInfo.InvariantCulture)));
                if (a.MaxSampleRate > 0 && s.AudioSampleRate is { } rate && rate > a.MaxSampleRate)
                {
                    q.Add(("AudioSampleRate", a.MaxSampleRate.ToString(CultureInfo.InvariantCulture)));
                }
            }
        }

        return string.Join('&', q.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2)));
    }

    // The segments' URIs of a media playlist, in order (the init segment
    // aside).
    internal static List<string> Segments(string playlist)
    {
        var list = new List<string>();
        foreach (var line in playlist.Split('\n'))
        {
            var l = line.Trim();
            if (l.Length > 0 && !l.StartsWith('#'))
            {
                list.Add(l);
            }
        }

        return list;
    }

    /// <summary>
    /// Jellyfin's index of the stream ffmpeg maps as 0:map: the map-th
    /// stream of the file, as Jellyfin counts a file's own streams (those
    /// beside it, in other files, aside); -1 when there's none.
    /// </summary>
    internal static int JellyfinIndex(IReadOnlyList<(int Index, bool External)> streams, int map)
    {
        var own = streams.Where(s => !s.External).ToList();
        return map >= 0 && map < own.Count ? own[map].Index : -1;
    }
}
