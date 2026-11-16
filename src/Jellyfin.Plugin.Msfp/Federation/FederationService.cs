using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Msfp.Federation;

/// <summary>
/// This server's address and name, as the operator set them, and the pins
/// the operator set (empty: the plugin reads its own certificate).
/// </summary>
public sealed record ServerSettings(string Address, string Name, IReadOnlyList<string>? Pins = null);

/// <summary>An answer to a federation call, as bytes: what the controller sends.</summary>
public sealed record Answer(int Status, byte[] Body, string? ContentType, string? Signature)
{
    /// <summary>The 404 problem of MSFP section 8.2, with its trailing line feed.</summary>
    public static readonly byte[] NotFoundBody = Encoding.ASCII.GetBytes("{\"type\":\"not-found\",\"title\":\"Not found\",\"status\":404}\n");

    /// <summary>The unsigned 404 every failed check gets.</summary>
    public static readonly Answer NotFound = new(404, NotFoundBody, "application/problem+json", null);
}

/// <summary>What became of a call this server made to a peer.</summary>
public enum CallOutcome
{
    /// <summary>A 200 whose signature verified.</summary>
    Ok,

    /// <summary>A refusal whose signature verified (or any refusal on pair).</summary>
    Refused,

    /// <summary>No answer, or one that doesn't count: change nothing.</summary>
    Unreachable,

    /// <summary>The peer's certificate was refused (a checked call, section 3.1).</summary>
    CertificateRefused,
}

/// <summary>The result of a call to a peer.</summary>
public sealed record CallResult(CallOutcome Outcome, int Status, JsonElement? Json)
{
    public static readonly CallResult Unreachable = new(CallOutcome.Unreachable, 0, null);
}

/// <summary>What redeeming a code gave, for the operator.</summary>
public sealed record RedeemResult(bool Ok, string Message, Peer? Peer);

/// <summary>
/// Federation as far as pairing goes (MSFP sections 6, 7, 9.1 and 9.2):
/// answering pair and hello, and the operator's side (making and redeeming
/// codes, accepting, ending, asking where a pairing stands).
/// </summary>
public sealed class FederationService : IDisposable
{
    /// <summary>The largest request body read: 64 KiB.</summary>
    public const int MaxRequestBody = 64 * 1024;

    /// <summary>The largest answer body read: 4 MiB.</summary>
    public const int MaxAnswerBody = 4 * 1024 * 1024;

    public const string PairPath = "/api/v1/federation/pair";
    public const string HelloPath = "/api/v1/federation/hello";
    public const string LibrariesPath = "/api/v1/federation/libraries";
    public const string TitlesPath = "/api/v1/federation/titles";

    /// <summary>The longest `after` of a titles request, in bytes.</summary>
    public const int MaxAfter = 512;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ServerKey _key;
    private readonly PeerStore _store;
    private readonly TimeProvider _clock;
    private readonly Func<ServerSettings> _settings;
    /// <summary>How often this server reads its own certificate at most.</summary>
    public static readonly TimeSpan ReadOwnCertificateEvery = TimeSpan.FromMinutes(1);

    private readonly IPeerTransport _transport;
    private readonly ICertificateReader _reader;
    private readonly ILogger _log;
    private readonly RequestCheck _check;
    private readonly SemaphoreSlim _ownPinsLock = new(1, 1);
    private (string Address, DateTimeOffset At, IReadOnlyList<string> Pins)? _ownReading;

    public FederationService(ServerKey key, PeerStore store, TimeProvider clock, Func<ServerSettings> settings, IPeerTransport transport, ICertificateReader? reader = null, ILogger? log = null, OwnCatalog? catalog = null)
    {
        Catalog = catalog;
        _key = key;
        _store = store;
        _clock = clock;
        _settings = settings;
        _transport = transport;
        _reader = reader ?? new TlsCertificateReader();
        _log = log ?? NullLogger.Instance;
        Nonces = new NonceMemory(clock);
        _check = new RequestCheck(clock, Nonces);
    }

