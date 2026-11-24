using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Msfp;
using Msfp.Protocol;
using PlayRequest = Msfp.PlayRequest;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// The hand-over of a paired server's title to Jellyfin, at PlaybackInfo
/// (migration plan 4.2): everything that can fail is decided here, because
/// after it Jellyfin has handed the client its source. The paired server
/// holding the copy is chosen (the best copy first, or the one picked, on a
/// server with a stream free by its limits and this server's count; all
/// busy: Jellyfin's own "too many streams" answer), and the way: the copy
/// read as a plain file behind a per-play token (<see cref="PlayToken"/>),
/// which Jellyfin's decision engine judges as a local file, or the paired
/// server's HLS as Jellyfin's live stream where a plain file can't be had
/// (a copy of several parts, a server that refused delivery file before)
/// or shouldn't be (its bitrate above the pairing's cap: the paired server
/// converts it, decision 7).
/// </summary>
internal static class HandOver
{
    /// <summary>Why no play could be made.</summary>
    internal enum Failure
    {
        None,

        /// <summary>No paired server holds the title now.</summary>
        NotHeld,

        /// <summary>Every paired server holding it is busy (by its limits, our count, or a recent refusal as busy).</summary>
        Busy,
    }

    /// <summary>A choice: the holder, the copy, the way, and for HLS what to ask.</summary>
    internal sealed record Choice(Holder Holder, Copy Copy, Way Way, PlayRequest? Asked, Failure Failure = Failure.None)
    {
        public static Choice Failed(Failure f) => new(null!, null!, Way.File, null, f);
    }

    /// <summary>What is known of a paired server for a choice.</summary>
    /// <param name="Free">A stream is free on it: its limits and our count of plays on it, and no recent busy answer.</param>
    /// <param name="RefusedFile">It refused delivery file since its catalog was last read.</param>
    /// <param name="CapKbps">The pairing's bitrate cap (its limits for us); 0 for none.</param>
    internal sealed record PeerState(bool Free, bool RefusedFile, int CapKbps);

    /// <summary>
    /// The play to make of a title: the holder chosen for the stand-in (its
    /// copy <paramref name="picked"/>, a source the client picked, else its
    /// best) when a stream is free on it; else the next holder with one
    /// free, its best copy. The way follows the copy and the server.
    /// </summary>
    /// <param name="title">The title's holders, best first.</param>
    /// <param name="picked">The copy picked (its ID on the stand-in's holder), null for none.</param>
    /// <param name="state">What is known of each holder.</param>
    /// <param name="audioTrack">The audio track asked, for HLS ("" for the copy's default).</param>
    /// <param name="audioPlayed">Whether the client plays an audio codec as it is (HLS: copied, else converted).</param>
    internal static Choice Choose(Playable title, string? picked, Func<string, PeerState> state, string audioTrack = "", Func<string, bool>? audioPlayed = null)
    {
        if (title.Holders.Count == 0)
        {
            return Choice.Failed(Failure.NotHeld);
        }

        foreach (var holder in title.Holders)
        {
            var s = state(holder.Peer);
            if (!s.Free)
            {
                continue;
            }

            var copy = holder == title.Chosen && picked is not null ? holder.Copies.FirstOrDefault(c => c.Id == picked) ?? holder.Best : holder.Best;
            var way = WayOf(copy, s);
            return new Choice(holder, copy, way, way == Way.Hls ? HlsAsk(holder.Key, copy, s.CapKbps, s.RefusedFile, audioTrack, audioPlayed ?? (_ => false)) : null);
        }

        return Choice.Failed(Failure.Busy);
    }

    /// <summary>
    /// The way a copy plays from a server: a plain file, unless the copy has
    /// several parts, the server refused delivery file since its last
    /// reading, or the copy's bitrate is above the pairing's cap (then the
    /// server converts it, answer 7).
    /// </summary>
    internal static Way WayOf(Copy copy, PeerState s) =>
        copy.Parts is > 1 || s.RefusedFile || OverCap(copy, s.CapKbps) ? Way.Hls : Way.File;

