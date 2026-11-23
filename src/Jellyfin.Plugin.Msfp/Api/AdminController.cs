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
using GroupSettings = Msfp.Protocol.GroupSettings;

namespace Jellyfin.Plugin.Msfp.Api;

/// <summary>A pairing code, from the operator's page.</summary>
public sealed class CodeRequest
{
    public string Code { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    /// <summary>How many servers may use a new code (0: one).</summary>
    public int Uses { get; set; }

    /// <summary>How many hours a new code works (0: 24).</summary>
    public int Hours { get; set; }

    /// <summary>A group's ID: the new code invites to it ("" for a pairing).</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Using a group's invite: what this server shares with the group, set as it joins.</summary>
    public List<string> Libraries { get; set; } = new();

    /// <summary>Using a group's invite: the limits it lends the group.</summary>
    public LimitsRequest? Pool { get; set; }

    /// <summary>Using a group's invite: this server's local titles go to the group too (off by default).</summary>
    public bool Local { get; set; }
}

/// <summary>A group's name and settings, from the operator's page.</summary>
public sealed class GroupRequest
{
    public string Name { get; set; } = string.Empty;

    public long CapPerDay { get; set; } = 5;

    /// <summary>Creating: what this server shares with the group.</summary>
    public List<string> Libraries { get; set; } = new();

    /// <summary>Creating: the limits it lends the group.</summary>
    public LimitsRequest? Pool { get; set; }

    /// <summary>Creating: this server's local titles go to the group too (off by default).</summary>
    public bool Local { get; set; }

    /// <summary>Creating: this server automatically accepts new members.</summary>
    public bool Accept { get; set; }
}

/// <summary>An action on a group's member or request, from the operator's page.</summary>
public sealed class GroupActionRequest
{
    /// <summary>A member ID ("" where none means something: no heir, the default successor).</summary>
    public string Member { get; set; } = string.Empty;

    public bool On { get; set; }

    /// <summary>A ban's reason; optional.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>For a block: the members it invited too, and theirs.</summary>
    public bool Invitees { get; set; }

    /// <summary>For leaving: the servers only this group pairs us with, asked to stay paired directly first.</summary>
    public List<string> Ask { get; set; } = new();

    /// <summary>For dissolving: the pairings the group made are kept.</summary>
    public bool KeepPairings { get; set; } = true;
}

/// <summary>What this server offers a group, and the pool it lends it.</summary>
public sealed class GroupSharingRequest
{
    public List<string> Libraries { get; set; } = new();

    public LimitsRequest Pool { get; set; } = new();

    /// <summary>This server automatically accepts new members; null: unchanged.</summary>
    public bool? Accept { get; set; }

    /// <summary>This server's local titles go to the group too; null: unchanged.</summary>
    public bool? Local { get; set; }
}

/// <summary>A switch, from the operator's page.</summary>
public sealed class SwitchRequest
{
    public bool On { get; set; }
}

/// <summary>The libraries a group doesn't show here, from the operator's page.</summary>
public sealed class GroupShowsRequest
{
    public List<HiddenRequest> Hidden { get; set; } = new();
}

/// <summary>A member's library, hidden.</summary>
public sealed class HiddenRequest
{
    public string Member { get; set; } = string.Empty;

    public string Library { get; set; } = string.Empty;
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

    /// <summary>Downloads at once (0, the default: none).</summary>
    public int Downloads { get; set; }

    /// <summary>Downloads started in any 24 hours (0: no cap besides Downloads).</summary>
    public int DownloadsPerDay { get; set; }

    /// <summary>Each download's speed cap, kbit/s (0: none).</summary>
    public int DownloadKbps { get; set; }

    /// <summary>Scans started in any 24 hours (0, the default: no scan access).</summary>
    public int ScansPerDay { get; set; }

    /// <summary>The bytes one scan may read (0: the floor, 4 MiB; else 4 MiB to 1 GiB).</summary>
    public int ScanBytes { get; set; }

