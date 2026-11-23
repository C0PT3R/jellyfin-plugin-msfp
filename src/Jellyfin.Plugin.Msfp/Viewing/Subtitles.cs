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
/// A paired server's subtitle track served apart, whole, as Jellyfin reads
/// an external subtitle file: a text track in one answer, else its parts
/// (MSFP video section 6.2, 120 seconds each, starting 10 seconds early)
/// fetched through the engine, which rebuilds each as section 6.3 says,
/// then joined, the doubles dropped; a picture track's parts joined.
/// </summary>
internal static class Subtitles
{
    /// <summary>The track's file name at the relay route.</summary>
    public const string Whole = "all.vtt";

    private const int Stretch = 120_000;

    /// <summary>The whole track; null when a part couldn't be fetched.</summary>
    public static async Task<byte[]?> WholeAsync(MsfpEngine engine, string playback, string track, bool pictures, long durationMs, CancellationToken ct)
    {
        // A text track whole, in one answer (MSFP video section 6.2); its
        // stretches when the paired server doesn't serve it so.
        if (!pictures)
        {
            try
            {
                var sink = new MemorySink();
                await engine.FetchAsync(playback, new MediaPath { Kind = MediaKind.Subtitles, Track = track, Whole = true }, string.Empty, sink, ct).ConfigureAwait(false);
                if (sink.Status == 200)
                {
                    return sink.Body.ToArray();
                }
            }
            catch (MsfpException)
            {
            }
        }

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
