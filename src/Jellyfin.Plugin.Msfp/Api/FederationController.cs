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
/// the specification's paths. Only pair and hello are built.
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

    private async Task Handle(string call)
    {
        var answer = await Answer(call).ConfigureAwait(false);
        Response.StatusCode = answer.Status;
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
        return await _federation.HandleAsync(call, target, headers, body, HttpContext.RequestAborted).ConfigureAwait(false);
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
