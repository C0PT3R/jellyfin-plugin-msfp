using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Playback;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Msfp;

namespace Jellyfin.Plugin.Msfp.Api;

/// <summary>A pairing code, from the operator's page.</summary>
public sealed class CodeRequest
{
    public string Code { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;
}

/// <summary>A list of libraries, from the operator's page.</summary>
public sealed class LibrariesRequest
{
    public List<string> Libraries { get; set; } = new();

    /// <summary>For wants: read every library the peer offers, those it adds later too.</summary>
    public bool All { get; set; }
}

/// <summary>A pairing's limits, from the operator's page.</summary>
public sealed class LimitsRequest
{
    public int Streams { get; set; }

    public int Transcodes { get; set; }

    public int BitrateKbps { get; set; }
}

/// <summary>A key change, from the operator's page.</summary>
public sealed class KeyRequest
{
    /// <summary>A next key exported earlier ("" or null: the one on this server).</summary>
    public string? NextKey { get; set; }

    /// <summary>For a stolen key: revoke it without the next key (every pairing ends).</summary>
    public bool WithoutNext { get; set; }
}

/// <summary>
/// The operator's side of federation, for Jellyfin administrators only:
/// pairing, keys, what each peer is offered and what is read of it, through
/// the engine's own API.
/// </summary>
[ApiController]
[Route("Msfp")]
[Authorize(Policy = "RequiresElevation")]
[ApiExplorerSettings(IgnoreApi = true)]
public class AdminController : ControllerBase
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly EngineHost _host;
    private readonly HostCatalog _catalog;
    private readonly WantsKeeper _wants;
    private readonly JellyfinPlayback _playback;
    private readonly Identification.Identifier _identifier;

    public AdminController(EngineHost host, HostCatalog catalog, WantsKeeper wants, JellyfinPlayback playback, Identification.Identifier identifier)
    {
        _host = host;
        _catalog = catalog;
        _wants = wants;
        _playback = playback;
        _identifier = identifier;
    }

    /// <summary>This server: its fingerprint, address, keys and parts; why federation is off.</summary>
    [HttpGet("Status")]
    public Task<ContentResult> Status(CancellationToken ct) => Run(async e =>
    {
        var s = await e.ServerAsync(ct).ConfigureAwait(false);
        return new
        {
            on = true,
            fingerprint = s.Fingerprint,
            address = s.Address,
            named = s.Named,
            former = s.Former,
            waiting = s.Waiting,
            nextKey = s.NextKey,
            nextOnServer = s.NextOnServer,
            rotationSince = s.RotationSince,
            revokedAt = s.RevokedAt,
            discovery = s.Discovery,
            parts = s.Parts.Keys,
            playing = e.Playbacks().Count,
            transcodes = _playback.Transcodes is var t ? new { own = t.Own, peers = t.Peers, most = t.Most } : null,
        };
    }, off: true);

    /// <summary>Makes a pairing code, shown once.</summary>
    [HttpPost("Codes")]
    public Task<ContentResult> MakeCode([FromBody] CodeRequest? request, CancellationToken ct) => Run(async e =>
    {
        var (text, code) = await e.MakeCodeAsync(request?.Note, ct).ConfigureAwait(false);
        return new { code = text, id = code.Id, expires = code.Expires };
    });

    /// <summary>The live codes (never their text).</summary>
    [HttpGet("Codes")]
    public Task<ContentResult> Codes(CancellationToken ct) => Run(async e => await e.CodesAsync(ct).ConfigureAwait(false));

    /// <summary>Deletes a code.</summary>
    [HttpPost("Codes/{id}/Delete")]
    public Task<ContentResult> DeleteCode(string id, CancellationToken ct) => Run(async e =>
    {
        await e.DeleteCodeAsync(id, ct).ConfigureAwait(false);
        return new { ok = true };
    });

    /// <summary>What a code says, before redeeming it.</summary>
    [HttpPost("Preview")]
    public Task<ContentResult> Preview([FromBody] CodeRequest request, CancellationToken ct) => Run(async e =>
        await e.ReadCodeAsync(request.Code.Trim(), ct).ConfigureAwait(false));

    /// <summary>Redeems another server's code.</summary>
    [HttpPost("Redeem")]
    public Task<ContentResult> Redeem([FromBody] CodeRequest request, CancellationToken ct) => Run(async e =>
        View(await e.RedeemCodeAsync(request.Code.Trim(), ct).ConfigureAwait(false)));

    /// <summary>The peers, with their fingerprints.</summary>
    [HttpGet("Peers")]
    public Task<ContentResult> Peers(CancellationToken ct) => Run(async e =>
    {
        e.PanelOpen();
        return (await e.PeersAsync(ct).ConfigureAwait(false)).Select(View).ToList();
    });

    /// <summary>Accepts a peer that used this server's code.</summary>
    [HttpPost("Peers/{id}/Accept")]
    public Task<ContentResult> Accept(string id, CancellationToken ct) => Run(async e =>
    {
        var p = View(await e.AcceptAsync(id, ct).ConfigureAwait(false));
        _wants.Apply(id);
        return p;
    });

