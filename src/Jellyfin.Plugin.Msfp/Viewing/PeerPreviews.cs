using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Trickplay;
using Microsoft.Extensions.Logging;
using Msfp;
using Copy = Msfp.Protocol.Copy;
using PreviewLayout = Msfp.Protocol.PreviewLayout;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// A paired server's seek-bar previews as the stand-in's own trickplay
/// (migration plan 4.3, answer 8). The stand-in's first source (its
/// holder's best copy) has its layout registered with Jellyfin from the
/// catalog, without a sheet: Jellyfin then lists the trickplay with the
/// item, as clients read it before they play. The sheets themselves are
/// fetched only when that copy's playback starts, all of them, in the
/// background, under the playback's ticket (the engine), one fetch at a time
/// across the server, a rate limit waited out; each re-encoded by the
/// engine, written where Jellyfin keeps an item's trickplay. They stay for
/// the next viewers while the copy's etag and layout hold; never fetched
/// for a whole library ahead, nor for a title that is only part of its copy.
/// </summary>
public sealed class PeerPreviews
{
    /// <summary>The file beside the sheets that names the copy they are of (its etag and layout).</summary>
    internal const string MarkName = "msfp-copy.txt";

    private static readonly TimeSpan[] Waits = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private readonly ITrickplayManager _trickplay;
    private readonly ILogger<PeerPreviews> _log;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly ConcurrentDictionary<Guid, bool> _fetching = new();

    public PeerPreviews(ITrickplayManager trickplay, ILogger<PeerPreviews> log)
    {
        _trickplay = trickplay;
        _log = log;
    }

    /// <summary>Whether a copy's previews can be a stand-in's: a layout, and the copy is the title whole.</summary>
    internal static bool Usable(Copy copy) => copy.PreviewLayout is { Sheets: > 0 } && !StandInStreams.Spanned(copy);

    /// <summary>
    /// What Jellyfin keeps of a copy's previews (its TrickplayInfo): tile
    /// sizes, the grid, the count and interval; the bandwidth estimated (at
    /// one bit a pixel) until the sheets are fetched and measured.
    /// </summary>
    internal static TrickplayInfo InfoOf(Guid item, PreviewLayout l, long sheetBytes = 0)
    {
        var seconds = Math.Max(l.IntervalMs * (double)l.Columns * l.Rows / 1000, 1);
        var bytes = sheetBytes > 0 ? sheetBytes : (long)l.TileWidth * l.TileHeight * l.Columns * l.Rows / 8;
        return new TrickplayInfo
        {
            ItemId = item,
            Width = l.TileWidth,
            Height = l.TileHeight,
            TileWidth = l.Columns,
            TileHeight = l.Rows,
            ThumbnailCount = l.TileCount,
            Interval = l.IntervalMs,
            Bandwidth = (int)Math.Min((long)Math.Ceiling(bytes * 8 / seconds), int.MaxValue),
        };
    }

    /// <summary>What names a copy's sheets: its etag and its layout.</summary>
    internal static string Mark(Copy copy) => copy.ETag + "\n" + copy.PreviewLayout;

    /// <summary>
    /// At each pass of the stand-ins: the stand-in's trickplay as its first
    /// source's copy says (its layout registered, sheets of another copy or
    /// layout removed), or none when the copy has no usable previews.
    /// </summary>
    internal async Task RegisterAsync(BaseItem item, Copy best)
    {
        var known = await _trickplay.GetTrickplayResolutions(item.Id).ConfigureAwait(false);
        if (!Usable(best) || best.PreviewLayout is not { } l)
        {
            if (known.Count > 0)
            {
                await ForgetAsync(item, known).ConfigureAwait(false);
            }

            return;
        }

        var wanted = InfoOf(item.Id, l);
        var dir = _trickplay.GetTrickplayDirectory(item, l.Columns, l.Rows, l.TileWidth, false);
        var mark = Path.Combine(dir, MarkName);
        var same = known.Count == 1 && known.TryGetValue(l.TileWidth, out var had) && Same(had, wanted);
        if (same && (!File.Exists(mark) || File.ReadAllText(mark) == Mark(best)))
        {
            return;
        }

        // Another copy, layout or etag: what was kept goes, the new layout is registered.
        if (known.Count > 0)
        {
            await ForgetAsync(item, known).ConfigureAwait(false);
        }

        await _trickplay.SaveTrickplayInfo(wanted).ConfigureAwait(false);
    }

