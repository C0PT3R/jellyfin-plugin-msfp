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
/// The stand-ins' media: the best copy's streams saved with each
/// (<see cref="StandIns"/>), which Jellyfin shows as a library item's
/// source; at PlaybackInfo the play's own (<see cref="HandOverFilter"/>):
/// a plain file's, given here, or the HLS way's, given here and opened here
/// as a live stream (MSFP play, <see cref="RemotePlays"/>) and closed when
/// Jellyfin is done (MSFP stop).
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

    /// <summary>Whether an item is a stand-in: at an msfp:// path.</summary>
    public static bool IsStandIn(BaseItem item) =>
        item.Path?.StartsWith(Wanted.PathOf(string.Empty), StringComparison.Ordinal) == true;

    /// <inheritdoc />
    // A play's source (HandOverFilter): within the PlaybackInfo request
    // that made it; the plain file's also in each later request naming its
    // handle (Jellyfin's stream, HLS and subtitle routes), for the user it
    // was made for. Nothing otherwise: the stand-in's own source is
    // Jellyfin's, from its saved streams.
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (_http.HttpContext is not { } ctx || !IsStandIn(item))
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        var play = ctx.Items[HandOverFilter.ItemKey] as PlayToken;
        if (HlsOf(play, item.Id) is { } hls)
        {
            return Task.FromResult<IEnumerable<MediaSourceInfo>>([hls]);
        }

        string? shownAs = null;
        if (play is null && Guid.TryParse(ctx.User.FindFirst("Jellyfin-UserId")?.Value, out var user))
        {
            var device = ctx.User.FindFirst("Jellyfin-DeviceId")?.Value;
            foreach (var h in Handles(ctx))
            {
                if (_plays.Tokens.OfHandle(h, user, item.Id) is { } byHandle)
                {
                    play = byHandle;
                    break;
                }

                // A client naming the ID it was shown (Kodi's static stream).
                if (_plays.Tokens.OfShown(h, user, item.Id, device) is { } byShown)
                {
                    play = byShown;
                    shownAs = byShown.Shown;
                    break;
                }
            }
        }

        if (play is not { Way: Way.File, Source: { } source } || play.Item != item.Id)
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        var clone = RemotePlays.Clone(source);
        if (shownAs is not null)
        {
            clone.Id = shownAs;
        }

        return Task.FromResult<IEnumerable<MediaSourceInfo>>([clone]);
    }

    /// <summary>The source of a play of the HLS way made for this request and this stand-in (a copy), for Jellyfin to open; null otherwise.</summary>
    internal static MediaSourceInfo? HlsOf(PlayToken? requestPlay, Guid item) =>
        requestPlay is { Way: Way.Hls, Source: { } source } && requestPlay.Item == item ? RemotePlays.Clone(source) : null;

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
        // None: Jellyfin sorts a play's source, of the same ID when the
        // client names the one it was shown, before this saved one (its
        // VideoFile first).
        VideoType = null,
        Video3DFormat = StandInStreams.ThreeD(copy.ThreeD),
        MediaStreams = streams,
        HasSegments = hasSegments,
    };
}
