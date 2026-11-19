using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Viewing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Api;

/// <summary>
/// A paired server's playback as Jellyfin reads it: each piece fetched by
/// the engine from the paired server, with the playback's ticket, which
/// never leaves the engine. Only for this machine (Jellyfin's own ffmpeg),
/// and only with the playback's random token.
/// </summary>
[ApiController]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public class RelayController : ControllerBase
{
    private readonly RemotePlays _plays;
    private readonly Federation.EngineHost _host;

    public RelayController(RemotePlays plays, Federation.EngineHost host)
    {
        _plays = plays;
        _host = host;
    }

    [HttpGet("Msfp/Relay/{token}/{**rest}")]
    public async Task Get(string token, string rest)
    {
        if (HttpContext.Connection.RemoteIpAddress is not { } from || !System.Net.IPAddress.IsLoopback(from)
            || _plays.ByToken(token) is not { } live || _host.Engine is not { } engine || !MediaPath.TryParse(rest ?? string.Empty, out var path))
        {
            Response.StatusCode = 404;
            return;
        }

        try
        {
            await engine.FetchAsync(live.Playback.Id, path, Request.Headers.Range.ToString(), new FederationController.Sink(Response), HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (MsfpException e) when (!Response.HasStarted)
        {
            Response.StatusCode = e.Code switch
            {
                MsfpError.RangeNotSatisfiable => 416,
                MsfpError.ServerBusy => 503,
                _ => 404,
            };
        }
    }
}