    /// <summary>This server's catalog, for libraries and titles; null: none offered.</summary>
    public OwnCatalog? Catalog { get; }

    /// <summary>This server's key.</summary>
    public ServerKey Key => _key;

    /// <summary>The nonce memory, begun when this service was made.</summary>
    public NonceMemory Nonces { get; }

    /// <summary>
    /// This server's own address, when it is valid: the configured one,
    /// without a trailing slash. Null: federation is off.
    /// </summary>
    public string? OwnAddress
    {
        get
        {
            var address = _settings().Address.Trim();
            if (address.EndsWith('/'))
            {
                address = address[..^1];
            }

            return Address.IsValid(address) ? address : null;
        }
    }

    /// <summary>This server's name, cleaned; the address's host when empty.</summary>
    public string OwnName(string ownAddress)
    {
        Address.TryGetHost(ownAddress, out var host);
        return Names.Clean(_settings().Name, host);
    }

    public void Dispose()
    {
        _ownPinsLock.Dispose();
        (_transport as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The pins this server names in hello (section 3.1): none over http;
    /// the operator's when set; otherwise the pin of the leaf served at its
    /// own address, read again when the last reading is a minute old. A
    /// failed reading keeps the last good one.
    /// </summary>
    public async Task<IReadOnlyList<string>> OwnPinsAsync(CancellationToken ct = default)
    {
        var own = OwnAddress;
        if (own == null || !own.StartsWith("https://", StringComparison.Ordinal))
        {
            return Array.Empty<string>();
        }

        var configured = (_settings().Pins ?? Array.Empty<string>()).Where(Pins.IsValid).Distinct().Take(Pins.MaxPins).ToList();
        if (configured.Count > 0)
        {
            return configured;
        }

        await _ownPinsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            var last = _ownReading is { } r && r.Address == own ? r.Pins : null;
            if (last != null && now - _ownReading!.Value.At < ReadOwnCertificateEvery)
            {
                return last;
            }

            var leaf = await _reader.ReadLeafAsync(own, ct).ConfigureAwait(false);
            IReadOnlyList<string>? pins = null;
            if (leaf != null)
            {
                try
                {
                    pins = new[] { Pins.Of(leaf) };
                }
                catch (System.Formats.Asn1.AsnContentException)
                {
                }
            }

            if (pins == null)
            {
                _log.LogInformation("MSFP: couldn't read the certificate served at {Address}", own);
                pins = last ?? Array.Empty<string>();
            }

            _ownReading = (own, now, pins);
            return pins;
        }
        finally
        {
            _ownPinsLock.Release();
        }
    }

    // ---- Answering calls ------------------------------------------------

    /// <summary>
    /// Answers a federation call: the call's name (pair or hello), the raw
    /// request target, the signature headers and the body. Everything before
    /// the call's own code (the checks of section 6.4) is here.
    /// </summary>
    public async Task<Answer> HandleAsync(string call, string target, SignatureHeaders headers, byte[] body, CancellationToken ct = default)
    {
        var own = OwnAddress;
        if (own == null || body.Length > MaxRequestBody || !Address.TryGetHost(own, out var host))
        {
            return Answer.NotFound;
        }

        var isPair = call == "pair";
        if (!isPair && call is not ("hello" or "libraries" or "titles"))
        {
            return Answer.NotFound;
        }

        var now = _clock.GetUtcNow();
        var result = _check.Check(host, "POST", target, headers, body, key => Hears(key, isPair, now));
        switch (result.Outcome)
        {
            case CheckOutcome.RefusedSigned:
                return Signed(result.Nonce!, 404, Answer.NotFoundBody, "application/problem+json");
            case CheckOutcome.Accepted:
                break;
            default:
                return Answer.NotFound;
        }

        try
        {
            return call switch
            {
                "pair" => HandlePair(result, body, own, now),
                "hello" => await HandleHelloAsync(result, body, now, ct).ConfigureAwait(false),
                "libraries" => HandleLibraries(result, body, own),
                _ => HandleTitles(result, body),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.LogError(e, "MSFP: storing failed");
            return Refusal(result, 500, "internal-error", "Internal error");
        }
    }

    /// <summary>
    /// Whom this server hears (section 6.5): its peers in any state; on pair,
    /// anyone while a code is live, forgotten keys included (SPEC-GAPS.md);
    /// forgotten keys otherwise get a signed 404.
    /// </summary>
    private Hearing Hears(byte[] key, bool isPair, DateTimeOffset now)
    {
        var text = Encodings.Base64Url(key);
        var hash = PeerStore.KeyHash(key);
        return _store.Read(d =>
        {
            if (d.Find(text) != null)
            {
                return Hearing.Peer;
            }

            if (isPair && d.HasLiveCode(now))
            {
                return Hearing.Stranger;
            }

            return d.Forgotten.Contains(hash) ? Hearing.Forgotten : Hearing.NotHeard;
        });
    }

    private Answer HandlePair(CheckResult req, byte[] body, string own, DateTimeOffset now)
    {
        // A bad body, no such code, a wrong proof: 404 (signed to a peer).
        if (!TryReadObject(body, out var json)
            || !TryGetString(json, "address", out var address) || address == null || !Address.TryGetHost(address, out var callerHost)
            || !TryGetString(json, "secretHash", out var hashText) || hashText == null
            || !Encodings.TryDecodeBase64Url(hashText, out var hash) || hash.Length != 32
            || !TryGetString(json, "proof", out var proofText) || proofText == null
            || !Encodings.TryDecodeBase64Url(proofText, out var proof)
            || !TryGetString(json, "name", out var name)
            || req.KeyText == _key.PublicKeyText)
        {
            return NotFoundFor(req);
        }

        var nowSeconds = now.ToUnixTimeSeconds();
        var canonicalHash = Encodings.Base64Url(hash);
        var code = _store.Read(d => d.Codes.FirstOrDefault(c => c.SecretHash == canonicalHash && c.Expires > nowSeconds));
        if (code == null || !Encodings.TryDecodeBase64Url(code.Secret, out var secret)
            || !PairingProof.Check(secret, req.Key!, Encoding.UTF8.GetBytes(address), proof))
        {
            return NotFoundFor(req);
        }

        // Already a peer: 409, empty body, signed since the key is known.
        if (req.Hearing == Hearing.Peer)
        {
            return Signed(req.Nonce!, 409, Array.Empty<byte>(), null);
        }

        var stored = _store.Change(d =>
        {
            // Checked again under the lock: one code works once.
            var live = d.Codes.FirstOrDefault(c => c.SecretHash == canonicalHash && c.Expires > nowSeconds);
            if (live == null || d.Find(req.KeyText!) != null)
            {
                return false;
            }

            d.Codes.Remove(live);
            d.Forgotten.Remove(PeerStore.KeyHash(req.Key!));
            d.Peers.Add(new Peer
            {
                Key = req.KeyText!,
                Address = address,
                Name = Names.Clean(name, callerHost),
                State = PeerState.Ours,
                Added = now,
                LastHeard = now,
            });
            return true;
        });
        if (!stored)
        {
            return NotFoundFor(req);
        }

        _log.LogInformation("MSFP: {Address} used a pairing code; waiting for the operator to accept", address);
        return Json(req, 200, w => w.WriteString("name", OwnName(own)));
    }

    /// <summary>The peer behind a request, when it is paired; null otherwise.</summary>
    private Peer? Paired(CheckResult req) =>
        _store.Read(d => d.Find(req.KeyText!) is { State: PeerState.Paired } p ? Copy(p) : null);

    /// <summary>libraries (section 9.3): what is offered to this peer.</summary>
    private Answer HandleLibraries(CheckResult req, byte[] body, string own)
    {
        var peer = Paired(req);
        if (peer == null || !TryReadObject(body, out _))
        {
            return NotFoundFor(req);
        }

        var offered = Catalog?.Offered(peer.Offered) ?? Array.Empty<LibraryInfo>();
        return Json(req, 200, w =>
        {
            w.WriteString("name", OwnName(own));
            w.WriteStartArray("libraries");
            foreach (var lib in offered)
            {
                w.WriteStartObject();
                w.WriteString("id", Catalog!.PeerId(lib.Id));
                w.WriteString("name", Names.Clean(lib.Name, "?"));
                w.WriteString("kind", lib.Kind);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        });
    }

    /// <summary>titles (section 9.4): a page of what the peer may see.</summary>
    private Answer HandleTitles(CheckResult req, byte[] body)
    {
        var peer = Paired(req);
        if (peer == null || !TryReadObject(body, out var json)
            || !json.TryGetProperty("libraries", out var libsField) || libsField.ValueKind != JsonValueKind.Array
            || !TryGetString(json, "after", out var after) || Encoding.UTF8.GetByteCount(after ?? string.Empty) > MaxAfter)
        {
            return NotFoundFor(req);
        }

        var requested = new List<string>();
        foreach (var e in libsField.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String)
            {
                return NotFoundFor(req);
            }

            requested.Add(e.GetString()!);
        }

        long since = 0;
        if (json.TryGetProperty("since", out var sinceField) && sinceField.ValueKind != JsonValueKind.Null)
        {
            if (sinceField.ValueKind != JsonValueKind.Number || !sinceField.TryGetInt64(out since))
            {
                return NotFoundFor(req);
            }
        }

        var page = Catalog == null
            ? new TitlesPage(Array.Empty<TitleOut>(), Array.Empty<string>(), null, false, 1)
            : Catalog.Page(requested, peer.Offered, after ?? string.Empty, since, peer.OffersSeq);
        return Json(req, 200, w =>
        {
            w.WriteStartArray("titles");
            foreach (var t in page.Titles)
            {
                w.WriteStartObject();
                w.WriteString("key", t.Key);
                w.WriteString("kind", t.Kind);
                w.WriteString("library", t.Library);
                WriteTags(w, "audio", t.Audio);
                WriteTags(w, "subtitles", t.Subtitles);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            if (page.Next != null)
            {
                w.WriteString("next", page.Next);
            }

            if (page.Changes)
            {
                w.WriteBoolean("changes", true);
                w.WriteStartArray("gone");
                foreach (var g in page.Gone)
                {
                    w.WriteStringValue(g);
                }

                w.WriteEndArray();
            }

            w.WriteNumber("seq", page.Seq);
        });
    }

    private static void WriteTags(Utf8JsonWriter w, string name, IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return;
        }

        w.WriteStartArray(name);
        foreach (var t in tags)
        {
            w.WriteStringValue(t);
        }

        w.WriteEndArray();
    }

    /// <summary>
    /// Sets which of this server's libraries a peer is offered (media server
    /// IDs). A change starts a new seq for that peer: its next reading is a
    /// whole one. Null when the peer is unknown; unknown libraries refused.
    /// </summary>
    public Peer? SetOffers(string peerKey, IEnumerable<string> libraries)
    {
        var known = (Catalog?.Libraries ?? Array.Empty<LibraryInfo>()).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var wanted = libraries.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Any(l => !known.Contains(l)))
        {
            throw new ArgumentException("not a library of this server");
        }

        var seq = Catalog?.Bump() ?? 0;
        return _store.Change(d =>
        {
            if (d.Find(peerKey) is not { } p)
            {
                return null;
            }

            p.Offered = wanted;
            p.OffersSeq = seq;
            return Copy(p);
        });
    }

    /// <summary>Sets which of a peer's libraries this server reads: all, or these.</summary>
    public Peer? SetWants(string peerKey, bool all, IEnumerable<string> libraries)
    {
        return _store.Change(d =>
        {
            if (d.Find(peerKey) is not { } p)
            {
                return null;
            }

            p.WantAll = all;
            p.Wanted = libraries.Distinct(StringComparer.Ordinal).ToList();
            return Copy(p);
        });
    }

    /// <summary>Sets a peer's name, cleaned, as its libraries answer gives it.</summary>
    internal void Rename(string peerKey, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        _store.Change(d =>
        {
            if (d.Find(peerKey) is { } p)
            {
                Address.TryGetHost(p.Address, out var host);
                p.Name = Names.Clean(name, host);
            }

            return true;
        });
    }

    private async Task<Answer> HandleHelloAsync(CheckResult req, byte[] body, DateTimeOffset now, CancellationToken ct)
    {
        if (!TryReadObject(body, out var json) || !TryGetString(json, "state", out var state) || state == null
            || !Pins.TryRead(json, out var theirPins))
        {
            return NotFoundFor(req);
        }

        if (state == "ended")
        {
            _store.Change(d => d.Peers.RemoveAll(p => p.Key == req.KeyText));
            _log.LogInformation("MSFP: a peer ended its pairing");
            return Json(req, 200, w => w.WriteString("state", "ended"));
        }

        var view = _store.Change(d =>
        {
            var peer = d.Find(req.KeyText!);
            if (peer == null)
            {
                return "waiting";
            }

            if (state == "paired")
            {
                ApplyPaired(peer);
            }

            // The latest signed hello's pins replace the last ones.
            peer.Pins = theirPins;
            peer.LastHeard = now;
            return View(peer);
        });
        var ours = await OwnPinsAsync(ct).ConfigureAwait(false);
        return Json(req, 200, w =>
        {
            w.WriteString("state", view);
            WritePins(w, ours);
        });
    }

    /// <summary>What a paired hello does (section 9.2), on either side.</summary>
    private static void ApplyPaired(Peer peer)
    {
        if (peer.State == PeerState.Theirs || (peer.State == PeerState.Gone && peer.WasPaired))
        {
            peer.State = PeerState.Paired;
            peer.WasPaired = true;
        }
    }

    /// <summary>The hello view of a state (section 7.3).</summary>
    private static string View(Peer peer) => peer.State == PeerState.Paired ? "paired" : "waiting";

    private Answer NotFoundFor(CheckResult req)
    {
        return Refusal(req, 404, "not-found", "Not found");
    }

    /// <summary>A refusal: signed to a peer, plain to a stranger (section 6.6).</summary>
    private Answer Refusal(CheckResult req, int status, string type, string title)
    {
        var body = status == 404 ? Answer.NotFoundBody : Problem(status, type, title);
        return req.Hearing == Hearing.Peer
            ? Signed(req.Nonce!, status, body, "application/problem+json")
            : new Answer(status, body, "application/problem+json", null);
    }

    private static byte[] Problem(int status, string type, string title)
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            w.WriteString("title", title);
            w.WriteNumber("status", status);
            w.WriteEndObject();
        }

