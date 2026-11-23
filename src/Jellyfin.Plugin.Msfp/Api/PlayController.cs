using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Viewing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Msfp;
using Msfp.Protocol;

namespace Jellyfin.Plugin.Msfp.Api;

/// <summary>
/// A paired server's copy as Jellyfin and its clients read it, behind a
/// play's token (<see cref="PlayToken"/>), never the item ID: the plain
/// file by byte ranges (the client playing it as it is, or Jellyfin's own
/// ffmpeg), each piece fetched by the engine from the paired server with
/// the playback's ticket, which never leaves the engine; the subtitle
/// tracks served apart, and the HLS way's playlists and segments, for this
/// machine only (Jellyfin reads them). No address, user agent or token is
/// logged.
/// </summary>
[ApiController]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public class PlayController : ControllerBase
{
    private readonly RemotePlays _plays;
    private readonly Federation.EngineHost _host;

    public PlayController(RemotePlays plays, Federation.EngineHost host)
    {
        _plays = plays;
        _host = host;
    }

    /// <summary>The plain file; HEAD is answered from the catalog (size, etag), starting no playback.</summary>
    [HttpGet("Msfp/File/{token}")]
    [HttpHead("Msfp/File/{token}")]
    public Task File(string token) => FileAsync(token, null);

    /// <summary>The file again under a name (f), or the span script of a title that is part of its copy.</summary>
    [HttpGet("Msfp/File/{token}/{name}")]
    [HttpHead("Msfp/File/{token}/{name}")]
    public Task Named(string token, string name) => FileAsync(token, name);

    private async Task FileAsync(string token, string? name)
    {
        var play = _plays.Tokens.Find(token);
        if (play is not { Way: Way.File } || _host.Engine is not { } engine || name is not (null or "f" or RemotePlays.SpanName))
        {
            Response.StatusCode = 404;
            return;
        }

        if (name == RemotePlays.SpanName)
        {
            if (!StandInStreams.Spanned(play.Copy))
            {
                Response.StatusCode = 404;
                return;
            }

            play.Touched = DateTimeOffset.UtcNow;
            play.FirstRead ??= play.Touched;
            var script = Encoding.UTF8.GetBytes(RemotePlays.SpanScript(play.Copy));
            Response.ContentType = "text/plain; charset=utf-8";
            Response.ContentLength = script.Length;
            Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsHead(Request.Method))
            {
                await Response.Body.WriteAsync(script, HttpContext.RequestAborted).ConfigureAwait(false);
            }

            return;
        }

        if (HttpMethods.IsHead(Request.Method))
        {
            Response.StatusCode = 200;
            Response.ContentType = Media.RelayFileType(play.Copy.MediaType);
            Response.ContentLength = play.Copy.Size;
            Response.Headers.AcceptRanges = "bytes";
            if (play.Copy.ETag.Length > 0)
            {
                Response.Headers.ETag = "\"" + play.Copy.ETag + "\"";
            }

            return;
        }

        var range = Request.Headers.Range.ToString();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var playback = await _plays.PlaybackAsync(play, HttpContext.RequestAborted).ConfigureAwait(false);
            if (playback is null)
            {
                // Refused or busy: remembered for the next PlaybackInfo.
                Response.StatusCode = 503;
                return;
            }

            try
            {
                await engine.FetchAsync(playback.Id, new MediaPath { Kind = MediaKind.File }, range, new FederationController.Sink(Response), HttpContext.RequestAborted).ConfigureAwait(false);
                play.Touched = DateTimeOffset.UtcNow;
                return;
            }
            catch (MsfpException e) when (!Response.HasStarted && e.Code == MsfpError.NotFound && attempt == 0)
            {
                // The playback ended (a long pause, the paired server's
                // side): started again on the same etag.
                _plays.Ended(play, playback);
            }
            catch (MsfpException e) when (!Response.HasStarted)
            {
                Response.StatusCode = Status(e.Code);
                return;
            }
        }

        Response.StatusCode = 404;
    }

    /// <summary>A subtitle track served apart, whole: text as WebVTT, pictures as PGS. For this machine only (Jellyfin reads it).</summary>
    [HttpGet("Msfp/Subtitle/{token}/{track}")]
    public async Task Subtitle(string token, string track)
    {
        var dot = track.LastIndexOf('.');
        var id = dot > 0 ? track[..dot] : track;
        var pictures = dot > 0 && track[(dot + 1)..] == "sup";
        if (!Loopback() || _plays.Tokens.Find(token) is not { } play || _host.Engine is not { } engine
            || !global::Msfp.Protocol.Playback.TryTrackId(id, 's', out _)
            || await _plays.PlaybackForSubtitlesAsync(play, HttpContext.RequestAborted).ConfigureAwait(false) is not { } playback)
        {
            Response.StatusCode = 404;
            return;
        }

        var key = (pictures ? "pictures/" : "subtitles/") + id;
        var whole = await play.Subtitles.GetOrAdd(key, _ => new Lazy<Task<byte[]?>>(() =>
            Subtitles.WholeAsync(engine, playback.Id, id, pictures, play.Copy.DurationMs, CancellationToken.None))).Value.ConfigureAwait(false);
        if (whole is null)
        {
            play.Subtitles.TryRemove(key, out _);
            Response.StatusCode = 404;
            return;
        }

        Response.ContentType = pictures ? "application/octet-stream" : "text/vtt; charset=utf-8";
        Response.Headers.CacheControl = "no-store";
        Response.ContentLength = whole.Length;
        await Response.Body.WriteAsync(whole, HttpContext.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The HLS way's playlists and segments. For this machine only (Jellyfin's own ffmpeg).</summary>
    [HttpGet("Msfp/Relay/{token}/{**rest}")]
    public async Task Relay(string token, string rest)
    {
        if (!Loopback() || _plays.Tokens.Find(token) is not { Way: Way.Hls, Playback: { } playback } play || _host.Engine is not { } engine
            || !MediaPath.TryParse(rest ?? string.Empty, out var path))
        {
            Response.StatusCode = 404;
            return;
        }

        play.Touched = DateTimeOffset.UtcNow;
        try
        {
            await engine.FetchAsync(playback.Id, path, Request.Headers.Range.ToString(), new FederationController.Sink(Response), HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (MsfpException e) when (!Response.HasStarted)
        {
            Response.StatusCode = Status(e.Code);
        }
    }

    private bool Loopback() => HttpContext.Connection.RemoteIpAddress is { } from && System.Net.IPAddress.IsLoopback(from);

    private static int Status(MsfpError code) => code switch
    {
        MsfpError.RangeNotSatisfiable => 416,
        MsfpError.ServerBusy => 503,
        _ => 404,
    };
}
