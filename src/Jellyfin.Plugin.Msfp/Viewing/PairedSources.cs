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
using Msfp.Protocol;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// The stand-ins' media: one source per copy saved with each
/// (<see cref="StandIns"/>), which Jellyfin shows; at PlaybackInfo the
/// play's own (<see cref="HandOverFilter"/>): a plain file's, given here,
/// or the HLS way's, opened here as a live stream (MSFP play,
/// <see cref="RemotePlays"/>) and closed when Jellyfin is done (MSFP stop).
/// </summary>
public sealed class PairedSources : IMediaSourceProvider
{
    private readonly ILibraryManager _library;
    private readonly RemotePlays _plays;
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _http;

    public PairedSources(ILibraryManager library, RemotePlays plays, Microsoft.AspNetCore.Http.IHttpContextAccessor http)
    {
        _library = library;
        _plays = plays;
        _http = http;
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
    // A play's source (the plain file, HandOverFilter): within the
    // PlaybackInfo request that made it, and in each later request naming
    // its handle (Jellyfin's stream, HLS and subtitle routes), for the user
    // it was made for. Nothing otherwise: the saved sources are the
    // stand-in's own.
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (_http.HttpContext is not { } ctx || !IsStandIn(item))
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        var play = ctx.Items[HandOverFilter.ItemKey] as PlayToken;
        if (play is null && Guid.TryParse(ctx.User.FindFirst("Jellyfin-UserId")?.Value, out var user))
        {
            play = Handles(ctx).Select(h => _plays.Tokens.OfHandle(h, user, item.Id)).FirstOrDefault(p => p is not null);
        }

        if (play is not { Way: Way.File, Source: { } source } || play.Item != item.Id)
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        return Task.FromResult<IEnumerable<MediaSourceInfo>>([RemotePlays.Clone(source)]);
    }

    // The media source IDs a request names: its query's MediaSourceId, its
    // route's (the subtitle routes carry it in the path).
    private static IEnumerable<string> Handles(Microsoft.AspNetCore.Http.HttpContext ctx)
    {
        foreach (var q in ctx.Request.Query)
        {
            if (string.Equals(q.Key, "MediaSourceId", StringComparison.OrdinalIgnoreCase))
            {
                yield return q.Value.ToString();
            }
        }

        foreach (var r in ctx.Request.RouteValues)
        {
            if (r.Key.Contains("mediaSourceId", StringComparison.OrdinalIgnoreCase) && r.Value is string v)
            {
                yield return v;
            }
        }
    }

    /// <inheritdoc />
    // The HLS way: the play the PlaybackInfo filter made for this request.
    public async Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        // The token carries the stand-in's item ID and its source's after the prefix.
        var ids = openToken[(openToken.LastIndexOf('_') + 1)..].Split('-');
        if (!Guid.TryParseExact(ids[0], "N", out var itemId) || _library.GetItemById(itemId) is not { } item || !IsStandIn(item))
        {
            throw new ResourceNotFoundException("No such title from a paired server.");
        }

        if (_http.HttpContext?.Items[HandOverFilter.ItemKey] is not PlayToken { Way: Way.Hls } play || play.Item != item.Id)
        {
            throw new InvalidOperationException("Paired servers' titles open through PlaybackInfo.");
        }

        return await _plays.OpenAsync(item, play, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The open token of a stand-in's source.</summary>
    internal static string TokenOf(BaseItem item, string sourceId) => TokenPrefix + item.Id.ToString("N") + "-" + sourceId;

    /// <summary>
    /// The ID of a stand-in's source for a holder's copy: the item's own for
    /// the first (Jellyfin's HLS takes a source's ID for a GUID, and a local
    /// file's first source has its item's), a GUID of the item, the holder
    /// and the copy for the others.
    /// </summary>
    internal static string SourceIdOf(BaseItem item, string peer, string copyId, bool first) =>
        first ? item.Id.ToString("N") : (item.Id.ToString("N") + "/" + peer + "/" + copyId).GetMD5().ToString("N");

    /// <summary>
    /// A stand-in's source for one of its copies, as saved with it, before
    /// any play: the copy's container, size, bitrate (size × 8 /
    /// duration), length and streams, as Jellyfin's reading of the file
    /// would give them; opened at play (migration phase 6), never probed.
    /// </summary>
    internal static MediaSourceInfo SourceOf(BaseItem item, string id, string name, Copy copy, List<MediaStream> streams, bool hasSegments) => new()
    {
        Id = id,
        Name = name,
        Protocol = MediaProtocol.Http,
        Path = item.Path,
        // Not remote: Jellyfin lists the trickplay of local sources only (a
        // paired server's previews, PeerPreviews); opened at play anyway.
        IsRemote = false,
        RequiresOpening = true,
        RequiresClosing = true,
        OpenToken = TokenOf(item, id),
        SupportsProbing = false,
        SupportsDirectPlay = false,
        SupportsDirectStream = true,
        SupportsTranscoding = true,
        Container = StandInStreams.Container(copy.MediaType),
        Size = copy.Size,
        Bitrate = StandInStreams.Bitrate(copy),
        ETag = copy.ETag,
        RunTimeTicks = StandInStreams.DurationMs(copy) is > 0 and var ms ? ms * TimeSpan.TicksPerMillisecond : item.RunTimeTicks,
        Type = MediaSourceType.Default,
        VideoType = VideoType.VideoFile,
        Video3DFormat = StandInStreams.ThreeD(copy.ThreeD),
        MediaStreams = streams,
        HasSegments = hasSegments,
    };
}
