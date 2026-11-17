using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Viewing;
using Msfp;
using Msfp.Tests;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>
/// Routes the plugin's outgoing calls to other FederationService instances
/// in the same process, as if over HTTP.
/// </summary>
internal sealed class Network : HttpMessageHandler, IPeerTransport
{
    public HttpMessageHandler Any => this;

    public HttpMessageHandler TrustedOrNamed(string peerKey, IReadOnlyList<string> pins) => this;

    public Dictionary<string, FederationService> Servers { get; } = new();

    public HashSet<string> Down { get; } = new();

    /// <summary>Changes answers on the way, like someone between the servers.</summary>
    public Func<HttpResponseMessage, HttpResponseMessage>? Tamper { get; set; }

    public int Calls { get; private set; }

    public List<string> Bodies { get; } = new();

    public List<string> Paths { get; } = new();

    /// <summary>Answers made up by a test for a host, signed with the given key.</summary>
    public Dictionary<string, (ServerKey Key, Func<string, string, (int Status, string Body)> Answer)> Fakes { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        var host = request.RequestUri!.Authority;
        Paths.Add(request.RequestUri.AbsolutePath);
        if (Fakes.TryGetValue(host, out var fake))
        {
            var sent = await request.Content!.ReadAsStringAsync(cancellationToken);
            var (status, answerBody) = fake.Answer(request.RequestUri.AbsolutePath, sent);
            var bytes = Encoding.UTF8.GetBytes(answerBody);
            var nonce = request.Headers.GetValues("Overfin-Nonce").Single();
            var made = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(bytes) };
            made.Headers.Add("Overfin-Signature", Signing.SignAnswer(fake.Key, nonce, status, bytes));
            return made;
        }

        if (Down.Contains(host) || !Servers.TryGetValue(host, out var server))
        {
            throw new HttpRequestException("unreachable");
        }

        string? H(string name) => request.Headers.TryGetValues(name, out var v) ? v.Single() : null;
        var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
        Bodies.Add(Encoding.UTF8.GetString(body));
        var call = request.RequestUri.AbsolutePath.Split('/').Last();
        var answer = await server.HandleAsync(call, request.RequestUri.PathAndQuery, new SignatureHeaders(H("Overfin-Key"), H("Overfin-Date"), H("Overfin-Nonce"), H("Overfin-Signature")), body, cancellationToken);
        var response = new HttpResponseMessage((HttpStatusCode)answer.Status) { Content = new ByteArrayContent(answer.Body) };
        if (answer.Signature != null)
        {
            response.Headers.Add("Overfin-Signature", answer.Signature);
        }

        return Tamper == null ? response : Tamper(response);
    }
}

/// <summary>A catalog source the tests fill by hand.</summary>
internal sealed class FakeSource : ICatalogSource
{
    public List<LibraryInfo> Libs { get; } = new();

    public List<CatalogEntry> Items { get; } = new();

    public IReadOnlyList<LibraryInfo> Libraries() => Libs.ToList();

    public IEnumerable<CatalogEntry> Entries() => Items.ToList();

    public void Movie(string library, int id, params string[] audio) =>
        Items.Add(new CatalogEntry(library, TitleKey.ForMovie(id.ToString(CultureInfo.InvariantCulture)), TitleKey.Movie, audio, Array.Empty<string>()));
}

/// <summary>A certificate reader the tests set by hand.</summary>
internal sealed class FakeReader : ICertificateReader
{
    public byte[]? Leaf { get; set; }

    public int Reads { get; private set; }

    public Task<byte[]?> ReadLeafAsync(string address, CancellationToken ct)
    {
        Reads++;
        return Task.FromResult(Leaf);
    }
}

/// <summary>One server under test, on a shared network and clock.</summary>
internal sealed class TestServer
{
    public TestServer(Network network, ManualClock clock, string address, string name, ServerKey? key = null)
    {
        Address = address;
        Store = new PeerStore(null);
        Settings = new ServerSettings(address, name);
        Catalog = new OwnCatalog(Source, clock, null);
        Service = new FederationService(key ?? ServerKey.Generate(), Store, clock, () => Settings, network, Reader, null, Catalog);
        Catalogs = new CatalogReader(Service, new PeerCatalogStore(null), clock);
        global::Msfp.Address.TryGetHost(address, out var host);
        network.Servers[host] = Service;
    }