    /// <summary>Asks a peer where the pairing stands.</summary>
    [HttpPost("Peers/{id}/Hello")]
    public Task<ContentResult> Hello(string id, CancellationToken ct) => Run(async e =>
        View(await e.CheckAsync(id, ct).ConfigureAwait(false)));

    /// <summary>Ends a pairing, or refuses a waiting one.</summary>
    [HttpPost("Peers/{id}/End")]
    public Task<ContentResult> End(string id, CancellationToken ct) => Run(async e =>
    {
        await e.EndAsync(id, ct).ConfigureAwait(false);
        _wants.Forget(id);
        return new { ok = true };
    });

    /// <summary>Accepts a peer's new key, once the operators compared fingerprints.</summary>
    [HttpPost("Peers/{id}/Confirm")]
    public Task<ContentResult> Confirm(string id, CancellationToken ct) => Run(async e =>
        View(await e.ConfirmRotationAsync(id, ct).ConfigureAwait(false)));

    /// <summary>Sets a pairing's limits on what it plays here.</summary>
    [HttpPost("Peers/{id}/Limits")]
    public Task<ContentResult> Limits(string id, [FromBody] LimitsRequest request, CancellationToken ct) => Run(async e =>
    {
        await e.SetLimitsAsync(id, new Limits { Streams = request.Streams, Transcodes = request.Transcodes, BitrateKbps = request.BitrateKbps }, ct).ConfigureAwait(false);
        return View(await e.PeerAsync(id, ct).ConfigureAwait(false));
    });

    /// <summary>This server's libraries, those offered to the peer marked.</summary>
    [HttpGet("Peers/{id}/Offers")]
    public Task<ContentResult> Offers(string id, CancellationToken ct) => Run(async e =>
        (await e.OffersAsync(id, ct).ConfigureAwait(false)).Select(l => new { id = l.Id, name = l.Name, kind = l.Kind, offered = l.Offered }));

    /// <summary>Sets which libraries a peer is offered.</summary>
    [HttpPost("Peers/{id}/Offers")]
    public Task<ContentResult> SetOffers(string id, [FromBody] LibrariesRequest request, CancellationToken ct) => Run(async e =>
    {
        var ours = _catalog.Read().Libraries.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        if (request.Libraries.Any(l => !ours.Contains(l)))
        {
            throw new MsfpException(MsfpError.NotFound, "Not a library of this server.");
        }

        await e.SetOffersAsync(id, request.Libraries, ct).ConfigureAwait(false);
        // A library newly offered: its titles are identified before they're listed.
        _identifier.Poke();
        return View(await e.PeerAsync(id, ct).ConfigureAwait(false));
    });

    /// <summary>The libraries the peer offers this server, asked now, those read marked.</summary>
    [HttpGet("Peers/{id}/Theirs")]
    public Task<ContentResult> Theirs(string id, CancellationToken ct) => Run(async e => new
    {
        all = _wants.WantsAll(id),
        libraries = (await e.TheirLibrariesAsync(id, ct).ConfigureAwait(false)).Select(l => new { id = l.Id, name = l.Name, kind = l.Kind, wanted = l.Wanted, gone = l.Gone }),
    });

    /// <summary>Sets which of a peer's libraries this server reads; a reading follows at once.</summary>
    [HttpPost("Peers/{id}/Wants")]
    public Task<ContentResult> SetWants(string id, [FromBody] LibrariesRequest request, CancellationToken ct) => Run(async e =>
    {
        _wants.Set(id, request.All);
        var libraries = request.All
            ? (await e.TheirLibrariesAsync(id, ct).ConfigureAwait(false)).Where(l => !l.Gone).Select(l => l.Id).ToList()
            : request.Libraries;
        await e.SetWantsAsync(id, libraries, ct).ConfigureAwait(false);
        return View(await e.PeerAsync(id, ct).ConfigureAwait(false));
    });

    /// <summary>Reads a peer's catalog now.</summary>
    [HttpPost("Peers/{id}/Read")]
    public Task<ContentResult> Read(string id, CancellationToken ct) => Run(async e =>
        new { titles = await e.ReadNowAsync(id, ct).ConfigureAwait(false) });

