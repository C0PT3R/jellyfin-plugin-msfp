using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
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
/// </summary>
public sealed class AudioSwitchFilter : IAsyncActionFilter
{
    /// <summary>Where the request carries the picked track to <see cref="PairedSources"/>.</summary>
    internal const string ItemKey = "msfp-audio-switch";

    private readonly RemotePlays _plays;
    private readonly IMediaSourceManager _sources;
    private readonly ILogger<AudioSwitchFilter> _log;

    public AudioSwitchFilter(RemotePlays plays, IMediaSourceManager sources, ILogger<AudioSwitchFilter> log)
    {
        _plays = plays;
        _sources = sources;
        _log = log;
    }

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "MediaInfo", ActionName: "GetPostedPlaybackInfo" })
        {
            await SwitchAsync(context).ConfigureAwait(false);
        }

        await next().ConfigureAwait(false);
    }

    private async Task SwitchAsync(ActionExecutingContext context)
    {
        // Jellyfin takes these from the query first, then the body.
        var args = context.ActionArguments;
        args.TryGetValue("playbackInfoDto", out var dto);
        var liveStreamId = Arg<string>(args, "liveStreamId") ?? Member<string>(dto, "LiveStreamId");
        var audioIndex = Arg<int?>(args, "audioStreamIndex") ?? Member<int?>(dto, "AudioStreamIndex");
        if (string.IsNullOrEmpty(liveStreamId) || audioIndex is not { } index
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