    /// <summary>Whether a copy's bitrate (size × 8 / duration) is above a cap in kbit/s (0: none).</summary>
    internal static bool OverCap(Copy copy, int capKbps) =>
        capKbps > 0 && StandInStreams.Bitrate(copy) is { } bps && bps > (long)capKbps * 1000;

    /// <summary>
    /// What the paired server is asked for the HLS way: within the cap, a
    /// remux (MPEG-TS for the codecs it carries, else fMP4); above it, a
    /// conversion within the cap: H.264 (the baseline's), the audio copied
    /// when the client plays it, else AAC in two channels.
    /// </summary>
    internal static PlayRequest HlsAsk(string key, Copy copy, int capKbps, bool refusedFile, string audioTrack, Func<string, bool> audioPlayed)
    {
        _ = refusedFile;
        var audio = copy.AudioTracks.FirstOrDefault(a => a.Id == audioTrack) ?? copy.AudioTracks.FirstOrDefault(a => a.Default) ?? copy.AudioTracks.FirstOrDefault();
        var request = new PlayRequest { Title = key, VersionId = copy.Id, AudioTrack = audio?.Id ?? string.Empty, StartMs = StandInStreams.Span(copy).StartMs };
        if (!OverCap(copy, capKbps))
        {
            var ts = copy.Video?.Codec is null or "h264" or "hevc" or "mpeg2video";
            return request with { Delivery = global::Msfp.Protocol.Playback.DeliveryRemux, MediaType = ts ? "video/mp2t" : "video/mp4" };
        }

        var copyAudio = audio is not null && audioPlayed(audio.Codec);
        return request with
        {
            Delivery = global::Msfp.Protocol.Playback.DeliveryTranscode,
            Transcode = new TranscodeTargets
            {
                VideoCodec = "h264",
                MaxBitrateKbps = capKbps,
                AudioCodec = copyAudio ? global::Msfp.Protocol.Names.Copy : "aac",
                AudioChannels = copyAudio ? 0 : 2,
            },
        };
    }

    /// <summary>The next step down when a paired server refuses an HLS delivery: a remux becomes a conversion, a conversion the baseline; null after it.</summary>
    internal static PlayRequest? Lower(PlayRequest asked) => asked.Delivery switch
    {
        global::Msfp.Protocol.Playback.DeliveryRemux => asked with
        {
            Delivery = global::Msfp.Protocol.Playback.DeliveryTranscode,
            MediaType = string.Empty,
            Transcode = new TranscodeTargets { VideoCodec = "h264", AudioCodec = "aac", AudioChannels = 2 },
        },
        global::Msfp.Protocol.Playback.DeliveryTranscode when asked.Transcode is { } t && (t.VideoCodec.Length > 0 || t.AudioCodec.Length > 0) =>
            asked with { Delivery = string.Empty, Transcode = null, MaxBitrateKbps = t.MaxBitrateKbps },
        _ => null,
    };

    /// <summary>The audio codecs a device profile plays inside a video (its direct-play profiles' audio codecs, Jellyfin's names).</summary>
    internal static Func<string, bool> AudioPlayedBy(DeviceProfile? profile)
    {
        var codecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profile?.DirectPlayProfiles ?? [])
        {
            if (p.Type == DlnaProfileType.Video)
            {
                foreach (var c in (p.AudioCodec ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    codecs.Add(c);
                }
            }
        }

        // An MSFP codec token as Jellyfin names it.
        return codec => codecs.Contains(codec) || codecs.Contains(codec switch { "ac-3" => "ac3", "e-ac-3" => "eac3", "dts" => "dca", _ => codec });
    }
}

