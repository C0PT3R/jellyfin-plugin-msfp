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
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _http;
    private readonly IUserManager _users;

    public PairedSources(ILibraryManager library, RemotePlays plays, Microsoft.AspNetCore.Http.IHttpContextAccessor http, IUserManager users)
    {
        _library = library;
        _plays = plays;
        _http = http;
        _users = users;
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

        var audioSwitch = _http.HttpContext?.Items[AudioSwitchFilter.ItemKey] as RemotePlays.AudioSwitch;
        return await _plays.OpenAsync(item, Wishes(), cancellationToken, audioSwitch).ConfigureAwait(false);
    }

    // The viewer's audio and subtitle languages, from the Jellyfin user
    // pressing Play (the request opening the source): what a paired server
    // converting the stream needs to send the right audio track.
    private (string Audio, string Subtitles) Wishes()
    {
        var claim = _http.HttpContext?.User.FindFirst("Jellyfin-UserId")?.Value;
        if (!Guid.TryParse(claim, out var id) || _users.GetUserById(id) is not { } user)
        {
            return (string.Empty, string.Empty);
        }

        var showSubtitles = user.SubtitleMode is not (Jellyfin.Database.Implementations.Enums.SubtitlePlaybackMode.None);
        return (user.AudioLanguagePreference ?? string.Empty, showSubtitles ? user.SubtitleLanguagePreference ?? string.Empty : string.Empty);
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
        // Not remote: Jellyfin lists the trickplay of local sources only (a
        // paired server's previews, PeerPreviews); opened at play anyway.
        IsRemote = false,
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