    /// <summary>
    /// A copy's playback started: its whole preview set fetched in the
    /// background if it is the stand-in's first source's copy and its sheets
    /// aren't here yet. Returns at once.
    /// </summary>
    internal void Fetch(MsfpEngine engine, BaseItem item, string peer, string key, Copy copy)
    {
        if (!Usable(copy) || !_fetching.TryAdd(item.Id, true))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await FetchAsync(engine, item, peer, key, copy).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogInformation("MSFP: the previews of {Key} couldn't be fetched: {Message}", key, e.Message);
            }
            finally
            {
                _fetching.TryRemove(item.Id, out _);
            }
        });
    }

    private async Task FetchAsync(MsfpEngine engine, BaseItem item, string peer, string key, Copy copy)
    {
        var l = copy.PreviewLayout!;
        var dir = _trickplay.GetTrickplayDirectory(item, l.Columns, l.Rows, l.TileWidth, false);
        var mark = Path.Combine(dir, MarkName);
        if (File.Exists(mark) && File.ReadAllText(mark) == Mark(copy))
        {
            return;
        }

        await _one.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            Directory.CreateDirectory(dir);
            long biggest = 0;
            for (var n = 0; n < l.Sheets; n++)
            {
                var sheet = await SheetAsync(engine, peer, key, copy.Id, n).ConfigureAwait(false);
                var file = Path.Combine(dir, n.ToString(CultureInfo.InvariantCulture) + ".jpg");
                await File.WriteAllBytesAsync(file + ".part", sheet).ConfigureAwait(false);
                File.Move(file + ".part", file, true);
                biggest = Math.Max(biggest, sheet.Length);
            }

            await _trickplay.SaveTrickplayInfo(InfoOf(item.Id, l, biggest)).ConfigureAwait(false);
            await File.WriteAllTextAsync(mark, Mark(copy)).ConfigureAwait(false);
            _log.LogInformation("MSFP: previews of {Key} fetched ({Sheets} sheets, {Width} px)", key, l.Sheets, l.TileWidth);
        }
        finally
        {
            _one.Release();
        }
    }

    // One sheet; a rate limit or a busy peer waited out a few times.
    private static async Task<byte[]> SheetAsync(MsfpEngine engine, string peer, string key, string copy, int n)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await engine.PreviewSheetAsync(peer, key, copy, n).ConfigureAwait(false);
            }
            catch (MsfpException e) when (Again(e.Code) && attempt < Waits.Length)
            {
                await Task.Delay(Waits[attempt]).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether a previews fetch that failed so is worth trying again later.</summary>
    internal static bool Again(MsfpError code) => code is MsfpError.RateLimited or MsfpError.BackingOff or MsfpError.ServerBusy;

    private async Task ForgetAsync(BaseItem item, System.Collections.Generic.Dictionary<int, TrickplayInfo> known)
    {
        foreach (var t in known.Values)
        {
            var dir = _trickplay.GetTrickplayDirectory(item, t.TileWidth, t.TileHeight, t.Width, false);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        await _trickplay.DeleteTrickplayDataAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool Same(TrickplayInfo a, TrickplayInfo b) =>
        a.Width == b.Width && a.Height == b.Height && a.TileWidth == b.TileWidth && a.TileHeight == b.TileHeight
        && a.ThumbnailCount == b.ThumbnailCount && a.Interval == b.Interval;
}
