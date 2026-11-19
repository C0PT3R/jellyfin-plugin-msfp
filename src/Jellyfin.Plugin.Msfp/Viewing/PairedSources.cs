using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// The stand-ins' media: each has one source of its own, saved with it
/// (<see cref="StandIns"/>), which must be opened. Jellyfin opens it through
/// here when someone presses Play (MSFP play, <see cref="RemotePlays"/>),
/// and closes it when they stop (MSFP stop).
/// </summary>
public sealed class PairedSources : IMediaSourceProvider
{
    private readonly ILibraryManager _library;
    private readonly RemotePlays _plays;

    public PairedSources(ILibraryManager library, RemotePlays plays)
    {
        _library = library;
        _plays = plays;
    }

    /// <summary>
    /// The prefix of the sources' open tokens: the MD5 of this type's full
    /// name, as Jellyfin gives its providers' tokens, which tells it whose
    /// source it is.
    /// </summary>
    public static readonly string TokenPrefix = typeof(PairedSources).FullName!.GetMD5().ToString("N") + "_";

    /// <summary>Whether an item is a stand-in: of the paired servers' channel, at an msfp:// path.</summary>
    public static bool IsStandIn(BaseItem item) =>
        item.Path?.StartsWith(Wanted.PathOf(string.Empty), StringComparison.Ordinal) == true;

    /// <inheritdoc />
    // The stand-ins' sources are their own, saved with them: nothing to add.
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken) =>
        Task.FromResult(Enumerable.Empty<MediaSourceInfo>());

    /// <inheritdoc />
    public async Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        // The token carries the stand-in's item ID after the prefix.
        var id = openToken[(openToken.LastIndexOf('_') + 1)..];
        if (!Guid.TryParseExact(id, "N", out var itemId) || _library.GetItemById(itemId) is not { } item || !IsStandIn(item))
        {
            throw new ResourceNotFoundException("No such title from a paired server.");
        }

        return await _plays.OpenAsync(item, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The open token of a stand-in's source.</summary>
    internal static string TokenOf(BaseItem item) => TokenPrefix + item.Id.ToString("N");

    /// <summary>A stand-in's own source, as saved with it: opened at play, never probed.</summary>
    internal static MediaSourceInfo SourceOf(BaseItem item, List<MediaStream> streams) => new()
    {
        // Jellyfin's HLS takes the source's ID for a GUID: the item's.
        Id = item.Id.ToString("N"),
        Name = "From a paired server",
        Protocol = MediaProtocol.Http,
        Path = item.Path,
        IsRemote = true,
        RequiresOpening = true,
        RequiresClosing = true,
        OpenToken = TokenOf(item),
        SupportsProbing = false,
        SupportsDirectPlay = false,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
        RunTimeTicks = item.RunTimeTicks,
        Type = MediaSourceType.Default,
        MediaStreams = streams,
    };
}