    public string Address { get; }

    public FakeReader Reader { get; } = new();

    public FakeSource Source { get; } = new();

    public OwnCatalog Catalog { get; }

    public CatalogReader Catalogs { get; }

    public PeerStore Store { get; }

    public ServerSettings Settings { get; set; }

    public FederationService Service { get; }

    public string KeyText => Service.Key.PublicKeyText;

    public Peer? PeerOf(TestServer other) => Service.Peers().SingleOrDefault(p => p.Key == other.KeyText);
}

public class FederationVectorTests
{
    private static JsonElement Exchange(string name) =>
        Vectors.Root.GetProperty("exchanges").EnumerateArray().Single(e => e.Str("name") == name);

    private static ServerKey VectorKey(string name) => ServerKey.FromSeed(Vectors.Key(name).Hex("seed"));

    private static (FederationService Service, PeerStore Store) Server(string key, string address, string name, long date)
    {
        var clock = ManualClock.AtSeconds(date - 60);
        var store = new PeerStore(null);
        var service = new FederationService(VectorKey(key), store, clock, () => new ServerSettings(address, name), new Network(), new FakeReader());
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(date);
        return (service, store);
    }

    private static Answer Send(FederationService service, JsonElement e)
    {
        var headers = new SignatureHeaders(VectorKey(e.Str("signer")).PublicKeyText, e.Str("date"), e.Str("nonceText"), e.Str("signature"));
        return service.HandleAsync(e.Str("name") == "pair" ? "pair" : "hello", e.Str("target"), headers, Encoding.UTF8.GetBytes(e.Str("body"))).GetAwaiter().GetResult();
    }

    private static long Date(JsonElement e) => long.Parse(e.Str("date"), CultureInfo.InvariantCulture);

    [Fact]
    public void ThePairExchangeByteForByte()
    {
        var e = Exchange("pair");
        var code = Vectors.Root.GetProperty("code");
        var (a, store) = Server("a", "https://a.example.org", "Server A", Date(e));
        a.AddCode(code.Hex("secret"), code.GetProperty("expires").GetInt64());

        var answer = Send(a, e);
        Assert.Equal(e.GetProperty("status").GetInt32(), answer.Status);
        Assert.Equal(e.Str("answerBody"), Encoding.UTF8.GetString(answer.Body));
        Assert.Equal(e.Str("answerSignature"), answer.Signature);
        Assert.Equal("application/json", answer.ContentType);

        var peer = Assert.Single(a.Peers());
        Assert.Equal(VectorKey("b").PublicKeyText, peer.Key);
        Assert.Equal("http://192.0.2.10:8096", peer.Address);
        Assert.Equal("Server B", peer.Name);
        Assert.Equal(PeerState.Ours, peer.State);
        Assert.Empty(store.Read(d => d.Codes));
    }

    [Fact]
    public void TheHelloExchangeByteForByte()
    {
        var e = Exchange("hello");
        var (b, store) = Server("b", "http://192.0.2.10:8096", "Server B", Date(e));
        store.Change(d =>
        {
            d.Peers.Add(new Peer { Key = VectorKey("a").PublicKeyText, Address = "https://a.example.org", Name = "Server A", State = PeerState.Theirs });
            return true;
        });

        var answer = Send(b, e);
        Assert.Equal(200, answer.Status);
        Assert.Equal(e.Str("answerBody"), Encoding.UTF8.GetString(answer.Body));
        Assert.Equal(e.Str("answerSignature"), answer.Signature);
        Assert.Equal(PeerState.Paired, Assert.Single(b.Peers()).State);
    }

    [Fact]
    public void TheRefusedHelloExchangeByteForByte()
    {
        var e = Exchange("refused hello");
        var (b, store) = Server("b", "http://192.0.2.10:8096", "Server B", Date(e));
        store.Change(d =>
        {
            d.Forgotten.Add(PeerStore.KeyHash(VectorKey("a").PublicKey));
            return true;
        });

        var answer = Send(b, e);
        Assert.Equal(404, answer.Status);
        Assert.Equal(e.Str("answerBody"), Encoding.UTF8.GetString(answer.Body));
        Assert.Equal("application/problem+json", answer.ContentType);
        Assert.Equal(e.Str("answerSignature"), answer.Signature);
    }