    /// <summary>The engine's limits of these.</summary>
    public Limits ToLimits() => new()
    {
        Streams = Streams, Transcodes = Transcodes, BitrateKbps = BitrateKbps, Downloads = Downloads, DownloadsPerDay = DownloadsPerDay,
        DownloadKbps = DownloadKbps, ScansPerDay = ScansPerDay, ScanBytes = ScanBytes,
    };
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
    private readonly CopyKeeper _copies;

    public AdminController(EngineHost host, HostCatalog catalog, WantsKeeper wants, JellyfinPlayback playback, Identification.Identifier identifier, CopyKeeper copies)
    {
        _copies = copies;
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

    /// <summary>The copies described: versions, those not read yet, files waiting to be read, the last import. Counts only.</summary>
    [HttpGet("Copies")]
    public ContentResult Copies()
    {
        var c = _copies.Counts;
        return Content(JsonSerializer.Serialize(new { versions = c.Versions, notRead = c.NotRead, pending = c.Pending, passes = c.Passes, probed = c.Probed, import = c.Import }, Options), "application/json");
    }

    /// <summary>
    /// Imports the header read made for Overfin (its file put by the
    /// operator in the plugin's data folder, import/probe.jsonl.gz or
    /// import/probe.jsonl), its paths relative to root (the libraries'
    /// mount as this server sees it), so that the files aren't probed
    /// again. Counts only; the file is left where it is.
    /// </summary>
    [HttpPost("Copies/Import")]
    public async Task<ContentResult> ImportCopies([FromQuery] string root, CancellationToken ct)
    {
        var dir = System.IO.Path.Combine(Plugin.Instance?.DataFolderPath ?? string.Empty, "import");
        var path = new[] { "probe.jsonl.gz", "probe.jsonl" }.Select(n => System.IO.Path.Combine(dir, n)).FirstOrDefault(System.IO.File.Exists);
        if (path is null || string.IsNullOrEmpty(root) || !System.IO.Path.IsPathFullyQualified(root))
        {
            Response.StatusCode = 400;
            return Content(JsonSerializer.Serialize(new { error = "put the header read in the plugin's data folder (import/probe.jsonl.gz) and give the libraries' mount as root" }, Options), "application/json");
        }

        await using var read = System.IO.File.OpenRead(path);
        var counts = await _copies.ImportAsync(read, root, ct).ConfigureAwait(false);
        return Content(JsonSerializer.Serialize(counts, Options), "application/json");
    }

    /// <summary>Makes a pairing code, shown once.</summary>
    [HttpPost("Codes")]
    public Task<ContentResult> MakeCode([FromBody] CodeRequest? request, CancellationToken ct) => Run(async e =>
    {
        var options = new CodeOptions
        {
            Note = request?.Note,
            Uses = request?.Uses ?? 0,
            Lifetime = TimeSpan.FromHours(request?.Hours ?? 0),
            Group = string.IsNullOrEmpty(request?.Group) ? null : request.Group,
        };
        var (text, code) = await e.MakeCodeWithAsync(options, ct).ConfigureAwait(false);
        return new { code = text, id = code.Id, expires = code.Expires, uses = code.Uses, group = code.Group };
    });

    /// <summary>The live codes, without their text (Codes/{id}/Text gives it).</summary>
    [HttpGet("Codes")]
    public Task<ContentResult> Codes(CancellationToken ct) => Run(async e => await e.CodesAsync(ct).ConfigureAwait(false));

    /// <summary>A live code's text again, to copy once more.</summary>
    [HttpGet("Codes/{id}/Text")]
    public Task<ContentResult> CodeText(string id, CancellationToken ct) => Run(async e =>
        new { code = await e.CodeTextAsync(id, ct).ConfigureAwait(false) });

    /// <summary>
    /// Waits, 25 seconds at most, for federation to change past the count
    /// the page last saw, and says the count: the page reloads when it moved.
    /// </summary>
    [HttpGet("Changes")]
    public async Task<ContentResult> Changes([FromQuery] long after, CancellationToken ct)
    {
        var seq = await _host.Changes.WaitAsync(after, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        return Content(JsonSerializer.Serialize(new { seq }, Options), "application/json");
    }

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
    {
        var code = request.Code.Trim();
        var preview = await e.ReadCodeAsync(code, ct).ConfigureAwait(false);
        var peer = View(await e.RedeemCodeAsync(code, ct).ConfigureAwait(false));
        if (preview.Group is { Length: > 0 } group)
        {
            // A group's invite: what this server shares with the group, from the start.
            await ShareWithGroupAsync(e, group, request.Libraries, request.Pool, request.Local, ct).ConfigureAwait(false);
        }

        return peer;
    });

    /// <summary>This server's libraries: what it may share, before a group exists (creating or joining one).</summary>
    [HttpGet("Libraries")]
    public ContentResult Libraries()
    {
        var counts = _catalog.Counts();
        return Json(_catalog.Read().Libraries.Select(l => LibraryJson(l.Id, l.Name, counts)).ToList());
    }

    // A library as the page shows it: since MSFP's revision a library has no
    // kind, so it is described by its titles (films, series, those under a
    // local key).
    private static Dictionary<string, object> LibraryJson(string id, string name, IReadOnlyDictionary<string, LibraryCounts> counts)
    {
        var c = counts.GetValueOrDefault(id) ?? new LibraryCounts(0, 0, 0);
        return new Dictionary<string, object> { ["id"] = id, ["name"] = name, ["films"] = c.Films, ["series"] = c.Series, ["local"] = c.Local };
    }

    // What this server shares with a group and the limits it lends it; the
    // libraries are this server's own.
    private async Task ShareWithGroupAsync(MsfpEngine e, string group, List<string> libraries, LimitsRequest? pool, bool local, CancellationToken ct)
    {
        var ours = _catalog.Read().Libraries.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        if (pool is { } p)
        {
            await e.SetGroupPoolAsync(group, p.ToLimits(), ct).ConfigureAwait(false);
        }

        await e.SetGroupOffersAsync(group, libraries.Where(ours.Contains).ToList(), ct).ConfigureAwait(false);
        if (local)
        {
            await e.SetGroupLocalAsync(group, true, ct).ConfigureAwait(false);
        }

        _identifier.Poke();
    }

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

    /// <summary>The groups that ending this pairing would cut this server off from (MSFP groups section 6, "No way in").</summary>
    [HttpGet("Peers/{id}/Cuts")]
    public Task<ContentResult> Cuts(string id, CancellationToken ct) => Run(async e =>
        (await e.GroupsCutByAsync(id, ct).ConfigureAwait(false)).Select(g => new { id = g.Id, name = g.Name }).ToList());

    /// <summary>Pairs directly with a server known through a group, accepts its ask, or keeps a server whose group was closed.</summary>
    [HttpPost("Peers/{id}/Individual")]
    public Task<ContentResult> Individual(string id, CancellationToken ct) => Run(async e =>
        View(await e.AskIndividualAsync(id, ct).ConfigureAwait(false)));

    /// <summary>Unpairs directly: a server still in a shared group stays paired through it; otherwise the pairing ends.</summary>
    [HttpPost("Peers/{id}/Unpair")]
    public Task<ContentResult> Unpair(string id, CancellationToken ct) => Run(async e =>
    {
        await e.EndIndividualAsync(id, ct).ConfigureAwait(false);
        return await ForgetIfGoneAsync(e, id, ct).ConfigureAwait(false);
    });

    /// <summary>Refuses a server's ask to pair directly, or ends one whose group was closed.</summary>
    [HttpPost("Peers/{id}/Refuse")]
    public Task<ContentResult> RefuseIndividual(string id, CancellationToken ct) => Run(async e =>
    {
        await e.RefuseIndividualAsync(id, ct).ConfigureAwait(false);
        return await ForgetIfGoneAsync(e, id, ct).ConfigureAwait(false);
    });

    /// <summary>Blocks a server: everything with it ends, and it is kept out of every group.</summary>
    [HttpPost("Peers/{id}/Block")]
    public Task<ContentResult> BlockPeer(string id, CancellationToken ct) => Run(async e =>
    {
        await e.BlockPeerAsync(id, ct).ConfigureAwait(false);
        _wants.Forget(id);
        return (object)new { ok = true };
    });

    // What a server's titles were read for goes with the pairing.
    private async Task<object> ForgetIfGoneAsync(MsfpEngine e, string id, CancellationToken ct)
    {
        try
        {
            await e.PeerAsync(id, ct).ConfigureAwait(false);
        }
        catch (MsfpException x) when (x.Code == MsfpError.NotFound)
        {
            _wants.Forget(id);
        }

        return new { ok = true };
    }

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
        await e.SetLimitsAsync(id, request.ToLimits(), ct).ConfigureAwait(false);
        return View(await e.PeerAsync(id, ct).ConfigureAwait(false));
    });

