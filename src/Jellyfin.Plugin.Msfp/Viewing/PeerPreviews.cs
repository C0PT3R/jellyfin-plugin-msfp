using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Trickplay;
using Microsoft.Extensions.Logging;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// A paired server's seek-bar previews as the stand-in's own trickplay:
/// when a playback's answer carries previews (MSFP video section 5.5) and
/// the stand-in has none of that size, its sheets are fetched through the
/// engine (re-encoded, <see cref="SheetEncoder"/>), written where Jellyfin
/// keeps an item's trickplay, and registered. In the background, so that
/// playing doesn't wait: they show from the next time the title is opened.
/// They stay for the next viewers, and go with the stand-in.
/// </summary>
internal sealed class PeerPreviews
{
    private readonly ITrickplayManager _trickplay;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _one = new(1, 1);

    public PeerPreviews(ITrickplayManager trickplay, ILogger log)
    {
        _trickplay = trickplay;
        _log = log;
    }

    public void Fetch(MsfpEngine engine, BaseItem item, RemotePlayback playback) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await FetchAsync(engine, item, playback).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogInformation(e, "MSFP: the previews of {Key} couldn't be fetched", playback.Title);
            }
        });

    private async Task FetchAsync(MsfpEngine engine, BaseItem item, RemotePlayback playback)
    {
        // Since MSFP's revision the previews' layout is the copy's
        // (Protocol.Copy.PreviewLayout) and sheets come under the playback's
        // ticket one by one: phase 7 of the migration takes the layout from
        // the stand-in's copy. Until then, none.
        global::Msfp.Protocol.PreviewLayout? layout = null;
        if (layout is not { Sheets: > 0 } tp)
        {
            return;
        }

        // One fetch at a time across the server: a few megabytes each.
        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            var known = await _trickplay.GetTrickplayResolutions(item.Id).ConfigureAwait(false);
            if (known.ContainsKey(tp.TileWidth))
            {
                return;
            }

            var dir = _trickplay.GetTrickplayDirectory(item, tp.Columns, tp.Rows, tp.TileWidth, false);
            var temp = dir + ".msfp-" + Guid.NewGuid().ToString("N")[..8];
            Directory.CreateDirectory(temp);
            long bandwidth = 0;
            try
            {
                for (var n = 0; n < tp.Sheets; n++)
                {
                    var sink = new Bytes();
                    await engine.FetchAsync(playback.Id, new MediaPath { Kind = MediaKind.Sheet, Sheet = n }, string.Empty, sink, CancellationToken.None).ConfigureAwait(false);
                    if (sink.Status != 200 || sink.Body.Length == 0)
                    {
                        return;
                    }

                    await File.WriteAllBytesAsync(Path.Combine(temp, n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg"), sink.Body.ToArray()).ConfigureAwait(false);
                    // Jellyfin's measure: a sheet's bits over the time its tiles cover.
                    var seconds = Math.Max(tp.IntervalMs * (double)tp.Columns * tp.Rows / 1000, 1);
                    bandwidth = Math.Max(bandwidth, (long)Math.Ceiling(sink.Body.Length * 8 / seconds));
                }

                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }

                Directory.Move(temp, dir);
            }
            finally
            {
                if (Directory.Exists(temp))
                {
                    Directory.Delete(temp, true);
                }
            }

            await _trickplay.SaveTrickplayInfo(new TrickplayInfo
            {
                ItemId = item.Id,
                Width = tp.TileWidth,
                Height = tp.TileHeight,
                TileWidth = tp.Columns,
                TileHeight = tp.Rows,
                ThumbnailCount = tp.TileCount,
                Interval = tp.IntervalMs,
                Bandwidth = (int)Math.Min(bandwidth, int.MaxValue),
            }).ConfigureAwait(false);
            _log.LogInformation("MSFP: previews of {Key} kept ({Sheets} sheets, {Width} px)", playback.Title, tp.Sheets, tp.TileWidth);
        }
        finally
        {
            _one.Release();
        }
    }

    private sealed class Bytes : IResponseSink
    {
        public int Status { get; private set; }

        public MemoryStream Body { get; } = new();

        public void Start(int status, string contentType, long length, string contentRange) => Status = status;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Body.WriteAsync(data, ct);
    }
}