        ms.WriteByte((byte)'\n');
        return ms.ToArray();
    }

    private Answer Json(CheckResult req, int status, Action<Utf8JsonWriter> fields)
    {
        return Signed(req.Nonce!, status, JsonObject(fields), "application/json");
    }

    private Answer Signed(string nonce, int status, byte[] body, string? contentType)
    {
        return new Answer(status, body, contentType, Signing.SignAnswer(_key, nonce, status, body));
    }

    // ---- The operator's side --------------------------------------------

    /// <summary>Makes a pairing code: stores its secret and expiry, returns it.</summary>
    public PairingCode MakeCode()
    {
        var own = OwnAddress ?? throw new InvalidOperationException("Set this server's address first.");
        var now = _clock.GetUtcNow();
        var code = PairingCode.Create(_key.PublicKey, own, now);
        _store.Change(d =>
        {
            d.Codes.RemoveAll(c => c.Expires <= now.ToUnixTimeSeconds());
            d.Codes.Add(new StoredCode
            {
                SecretHash = Encodings.Base64Url(PairingProof.SecretHash(code.Secret)),
                Secret = Encodings.Base64Url(code.Secret),
                Expires = (long)code.Expires,
            });
            return true;
        });
        return code;
    }

    /// <summary>Stores a code made elsewhere (tests: the vectors' code).</summary>
    internal void AddCode(byte[] secret, long expires)
    {
        _store.Change(d =>
        {
            d.Codes.Add(new StoredCode
            {
                SecretHash = Encodings.Base64Url(PairingProof.SecretHash(secret)),
                Secret = Encodings.Base64Url(secret),
                Expires = expires,
            });
            return true;
        });
    }

    /// <summary>The peers, for the operator's page.</summary>
    public IReadOnlyList<Peer> Peers() => _store.Read(d => d.Peers.Select(Copy).ToList());

    /// <summary>
    /// Redeems a peer's code (section 7.1, steps 2, 3 and 5): calls pair on
    /// the code's address, and stores the peer as "theirs" once the signed
    /// answer is in.
    /// </summary>
    public async Task<RedeemResult> RedeemAsync(string text, CancellationToken ct = default)
    {
        var own = OwnAddress;
        if (own == null)
        {
            return new RedeemResult(false, "Set this server's address first.", null);
        }

        if (!PairingCode.TryParse(text, out var code))
        {
            return new RedeemResult(false, "This isn't a valid pairing code.", null);
        }

        var peerKey = Encodings.Base64Url(code.PublicKey);
        if (peerKey == _key.PublicKeyText)
        {
            return new RedeemResult(false, "This code is this server's own.", null);
        }

        if (_store.Read(d => d.Find(peerKey)) != null)
        {
            return new RedeemResult(false, "This server is already a peer.", null);
        }

        var addressBytes = Encoding.UTF8.GetBytes(own);
        var body = JsonObject(w =>
        {
            w.WriteString("address", own);
            w.WriteString("name", OwnName(own));
            w.WriteString("secretHash", Encodings.Base64Url(PairingProof.SecretHash(code.Secret)));
            w.WriteString("proof", Encodings.Base64Url(PairingProof.Compute(code.Secret, _key.PublicKey, addressBytes)));
        });

        var result = await CallAsync(code.Address, code.PublicKey, PairPath, body, isPair: true, _transport.Any, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case CallOutcome.Ok:
                break;
            case CallOutcome.Refused:
                return new RedeemResult(false, result.Status == 409
                    ? "That server already knows this one as a peer."
                    : $"That server refused the code ({result.Status}): it may have expired or been used.", null);
            default:
                return new RedeemResult(false, "That server couldn't be reached, or its answer wasn't signed by the code's key.", null);
        }

        Address.TryGetHost(code.Address, out var host);
        string? name = null;
        if (result.Json is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
        {
            name = n.GetString();
        }

        var now = _clock.GetUtcNow();
        var peer = _store.Change(d =>
        {
            if (d.Find(peerKey) is { } existing)
            {
                return Copy(existing);
            }

            d.Forgotten.Remove(PeerStore.KeyHash(code.PublicKey));
            var p = new Peer
            {
                Key = peerKey,
                Address = code.Address,
                Name = Names.Clean(name, host),
                State = PeerState.Theirs,
                Added = now,
                LastHeard = now,
            };
            d.Peers.Add(p);
            return Copy(p);
        });
        return new RedeemResult(true, "Paired on this side; waiting for the other operator to accept.", peer);
    }

    /// <summary>
    /// Accepts a peer that used our code (section 7.1, step 6): paired here,
    /// then a paired hello to tell it.
    /// </summary>
    public async Task<Peer?> AcceptAsync(string peerKey, CancellationToken ct = default)
    {
        var accepted = _store.Change(d =>
        {
            var p = d.Find(peerKey);
            if (p == null || p.State != PeerState.Ours)
            {
                return false;
            }

            p.State = PeerState.Paired;
            p.WasPaired = true;
            return true;
        });
        if (!accepted)
        {
            return null;
        }

        return await HelloAsync(peerKey, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a hello with this server's view and acts on the answer
    /// (section 9.2): paired on a signed paired answer, gone on a signed 404.
    /// Returns the peer as it is afterwards, or null when it's unknown.
    /// </summary>
    public async Task<Peer?> HelloAsync(string peerKey, CancellationToken ct = default)
    {
        var peer = _store.Read(d => d.Find(peerKey) is { } p ? Copy(p) : null);
        if (peer == null || !Encodings.TryDecodeBase64Url(peerKey, out var key))
        {
            return null;
        }

        var ours = await OwnPinsAsync(ct).ConfigureAwait(false);
        var body = JsonObject(w =>
        {
            w.WriteString("state", View(peer));
            WritePins(w, ours);
        });
        var result = await CallAsync(peer.Address, key, HelloPath, body, isPair: false, _transport.Any, ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        return _store.Change(d =>
        {
            var p = d.Find(peerKey);
            if (p == null)
            {
                return null;
            }

            if (result.Outcome == CallOutcome.Ok)
            {
                p.LastHeard = now;

                // A signed answer's pins replace the last ones; a field
                // that isn't a list counts as none (SPEC-GAPS.md, 3.1).
                p.Pins = Pins.TryRead(result.Json!.Value, out var named) ? named : new List<string>();
                if (result.Json is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("state", out var s)
                    && s.ValueKind == JsonValueKind.String && s.GetString() == "paired")
                {
                    ApplyPaired(p);
                }
            }
            else if (result.Outcome == CallOutcome.Refused && result.Status == 404)
            {
                p.LastHeard = now;
                p.State = PeerState.Gone;
            }

            return Copy(p);
        });
    }

    /// <summary>
    /// Ends a pairing, or refuses a waiting one (section 7.4): the peer
    /// deleted, its key forgotten, then an ended hello, best effort.
    /// </summary>
    public async Task<bool> EndAsync(string peerKey, CancellationToken ct = default)
    {
        if (!Encodings.TryDecodeBase64Url(peerKey, out var key))
        {
            return false;
        }

        var peer = _store.Change(d =>
        {
            var p = d.Find(peerKey);
            if (p == null)
            {
                return null;
            }

            d.Peers.Remove(p);
            var hash = PeerStore.KeyHash(key);
            if (!d.Forgotten.Contains(hash))
            {
                d.Forgotten.Add(hash);
            }

            return p;
        });
        if (peer == null)
        {
            return false;
        }

        var body = JsonObject(w => w.WriteString("state", "ended"));
        await CallAsync(peer.Address, key, HelloPath, body, isPair: false, _transport.Any, ct).ConfigureAwait(false);
        return true;
    }

    // ---- Calling peers ---------------------------------------------------

    /// <summary>
    /// Makes one signed call and checks the answer (section 6.6): a 200 or a
    /// refusal counts only when signed by the peer's key; on pair, refusals
    /// count signed or not.
    /// </summary>
    internal async Task<CallResult> CallAsync(string address, byte[] peerKey, string path, byte[] body, bool isPair, HttpMessageHandler handler, CancellationToken ct)
    {
        if (!Address.TryGetHost(address, out var host))
        {
            return CallResult.Unreachable;
        }

        var baseAddress = address.EndsWith('/') ? address[..^1] : address;
        var headers = Signing.SignRequest(_key, "POST", host, path, body, _clock.GetUtcNow());
        using var request = new HttpRequestMessage(HttpMethod.Post, baseAddress + path)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(SignatureHeaders.KeyHeader, headers.Key);
        request.Headers.Add(SignatureHeaders.DateHeader, headers.Date);
        request.Headers.Add(SignatureHeaders.NonceHeader, headers.Nonce);
        request.Headers.Add(SignatureHeaders.SignatureHeader, headers.Signature);

        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var answer = await ReadLimitedAsync(response.Content, ct).ConfigureAwait(false);
            string? signature = response.Headers.TryGetValues(SignatureHeaders.SignatureHeader, out var values) ? values.FirstOrDefault() : null;
            var signed = answer != null && Signing.CheckAnswer(peerKey, headers.Nonce!, status, answer, signature);

            if (status == 200)
            {
                if (!signed)
                {
                    return CallResult.Unreachable;
                }

                try
                {
                    using var doc = JsonDocument.Parse(answer);
                    return new CallResult(CallOutcome.Ok, 200, doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                    return CallResult.Unreachable;
                }
            }

            return signed || isPair ? new CallResult(CallOutcome.Refused, status, null) : CallResult.Unreachable;
        }
        catch (HttpRequestException e) when (IsCertificateRefusal(e))
        {
            _log.LogInformation("MSFP: {Address} served a certificate that is neither trusted nor named", address);
            return new CallResult(CallOutcome.CertificateRefused, 0, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            _log.LogInformation("MSFP: {Address} couldn't be reached: {Error}", address, e.Message);
            return CallResult.Unreachable;
        }
    }

    /// <summary>
    /// A call that needs a *trusted* or *named* certificate (section 3.1):
    /// every call but pair and hello. When the certificate is refused, one
    /// hello, and when it brought other pins, the call once more.
    /// </summary>
    internal async Task<CallResult> CallCheckedAsync(string peerKey, string path, byte[] body, CancellationToken ct = default)
    {
        var peer = _store.Read(d => d.Find(peerKey) is { } p ? Copy(p) : null);
        if (peer == null || !Encodings.TryDecodeBase64Url(peerKey, out var key))
        {
            return CallResult.Unreachable;
        }

        var result = await CallAsync(peer.Address, key, path, body, false, _transport.TrustedOrNamed(peerKey, peer.Pins), ct).ConfigureAwait(false);
        if (result.Outcome != CallOutcome.CertificateRefused)
        {
            return result;
        }

        var after = await HelloAsync(peerKey, ct).ConfigureAwait(false);
        if (after == null || after.Pins.SequenceEqual(peer.Pins))
        {
            return result;
        }

        return await CallAsync(after.Address, key, path, body, false, _transport.TrustedOrNamed(peerKey, after.Pins), ct).ConfigureAwait(false);
    }

    private static bool IsCertificateRefusal(Exception e)
    {
        for (var inner = e.InnerException; inner != null; inner = inner.InnerException)
        {
            if (inner is System.Security.Authentication.AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private static void WritePins(Utf8JsonWriter w, IReadOnlyList<string> pins)
    {
        // Left out when this server names none.
        if (pins.Count == 0)
        {
            return;
        }

        w.WriteStartArray("certificates");
        foreach (var pin in pins)
        {
            w.WriteStringValue(pin);
        }

        w.WriteEndArray();
    }

    /// <summary>The answer's body, or null when it is over 4 MiB.</summary>
    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxAnswerBody)
            {
                return null;
            }

            ms.Write(buffer, 0, n);
        }

        return ms.ToArray();
    }

    // ---- JSON ------------------------------------------------------------

    private static byte[] JsonObject(Action<Utf8JsonWriter> fields)
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
        {
            w.WriteStartObject();
            fields(w);
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    /// <summary>
    /// A request body as the plugin reads it: one JSON object, field names
    /// matched case-sensitively, no field twice, unknown fields ignored.
    /// </summary>
    internal static bool TryReadObject(byte[] body, out JsonElement json)
    {
        json = default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!seen.Add(p.Name))
                {
                    return false;
                }
            }

            json = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// A string field: true with null when absent (or null), true with the
    /// text when a string, false when of another type.
    /// </summary>
    internal static bool TryGetString(JsonElement json, string name, out string? value)
    {
        value = null;
        if (!json.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (e.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = e.GetString();
        return true;
    }

    private static Peer Copy(Peer p) => new()
    {
        Key = p.Key,
        Address = p.Address,
        Name = p.Name,
        State = p.State,
        WasPaired = p.WasPaired,
        Pins = p.Pins.ToList(),
        Offered = p.Offered.ToList(),
        OffersSeq = p.OffersSeq,
        WantAll = p.WantAll,
        Wanted = p.Wanted.ToList(),
        Added = p.Added,
        LastHeard = p.LastHeard,
    };
}