    /// <summary>This server's libraries, those offered to the peer marked.</summary>
    [HttpGet("Peers/{id}/Offers")]
    public Task<ContentResult> Offers(string id, CancellationToken ct) => Run(async e =>
    {
        var counts = _catalog.Counts();
        return (await e.OffersAsync(id, ct).ConfigureAwait(false)).Select(l => Offered(LibraryJson(l.Id, l.Name, counts), l.Offered)).ToList();
    });

    private static Dictionary<string, object> Offered(Dictionary<string, object> library, bool offered)
    {
        library["offered"] = offered;
        return library;
    }

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

    /// <summary>
    /// Whether the peer also gets this server's local titles (those nothing
    /// identifies: their name, year and image go with them) in the libraries
    /// it is offered. Off by default.
    /// </summary>
    [HttpPost("Peers/{id}/Local")]
    public Task<ContentResult> SetLocal(string id, [FromBody] SwitchRequest request, CancellationToken ct) => Run(async e =>
    {
        await e.SetLocalAsync(id, request.On, ct).ConfigureAwait(false);
        return View(await e.PeerAsync(id, ct).ConfigureAwait(false));
    });

    /// <summary>The libraries the peer offers this server, asked now, those read marked.</summary>
    [HttpGet("Peers/{id}/Theirs")]
    public Task<ContentResult> Theirs(string id, CancellationToken ct) => Run(async e => new
    {
        all = _wants.WantsAll(id),
        libraries = (await e.TheirLibrariesAsync(id, ct).ConfigureAwait(false)).Select(l => new { id = l.Id, name = l.Name, wanted = l.Wanted, gone = l.Gone }),
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
            libraries = wants.Select(l => new { id = l.Id, name = l.Name, wanted = l.Wanted, gone = l.Gone }),
            titles = titles.Select(t => t is VideoTitle v
                ? new { key = t.Key, kind = t.Kind, library = t.Libraries?.FirstOrDefault(), audio = v.AudioLanguages().ToList(), subtitles = v.SubtitleLanguages().ToList(), height = v.BestCopy()?.Video?.Height }
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

    // ---- Groups (MSFP groups part) ------------------------------------------

    /// <summary>The groups this server is in, with members, requests and the pairings made through each.</summary>
    [HttpGet("Groups")]
    public Task<ContentResult> Groups(CancellationToken ct) => Run(async e =>
    {
        var peers = await e.PeersAsync(ct).ConfigureAwait(false);
        return (await e.GroupsAsync(ct).ConfigureAwait(false)).Select(g => GroupJson(g, peers)).ToList();
    });

    /// <summary>Founds a group, this server its owner.</summary>
    [HttpPost("Groups")]
    public Task<ContentResult> CreateGroup([FromBody] GroupRequest request, CancellationToken ct) => Run(async e =>
    {
        var g = await e.CreateGroupAsync(request.Name, new GroupSettings(0, request.CapPerDay), ct).ConfigureAwait(false);
        await ShareWithGroupAsync(e, g.Id, request.Libraries, request.Pool, request.Local, ct).ConfigureAwait(false);
        if (request.Accept)
        {
            await e.SetGroupAnswerAsync(g.Id, true, ct).ConfigureAwait(false);
        }

        return GroupJson(await e.GroupAsync(g.Id, ct).ConfigureAwait(false), []);
    });

    /// <summary>Changes a group's name and settings (the owner only).</summary>
    [HttpPost("Groups/{id}/Settings")]
    public Task<ContentResult> GroupSettingsSet(string id, [FromBody] GroupRequest request, CancellationToken ct) =>
        Act(e => e.SetGroupSettingsAsync(id, request.Name, new GroupSettings(0, request.CapPerDay), ct));

    /// <summary>Whether this server automatically accepts the group's new members (each member chooses for itself).</summary>
    [HttpPost("Groups/{id}/Answer")]
    public Task<ContentResult> GroupAnswer(string id, [FromBody] GroupActionRequest r, CancellationToken ct) => Act(e => e.SetGroupAnswerAsync(id, r.On, ct));

    /// <summary>What this server shows from a group: each member's libraries, all shown unless hidden.</summary>
    [HttpGet("Groups/{id}/Shows")]
    public Task<ContentResult> GroupShows(string id, CancellationToken ct) => Run(async e =>
        (await e.GroupShowsAsync(id, ct).ConfigureAwait(false)).Members.Select(m => new
        {
            member = m.Member,
            name = m.Name,
            libraries = m.Libraries.Select(l => new { id = l.Id, name = l.Name, shown = l.Shown }),
        }).ToList());

    /// <summary>Sets the members' libraries this server doesn't show from a group.</summary>
    [HttpPost("Groups/{id}/Shows")]
    public Task<ContentResult> SetGroupShows(string id, [FromBody] GroupShowsRequest r, CancellationToken ct) =>
        Act(e => e.SetGroupShowsAsync(id, r.Hidden.Select(h => new HiddenLibrary(h.Member, h.Library)).ToList(), ct));

    /// <summary>This server's libraries, those offered to the group marked, and the pool it lends.</summary>
    [HttpGet("Groups/{id}/Sharing")]
    public Task<ContentResult> GroupSharing(string id, CancellationToken ct) => Run(async e =>
    {
        var g = await e.GroupAsync(id, ct).ConfigureAwait(false);
        var libs = await e.GroupOffersAsync(id, ct).ConfigureAwait(false);
        var counts = _catalog.Counts();
        return new
        {
            libraries = libs.Select(l => Offered(LibraryJson(l.Id, l.Name, counts), l.Offered)).ToList(),
            pool = g.Pool,
            accept = g.Accept,
            local = g.Local,
        };
    });

    /// <summary>Sets what the group is offered, the pool, and how requests are answered.</summary>
    [HttpPost("Groups/{id}/Sharing")]
    public Task<ContentResult> SetGroupSharing(string id, [FromBody] GroupSharingRequest request, CancellationToken ct) => Act(async e =>
    {
        var ours = _catalog.Read().Libraries.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        if (request.Libraries.Any(l => !ours.Contains(l)))
        {
            throw new MsfpException(MsfpError.NotFound, "Not a library of this server.");
        }

        var p = request.Pool;
        await e.SetGroupPoolAsync(id, p.ToLimits(), ct).ConfigureAwait(false);
        await e.SetGroupOffersAsync(id, request.Libraries, ct).ConfigureAwait(false);
        if (request.Accept is { } accept)
        {
            await e.SetGroupAnswerAsync(id, accept, ct).ConfigureAwait(false);
        }

        if (request.Local is { } local)
        {
            await e.SetGroupLocalAsync(id, local, ct).ConfigureAwait(false);
        }

        // A library newly offered: its titles are identified before they're listed.
        _identifier.Poke();
    });

    /// <summary>Names a member admin, or removes its role (the owner only).</summary>
    [HttpPost("Groups/{id}/Admin")]
    public Task<ContentResult> GroupAdmin(string id, [FromBody] GroupActionRequest r, CancellationToken ct) => Act(e => e.SetAdminAsync(id, r.Member, r.On, ct));

    /// <summary>Names the heir ("" for none).</summary>
    [HttpPost("Groups/{id}/Heir")]
    public Task<ContentResult> GroupHeir(string id, [FromBody] GroupActionRequest r, CancellationToken ct) =>
        Act(e => e.SetHeirAsync(id, r.Member.Length == 0 ? null : r.Member, ct));

    /// <summary>Gives the group to a member ("" for the successor).</summary>
    [HttpPost("Groups/{id}/Give")]
    public Task<ContentResult> GiveGroup(string id, [FromBody] GroupActionRequest r, CancellationToken ct) =>
        Act(e => e.GiveGroupAsync(id, r.Member.Length == 0 ? null : r.Member, ct));

    /// <summary>Bans a member, with an optional reason.</summary>
    [HttpPost("Groups/{id}/Ban")]
    public Task<ContentResult> Ban(string id, [FromBody] GroupActionRequest r, CancellationToken ct) =>
        Act(e => e.BanAsync(id, r.Member, r.Reason.Trim().Length == 0 ? null : r.Reason.Trim(), ct));

    /// <summary>Lifts a ban (the owner only).</summary>
    [HttpPost("Groups/{id}/Unban")]
    public Task<ContentResult> Unban(string id, [FromBody] GroupActionRequest r, CancellationToken ct) => Act(e => e.UnbanAsync(id, r.Member, ct));

    /// <summary>Blocks a member: its pairing ends, it's ignored in every group.</summary>
    [HttpPost("Groups/{id}/Block")]
    public Task<ContentResult> Block(string id, [FromBody] GroupActionRequest r, CancellationToken ct) => Act(e => e.BlockAsync(id, r.Member, r.Invitees, ct));

    /// <summary>Sends this server's request to a member.</summary>
    [HttpPost("Groups/{id}/Ask")]
    public Task<ContentResult> AskMember(string id, [FromBody] GroupActionRequest r, CancellationToken ct) => Act(e => e.RequestMemberAsync(id, r.Member, ct));

    /// <summary>Hears a refused member's requests again.</summary>
    [HttpPost("Groups/{id}/LiftRefusal")]
    public Task<ContentResult> LiftRefusal(string id, [FromBody] GroupActionRequest r, CancellationToken ct) => Act(e => e.LiftRefusalAsync(id, r.Member, ct));

    /// <summary>Accepts a member's request: the two pair through the group.</summary>
    [HttpPost("Groups/{id}/Requests/{mail}/Accept")]
    public Task<ContentResult> AcceptRequest(string id, string mail, CancellationToken ct) => Act(e => e.AcceptRequestAsync(id, mail, ct));

    /// <summary>Refuses a member's request; it isn't told.</summary>
    [HttpPost("Groups/{id}/Requests/{mail}/Refuse")]
    public Task<ContentResult> RefuseRequest(string id, string mail, CancellationToken ct) => Act(e => e.RefuseRequestAsync(id, mail, ct));

    /// <summary>Accepts a server already paired that used this server's code for the group.</summary>
    [HttpPost("Groups/{id}/Joins/{peer}/Accept")]
    public Task<ContentResult> AcceptJoin(string id, string peer, CancellationToken ct) => Act(e => e.AcceptJoinAsync(id, peer, ct));

    /// <summary>Refuses it; it isn't told.</summary>
    [HttpPost("Groups/{id}/Joins/{peer}/Refuse")]
    public Task<ContentResult> RefuseJoin(string id, string peer, CancellationToken ct) => Act(e => e.RefuseJoinAsync(id, peer, ct));

    /// <summary>Leaves a group (or gives up joining it); End lists the pairings that end too.</summary>
    [HttpPost("Groups/{id}/Leave")]
    public Task<ContentResult> LeaveGroup(string id, [FromBody] GroupActionRequest r, CancellationToken ct) => Act(e => e.LeaveGroupAsync(id, r.Ask, ct));

    /// <summary>Dissolves a group (the owner only).</summary>
    [HttpPost("Groups/{id}/Dissolve")]
    public Task<ContentResult> DissolveGroup(string id, [FromBody] GroupActionRequest r, CancellationToken ct) => Act(e => e.DissolveGroupAsync(id, r.KeepPairings, ct));

    // A group as the page shows it: the pairings with its members, and those
    // made through it (Only: nothing else holds them).
    private static object GroupJson(GroupView g, IReadOnlyList<PeerView> peers)
    {
        var names = g.Members.ToDictionary(m => m.Id, m => m.Name, StringComparer.Ordinal);
        var byId = peers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        return new
        {
            id = g.Id,
            name = g.Name,
            capPerDay = g.Settings.CapPerDay,
            role = g.Role,
            me = g.Me,
            owner = g.Owner,
            heir = g.Heir,
            accept = g.Accept,
            offers = g.Offers.Count,
            local = g.Local,
            pool = g.Pool,
            dissolved = g.Dissolved,
            joining = g.Joining,
            noWayIn = g.NoWayIn,
            members = g.Members.Select(m => new
            {
                id = m.Id,
                name = m.Name,
                fingerprint = m.Fingerprint,
                role = m.Role,
                invitedBy = names.GetValueOrDefault(m.InvitedBy, ""),
                joined = m.Joined,
                peer = m.Peer,
                paired = m.Peer.Length > 0 && byId.TryGetValue(m.Peer, out var p) && p.State == PeerState.Paired,
                individual = m.Peer.Length > 0 && byId.TryGetValue(m.Peer, out var ip) && ip.Individual,
                refused = m.Refused,
                asked = m.Asked,
                requested = g.Requests.Any(r => r.From == m.Id),
                me = m.Id == g.Me,
            }),
            banned = g.Banned.Select(b => new { id = b.Id, name = b.Name, fingerprint = b.Fingerprint, reason = b.Reason }),
            requests = g.Requests.Select(r => new { mail = r.Mail, from = r.From, name = r.Name, fingerprint = r.Fingerprint, received = r.Received }),
            joins = g.Joins.Select(j => new { peer = j.Peer, name = j.Name, fingerprint = j.Fingerprint, received = j.Received }),
            links = peers.Where(p => p.Groups.Contains(g.Id)).Select(p => new { peer = p.Id, name = p.Name, only = !p.Individual && !p.IndividualAsked && p.Groups.Count == 1 }),
        };
    }

    // An action that answers nothing but that it was done.
    private Task<ContentResult> Act(Func<MsfpEngine, Task> action) => Run(async e =>
    {
        await action(e).ConfigureAwait(false);
        return (object)new { ok = true };
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
            var message = e.Code == MsfpError.NoWayIn
                ? "This server has no pairing through this group any more: nothing it sends reaches the other members, and nothing from them reaches it."
                : e.Message;
            return Json(new { error = message, code = e.Code.ToString() }, e.Code == MsfpError.NotFound ? 404 : 400);
        }
    }

    private Task<ContentResult> Run<T>(Func<MsfpEngine, Task<T>> action, bool off = false) =>
        Run(async e => (object)(await action(e).ConfigureAwait(false))!, off);

    private object View(PeerView p) => new
    {
        core = p.Core,
        local = p.Local,
        lastRead = _host.Reads.Of(p.Id) is { } r ? new { at = r.At, titles = r.Titles, error = r.Error } : null,
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
        groups = p.Groups,
        individual = p.Individual,
        asked = p.IndividualAsked,
        asks = p.IndividualAsks,
        keepChoice = p.KeepChoice,
        joiningGroup = p.JoiningGroup,
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
