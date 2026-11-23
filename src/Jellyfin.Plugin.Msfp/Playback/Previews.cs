using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;
using Msfp;
using TrickplayInfo = Jellyfin.Database.Implementations.Entities.TrickplayInfo;

namespace Jellyfin.Plugin.Msfp.Playback;

/// <summary>
/// A copy's seek-bar previews (MSFP video section 5.5), from Jellyfin's own
/// trickplay: Jellyfin keeps, per width, sheets of thumbnails in a grid
/// (its TileWidth and TileHeight are the grid, its Width and Height a
/// thumbnail), one thumbnail every Interval from the start, which is MSFP's
/// layout. Jellyfin draws the sheets itself (its image encoder), so they are
/// served as they are, checked to be JPEGs of at most 8 MiB.
/// </summary>
internal sealed class Previews
{
    /// <summary>The largest sheet MSFP takes (video section 6.2).</summary>
    public const int MaxSheetBytes = 8 * 1024 * 1024;

    private readonly ITrickplayManager _trickplay;
    private readonly ILibraryManager _library;

    public Previews(ITrickplayManager trickplay, ILibraryManager library)
    {
        _trickplay = trickplay;
        _library = library;
    }

    /// <summary>The previews to send for the item, or null for none.</summary>
    // A copy's previewLayout since MSFP's revision (put on the copy in phase 2).
    public async Task<global::Msfp.Protocol.PreviewLayout?> ForAsync(BaseItem item)
    {
        var chosen = Choose(await _trickplay.GetTrickplayResolutions(item.Id).ConfigureAwait(false));
        if (chosen is null || !File.Exists(await SheetPathAsync(item, chosen, 0).ConfigureAwait(false)))
        {
            return null;
        }

        return new global::Msfp.Protocol.PreviewLayout(chosen.Width, chosen.Height, chosen.TileWidth, chosen.TileHeight, chosen.Interval, chosen.ThumbnailCount);
    }

    /// <summary>Sheet n of the previews <see cref="ForAsync"/> chose, as a JPEG.</summary>
    public async Task<byte[]> SheetAsync(BaseItem item, int sheet, CancellationToken ct)
    {
        var chosen = Choose(await _trickplay.GetTrickplayResolutions(item.Id).ConfigureAwait(false))
            ?? throw new MsfpException(MsfpError.NotFound);
        byte[] bytes;
        try
        {
            var path = await SheetPathAsync(item, chosen, sheet).ConfigureAwait(false);
            if (new FileInfo(path).Length > MaxSheetBytes)
            {
                throw new MsfpException(MsfpError.NotFound);
            }

            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        if (!IsJpeg(bytes))
        {
            throw new MsfpException(MsfpError.NotFound);
        }

        return bytes;
    }

    /// <summary>
    /// The set closest to 320 pixels wide (MSFP video section 5.5) within
    /// MSFP's bounds; null when none is.
    /// </summary>
    internal static TrickplayInfo? Choose(IReadOnlyDictionary<int, TrickplayInfo>? resolutions) =>
        resolutions?.Values.Where(InBounds).OrderBy(t => Math.Abs(t.Width - 320)).ThenBy(t => t.Width).FirstOrDefault();

    internal static bool InBounds(TrickplayInfo t) =>
        t.Width is >= 1 and <= 2048 && t.Height is >= 1 and <= 2048
        && t.TileWidth is >= 1 and <= 100 && t.TileHeight is >= 1 and <= 100
        && t.Interval is >= 100 and <= 3_600_000 && t.ThumbnailCount >= 1
        && (long)t.TileWidth * t.Width <= 4096 && (long)t.TileHeight * t.Height <= 4096
        && (t.ThumbnailCount + (long)t.TileWidth * t.TileHeight - 1) / ((long)t.TileWidth * t.TileHeight) <= 1000;

    /// <summary>A JPEG by its content: the start-of-image marker and a marker after it.</summary>
    internal static bool IsJpeg(byte[] bytes) =>
        bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private Task<string> SheetPathAsync(BaseItem item, TrickplayInfo chosen, int sheet) =>
        _trickplay.GetTrickplayTilePathAsync(item, chosen.Width, sheet, _library.GetLibraryOptions(item)?.SaveTrickplayWithMedia ?? false);
}