    [Fact]
    public void TheSameHelloFromAStrangerGetsAPlain404()
    {
        var e = Exchange("hello");
        var (b, _) = Server("b", "http://192.0.2.10:8096", "Server B", Date(e));
        var answer = Send(b, e);
        Assert.Equal(404, answer.Status);
        Assert.Null(answer.Signature);
        Assert.Empty(answer.Body);
    }

    [Fact]
    public void ThePairExchangeAtAnotherHostFails()
    {
        var e = Exchange("pair");
        var code = Vectors.Root.GetProperty("code");
        var (a, _) = Server("a", "https://a2.example.org", "Server A", Date(e));
        a.AddCode(code.Hex("secret"), code.GetProperty("expires").GetInt64());
        var answer = Send(a, e);
        Assert.Equal(404, answer.Status);
        Assert.Null(answer.Signature);
        Assert.Empty(a.Peers());
    }
}

public class FederationTests
{
    private readonly ManualClock _clock = ManualClock.AtSeconds(1_800_000_000);
    private readonly Network _net = new();
    private readonly TestServer _a;
    private readonly TestServer _b;

    public FederationTests()
    {
        _a = new TestServer(_net, _clock, "https://a.example.org", "Server A");
        _b = new TestServer(_net, _clock, "http://192.0.2.10:8096", "Server B");

        // Requests dated the second the servers started are refused.
        _clock.Advance(TimeSpan.FromSeconds(1));
    }

    private async Task PairAsync()
    {
        var code = _a.Service.MakeCode();
        var redeemed = await _b.Service.RedeemAsync(code.Format());
        Assert.True(redeemed.Ok, redeemed.Message);
        await _a.Service.AcceptAsync(_b.KeyText);
    }

    [Fact]
    public async Task PairingStepByStep()
    {
        var code = _a.Service.MakeCode();
        var redeemed = await _b.Service.RedeemAsync(code.Format());
        Assert.True(redeemed.Ok, redeemed.Message);

        var aAtB = _b.PeerOf(_a)!;
        Assert.Equal(PeerState.Theirs, aAtB.State);
        Assert.Equal("Server A", aAtB.Name);
        Assert.Equal("https://a.example.org", aAtB.Address);

        var bAtA = _a.PeerOf(_b)!;
        Assert.Equal(PeerState.Ours, bAtA.State);
        Assert.Equal("Server B", bAtA.Name);
        Assert.Equal("http://192.0.2.10:8096", bAtA.Address);

        // B asks before A's operator accepts: still waiting.
        Assert.Equal(PeerState.Theirs, (await _b.Service.HelloAsync(_a.KeyText))!.State);

        await _a.Service.AcceptAsync(_b.KeyText);
        Assert.Equal(PeerState.Paired, _a.PeerOf(_b)!.State);
        Assert.Equal(PeerState.Paired, _b.PeerOf(_a)!.State);
    }

    [Fact]
    public async Task BLearnsAtItsNextHelloWhenItMissedTheAcceptance()
    {
        var code = _a.Service.MakeCode();
        Assert.True((await _b.Service.RedeemAsync(code.Format())).Ok);
        _net.Down.Add("192.0.2.10:8096");
        await _a.Service.AcceptAsync(_b.KeyText);
        Assert.Equal(PeerState.Paired, _a.PeerOf(_b)!.State);
        Assert.Equal(PeerState.Theirs, _b.PeerOf(_a)!.State);

        _net.Down.Clear();
        Assert.Equal(PeerState.Paired, (await _b.Service.HelloAsync(_a.KeyText))!.State);
    }

    [Fact]
    public async Task BCantMakeAAccept()
    {
        var code = _a.Service.MakeCode();
        Assert.True((await _b.Service.RedeemAsync(code.Format())).Ok);

        Assert.True(Encodings.TryDecodeBase64Url(_a.KeyText, out var aKey));
        var result = await _b.Service.CallAsync(_a.Address, aKey, FederationService.HelloPath, Encoding.UTF8.GetBytes("{\"state\":\"paired\"}"), false, _net, default);
        Assert.Equal(CallOutcome.Ok, result.Outcome);
        Assert.Equal("waiting", result.Json!.Value.GetProperty("state").GetString());
        Assert.Equal(PeerState.Ours, _a.PeerOf(_b)!.State);
    }

