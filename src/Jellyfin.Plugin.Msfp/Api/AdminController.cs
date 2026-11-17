using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Viewing;
using Msfp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Msfp.Api;

/// <summary>A code to redeem, from the operator's page.</summary>
public sealed class RedeemRequest
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>A list of libraries, from the operator's page.</summary>
public sealed class LibrariesRequest
{
    public List<string> Libraries { get; set; } = new();
}

/// <summary>Which of a peer's libraries to read.</summary>
public sealed class WantsRequest
{
    public bool All { get; set; } = true;

    public List<string> Libraries { get; set; } = new();
}

/// <summary>The operator's side of pairing, for Jellyfin administrators only.</summary>
[ApiController]
[Route("Msfp")]
[Authorize(Policy = "RequiresElevation")]
public class AdminController : ControllerBase
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly FederationService _federation;
    private readonly OwnCatalog _catalog;
    private readonly CatalogReader _reader;

    public AdminController(FederationService federation, OwnCatalog catalog, CatalogReader reader)
    {
        _federation = federation;
        _catalog = catalog;
        _reader = reader;
    }

    /// <summary>This server's libraries, with how many titles each has a key for.</summary>
    [HttpGet("Libraries")]
    public ContentResult Libraries()
    {
        if (_catalog.Libraries.Count == 0)
        {
            _catalog.Refresh();
        }

        return Json(_catalog.Libraries.Select(l => new { id = l.Id, name = l.Name, kind = l.Kind }));
    }

    /// <summary>Sets which libraries a peer is offered.</summary>
    [HttpPost("Peers/{key}/Offers")]
    public ContentResult SetOffers(string key, [FromBody] LibrariesRequest request)
    {
        try
        {
            var peer = _federation.SetOffers(key, request.Libraries);
            return peer == null ? Json(new { error = "No such peer." }, 404) : Json(View(peer));
        }
        catch (ArgumentException)
        {
            return Json(new { error = "Not a library of this server." }, 400);
        }
    }

    /// <summary>Sets which of a peer's libraries this server reads.</summary>
    [HttpPost("Peers/{key}/Wants")]
    public ContentResult SetWants(string key, [FromBody] WantsRequest request)
    {
        var peer = _federation.SetWants(key, request.All, request.Libraries);
        return peer == null ? Json(new { error = "No such peer." }, 404) : Json(View(peer));
    }

    /// <summary>What this server read of a peer's catalog: its libraries and up to 1,000 titles from `after`.</summary>
    [HttpGet("Peers/{key}/Catalog")]
    public ContentResult Catalog(string key, [FromQuery] string? after)
    {
        if (_federation.Peers().All(p => p.Key != key))
        {
            return Json(new { error = "No such peer." }, 404);
        }

        var c = _reader.Store.Get(key);
        var page = c.Titles.Where(t => after == null || string.CompareOrdinal(t.Key, after) > 0).Take(1000)
            .Select(t => new { key = t.Key, kind = t.Value.Kind, library = t.Value.Library, audio = t.Value.Audio, subtitles = t.Value.Subtitles });
        return Json(new { libraries = c.Libraries, requested = c.Requested, seq = c.Seq, readAt = c.ReadAt, lastResult = c.LastResult, count = c.Titles.Count, titles = page });
    }

    /// <summary>Reads a peer's catalog now.</summary>
    [HttpPost("Peers/{key}/Read")]
    public async Task<ContentResult> Read(string key, CancellationToken ct)
    {
        var result = await _reader.ReadAsync(key, ct).ConfigureAwait(false);
        return Json(new { result });
    }

    /// <summary>This server's key, fingerprint and address.</summary>
    [HttpGet("Status")]
    public async Task<ContentResult> Status(CancellationToken ct)
    {
        return Json(new
        {
            publicKey = _federation.Key.PublicKeyText,
            fingerprint = _federation.Key.Fingerprint,
            address = _federation.OwnAddress,
            certificates = await _federation.OwnPinsAsync(ct).ConfigureAwait(false),
        });
    }

    /// <summary>Makes a pairing code.</summary>
    [HttpPost("Codes")]
    public ContentResult MakeCode()
    {
        if (_federation.OwnAddress == null)
        {
            return Json(new { error = "Set this server's address first, and save." }, 400);
        }

        var code = _federation.MakeCode();
        return Json(new { code = code.Format(), expires = code.Expires });
    }

    /// <summary>Redeems another server's code.</summary>
    [HttpPost("Redeem")]
    public async Task<ContentResult> Redeem([FromBody] RedeemRequest request, CancellationToken ct)
    {
        var result = await _federation.RedeemAsync(request.Code, ct).ConfigureAwait(false);
        return Json(new { ok = result.Ok, message = result.Message, peer = result.Peer == null ? null : View(result.Peer) }, result.Ok ? 200 : 400);
    }

    /// <summary>The peers, with their fingerprints.</summary>
    [HttpGet("Peers")]
    public ContentResult Peers()
    {
        return Json(_federation.Peers().Select(View));
    }

    /// <summary>Accepts a peer that used this server's code.</summary>
    [HttpPost("Peers/{key}/Accept")]
    public async Task<ContentResult> Accept(string key, CancellationToken ct)
    {
        var peer = await _federation.AcceptAsync(key, ct).ConfigureAwait(false);
        return peer == null ? Json(new { error = "No peer waiting for this server's operator." }, 404) : Json(View(peer));
    }

    /// <summary>Asks a peer where the pairing stands.</summary>
    [HttpPost("Peers/{key}/Hello")]
    public async Task<ContentResult> Hello(string key, CancellationToken ct)
    {
        var peer = await _federation.HelloAsync(key, ct).ConfigureAwait(false);
        return peer == null ? Json(new { error = "No such peer." }, 404) : Json(View(peer));
    }

    /// <summary>Ends a pairing, or refuses a waiting one.</summary>
    [HttpPost("Peers/{key}/End")]
    public async Task<ContentResult> End(string key, CancellationToken ct)
    {
        var ended = await _federation.EndAsync(key, ct).ConfigureAwait(false);
        return ended ? Json(new { ok = true }) : Json(new { error = "No such peer." }, 404);
    }

    private static object View(Peer p)
    {
        Encodings.TryDecodeBase64Url(p.Key, out var raw);
        return new
        {
            key = p.Key,
            fingerprint = Keys.Fingerprint(raw),
            address = p.Address,
            name = p.Name,
            state = p.State.ToString().ToLowerInvariant(),
            certificates = p.Pins,
            offered = p.Offered,
            wantAll = p.WantAll,
            wanted = p.Wanted,
            lastHeard = p.LastHeard,
        };
    }

    private static ContentResult Json(object value, int status = 200)
    {
        return new ContentResult
        {
            Content = JsonSerializer.Serialize(value, Options),
            ContentType = "application/json",
            StatusCode = status,
        };
    }
}
