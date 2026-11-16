using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>
/// A server over real HTTPS on 127.0.0.1: Kestrel with a certificate the
/// test chooses, answering federation calls through a FederationService
/// (or relaying them to another one, as someone between two servers would).
/// </summary>
internal sealed class HttpsPeer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private HttpsPeer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public string Address => $"https://127.0.0.1:{Port}";

    /// <summary>Requests seen, by path.</summary>
    public ConcurrentDictionary<string, int> Seen { get; } = new();

    /// <summary>The service that answers.</summary>
    public FederationService? Service { get; set; }

    public static async Task<HttpsPeer> StartAsync(X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0, o => o.UseHttps(certificate)));
        var app = builder.Build();
        HttpsPeer? peer = null;
        app.MapPost("/api/v1/federation/{call}", async (HttpContext ctx, string call) =>
        {
            peer!.Seen.AddOrUpdate(call, 1, (_, n) => n + 1);
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            string? H(string name) => ctx.Request.Headers[name].Count == 1 ? ctx.Request.Headers[name][0] : null;
            var target = ctx.Features.Get<IHttpRequestFeature>()!.RawTarget;
            var answer = await peer.Service!.HandleAsync(call, target, new SignatureHeaders(H("Overfin-Key"), H("Overfin-Date"), H("Overfin-Nonce"), H("Overfin-Signature")), ms.ToArray());
            ctx.Response.StatusCode = answer.Status;
            if (answer.Signature != null)
            {
                ctx.Response.Headers["Overfin-Signature"] = answer.Signature;
            }

            await ctx.Response.Body.WriteAsync(answer.Body);
        });
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;
        peer = new HttpsPeer(app, port);
        return peer;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Which certificates each call accepts (MSFP section 3.1), over real TLS:
