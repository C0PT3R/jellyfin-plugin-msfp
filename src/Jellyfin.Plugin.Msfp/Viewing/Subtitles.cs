using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// A paired server's text subtitle track, whole, as Jellyfin reads an
/// external subtitle file: its parts (MSFP video section 6.2, 120 seconds
/// each, starting 10 seconds early) fetched through the engine, which
/// rebuilds each as section 6.3 says, then joined, the doubles dropped.
/// </summary>
internal static class Subtitles
{
    /// <summary>The track's file name at the relay route.</summary>
    public const string Whole = "all.vtt";

    private const int Stretch = 120_000;

    /// <summary>Where a playback's PGS tracks are kept while it plays: Jellyfin reads PGS from disk only.</summary>
    public static string PictureFolder(string token) =>
        System.IO.Path.Combine(Plugin.Instance?.DataFolderPath ?? System.IO.Path.GetTempPath(), "playing", token);

    public static string PictureFile(string token, string track) => System.IO.Path.Combine(PictureFolder(token), track + ".sup");

    /// <summary>A PGS track, fetched whole in the background and written for Jellyfin to read.</summary>
    public static void FetchPictures(MsfpEngine engine, RemotePlays.Live live, string track, Microsoft.Extensions.Logging.ILogger log) =>
        _ = Task.Run(async () =>
        {
            try
            {
                var whole = await WholeAsync(engine, live.Playback.Id, track, true, live.DurationMs, CancellationToken.None).ConfigureAwait(false);
                if (whole is null)
                {
                    return;
                }

                var file = PictureFile(live.Token, track);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file + ".tmp", whole).ConfigureAwait(false);
                File.Move(file + ".tmp", file, true);
            }
            catch (Exception e)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(log, e, "MSFP: picture subtitles {Track} of {Key} couldn't be fetched", track, live.Playback.Title);
            }
        });

    /// <summary>A playback's PGS files, gone with it.</summary>
    public static void ForgetPictures(string token)
    {
        try
        {
            var dir = PictureFolder(token);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The whole track; null when a part couldn't be fetched.</summary>
    public static async Task<byte[]?> WholeAsync(MsfpEngine engine, string playback, string track, bool pictures, long durationMs, CancellationToken ct)
    {
        var parts = (int)Math.Clamp((durationMs + Stretch - 1) / Stretch, 1, 10_000);
        var texts = new byte[]?[parts];
        using var gate = new SemaphoreSlim(4, 4);
        await Task.WhenAll(Enumerable.Range(0, parts).Select(async n =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var sink = new MemorySink();
                await engine.FetchAsync(playback, new MediaPath { Kind = pictures ? MediaKind.Pictures : MediaKind.Subtitles, Track = track, Part = n }, string.Empty, sink, ct).ConfigureAwait(false);
                texts[n] = sink.Status == 200 ? sink.Body.ToArray() : null;
            }
            catch (MsfpException)
            {
                texts[n] = null;
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        if (texts.Any(t => t is null))
        {
            return null;
        }

        return pictures ? Playback.Pgs.Join(texts!) : Encoding.UTF8.GetBytes(Join(texts.Select(t => Encoding.UTF8.GetString(t!))));
    }

    /// <summary>
    /// Parts, in the canonical form section 6.3 writes, joined into one
    /// WebVTT file: each cue once, in the parts' order.
    /// </summary>
    internal static string Join(IEnumerable<string> parts)
    {
        var output = new StringBuilder("WEBVTT\n");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in parts)
        {
            foreach (var block in part.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var cue = block.Trim('\n');
                if (cue.Length == 0 || cue.StartsWith("WEBVTT", StringComparison.Ordinal) || !cue.Contains("-->", StringComparison.Ordinal) || !seen.Add(cue))
                {
                    continue;
                }

                output.Append('\n').Append(cue).Append('\n');
            }
        }

        return output.ToString();
    }

    private sealed class MemorySink : IResponseSink
    {
        public int Status { get; private set; }

        public MemoryStream Body { get; } = new();

        public void Start(int status, string contentType, long length, string contentRange) => Status = status;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Body.WriteAsync(data, ct);
    }
}
