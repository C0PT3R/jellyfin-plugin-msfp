using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Entities;
using Msfp;
using Msfp.Protocol;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>How a copy is played as HLS: Jellyfin's own HLS, asked for these.</summary>
internal sealed record HlsChoice(string Method, string VideoCodec, string AudioCodec, string Segments, int MaxHeight, long VideoBitrate, int AudioChannels);

/// <summary>
/// A copy served as HLS by Jellyfin itself: its master playlist asked over
/// loopback, with the plugin's API key, as a device of the playback's own,
/// and the playlists rewritten to the subset MSFP media section 3.1 allows
/// (master: v&lt;n&gt;.m3u8; media: init.&lt;ext&gt; and seg&lt;n&gt;.&lt;ext&gt;),
/// each name kept with Jellyfin's URL for it. Jellyfin remuxes (the video
/// copied) or transcodes, starts when the first segment is asked, and
/// stops when the playback ends.
/// </summary>
internal sealed partial class Hls
{
    /// <summary>The name of the plugin's API key in Jellyfin's list of keys.</summary>
    public const string KeyName = "MSFP Federation";

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

    private readonly IServerConfigurationManager _server;
    private readonly IAuthenticationManager _auth;
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private string? _key;

    public Hls(IServerConfigurationManager server, IAuthenticationManager auth)
    {
        _server = server;
        _auth = auth;
    }

    [GeneratedRegex(@"^hls1/[A-Za-z0-9_-]+/(-?[0-9]{1,9})\.(ts|mp4|m4s|aac|mp3)\?")]
    private static partial Regex SegmentUrl();