/// <summary>
/// The PlaybackInfo filter of the hand-over (<see cref="HandOver"/>): on a
/// stand-in, the user's access checked (libraries, parental rating), the
/// holder and the way chosen, and a play made (<see cref="PlayToken"/>);
/// Jellyfin then answers with that play's source only, its path at the
/// request's own address. Every other request goes through untouched.
/// </summary>
public sealed class HandOverFilter : IAsyncActionFilter
{
    /// <summary>Where the request carries its play to <see cref="PairedSources"/>.</summary>
    internal const string ItemKey = "msfp-play";

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IMediaSourceManager _sources;
    private readonly RemotePlays _plays;
    private readonly ILogger<HandOverFilter> _log;

    public HandOverFilter(ILibraryManager library, IUserManager users, IMediaSourceManager sources, RemotePlays plays, ILogger<HandOverFilter> log)
    {
        _library = library;
        _users = users;
        _sources = sources;
        _plays = plays;
        _log = log;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var action = context.ActionDescriptor as ControllerActionDescriptor;
        var universal = action is { ControllerName: "UniversalAudio", ActionName: "GetUniversalAudioStream" };
        if (action is null || (!universal && action is not { ControllerName: "MediaInfo", ActionName: "GetPostedPlaybackInfo" or "GetPlaybackInfo" })
            || Arg<Guid?>(context.ActionArguments, "itemId") is not { } itemId || itemId == Guid.Empty
            || _library.GetItemById(itemId) is not { } item || !PairedSources.IsStandIn(item))
        {
            await next().ConfigureAwait(false);
            return;
        }

        if (universal)
        {
            await UniversalAsync(context, next, item).ConfigureAwait(false);
            return;
        }

        var claim = context.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value;
        if (!Guid.TryParse(claim, out var userId) || _users.GetUserById(userId) is not { } user || !Allowed(item, user))
        {
            _log.LogInformation("MSFP: a paired server's title asked without access to it");
            context.Result = Refused(PlaybackErrorCode.NotAllowed);
            return;
        }

        // Jellyfin takes these from the query first, then the body.
        var args = context.ActionArguments;
        args.TryGetValue("playbackInfoDto", out var dto);
        var sourceId = Arg<string>(args, "mediaSourceId") ?? Member<string>(dto, "MediaSourceId");
        var audioIndex = Arg<int?>(args, "audioStreamIndex") ?? Member<int?>(dto, "AudioStreamIndex");
        var liveStreamId = Arg<string>(args, "liveStreamId") ?? Member<string>(dto, "LiveStreamId");

        // The play this user is on (a seek or a track picked in a
        // conversion, another PlaybackInfo of the same play, by its handle
        // or the ID its client was shown from this device): the same.
        var device = context.HttpContext.User.FindFirst("Jellyfin-DeviceId")?.Value;
        if ((_plays.Tokens.OfHandle(sourceId, user.Id, item.Id) ?? _plays.Tokens.OfShown(sourceId, user.Id, item.Id, device)) is { Way: Way.File } same)
        {
            await PlainAsync(context, next, same, args, dto).ConfigureAwait(false);
            return;
        }

        // A live stream of the HLS way: kept while its audio track is the
        // one asked; else closed, and a new playback made with that track.
        string replaces = string.Empty;
        if (!string.IsNullOrEmpty(liveStreamId) && _plays.ByLiveStreamId(liveStreamId) is { } live)
        {
            var asked = audioIndex is { } ai && live.Tracks.TryGetValue(ai, out var track) && track.StartsWith('a') ? track : null;
            if (asked is null || asked == live.Playback?.AudioTrack)
            {
                await next().ConfigureAwait(false);
                return;
            }

            _log.LogInformation("MSFP: audio track {Track} picked while a paired server converts {Key}: played again with it", asked, live.Key);
            replaces = live.Playback?.Id ?? string.Empty;
            await _sources.CloseLiveStream(liveStreamId).ConfigureAwait(false);
            args["liveStreamId"] = null;
            SetMember(dto, "LiveStreamId", null);
            sourceId = null;
        }

        var title = await _plays.TitleAsync(item, CancellationToken.None).ConfigureAwait(false);
        if (title is null)
        {
            context.Result = Refused(PlaybackErrorCode.NoCompatibleStream);
            return;
        }

        var picked = _plays.PickedCopy(item, title, sourceId);
        var tracks = _plays.TracksOf(item, title, picked);
        var audioTrack = audioIndex is { } index && tracks.TryGetValue(index, out var t) && t.StartsWith('a') ? t : string.Empty;
        var profile = Member<DeviceProfile>(dto, "DeviceProfile");
        var states = await _plays.PeerStatesAsync(CancellationToken.None).ConfigureAwait(false);
        var choice = HandOver.Choose(title, picked, p => states.TryGetValue(p, out var s) ? s : new HandOver.PeerState(true, false, 0), audioTrack, HandOver.AudioPlayedBy(profile));
        switch (choice.Failure)
        {
            case HandOver.Failure.NotHeld:
                context.Result = Refused(PlaybackErrorCode.NoCompatibleStream);
                return;
            case HandOver.Failure.Busy:
                _log.LogInformation("MSFP: every paired server holding {Path} is busy now", item.Path);
                context.Result = Refused(PlaybackErrorCode.RateLimitExceeded);
                return;
        }

        if (choice.Way == Way.File)
        {
            var play = _plays.Mint(user.Id, item, choice);
            play.Device = device;
            play.Shown = title.Chosen == choice.Holder ? _plays.SavedSourceId(item, title, choice) : null;
            _log.LogInformation("MSFP: {Key} handed over as a file from {Peer}", choice.Holder.Key, choice.Holder.Peer);
            await PlainAsync(context, next, play, args, dto).ConfigureAwait(false);
            return;
        }

        // The HLS way: Jellyfin opens the play's source (a live stream,
        // PairedSources gives it under the play's handle) within this
        // request.
        var hls = _plays.Mint(user.Id, item, choice with { Asked = choice.Asked! with { Replaces = replaces } });
        _log.LogInformation("MSFP: {Key} handed over as HLS from {Peer} ({Delivery})", choice.Holder.Key, choice.Holder.Peer, hls.Asked?.Delivery);
        context.HttpContext.Items[ItemKey] = hls;
        args["mediaSourceId"] = hls.Handle;
        SetMember(dto, "MediaSourceId", hls.Handle);
        await next().ConfigureAwait(false);
    }