    [Fact]
    public async Task ACodeWorksOnce()
    {
        var code = _a.Service.MakeCode().Format();
        Assert.True((await _b.Service.RedeemAsync(code)).Ok);

        var c = new TestServer(_net, _clock, "https://c.example.org", "Server C");
        var again = await c.Service.RedeemAsync(code);
        Assert.False(again.Ok);
        Assert.Null(c.PeerOf(_a));
        Assert.Null(_a.PeerOf(c));
    }

    [Fact]
    public async Task AnExpiredCodeIsRefusedByItsMaker()
    {
        var code = _a.Service.MakeCode().Format();
        _clock.Advance(PairingCode.Lifetime);
        var result = await _b.Service.RedeemAsync(code);
        Assert.False(result.Ok);
        Assert.Empty(_a.Service.Peers());
        Assert.Empty(_b.Service.Peers());
    }

    [Fact]
    public async Task OwnCodeAndKnownPeersAreRefusedBeforeAnyCall()
    {
        var own = _a.Service.MakeCode().Format();
        Assert.False((await _a.Service.RedeemAsync(own)).Ok);

        await PairAsync();
        var calls = _net.Calls;
        Assert.False((await _b.Service.RedeemAsync(_a.Service.MakeCode().Format())).Ok);
        Assert.Equal(calls, _net.Calls);
    }

    [Fact]
    public async Task AKnownPeerRedeemingAgainGets409AndTheCodeStaysLive()
    {
        await PairAsync();
        var code = _a.Service.MakeCode();
        Assert.True(Encodings.TryDecodeBase64Url(_a.KeyText, out var aKey));
        var body = PairBody(_b, code);
        var result = await _b.Service.CallAsync(_a.Address, aKey, FederationService.PairPath, body, true, _net, default);
        Assert.Equal(CallOutcome.Refused, result.Outcome);
        Assert.Equal(409, result.Status);

        // Signed, so B's check passed even though refusals on pair needn't be.
        Assert.Equal(1, _a.Store.Read(d => d.Codes.Count));
    }

    [Fact]
    public void A409IsSignedWithAnEmptyBody()
    {
        _a.Store.Change(d =>
        {
            d.Peers.Add(new Peer { Key = _b.KeyText, Address = _b.Address, Name = "B", State = PeerState.Paired });
            return true;
        });
        var code = _a.Service.MakeCode();
        var answer = HandleSigned(_a, _b, "pair", PairBody(_b, code));
        Assert.Equal(409, answer.Status);
        Assert.Empty(answer.Body);
        Assert.NotNull(answer.Signature);
    }

