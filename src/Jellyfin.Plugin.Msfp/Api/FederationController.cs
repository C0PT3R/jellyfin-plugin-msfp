using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Msfp;
using Msfp.Protocol;

namespace Jellyfin.Plugin.Msfp.Api;

/// <summary>
/// The engine's routes on Jellyfin's: the federation calls other servers
/// make (/api/v1/federation/), the media of their playbacks here
/// (/api/v1/stream/), and the discovery document (/.well-known/msfp). Every
/// method and every path under them goes to the engine, which answers what
/// MSFP says; what it refuses to strangers is Jellyfin's own 404 for a path
/// that doesn't exist (an empty body, no type), so a prober learns nothing
/// (MSFP core section 14).
/// </summary>
[ApiController]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public class FederationController : ControllerBase
{
    private readonly EngineHost _host;

    public FederationController(EngineHost host)
    {
        _host = host;
    }

    /// <summary>A federation call (MSFP core section 9).</summary>
    [AcceptVerbs("GET", "HEAD", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")]
    [Route("api/v1/federation/{**rest}")]
    public async Task Federation()
    {
        NoCompression();
        if (_host.Engine is not { } engine)
        {
            NotFound404();
            return;
        }

        var res = await engine.HandleFederationAsync(ToMsfp(Request.Body), HttpContext.RequestAborted).ConfigureAwait(false);
        await WriteAsync(res).ConfigureAwait(false);
    }

    /// <summary>The media of a peer's playback here, by its ticket (MSFP media).</summary>
    [AcceptVerbs("GET", "HEAD", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")]
    [Route("api/v1/stream/{**rest}")]
    public async Task Stream()
    {
        NoCompression();
        if (_host.Engine is not { } engine)
        {
            NotFound404();
            return;
        }

        var outcome = await engine.HandleStreamAsync(ToMsfp(System.IO.Stream.Null), new Sink(Response), HttpContext.RequestAborted).ConfigureAwait(false);
        if (outcome != StreamOutcome.Served && !Response.HasStarted)
        {
            // Not a ticket of a peer's playback here (Fallback: one of this
            // server's own viewers', which it has none of), or refused.
            NotFound404();
        }
    }

    /// <summary>The discovery document (MSFP core section 11), when the operator serves it.</summary>
    [AcceptVerbs("GET", "HEAD", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")]
    [Route(".well-known/msfp")]
    public async Task Discovery()
    {
        NoCompression();
        if (_host.Engine is not { } engine)
        {
            NotFound404();
            return;
        }

        await WriteAsync(engine.HandleDiscovery(ToMsfp(System.IO.Stream.Null))).ConfigureAwait(false);
    }

    private MsfpRequest ToMsfp(Stream body)
    {
        // The target as it was sent: the signature covers it so.
        var target = HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(target))
        {
            target = Request.PathBase.ToString() + Request.Path.ToString() + Request.QueryString.ToString();
        }

        return new MsfpRequest
        {
            Method = Request.Method,
            Target = target,
            Headers = new Headers(Request.Headers),
            Body = body,
            // Behind a reverse proxy, the proxy's address, unless Jellyfin's
            // known proxies are set (its forwarded headers then give the
            // client's).
            ClientAddress = HttpContext.Connection.RemoteIpAddress,
        };
    }

    private async Task WriteAsync(MsfpResponse res)
    {
        if (res.PlainNotFound)
        {
            NotFound404();
            return;
        }

        Response.StatusCode = res.Status;
        foreach (var (name, value) in res.Headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                Response.ContentType = value;
            }
            else
            {
                Response.Headers.Append(name, value);
            }
        }

        Response.Headers.CacheControl = "no-store";
        Response.ContentLength = res.Body.Length;
        if (res.Body.Length > 0 && !HttpMethods.IsHead(Request.Method))
        {
            await Response.Body.WriteAsync(res.Body, HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    // Jellyfin's 404 for a path that doesn't exist: no body, no type.
    private void NotFound404()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        Response.ContentLength = 0;
    }

    // Answers go out as the bytes signed, never content-coded: Jellyfin's
    // response compression would otherwise gzip them for a caller that
    // accepts gzip. The compression middleware reads Accept-Encoding when
    // the answer starts, so removing it here is enough; the feature covers
    // HTTPS too.
    private void NoCompression()
    {
        Request.Headers.Remove(HeaderNames.AcceptEncoding);
        if (HttpContext.Features.Get<IHttpsCompressionFeature>() is { } compression)
        {
            compression.Mode = HttpsCompressionMode.DoNotCompress;
        }
    }

    /// <summary>ASP.NET Core's request headers, read as MSFP reads headers.</summary>
    private sealed class Headers(IHeaderDictionary headers) : IHeaderReader
    {
        public System.Collections.Generic.IReadOnlyList<string> GetValues(string name) =>
            headers.TryGetValue(name, out var v) ? [.. v.OfType<string>()] : [];
    }

    /// <summary>The engine's media answer on ASP.NET Core's response: the status and only the headers MSFP needs.</summary>
    private sealed class Sink(HttpResponse response) : IResponseSink
    {
        public void Start(int status, string contentType, long length, string contentRange)
        {
            response.StatusCode = status;
            if (contentType.Length > 0)
            {
                response.ContentType = contentType;
            }

            if (length >= 0)
            {
                response.ContentLength = length;
            }

            if (contentRange.Length > 0)
            {
                response.Headers.ContentRange = contentRange;
            }

            if (status is 200 or 206)
            {
                response.Headers.AcceptRanges = "bytes";
            }

            response.Headers.CacheControl = "no-store";
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, System.Threading.CancellationToken ct) => response.Body.WriteAsync(data, ct);
    }
}