    /// <summary>What this server read of a peer's catalog: the libraries read and up to 1,000 titles after `after`.</summary>
    [HttpGet("Peers/{id}/Catalog")]
    public Task<ContentResult> Catalog(string id, [FromQuery] string? after, CancellationToken ct) => Run(async e =>
    {
        var wants = await e.WantsAsync(id, ct).ConfigureAwait(false);
        var store = _host.Store ?? throw new MsfpException(MsfpError.NotFound);
        var titles = await store.ReadAsync(tx => tx.PeerTitlesAsync(id, after ?? string.Empty, 1000), ct).ConfigureAwait(false);

        // Everything read, by kind: the page shows 1,000 at a time.
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var from = string.Empty;
        while (true)
        {
            var at = from;
            var page = await store.ReadAsync(tx => tx.PeerTitlesAsync(id, at, 5000), ct).ConfigureAwait(false);
            foreach (var t in page)
            {
                kinds[t.Kind] = kinds.GetValueOrDefault(t.Kind) + 1;
            }

            if (page.Count < 5000)
            {
                break;
            }

            from = page[^1].Key;
        }

        return new
        {
            total = kinds.Values.Sum(),
            kinds,
            libraries = wants.Select(l => new { id = l.Id, name = l.Name, kind = l.Kind, wanted = l.Wanted, gone = l.Gone }),
            titles = titles.Select(t => t is VideoTitle v
                ? new { key = t.Key, kind = t.Kind, library = t.Libraries?.FirstOrDefault(), audio = v.Audio ?? [], subtitles = v.Subtitles ?? [], height = v.Video?.Height }
                : new { key = t.Key, kind = t.Kind, library = t.Libraries?.FirstOrDefault(), audio = new List<string>(), subtitles = new List<string>(), height = (int?)null }),
            more = titles.Count == 1000,
            next = titles.Count == 1000 ? titles[^1].Key : null,
        };
    });

    /// <summary>Moves this server to its next key and tells the peers.</summary>
    [HttpPost("Key/Rotate")]
    public Task<ContentResult> Rotate([FromBody] KeyRequest? request, CancellationToken ct) => Run(async e =>
    {
        await e.RotateAsync(string.IsNullOrWhiteSpace(request?.NextKey) ? null : request.NextKey.Trim(), ct).ConfigureAwait(false);
        return new { ok = true };
    });

    /// <summary>Revokes this server's key (it was stolen) and rotates to the next one, or ends every pairing without it.</summary>
    [HttpPost("Key/Stolen")]
    public Task<ContentResult> Stolen([FromBody] KeyRequest? request, CancellationToken ct) => Run(async e =>
    {
        var rotated = await e.KeyStolenAsync(string.IsNullOrWhiteSpace(request?.NextKey) ? null : request.NextKey.Trim(), request?.WithoutNext == true, ct).ConfigureAwait(false);
        return new { rotated };
    });

    /// <summary>The next key, exported to keep apart (shown once), then deleted from this server.</summary>
    [HttpPost("Key/Export")]
    public Task<ContentResult> ExportNext(CancellationToken ct) => Run(async e =>
    {
        var (text, fingerprint) = await e.ExportNextKeyAsync(ct).ConfigureAwait(false);
        return new { key = text, fingerprint };
    });

    /// <summary>Peers' playbacks here now.</summary>
    [HttpGet("Playbacks")]
    public Task<ContentResult> Playbacks() => Run(e => Task.FromResult<object>(e.Playbacks()
        .Select(p => new { id = p.Id, peer = p.Peer, peerName = p.PeerName, title = p.Title, method = p.Method, since = p.Since, state = p.State })));

    /// <summary>Ends a peer's playback here.</summary>
    [HttpPost("Playbacks/{id}/Stop")]
    public Task<ContentResult> StopPlayback(string id) => Run(e =>
    {
        e.StopPlayback(id, EndReason.Operator);
        return Task.FromResult<object>(new { ok = true });
    });

    // Runs an action on the engine: the operator is told when federation is
    // off, and an engine error is a 400 with its code.
    private async Task<ContentResult> Run(Func<MsfpEngine, Task<object>> action, bool off = false)
    {
        if (_host.Engine is not { } engine)
        {
            return off
                ? Json(new { on = false, problem = _host.Problem })
                : Json(new { error = _host.Problem.Length > 0 ? _host.Problem : "Federation is off.", code = "off" }, 409);
        }

        try
        {
            return Json(await action(engine).ConfigureAwait(false));
        }
        catch (MsfpException e)
        {
            return Json(new { error = e.Message, code = e.Code.ToString() }, e.Code == MsfpError.NotFound ? 404 : 400);
        }
    }

    private Task<ContentResult> Run<T>(Func<MsfpEngine, Task<T>> action, bool off = false) =>
        Run(async e => (object)(await action(e).ConfigureAwait(false))!, off);

    private static object View(PeerView p) => new
    {
        id = p.Id,
        name = p.Name,
        address = p.Address,
        fingerprint = p.Fingerprint,
        state = p.State.Value,
        paired = p.Paired,
        seen = p.Seen,
        suspended = p.Suspended,
        inbound = p.Inbound.ToString(),
        callsFailed = p.CallsFailed,
        pending = p.Pending?.Fingerprint,
        refused = p.Refused?.Fingerprint,
        limits = p.Limits,
        theirLimits = p.TheirLimits,
        moving = p.Moving?.Address,
        offers = p.Offers,
        wants = p.Wants,
        wantsGone = p.WantsGone,
        parts = p.Parts.Keys,
    };

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