    [Fact]
    public void AWrongProofIsAPlain404AndKeepsTheCode()
    {
        var code = _a.Service.MakeCode();
        var body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(PairBody(_b, code)).Replace("192.0.2.10", "192.0.2.11"));
        var answer = HandleSigned(_a, _b, "pair", body);
        Assert.Equal(404, answer.Status);
        Assert.Null(answer.Signature);
        Assert.Empty(_a.Service.Peers());
        Assert.Equal(1, _a.Store.Read(d => d.Codes.Count));
    }

    [Fact]
    public void StrangersArentHeardWithoutALiveCode()
    {
        var code = PairingCode.Create(_a.Service.Key.PublicKey, _a.Address, _clock.Now);
        var answer = HandleSigned(_a, _b, "pair", PairBody(_b, code));
        Assert.Equal(404, answer.Status);
        Assert.Null(answer.Signature);
        Assert.Equal(0, _a.Service.Nonces.Count);

        var hello = HandleSigned(_a, _b, "hello", Encoding.UTF8.GetBytes("{\"state\":\"paired\"}"));
        Assert.Equal(404, hello.Status);
        Assert.Null(hello.Signature);
    }

    [Fact]
    public void StrangersOnHelloArentHeardEvenDuringALiveCode()
    {
        _a.Service.MakeCode();
        var hello = HandleSigned(_a, _b, "hello", Encoding.UTF8.GetBytes("{\"state\":\"paired\"}"));
        Assert.Equal(404, hello.Status);
        Assert.Null(hello.Signature);
        Assert.Equal(0, _a.Service.Nonces.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"state\":1}")]
    [InlineData("{\"State\":\"paired\"}")]
    [InlineData("{\"state\":\"paired\",\"state\":\"ended\"}")]
    public async Task ABadHelloBodyGetsASigned404FromAPeer(string body)
    {
        await PairAsync();
        var answer = HandleSigned(_a, _b, "hello", Encoding.UTF8.GetBytes(body));
        Assert.Equal(404, answer.Status);
        Assert.NotNull(answer.Signature);
        Assert.Equal(PeerState.Paired, _a.PeerOf(_b)!.State);
    }

    [Fact]
    public async Task AnUnknownHelloStateIsTakenAsWaiting()
    {
        await PairAsync();
        var answer = HandleSigned(_a, _b, "hello", Encoding.UTF8.GetBytes("{\"state\":\"maybe\",\"protocol\":1}"));
        Assert.Equal(200, answer.Status);
        Assert.Equal("{\"state\":\"paired\"}", Encoding.UTF8.GetString(answer.Body));
    }

    [Fact]
    public async Task EndingTellsThePeer()
    {
        await PairAsync();
        Assert.True(await _b.Service.EndAsync(_a.KeyText));
        Assert.Null(_b.PeerOf(_a));
        Assert.Null(_a.PeerOf(_b));

        // B forgot A: A's calls get a signed 404.
        Assert.Contains(PeerStore.KeyHash(_a.Service.Key.PublicKey), _b.Store.Read(d => d.Forgotten));
        var answer = HandleSigned(_b, _a, "hello", Encoding.UTF8.GetBytes("{\"state\":\"paired\"}"));
        Assert.Equal(404, answer.Status);
        Assert.NotNull(answer.Signature);
    }

    [Fact]
    public async Task APeerThatMissedTheEndMarksTheEnderGone()
    {
        await PairAsync();
        _net.Down.Add("a.example.org");
        Assert.True(await _b.Service.EndAsync(_a.KeyText));
        _net.Down.Clear();
        Assert.Equal(PeerState.Paired, _a.PeerOf(_b)!.State);

        Assert.Equal(PeerState.Gone, (await _a.Service.HelloAsync(_b.KeyText))!.State);
    }

    [Fact]
    public async Task UnsignedRefusalsChangeNothing()
    {
        await PairAsync();
        _net.Tamper = r =>
        {
            r.Headers.Remove("Overfin-Signature");
            r.StatusCode = HttpStatusCode.NotFound;
            return r;
        };
        Assert.Equal(PeerState.Paired, (await _a.Service.HelloAsync(_b.KeyText))!.State);
    }

    [Fact]
    public async Task APairAnswerNotSignedByTheCodesKeyStoresNothing()
    {
        var code = _a.Service.MakeCode();
        var mallory = ServerKey.Generate();
        _net.Tamper = r =>
        {
            r.Headers.Remove("Overfin-Signature");
            r.Headers.Add("Overfin-Signature", Encodings.Base64Url(mallory.Sign(new byte[] { 1 })));
            return r;
        };
        var result = await _b.Service.RedeemAsync(code.Format());
        Assert.False(result.Ok);
        Assert.Empty(_b.Service.Peers());
    }

    [Fact]
    public async Task AGonePeerComesBackOnlyIfItWasPaired()
    {
        await PairAsync();
        _a.Store.Change(d =>
        {
            d.Find(_b.KeyText)!.State = PeerState.Gone;
            return true;
        });
        Assert.Equal(200, HandleSigned(_a, _b, "hello", Encoding.UTF8.GetBytes("{\"state\":\"paired\"}")).Status);
        Assert.Equal(PeerState.Paired, _a.PeerOf(_b)!.State);

        _a.Store.Change(d =>
        {
            var p = d.Find(_b.KeyText)!;
            p.State = PeerState.Gone;
            p.WasPaired = false;
            return true;
        });
        HandleSigned(_a, _b, "hello", Encoding.UTF8.GetBytes("{\"state\":\"paired\"}"));
        Assert.Equal(PeerState.Gone, _a.PeerOf(_b)!.State);
    }

    [Fact]
    public async Task AnEndedPairingStartsAgainWithANewCode()
    {
        await PairAsync();
        _net.Down.Add("192.0.2.10:8096");
        Assert.True(await _a.Service.EndAsync(_b.KeyText));
        _net.Down.Clear();
        Assert.Equal(PeerState.Gone, (await _b.Service.HelloAsync(_a.KeyText))!.State);

        // B's operator removes the gone peer, then redeems a new code.
        Assert.True(await _b.Service.EndAsync(_a.KeyText));
        var redeemed = await _b.Service.RedeemAsync(_a.Service.MakeCode().Format());
        Assert.True(redeemed.Ok, redeemed.Message);
        Assert.Empty(_a.Store.Read(d => d.Forgotten));
        Assert.Empty(_b.Store.Read(d => d.Forgotten));
        await _a.Service.AcceptAsync(_b.KeyText);
        Assert.Equal(PeerState.Paired, _a.PeerOf(_b)!.State);
        Assert.Equal(PeerState.Paired, _b.PeerOf(_a)!.State);
    }

    [Fact]
    public void ReplayedPairRequestIsRefused()
    {
        var code = _a.Service.MakeCode();
        var body = PairBody(_b, code);
        Assert.True(global::Msfp.Address.TryGetHost(_a.Address, out var host));
        var headers = Signing.SignRequest(_b.Service.Key, "POST", host, FederationService.PairPath, body, _clock.Now);
        Assert.Equal(200, _a.Service.HandleAsync("pair", FederationService.PairPath, headers, body).GetAwaiter().GetResult().Status);
        var again = _a.Service.HandleAsync("pair", FederationService.PairPath, headers, body).GetAwaiter().GetResult();
        Assert.Equal(404, again.Status);
        Assert.Null(again.Signature);
    }

    [Fact]
    public async Task NamesAreCleanedAndFallBackToTheHost()
    {
        _b.Settings = new ServerSettings(_b.Address, "  ​The\t\tB   server\u0007 ");
        var code = _a.Service.MakeCode();
        Assert.True((await _b.Service.RedeemAsync(code.Format())).Ok);
        Assert.Equal("The B server", _a.PeerOf(_b)!.Name);

        var c = new TestServer(_net, _clock, "http://192.0.2.20:8096", "   ");
        Assert.True((await c.Service.RedeemAsync(_a.Service.MakeCode().Format())).Ok);
        Assert.Equal("192.0.2.20:8096", _a.PeerOf(c)!.Name);
    }

    [Fact]
    public void FederationIsOffWithoutAValidAddress()
    {
        _a.Settings = new ServerSettings("https://a.example.org/media", "A");
        Assert.Null(_a.Service.OwnAddress);
        Assert.Throws<InvalidOperationException>(() => _a.Service.MakeCode());
        var answer = HandleSigned(_a, _b, "hello", Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(404, answer.Status);
    }

    [Fact]
    public void OwnAddressLosesItsTrailingSlash()
    {
        _a.Settings = new ServerSettings("https://a.example.org/", "A");
        Assert.Equal("https://a.example.org", _a.Service.OwnAddress);
    }

    [Fact]
    public void BodiesOver64KiBAreRefused()
    {
        _a.Service.MakeCode();
        var answer = HandleSigned(_a, _b, "pair", new byte[FederationService.MaxRequestBody + 1]);
        Assert.Equal(404, answer.Status);
    }

    private static byte[] PairBody(TestServer redeemer, PairingCode code)
    {
        var json = JsonSerializer.Serialize(new
        {
            address = redeemer.Address,
            name = "Server B",
            secretHash = Encodings.Base64Url(PairingProof.SecretHash(code.Secret)),
            proof = Encodings.Base64Url(PairingProof.Compute(code.Secret, redeemer.Service.Key.PublicKey, Encoding.UTF8.GetBytes(redeemer.Address))),
        });
        return Encoding.UTF8.GetBytes(json);
    }

    private Answer HandleSigned(TestServer to, TestServer from, string call, byte[] body)
    {
        Assert.True(global::Msfp.Address.TryGetHost(to.Address, out var host));
        var path = "/api/v1/federation/" + call;
        var headers = Signing.SignRequest(from.Service.Key, "POST", host, path, body, _clock.Now);
        return to.Service.HandleAsync(call, path, headers, body).GetAwaiter().GetResult();
    }
}