/// pair and hello any, every other call trusted or named, with one hello
/// and one retry when refused.
/// </summary>
public class TlsTests
{
    private const string Libraries = "/api/v1/federation/libraries";

    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);

    // B is never reached: port 1 on loopback refuses at once.
    private readonly ServerSettings _bSettings = new("http://127.0.0.1:1", "Server B");

    private FederationService MakeA(HttpsPeer at, Func<ServerSettings> settings)
    {
        return new FederationService(ServerKey.Generate(), new PeerStore(null), _clock, settings, new DefaultTransport(), new TlsCertificateReader());
    }

    private FederationService MakeB() =>
        new(ServerKey.Generate(), new PeerStore(null), _clock, () => _bSettings, new DefaultTransport(), new FakeReader());

    /// <summary>B redeems A's code; A accepts (its hello can't reach B).</summary>
    private async Task<string> PairAsync(FederationService a, FederationService b)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var redeemed = await b.RedeemAsync(a.MakeCode().Format());
        Assert.True(redeemed.Ok, redeemed.Message);
        await a.AcceptAsync(b.Key.PublicKeyText);
        return a.Key.PublicKeyText;
    }

    private static byte[] Empty => Encoding.UTF8.GetBytes("{}");

    [Fact]
    public async Task ASelfMadeCertificateGetsPastPairAndHelloOnly()
    {
        using var cert = TestCertificates.SelfSigned();
        await using var server = await HttpsPeer.StartAsync(cert);

        // A names a pin that isn't the certificate served: as when someone
        // between the servers presents a certificate of its own.
        var named = Pins.Of(TestCertificates.SelfSigned());
        var a = MakeA(server, () => new ServerSettings(server.Address, "Server A", new[] { named }));
        server.Service = a;
        var b = MakeB();

        var aKey = await PairAsync(a, b);
        Assert.Empty(b.Peers().Single().Pins);

        // Every other call is refused the certificate. One hello brings A's
        // pins (new, so one retry), and the retry is refused too.
        var hellosBefore = server.Seen.GetValueOrDefault("hello");
        var first = await b.CallCheckedAsync(aKey, Libraries, Empty);
        Assert.Equal(CallOutcome.CertificateRefused, first.Outcome);
        Assert.Equal(new[] { named }, b.Peers().Single().Pins);
        Assert.Equal(hellosBefore + 1, server.Seen.GetValueOrDefault("hello"));

        // Next time the hello brings the same pins: no retry.
        var second = await b.CallCheckedAsync(aKey, Libraries, Empty);
        Assert.Equal(CallOutcome.CertificateRefused, second.Outcome);
        Assert.Equal(hellosBefore + 2, server.Seen.GetValueOrDefault("hello"));
        Assert.Equal(0, server.Seen.GetValueOrDefault("libraries"));

        // Pair and hello still pass.
        Assert.Equal(PeerState.Paired, (await b.HelloAsync(aKey))!.State);
    }

    [Fact]
    public async Task ANamedLeafPassesAfterOneHelloAndOneRetry()
    {
        using var cert = TestCertificates.SelfSigned();
        await using var server = await HttpsPeer.StartAsync(cert);

        // A reads the certificate served at its own address: no configuration.
        var a = MakeA(server, () => new ServerSettings(server.Address, "Server A"));
        server.Service = a;
        var b = MakeB();
        var aKey = await PairAsync(a, b);
        Assert.Equal(new[] { Pins.Of(cert) }, await a.OwnPinsAsync());

        var hellos = server.Seen.GetValueOrDefault("hello");
        var result = await b.CallCheckedAsync(aKey, Libraries, Empty);

        // Past TLS: A answers its (empty) offer, signed.
        Assert.Equal(CallOutcome.Ok, result.Outcome);
        Assert.Equal(1, server.Seen.GetValueOrDefault("libraries"));
        Assert.Equal(hellos + 1, server.Seen.GetValueOrDefault("hello"));
        Assert.Equal(new[] { Pins.Of(cert) }, b.Peers().Single().Pins);

        // With the pin known, no hello first.
        await b.CallCheckedAsync(aKey, Libraries, Empty);
        Assert.Equal(2, server.Seen.GetValueOrDefault("libraries"));
        Assert.Equal(hellos + 1, server.Seen.GetValueOrDefault("hello"));
    }

    [Fact]
    public async Task ARemovedPinOnlyMakesCallsFail()
    {
        using var cert = TestCertificates.SelfSigned();
        await using var server = await HttpsPeer.StartAsync(cert);
        var pins = Array.Empty<string>();
        var a = MakeA(server, () => new ServerSettings(server.Address, "Server A", pins));
        server.Service = a;
        var b = MakeB();
        var aKey = await PairAsync(a, b);

        Assert.Equal(CallOutcome.Ok, (await b.CallCheckedAsync(aKey, Libraries, Empty)).Outcome);
        Assert.Equal(1, server.Seen.GetValueOrDefault("libraries"));

        // A now names another pin, not the one it serves: its next hello
        // removes the served one at B, even with a connection still open.
        pins = new[] { Pins.Of(TestCertificates.SelfSigned()) };
        await b.HelloAsync(aKey);
        Assert.Equal(pins, b.Peers().Single().Pins);
        Assert.Equal(CallOutcome.CertificateRefused, (await b.CallCheckedAsync(aKey, Libraries, Empty)).Outcome);
        Assert.Equal(1, server.Seen.GetValueOrDefault("libraries"));

        // The pairing itself is untouched.
        Assert.Equal(PeerState.Paired, (await b.HelloAsync(aKey))!.State);
    }

    [Fact]
    public async Task AnAuthoritysPinDoesntPass()
    {
        var (ca, leaf) = TestCertificates.Issued();
        using (ca)
        using (leaf)
        {
            await using var server = await HttpsPeer.StartAsync(leaf);
            var a = MakeA(server, () => new ServerSettings(server.Address, "Server A", new[] { Pins.Of(ca) }));
            server.Service = a;
            var b = MakeB();
            var aKey = await PairAsync(a, b);
            Assert.Equal(CallOutcome.CertificateRefused, (await b.CallCheckedAsync(aKey, Libraries, Empty)).Outcome);
            Assert.Equal(0, server.Seen.GetValueOrDefault("libraries"));
        }
    }

    [Fact]
    public async Task TheReaderReadsTheServedLeaf()
    {
        using var cert = TestCertificates.SelfSigned();
        await using var server = await HttpsPeer.StartAsync(cert);
        var leaf = await new TlsCertificateReader().ReadLeafAsync(server.Address, default);
        Assert.Equal(cert.RawData, leaf);
        Assert.Null(await new TlsCertificateReader().ReadLeafAsync("http://127.0.0.1:" + server.Port, default));
        Assert.Null(await new TlsCertificateReader().ReadLeafAsync("https://127.0.0.1:1", default));
    }
}
