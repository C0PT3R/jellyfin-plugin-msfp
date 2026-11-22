using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Msfp.Viewing;

/// <summary>
/// Another audio track picked while a converted stream plays. Players ask
/// Jellyfin for playback info again with the track and the live stream
/// already open, which Jellyfin reuses as it is; a converted stream holds
/// one audio track only. For a stand-in's live stream and a track it
/// doesn't carry, this closes it (MSFP stop) and has Jellyfin open the
/// source again, which plays the copy with that track (MSFP play, video
/// section 5.1; playback section 2.3: a new playback for another track).
/// Every other request goes through untouched.
///
/// It also chooses, in Jellyfin's answer, the subtitles a stand-in starts
/// with as for a local file (<see cref="EmbeddedLike"/>).
/// </summary>
public sealed class AudioSwitchFilter : IAsyncActionFilter
{
    /// <summary>Where the request carries the picked track to <see cref="PairedSources"/>.</summary>
    internal const string ItemKey = "msfp-audio-switch";

    private readonly RemotePlays _plays;
    private readonly IMediaSourceManager _sources;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly ILogger<AudioSwitchFilter> _log;

    public AudioSwitchFilter(RemotePlays plays, IMediaSourceManager sources, ILibraryManager library, IUserManager users, IUserDataManager userData, ILogger<AudioSwitchFilter> log)
    {
        _plays = plays;
        _sources = sources;
        _library = library;
        _users = users;
        _userData = userData;
        _log = log;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "MediaInfo", ActionName: "GetPostedPlaybackInfo" })
        {
            await SwitchAsync(context).ConfigureAwait(false);
            var done = await next().ConfigureAwait(false);
            Subtitles(context, done);
            return;
        }

        await next().ConfigureAwait(false);
    }

    // The subtitles a stand-in starts with. Its paired server's tracks are
    // external to Jellyfin (it reads them from the plugin), and in the
    // "Default" subtitle mode Jellyfin turns any external track on, taking
    // it for a file put beside the video on purpose; a local file's own
    // tracks stay off unless the file marks one default or forced. The
    // choice is made again as for such tracks, unless the user's choice
    // for that title is remembered.
    private void Subtitles(ActionExecutingContext context, ActionExecutedContext done)
    {
        if (done.Result is not ObjectResult { Value: PlaybackInfoResponse answer }
            || Arg<Guid?>(context.ActionArguments, "itemId") is not { } itemId
            || _library.GetItemById(itemId) is not { } item || !PairedSources.IsStandIn(item))
        {
            return;
        }

        // A video bitrate the file doesn't state: none in the answer, as for
        // a local file (players count their quality choices from it).
        foreach (var source in answer.MediaSources ?? [])
        {
            if (!string.IsNullOrEmpty(source.LiveStreamId) && _plays.ByLiveStreamId(source.LiveStreamId) is { VideoBitrateFilled: true }
                && source.MediaStreams?.FirstOrDefault(st => st.Type == MediaStreamType.Video) is { } video)
            {
                video.BitRate = null;
            }
        }

        var claim = context.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value;
        if (!Guid.TryParse(claim, out var userId) || _users.GetUserById(userId) is not { } user
            || user.SubtitleMode != SubtitlePlaybackMode.Default
            || (user.RememberSubtitleSelections && _userData.GetUserData(user, item)?.SubtitleStreamIndex is not null))
        {
            return;
        }

        foreach (var source in answer.MediaSources ?? [])
        {
            if (source.MediaStreams is { } streams && streams.Any(st => st.Type == MediaStreamType.Subtitle))
            {
                source.DefaultSubtitleStreamIndex = EmbeddedLike(streams, user.SubtitleLanguagePreference);
            }
        }
    }

    /// <summary>
    /// Jellyfin's "Default" subtitle choice for tracks inside the file
    /// (Emby.Server.Implementations' MediaStreamSelector, without its
    /// preference for external tracks): the first track marked default or
    /// forced, default ones first, then those in the user's language; -1
    /// for none.
    /// </summary>
    internal static int EmbeddedLike(System.Collections.Generic.IEnumerable<MediaStream> streams, string? preferred)
    {
        bool Preferred(MediaStream st) =>
            !string.IsNullOrEmpty(preferred) && string.Equals(st.Language, preferred, StringComparison.OrdinalIgnoreCase);
        var pick = streams
            .Where(st => st.Type == MediaStreamType.Subtitle && (st.IsDefault || st.IsForced))
            .OrderByDescending(st => st.IsDefault)
            .ThenByDescending(st => !st.IsForced && Preferred(st))
            .ThenByDescending(st => st.IsForced && Preferred(st))
            .ThenByDescending(st => st.IsForced && (string.IsNullOrEmpty(st.Language) || st.Language == "und"))
            .ThenByDescending(st => st.IsForced)
            .FirstOrDefault();
        return pick?.Index ?? -1;
    }

    private async Task SwitchAsync(ActionExecutingContext context)
    {
        // Jellyfin takes these from the query first, then the body.
        var args = context.ActionArguments;
        args.TryGetValue("playbackInfoDto", out var dto);
        var liveStreamId = Arg<string>(args, "liveStreamId") ?? Member<string>(dto, "LiveStreamId");
        var audioIndex = Arg<int?>(args, "audioStreamIndex") ?? Member<int?>(dto, "AudioStreamIndex");
        if (string.IsNullOrEmpty(liveStreamId))
        {
            Picked(context, audioIndex);
            return;
        }

        if (audioIndex is not { } index
            || _plays.ByLiveStreamId(liveStreamId) is not { } live
            || !live.AudioByIndex.TryGetValue(index, out var track) || track.Id == live.Playback.AudioTrack)
        {
            return;
        }

        _log.LogInformation("MSFP: audio track {Track} picked while playing {Title}: played again with it", track.Id, live.Playback.Title);
        context.HttpContext.Items[ItemKey] = new RemotePlays.AudioSwitch(live.Playback.Peer, track.Id, track.Language ?? string.Empty);
        await _sources.CloseLiveStream(liveStreamId).ConfigureAwait(false);
        args["liveStreamId"] = null;
        args["autoOpenLiveStream"] = true;
        SetMember(dto, "LiveStreamId", null);
        SetMember(dto, "AutoOpenLiveStream", true);
    }

    // A track picked before Play (a client's audio menu on the title): its
    // language, from the stand-in's saved streams, for the paired server
    // to send. Without it, the user's audio language is asked.
    private void Picked(ActionExecutingContext context, int? audioIndex)
    {
        if (audioIndex is not { } index || Arg<Guid?>(context.ActionArguments, "itemId") is not { } itemId
            || _library.GetItemById(itemId) is not { } item || !PairedSources.IsStandIn(item))
        {
            return;
        }

        var stream = _sources.GetMediaStreams(itemId).FirstOrDefault(s => s.Index == index && s.Type == MediaStreamType.Audio);
        if (stream?.Language is { Length: > 0 } language)
        {
            context.HttpContext.Items[ItemKey] = new RemotePlays.AudioSwitch(string.Empty, string.Empty, language);
        }
    }

    private static T? Arg<T>(System.Collections.Generic.IDictionary<string, object?> args, string name) =>
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

/// <summary>Adds <see cref="AudioSwitchFilter"/> to Jellyfin's API.</summary>
public sealed class AudioSwitchFilterSetup : IConfigureOptions<MvcOptions>
{
    /// <inheritdoc />
    public void Configure(MvcOptions options) => options.Filters.Add<AudioSwitchFilter>();
}