    // Jellyfin's universal audio route (jellyfin-web's audio player: a
    // paired server's theme song), which gets its sources without the
    // PlaybackInfo action: a play of the plain file made here as there, its
    // handle the route's source, so that Jellyfin streams or converts it as a
    // local file. Its HLS way isn't offered on this route: busy or refused
    // answer 404.
    private async Task UniversalAsync(ActionExecutingContext context, ActionExecutionDelegate next, MediaBrowser.Controller.Entities.BaseItem item)
    {
        var claim = context.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value;
        if (!Guid.TryParse(claim, out var userId) || _users.GetUserById(userId) is not { } user || !Allowed(item, user)
            || await _plays.TitleAsync(item, CancellationToken.None).ConfigureAwait(false) is not { } title)
        {
            context.Result = new NotFoundResult();
            return;
        }

        // The same device asking again before reading (a HEAD, then the GET): the same play.
        var device = context.HttpContext.User.FindFirst("Jellyfin-DeviceId")?.Value ?? string.Empty;
        var play = _plays.Tokens.All.Where(t => t.User == user.Id && t.Item == item.Id && t.Way == Way.File && t.FirstRead is null && (t.Device ?? string.Empty) == device)
            .OrderByDescending(t => t.Made).FirstOrDefault();
        if (play is null)
        {
            var states = await _plays.PeerStatesAsync(CancellationToken.None).ConfigureAwait(false);
            var choice = HandOver.Choose(title, null, p => states.TryGetValue(p, out var s) ? s : new HandOver.PeerState(true, false, 0));
            if (choice.Failure != HandOver.Failure.None || choice.Way != Way.File)
            {
                _log.LogInformation("MSFP: {Path} can't be played as a file now", item.Path);
                context.Result = new NotFoundResult();
                return;
            }

            play = _plays.Mint(user.Id, item, choice);
            play.Device = device;
            _log.LogInformation("MSFP: {Key} handed over as a file from {Peer} (universal audio)", choice.Holder.Key, choice.Holder.Peer);
        }

        context.HttpContext.Items[ItemKey] = play;
        context.ActionArguments["mediaSourceId"] = play.Handle;

        // When Jellyfin converts, its playlist's URLs repeat this request's
        // query: the handle goes in it, so that its segments find the play.
        var request = context.HttpContext.Request;
        if (!request.Query.ContainsKey("mediaSourceId"))
        {
            request.QueryString = request.QueryString.Add("mediaSourceId", play.Handle);
        }

        await next().ConfigureAwait(false);
    }

