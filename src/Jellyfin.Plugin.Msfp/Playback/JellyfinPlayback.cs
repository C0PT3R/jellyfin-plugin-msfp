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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Msfp;
using Msfp.Protocol;
using Video = MediaBrowser.Controller.Entities.Video;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// The owning side of a peer's playback here (the engine's IPlayback and
/// IMedia): a peer's viewer plays one of this server's films or episodes.
/// The engine has checked the call, the pairing, its limits and the copy;
/// this server describes the copy from Jellyfin's streams and serves its
/// file as it is (direct play, with byte ranges) when the viewer's device
/// plays it, and its text subtitles as WebVTT, by Jellyfin's subtitle
/// encoder. What the device can't play as it is isn't played yet: no HLS
/// (README, "Playing").
/// </summary>
public sealed class JellyfinPlayback : IPlayback, IMedia
{
    private readonly ILibraryManager _library;
    private readonly ISubtitleEncoder _subtitles;
    private readonly ILogger<JellyfinPlayback> _log;
    private readonly ConcurrentDictionary<string, Live> _live = new(StringComparer.Ordinal);

    public JellyfinPlayback(ILibraryManager library, ISubtitleEncoder subtitles, ILogger<JellyfinPlayback> log)
    {
        _library = library;
        _subtitles = subtitles;
        _log = log;
    }

    /// <summary>The peers' playbacks here now, for the operator's page.</summary>
    public int Playing => _live.Count;

    /// <inheritdoc />
    public ValueTask<CopyInfo> DescribeAsync(string copy, CancellationToken ct)
    {
        var item = Item(copy);
        var streams = item.GetMediaStreams();
        var info = new CopyInfo
        {
            DurationMs = (item.RunTimeTicks ?? 0) / TimeSpan.TicksPerMillisecond,
            Parts = 1 + (item is Video v ? v.AdditionalParts?.Length ?? 0 : 0),
        };
        var audio = streams.Where(s => s.Type == MediaStreamType.Audio && s.Index is >= 0 and <= 999).ToList();
        if (audio.Count > 0)
        {
            info.AudioTracks = audio.Select(s => new Track
            {
                Id = "a" + s.Index.ToString(CultureInfo.InvariantCulture),
                Label = Label(s),
                Language = Lang(s.Language),
                Channels = s.Channels ?? 0,
                Default = s.IsDefault,
            }).ToList();
        }

        var subtitles = TextSubtitles(streams).ToList();
        if (subtitles.Count > 0)
        {
            info.SubtitleTracks = subtitles.Select(s => new Track
            {
                Id = "s" + s.Index.ToString(CultureInfo.InvariantCulture),
                Label = Label(s),
                Language = Lang(s.Language),
                Default = s.IsDefault,
                Forced = s.IsForced,
                HearingImpaired = s.IsHearingImpaired,
            }).ToList();
        }

        return ValueTask.FromResult(info);
    }

    /// <inheritdoc />
    public ValueTask<Started> StartAsync(StartRequest request, CancellationToken ct)
    {
        var item = Item(request.Copy);
        if (request.AudioOnly || request.Parts > 1 || item is not Video video || string.IsNullOrEmpty(video.Path) || !File.Exists(video.Path))
        {
            throw new MsfpException(MsfpError.NotPlayable);
        }

        var streams = item.GetMediaStreams();
        var file = FileInfoOf(video, streams);
        if (!Direct.Plays(file, request))
        {
            // Not as it is: HLS, Jellyfin remuxing or transcoding it, isn't
            // built yet.
            throw new MsfpException(MsfpError.NotPlayable);
        }

        var slot = request.Admit?.Invoke("direct");
        var handle = Base64UrlText.Encode(RandomNumberGenerator.GetBytes(16));
        _live[handle] = new Live(item.Id, video.Path, request.Peer.Name);
        _log.LogInformation("MSFP: {Peer} plays {Title} here, direct", request.Peer.Name, request.Title);
        _ = slot;
        return ValueTask.FromResult(new Started
        {
            Handle = handle,
            Method = "direct",
            Protocol = "file",
            MimeType = file.MimeType,
            PositionMs = request.StartMs,
            Info = new PlaybackInfo
            {
                Container = file.Container,
                BitrateKbps = file.BitrateKbps,
                Video = file.Video is { } vs ? new StreamInfo { Codec = vs.Codec, Profile = vs.Profile, Width = vs.Width ?? 0, Height = vs.Height ?? 0, BitDepth = vs.BitDepth ?? 0 } : null,
                Audio = file.Audio is { } a ? new StreamInfo { Codec = a.Codec, Channels = a.Channels ?? 0, Language = Lang(a.Language) } : null,
            },
        });
    }

    /// <inheritdoc />
    public ValueTask ProgressAsync(string handle, long positionMs, bool paused, CancellationToken ct)
    {
        if (!_live.TryGetValue(handle, out var live))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        live.PositionMs = positionMs;
        live.Paused = paused;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StopAsync(string handle, EndReason reason, CancellationToken ct)
    {
        if (_live.TryRemove(handle, out var live))
        {
            _log.LogInformation("MSFP: {Peer}'s playback here ended ({Reason})", live.Peer, reason);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask ServeAsync(string handle, MediaPath path, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        if (!_live.TryGetValue(handle, out var live))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        switch (path.Kind)
        {
            case MediaKind.File:
                await FileServing.ServeAsync(live.Path, range, MimeOf(live.Path), output, ct).ConfigureAwait(false);
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
            default:
                throw new MsfpException(MsfpError.NotFound);
        }
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

    private sealed class Live(Guid item, string path, string peer)
    {
        public Guid Item { get; } = item;

        public string Path { get; } = path;

        public string Peer { get; } = peer;

        public long PositionMs { get; set; }

        public bool Paused { get; set; }

        public ConcurrentDictionary<string, byte[]> Subtitles { get; } = new(StringComparer.Ordinal);
    }
}
