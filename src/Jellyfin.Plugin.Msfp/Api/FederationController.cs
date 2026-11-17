using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Msfp.Api;

/// <summary>
/// The federation calls other servers make (MSFP sections 3 and 9), under
/// the specification's paths: pair, hello, libraries and titles.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/federation")]
public class FederationController : ControllerBase
{
    private readonly FederationService _federation;

    public FederationController(FederationService federation)
    {
        _federation = federation;
    }

    /// <summary>The pair call (section 9.1).</summary>
    [HttpPost("pair")]
    public Task Pair() => Handle("pair");

    /// <summary>The hello call (section 9.2).</summary>
    [HttpPost("hello")]
    public Task Hello() => Handle("hello");

    /// <summary>The libraries call (section 9.3).</summary>
    [HttpPost("libraries")]
    public Task Libraries() => Handle("libraries");

    /// <summary>The titles call (section 9.4).</summary>
    [HttpPost("titles")]
    public Task Titles() => Handle("titles");

    /// <summary>
    /// Any other method on a federation path, and any other path under it:
    /// Jellyfin's 404 for a path that doesn't exist (an empty body, no
    /// type), not the 405 routing would give, so a prober learns nothing
    /// (section 14). Literal routes above win over this one.
    /// </summary>
    [AcceptVerbs("GET", "HEAD", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")]
    [Route("{**rest}")]
    public EmptyResult Elsewhere()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        Response.ContentLength = 0;
        return new EmptyResult();
    }

    private async Task Handle(string call)
    {
        // The answer goes out as the bytes signed, never content-coded:
        // Jellyfin's response compression would otherwise gzip it for a
        // caller that accepts gzip, and a caller that checks the bytes it
        // received, or a proxy between, would see other bytes than those
        // signed (SPEC-GAPS.md, section 6.3). The compression middleware
        // reads Accept-Encoding when the answer starts, so removing it here
        // is enough; the feature covers HTTPS too.
        Request.Headers.Remove(HeaderNames.AcceptEncoding);
        if (HttpContext.Features.Get<IHttpsCompressionFeature>() is { } compression)
        {
            compression.Mode = HttpsCompressionMode.DoNotCompress;
        }

        var answer = await Answer(call).ConfigureAwait(false);
        Response.StatusCode = answer.Status;
        if (answer.Signature == null && answer.Body.Length == 0)
        {
            // The unsigned 404: nothing more than Jellyfin's own.
            Response.ContentLength = 0;
            return;
        }

        if (answer.ContentType != null)
        {
            Response.ContentType = answer.ContentType;
        }

        if (answer.Signature != null)
        {
            Response.Headers[SignatureHeaders.SignatureHeader] = answer.Signature;
        }

        Response.Headers.CacheControl = "no-store";
        Response.ContentLength = answer.Body.Length;
        await Response.Body.WriteAsync(answer.Body).ConfigureAwait(false);
    }

    private async Task<Federation.Answer> Answer(string call)
    {
        // A browser's cross-site request, or no JSON: refused like a path
        // that doesn't exist (SPEC-GAPS.md, section 3).
        var site = Request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && site != "same-origin" && site != "none")
        {
            return Federation.Answer.NotFound;
        }

        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var type)
            || !type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return Federation.Answer.NotFound;
        }

        // A coded request is never decoded: refused at once (section 3).
        foreach (var coding in Request.Headers.ContentEncoding)
        {
            if (!string.Equals(coding?.Trim(), "identity", StringComparison.OrdinalIgnoreCase))
            {
                return Federation.Answer.NotFound;
            }
        }

        var body = await ReadBody(Request.Body).ConfigureAwait(false);
        if (body == null)
        {
            return Federation.Answer.NotFound;
        }

        // The target as it was sent: the signature covers it so.
        var target = HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (string.IsNullOrEmpty(target))
        {
            target = Request.Path.ToString() + Request.QueryString.ToString();
        }

        var headers = new SignatureHeaders(
            Single(SignatureHeaders.KeyHeader),
            Single(SignatureHeaders.DateHeader),
            Single(SignatureHeaders.NonceHeader),
            Single(SignatureHeaders.SignatureHeader));
        // The network address the request came from, for the limit before
        // any signature check (section 8.3). Behind a reverse proxy this is
        // the proxy's, unless Jellyfin's known proxies are set.
        var remote = HttpContext.Connection.RemoteIpAddress;
        var from = remote == null ? null : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
        return await _federation.HandleAsync(call, target, headers, body, HttpContext.RequestAborted, from).ConfigureAwait(false);
    }

    /// <summary>A header's value when it was sent exactly once.</summary>
    private string? Single(string name)
    {
        var values = Request.Headers[name];
        return values.Count == 1 ? values[0] : null;
    }

    /// <summary>The body, or null when it is over 64 KiB.</summary>
    private static async Task<byte[]?> ReadBody(Stream body)
    {
        var ms = new MemoryStream();
        var buffer = new byte[16384];
        int n;
        while ((n = await body.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > FederationService.MaxRequestBody)
            {
                return null;
            }

            ms.Write(buffer, 0, n);
        }

        return ms.ToArray();
    }
}