    // The user may see the stand-in: its parental rating and tags, and its
    // library (as Jellyfin's own checks, which earlier versions' channel
    // items passed by the channel's access alone); an extra, as the title it
    // belongs to.
    private bool Allowed(MediaBrowser.Controller.Entities.BaseItem item, Jellyfin.Database.Implementations.Entities.User user)
    {
        var title = !item.OwnerId.Equals(Guid.Empty) ? _library.GetItemById(item.OwnerId) : item;
        return title is not null && item.IsVisibleStandalone(user) && title.IsVisibleStandalone(user)
            && _library.GetCollectionFolders(title) is { Count: > 0 } folders && folders.Any(f => f.IsVisible(user));
    }

    // The plain file: Jellyfin's answer keeps only the play's source, its
    // path the request's own address (Jellyfin's own reads take the
    // loopback one, PairedSources).
    private async Task PlainAsync(ActionExecutingContext context, ActionExecutionDelegate next, PlayToken play, IDictionary<string, object?> args, object? dto)
    {
        context.HttpContext.Items[ItemKey] = play;
        args["mediaSourceId"] = play.Handle;
        SetMember(dto, "MediaSourceId", play.Handle);
        args["liveStreamId"] = null;
        SetMember(dto, "LiveStreamId", null);
        var done = await next().ConfigureAwait(false);
        if (done.Result is not ObjectResult { Value: PlaybackInfoResponse answer })
        {
            return;
        }

        var r = context.HttpContext.Request;
        var address = r.Scheme + "://" + r.Host + r.PathBase + RemotePlays.FilePath(play);
        answer.MediaSources = answer.MediaSources.Where(s => string.Equals(s.Id, play.Handle, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var s in answer.MediaSources)
        {
            s.Path = address;

            // The client sees the stand-in's source for the copy (its
            // trickplay, its version), Jellyfin's own URLs in the answer
            // keep the handle.
            if (play.Shown is { } shown)
            {
                s.Id = shown;
            }
        }

        if (answer.MediaSources.Count == 0 && answer.ErrorCode is null)
        {
            answer.ErrorCode = PlaybackErrorCode.NoCompatibleStream;
        }
    }

    private static OkObjectResult Refused(PlaybackErrorCode code) => new(new PlaybackInfoResponse { MediaSources = [], ErrorCode = code });

    private static T? Arg<T>(IDictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out var v) && v is T t ? t : default;

    // PlaybackInfoDto is Jellyfin's API's own type, which plugins can't
    // reference: its members by name.
    private static T? Member<T>(object? o, string name) =>
        o?.GetType().GetProperty(name)?.GetValue(o) is T t ? t : default;

    private static void SetMember(object? o, string name, object? value)
    {
        if (o?.GetType().GetProperty(name) is { CanWrite: true } p)
        {
            p.SetValue(o, value);
        }
    }
}

/// <summary>Adds <see cref="HandOverFilter"/> to Jellyfin's API.</summary>
public sealed class HandOverFilterSetup : IConfigureOptions<MvcOptions>
{
    /// <inheritdoc />
    public void Configure(MvcOptions options) => options.Filters.Add<HandOverFilter>();
}