    [GeneratedRegex(@"URI=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex UriAttribute();

    /// <summary>
    /// How to play the file as HLS for this request, the cheapest first: the
    /// video copied (remux) when the device plays its codec, HDR, height and
    /// bitrate; else transcoded to H.264. Nothing said about video codecs:
    /// H.264; about audio codecs: AAC (MSFP video section 5.1).
    /// </summary>
    public static HlsChoice Choose(FileFacts file, MediaStream? audio, StartRequest request)
    {
        var caps = request.Capabilities;
        var video = file.Video?.Codec?.ToLowerInvariant() ?? string.Empty;
        var videoCodecs = caps.VideoCodecs is { Count: > 0 } vc ? vc : ["h264"];
        var cap = new[] { request.MaxBitrateKbps, caps.MaxBitrateKbps }.Where(c => c > 0).DefaultIfEmpty(120_000).Min();
        var height = file.Picture?.Height ?? 0;
        var hdr = file.Picture?.Hdr ?? [];
        var hdrPlays = hdr.Count == 0 || hdr.Any(h => caps.Hdr?.Contains(h, StringComparer.Ordinal) == true);
        var heightPlays = caps.MaxHeight <= 0 || global::Msfp.Protocol.Video.StepHeight(height) <= caps.MaxHeight;
        var bitratePlays = file.BitrateKbps == 0 || file.BitrateKbps <= cap;
        var copy = video is "h264" or "hevc" && videoCodecs.Contains(video, StringComparer.Ordinal) && hdrPlays && heightPlays && bitratePlays;
        var segments = copy && video != "h264" ? "mp4" : "ts";

        var audioCodecs = caps.AudioCodecs is { Count: > 0 } ac ? ac : ["aac"];
        var source = audio?.Codec?.ToLowerInvariant() ?? string.Empty;
        string[] inSegments = segments == "ts" ? ["aac", "mp3", "ac3", "eac3"] : ["aac", "ac3", "eac3", "flac", "opus", "alac"];
        var channels = caps.MaxAudioChannels > 0 ? caps.MaxAudioChannels : 2;
        var audioCopies = source.Length > 0 && audioCodecs.Contains(source, StringComparer.Ordinal) && inSegments.Contains(source)
            && (audio?.Channels ?? 2) <= (caps.MaxAudioChannels > 0 ? caps.MaxAudioChannels : 8);
        var audioCodec = audioCopies ? source : "aac";

        if (copy)
        {
            // A bitrate above the file's own, so that Jellyfin copies.
            var kbps = Math.Max(file.BitrateKbps * 2L, 20_000);
            return new HlsChoice("remux", video, audioCodec, segments, 0, kbps * 1000, audioCopies ? audio?.Channels ?? channels : channels);
        }

        var maxHeight = caps.MaxHeight > 0 ? caps.MaxHeight : 2160;
        if (height > 0)
        {
            maxHeight = Math.Min(maxHeight, height);
        }

        // H.264 in 8 bits, within the cap less the audio's share.
        var videoKbps = Math.Max(cap - 256, 500);
        videoKbps = Math.Min(videoKbps, maxHeight switch { <= 480 => 2_500, <= 720 => 5_000, <= 1080 => 10_000, <= 1440 => 16_000, _ => 30_000 });
        return new HlsChoice("transcode", "h264", audioCodec, "ts", maxHeight, videoKbps * 1000L, channels);
    }

    /// <summary>Jellyfin's master playlist URL for this playback, relative to /Videos/{item}/.</summary>
    public async Task<string> MasterAsync(Guid item, string playback, HlsChoice choice, int audioIndex, CancellationToken ct)
    {
        var key = await KeyAsync(ct).ConfigureAwait(false);
        var q = new List<(string, string)>
        {
            ("MediaSourceId", item.ToString("N")),
            ("DeviceId", "msfp-" + playback),
            ("PlaySessionId", playback),
            ("VideoCodec", choice.VideoCodec),
            ("AudioCodec", choice.AudioCodec),
            ("SegmentContainer", choice.Segments),
            ("VideoBitrate", choice.VideoBitrate.ToString(CultureInfo.InvariantCulture)),
            ("AudioBitrate", "192000"),
            ("TranscodingMaxAudioChannels", choice.AudioChannels.ToString(CultureInfo.InvariantCulture)),
            ("AllowVideoStreamCopy", choice.Method == "remux" ? "true" : "false"),
            ("AllowAudioStreamCopy", "true"),
            ("BreakOnNonKeyFrames", "false"),
            ("RequireAvc", "false"),
            ("SubtitleMethod", "Drop"),
            ("ApiKey", key),
        };
        if (audioIndex >= 0)
        {
            q.Add(("AudioStreamIndex", audioIndex.ToString(CultureInfo.InvariantCulture)));
        }

        if (choice.MaxHeight > 0)
        {
            q.Add(("MaxHeight", choice.MaxHeight.ToString(CultureInfo.InvariantCulture)));
        }

        return "master.m3u8?" + string.Join('&', q.Select(p => p.Item1 + "=" + Uri.EscapeDataString(p.Item2)));
    }

    /// <summary>
    /// Fetches one of Jellyfin's playlists and rewrites it to MSFP's subset;
    /// the names it gives are noted in names. A playlist with a URI that
    /// isn't Jellyfin's own form is refused (NotFound).
    /// </summary>
    public async Task<byte[]> PlaylistAsync(Guid item, string url, bool master, ConcurrentDictionary<string, string> names, CancellationToken ct)
    {
        using var res = await GetAsync(item, url, null, ct).ConfigureAwait(false);
        if (res.StatusCode != HttpStatusCode.OK)
        {
            throw new MsfpException(res.StatusCode == HttpStatusCode.ServiceUnavailable ? MsfpError.ServerBusy : MsfpError.NotFound);
        }

        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetBytes(master ? Master(text, names) : Media(text, names));
    }

    /// <summary>A segment, or the range of it asked, as Jellyfin answers it.</summary>
    public async Task SegmentAsync(Guid item, string url, ByteRange? range, IResponseSink output, CancellationToken ct)
    {
        using var res = await GetAsync(item, url, range, ct).ConfigureAwait(false);
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

        var type = res.Content.Headers.ContentType?.MediaType ?? "video/mp2t";
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

    // The master playlist: its variants named v<n>.m3u8.
    internal static string Master(string text, ConcurrentDictionary<string, string> names)
    {
        var sb = new StringBuilder("#EXTM3U\n");
        var n = 0;
        foreach (var line in Lines(text))
        {
            if (line.StartsWith('#'))
            {
                if (line.Contains("URI=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal) || line == "#EXT-X-INDEPENDENT-SEGMENTS"
                    || line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    sb.Append(line).Append('\n');
                }

                continue;
            }

            if (!line.Contains(".m3u8?", StringComparison.Ordinal) || line.Contains("://", StringComparison.Ordinal) || line.StartsWith('/'))
            {
                throw new MsfpException(MsfpError.NotFound);
            }

            var name = "v" + n++.ToString(CultureInfo.InvariantCulture) + ".m3u8";
            names[name] = line;
            sb.Append(name).Append('\n');
        }

        return sb.ToString();
    }

    // A media playlist: init.<ext> and seg<n>.<ext>, durations kept, titles not.
    internal static string Media(string text, ConcurrentDictionary<string, string> names)
    {
        string[] keep = ["#EXT-X-VERSION:", "#EXT-X-TARGETDURATION:", "#EXT-X-MEDIA-SEQUENCE:", "#EXT-X-PLAYLIST-TYPE:"];
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var m = UriAttribute().Match(line);
                var seg = m.Success ? SegmentUrl().Match(m.Groups[1].Value) : Match.Empty;
                if (!seg.Success)
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                var name = "init." + seg.Groups[2].Value;
                names[name] = m.Groups[1].Value;
                sb.Append("#EXT-X-MAP:URI=\"").Append(name).Append("\"\n");
            }
            else if (line.StartsWith('#'))
            {
                if (line.Contains("URI=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    sb.Append(line.AsSpan(0, line.IndexOf(',', StringComparison.Ordinal) is var c and >= 0 ? c : line.Length)).Append(",\n");
                }
                else if (keep.Any(k => line.StartsWith(k, StringComparison.Ordinal))
                    || line is "#EXT-X-INDEPENDENT-SEGMENTS" or "#EXT-X-DISCONTINUITY" or "#EXT-X-ENDLIST")
                {
                    sb.Append(line).Append('\n');
                }
            }
            else
            {
                var seg = SegmentUrl().Match(line);
                if (!seg.Success)
                {
                    throw new MsfpException(MsfpError.NotFound);
                }

                var name = "seg" + seg.Groups[1].Value + "." + seg.Groups[2].Value;
                names[name] = line;
                sb.Append(name).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> Lines(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || lines[0] != "#EXTM3U")
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        return lines.Skip(1).Where(l => l.Length > 0);
    }

    private async Task<HttpResponseMessage> GetAsync(Guid item, string url, ByteRange? range, CancellationToken ct)
    {
        var net = _server.GetNetworkConfiguration();
        var baseUrl = (net.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length > 0 && !baseUrl.StartsWith('/'))
        {
            baseUrl = "/" + baseUrl;
        }

        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{net.InternalHttpPort}{baseUrl}/Videos/{item:N}/{url}");
        if (range is not null)
        {
            req.Headers.TryAddWithoutValidation("Range", range.Suffix > 0
                ? "bytes=-" + range.Suffix.ToString(CultureInfo.InvariantCulture)
                : "bytes=" + range.First.ToString(CultureInfo.InvariantCulture) + "-" + (range.Last < 0 ? string.Empty : range.Last.ToString(CultureInfo.InvariantCulture)));
        }

        try
        {
            return await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new MsfpException(MsfpError.ServerBusy);
        }
    }

    // The plugin's API key, made the first time: Jellyfin's HLS is asked
    // with it, over loopback only.
    private async Task<string> KeyAsync(CancellationToken ct)
    {
        if (_key is { } k)
        {
            return k;
        }

        await _keyLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var found = (await _auth.GetApiKeys().ConfigureAwait(false)).FirstOrDefault(a => a.AppName == KeyName && a.IsActive);
            if (found is null)
            {
                await _auth.CreateApiKey(KeyName).ConfigureAwait(false);
                found = (await _auth.GetApiKeys().ConfigureAwait(false)).FirstOrDefault(a => a.AppName == KeyName)
                    ?? throw new MsfpException(MsfpError.ServerBusy);
            }

            return _key = found.AccessToken;
        }
        finally
        {
            _keyLock.Release();
        }
    }
}
