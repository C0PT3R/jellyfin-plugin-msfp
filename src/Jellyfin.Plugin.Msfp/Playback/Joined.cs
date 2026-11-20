using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// A copy of several files (a film in parts, cd1 and cd2) as one HLS
/// stream, as MSFP media section 1 asks of such a copy: the parts joined
/// one after the other by Jellyfin's ffmpeg, the video and audio copied,
/// never converted. Only when every part has the same video (codec, size)
/// and audio (codecs, in the same order), and the viewer's device plays
/// them as they are; refused otherwise (the viewer's server asks another
/// server). The stream is written to a folder of the playback's own while
/// it plays; its playlist grows as the parts are copied (an EVENT playlist,
/// ended when the copy is done), which runs far faster than playing.
/// </summary>
internal sealed class Joined : IDisposable
{
    private const string Playlist = "main.m3u8";

    private readonly Process _ffmpeg;

    private Joined(string folder, Process ffmpeg, string segments, long bandwidth)
    {
        Folder = folder;
        _ffmpeg = ffmpeg;
        Segments = segments;
        Bandwidth = bandwidth;
    }

    public string Folder { get; }

    /// <summary>"ts" (H.264) or "mp4" (HEVC, with an init segment).</summary>
    public string Segments { get; }

    public long Bandwidth { get; }

    /// <summary>A film's parts, the first first; one for a film of one file.</summary>
    public static List<Video> Parts(Video video) =>
        [video, .. video.GetAdditionalParts(null!).OfType<Video>().Where(p => !string.IsNullOrEmpty(p.Path))];

    /// <summary>Whether the parts can be joined as they are: the same video and audio in each.</summary>
    public static bool SameStreams(IReadOnlyList<Video> parts)
    {
        string Shape(Video v)
        {
            var streams = v.GetMediaStreams().Where(s => !s.IsExternal).ToList();
            var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var audio = streams.Where(s => s.Type == MediaStreamType.Audio).Select(s => (s.Codec ?? string.Empty).ToLowerInvariant() + "/" + s.Channels);
            return video is null ? string.Empty
                : string.Join(';', (video.Codec ?? string.Empty).ToLowerInvariant(), video.Width, video.Height, string.Join(',', audio));
        }

        var first = Shape(parts[0]);
        return first.Length > 0 && parts.All(p => File.Exists(p.Path) && Shape(p) == first);
    }

    /// <summary>
    /// Starts joining the parts: the video and the audio track of the first
    /// part's stream number audioIndex (Jellyfin's), copied.
    /// </summary>
    public static Joined Start(string ffmpeg, string folder, IReadOnlyList<Video> parts, int audioIndex, string segments, long bandwidth)
    {
        Directory.CreateDirectory(folder);
        var list = new StringBuilder();
        foreach (var p in parts)
        {
            // The concat demuxer's list: each path quoted, its quotes escaped.
            list.Append("file '").Append(p.Path!.Replace("'", "'\\''", StringComparison.Ordinal)).Append("'\n");
        }

        File.WriteAllText(Path.Combine(folder, "parts.txt"), list.ToString());
        var streams = parts[0].GetMediaStreams();
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, WorkingDirectory = folder };
        var args = new List<string> { "-v", "error", "-nostdin", "-f", "concat", "-safe", "0", "-i", "parts.txt", "-map", "0:v:0" };
        if (audioIndex >= 0)
        {
            // Jellyfin numbers external files first: the file's own number.
            args.AddRange(["-map", "0:" + streams.Count(s => !s.IsExternal && s.Index < audioIndex).ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-c", "copy", "-f", "hls", "-hls_time", "6", "-hls_list_size", "0", "-hls_playlist_type", "event"]);
        args.AddRange(segments == "mp4"
            ? ["-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4", "-hls_segment_filename", "seg%d.m4s"]
            : ["-hls_segment_type", "mpegts", "-hls_segment_filename", "seg%d.ts"]);
        args.Add(Playlist);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        var process = Process.Start(psi) ?? throw new MsfpException(MsfpError.NotPlayable);
        // Its messages are drained, so that a full pipe never stops it.
        _ = process.StandardError.ReadToEndAsync();
        return new Joined(folder, process, segments, bandwidth);
    }

    /// <summary>The master playlist: the one stream.</summary>
    public byte[] Master() => Encoding.UTF8.GetBytes(
        "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-STREAM-INF:BANDWIDTH="
        + Bandwidth.ToString(CultureInfo.InvariantCulture) + "\n" + Playlist + "\n");

    /// <summary>Whether name is the media playlist.</summary>
    public static bool IsPlaylist(string name) => name == Playlist;

    /// <summary>
    /// The media playlist as written so far, in MSFP's subset (media
    /// section 3.1): waited for until it lists a segment (a minute at most).
    /// </summary>
    public async Task<byte[]> PlaylistAsync(CancellationToken ct)
    {
        var path = Path.Combine(Folder, Playlist);
        for (var i = 0; i < 600; i++)
        {
            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                if (text.Contains("#EXTINF", StringComparison.Ordinal))
                {
                    return Encoding.UTF8.GetBytes(Subset(text));
                }
            }

            if (_ffmpeg.HasExited && !File.Exists(path))
            {
                break;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        throw new MsfpException(MsfpError.NotFound);
    }

    /// <summary>The tags MSFP's media playlists may hold, and the segments' names.</summary>
    internal static string Subset(string playlist)
    {
        string[] tags = ["#EXTM3U", "#EXT-X-VERSION", "#EXT-X-TARGETDURATION", "#EXT-X-MEDIA-SEQUENCE", "#EXT-X-PLAYLIST-TYPE",
            "#EXT-X-INDEPENDENT-SEGMENTS", "#EXTINF", "#EXT-X-DISCONTINUITY", "#EXT-X-ENDLIST", "#EXT-X-MAP"];
        var output = new StringBuilder();
        foreach (var line in playlist.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (tags.Any(t => line == t || line.StartsWith(t + ":", StringComparison.Ordinal)))
                {
                    output.Append(line).Append('\n');
                }
            }
            else if (IsSegment(line))
            {
                output.Append(line).Append('\n');
            }
        }

        return output.ToString();
    }

    /// <summary>Whether name is one of this stream's segments (or its init segment).</summary>
    public static bool IsSegment(string name) =>
        name == "init.mp4"
        || (name.StartsWith("seg", StringComparison.Ordinal)
            && (name.EndsWith(".ts", StringComparison.Ordinal) || name.EndsWith(".m4s", StringComparison.Ordinal))
            && name[3..name.LastIndexOf('.')] is { Length: > 0 and <= 6 } n && n.All(char.IsAsciiDigit));

    /// <summary>A segment's file; null when it isn't one.</summary>
    public string? SegmentPath(string name) => IsSegment(name) ? Path.Combine(Folder, name) : null;

    public void Dispose()
    {
        try
        {
            if (!_ffmpeg.HasExited)
            {
                _ffmpeg.Kill(true);
                _ffmpeg.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _ffmpeg.Dispose();
        try
        {
            Directory.Delete(Folder, true);
        }
        catch (IOException)
        {
        }
    }
}
