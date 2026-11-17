using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>Pins in hello (MSFP sections 3.1 and 9.2), over the in-process network.</summary>
public class HelloPinsTests
{
    private readonly ManualClock _clock = ManualClock.AtSeconds(1_800_000_000);
    private readonly Network _net = new();
    private readonly TestServer _a;
    private readonly TestServer _b;
    private readonly string _pinX;
    private readonly string _pinY;

    public HelloPinsTests()
    {
        _a = new TestServer(_net, _clock, "https://a.example.org", "Server A");
        _b = new TestServer(_net, _clock, "https://b.example.org", "Server B");
        using var x = TestCertificates.SelfSigned("a.example.org");
        using var y = TestCertificates.SelfSigned("b.example.org");
        _a.Reader.Leaf = x.RawData;
        _b.Reader.Leaf = y.RawData;
        _pinX = Pins.Of(x);
        _pinY = Pins.Of(y);
        _clock.Advance(TimeSpan.FromSeconds(1));
    }

    private async Task PairAsync()
    {
        Assert.True((await _b.Service.RedeemAsync(_a.Service.MakeCode().Format())).Ok);
        await _a.Service.AcceptAsync(_b.KeyText);
        Assert.Equal(PeerState.Paired, _b.PeerOf(_a)!.State);
    }

    [Fact]
    public async Task EachSideKeepsThePinsOfTheOthersLatestHello()
    {
        await PairAsync();

        // A's accepting hello named X; B's answer named Y.
        Assert.Equal(new[] { _pinX }, _b.PeerOf(_a)!.Pins);
        Assert.Equal(new[] { _pinY }, _a.PeerOf(_b)!.Pins);
    }

    [Fact]
    public async Task AnsweringAHelloNamesTheCertificateServedNow()
    {
        // Section 3.1, corrected 2026-11-17: a renewal right after a hello
        // is named in the next answer, so the peer's one retry passes.
        await PairAsync();
        using var z = TestCertificates.SelfSigned("a.example.org");
        _a.Reader.Leaf = z.RawData;
        _clock.Advance(FederationService.ReadOwnCertificateWhenAnswering);
        await _b.Service.HelloAsync(_a.KeyText);
        Assert.Equal(new[] { Pins.Of(z) }, _b.PeerOf(_a)!.Pins);

        // A reading at most a few seconds old is reused.
        var reads = _a.Reader.Reads;
        await _b.Service.HelloAsync(_a.KeyText);
        Assert.Equal(reads, _a.Reader.Reads);
    }

    [Fact]
    public async Task PairAloneLearnsNoPins()
    {
        Assert.True((await _b.Service.RedeemAsync(_a.Service.MakeCode().Format())).Ok);
        Assert.Empty(_b.PeerOf(_a)!.Pins);
        Assert.Empty(_a.PeerOf(_b)!.Pins);
    }

    [Fact]
    public async Task ANewerHelloReplacesThePinsAndOneWithoutThemClearsThem()
    {
        await PairAsync();
        using var z = TestCertificates.SelfSigned("a.example.org");
        _a.Reader.Leaf = z.RawData;
        _clock.Advance(FederationService.ReadOwnCertificateEvery);
        await _a.Service.HelloAsync(_b.KeyText);
        Assert.Equal(new[] { Pins.Of(z) }, _b.PeerOf(_a)!.Pins);

        // A now names none: a hello without the field.
        _a.Settings = _a.Settings with { Address = "http://192.0.2.1:8096" };
        await _a.Service.HelloAsync(_b.KeyText);
        Assert.Empty(_b.PeerOf(_a)!.Pins);
        Assert.DoesNotContain("certificates", _net.Bodies.Last());
    }

    [Fact]
    public async Task AnAnswerWithoutPinsClearsThem()
    {
        _b.Reader.Leaf = null;
        await PairAsync();
        _a.Store.Change(d =>
        {
            d.Find(_b.KeyText)!.Pins = new() { _pinY };
            return true;
        });
        await _a.Service.HelloAsync(_b.KeyText);
        Assert.Empty(_a.PeerOf(_b)!.Pins);
    }

    [Fact]
    public async Task PinsAddedOnTheWayAreNeverKept()
    {
        await PairAsync();
        var before = _a.PeerOf(_b)!.Pins;
        var forged = new string('A', 43);
        _net.Tamper = r =>
        {
            var body = r.Content.ReadAsStringAsync().Result;
            var changed = body.Replace("\"certificates\":[", $"\"certificates\":[\"{forged}\",");
            var response = new HttpResponseMessage(r.StatusCode) { Content = new StringContent(changed) };
            foreach (var h in r.Headers)
            {
                response.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            return response;
        };
        Assert.Equal(before, (await _a.Service.HelloAsync(_b.KeyText))!.Pins);
    }

    [Fact]
    public async Task AStrangersHelloNamesNothing()
    {
        await PairAsync();
        var mallory = ServerKey.Generate();
        var body = Encoding.UTF8.GetBytes($"{{\"state\":\"paired\",\"certificates\":[\"{new string('A', 43)}\"]}}");
        var headers = Signing.SignRequest(mallory, "POST", "b.example.org", FederationService.HelloPath, body, _clock.Now);
        var answer = await _b.Service.HandleAsync("hello", FederationService.HelloPath, headers, body);
        Assert.Equal(404, answer.Status);
        Assert.Null(answer.Signature);
        Assert.Equal(new[] { _pinX }, _b.PeerOf(_a)!.Pins);
    }

    [Fact]
    public async Task AReplayedOlderHelloCantBringOldPinsBack()
    {
        await PairAsync();
        var old = Encoding.UTF8.GetBytes($"{{\"state\":\"paired\",\"certificates\":[\"{_pinX}\"]}}");
        var headers = Signing.SignRequest(_a.Service.Key, "POST", "b.example.org", FederationService.HelloPath, old, _clock.Now);
        Assert.Equal(200, (await _b.Service.HandleAsync("hello", FederationService.HelloPath, headers, old)).Status);

        _a.Settings = _a.Settings with { Pins = new[] { _pinY } };
        await _a.Service.HelloAsync(_b.KeyText);
        Assert.Equal(new[] { _pinY }, _b.PeerOf(_a)!.Pins);

        var replay = await _b.Service.HandleAsync("hello", FederationService.HelloPath, headers, old);
        Assert.Equal(404, replay.Status);
        Assert.Equal(new[] { _pinY }, _b.PeerOf(_a)!.Pins);
    }

    [Fact]
    public async Task AHelloWhoseCertificatesArentAListIsRefused()
    {
        await PairAsync();
        var body = Encoding.UTF8.GetBytes("{\"state\":\"paired\",\"certificates\":\"x\"}");
        var headers = Signing.SignRequest(_a.Service.Key, "POST", "b.example.org", FederationService.HelloPath, body, _clock.Now);
        var answer = await _b.Service.HandleAsync("hello", FederationService.HelloPath, headers, body);
        Assert.Equal(404, answer.Status);
        Assert.NotNull(answer.Signature);
        Assert.Equal(new[] { _pinX }, _b.PeerOf(_a)!.Pins);
    }

    [Fact]
    public async Task EndedHellosCarryNoPinsEitherWay()
    {
        await PairAsync();
        var body = Encoding.UTF8.GetBytes("{\"state\":\"ended\"}");
        var headers = Signing.SignRequest(_a.Service.Key, "POST", "b.example.org", FederationService.HelloPath, body, _clock.Now);
        var answer = await _b.Service.HandleAsync("hello", FederationService.HelloPath, headers, body);
        Assert.Equal("{\"state\":\"ended\"}", Encoding.UTF8.GetString(answer.Body));

        Assert.True(await _b.Service.EndAsync(_a.KeyText) == false);
        await PairAgain();
        Assert.True(await _b.Service.EndAsync(_a.KeyText));
        Assert.Equal("{\"state\":\"ended\"}", _net.Bodies.Last());
    }

    private async Task PairAgain()
    {
        // B was deleted by A's ended hello above; A still knows B.
        Assert.True(await _a.Service.EndAsync(_b.KeyText));
        Assert.True((await _b.Service.RedeemAsync(_a.Service.MakeCode().Format())).Ok);
        await _a.Service.AcceptAsync(_b.KeyText);
    }

    [Fact]
    public async Task HelloAnswersNameOwnPins()
    {
        await PairAsync();
        var body = Encoding.UTF8.GetBytes("{\"state\":\"paired\"}");
        var headers = Signing.SignRequest(_a.Service.Key, "POST", "b.example.org", FederationService.HelloPath, body, _clock.Now);
        var answer = await _b.Service.HandleAsync("hello", FederationService.HelloPath, headers, body);
        var json = JsonDocument.Parse(answer.Body).RootElement;
        Assert.Equal(_pinY, json.GetProperty("certificates")[0].GetString());
    }
}

/// <summary>How a server knows its own pins (MSFP section 3.1).</summary>
public class OwnPinsTests
{
    private readonly ManualClock _clock = ManualClock.AtSeconds(1_800_000_000);
    private readonly TestServer _a;

    public OwnPinsTests()
    {
        _a = new TestServer(new Network(), _clock, "https://a.example.org", "Server A");
    }

    [Fact]
    public async Task OverHttpNone()
    {
        _a.Settings = _a.Settings with { Address = "http://192.0.2.1:8096" };
        _a.Reader.Leaf = TestCertificates.SelfSigned().RawData;
        Assert.Empty(await _a.Service.OwnPinsAsync());
        Assert.Equal(0, _a.Reader.Reads);
    }

    [Fact]
    public async Task ReadsTheLeafServedAtItsAddressAtMostOnceAMinute()
    {
        using var x = TestCertificates.SelfSigned();
        using var y = TestCertificates.SelfSigned();
        _a.Reader.Leaf = x.RawData;
        Assert.Equal(new[] { Pins.Of(x) }, await _a.Service.OwnPinsAsync());

        _a.Reader.Leaf = y.RawData;
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(new[] { Pins.Of(x) }, await _a.Service.OwnPinsAsync());
        Assert.Equal(1, _a.Reader.Reads);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { Pins.Of(y) }, await _a.Service.OwnPinsAsync());
        Assert.Equal(2, _a.Reader.Reads);
    }

    [Fact]
    public async Task AFailedReadingKeepsTheLastGoodOne()
    {
        using var x = TestCertificates.SelfSigned();
        Assert.Empty(await _a.Service.OwnPinsAsync());

        _clock.Advance(FederationService.ReadOwnCertificateEvery);
        _a.Reader.Leaf = x.RawData;
        Assert.Equal(new[] { Pins.Of(x) }, await _a.Service.OwnPinsAsync());

        _clock.Advance(FederationService.ReadOwnCertificateEvery);
        _a.Reader.Leaf = null;
        Assert.Equal(new[] { Pins.Of(x) }, await _a.Service.OwnPinsAsync());

        _clock.Advance(FederationService.ReadOwnCertificateEvery);
        _a.Reader.Leaf = new byte[] { 1, 2, 3 };
        Assert.Equal(new[] { Pins.Of(x) }, await _a.Service.OwnPinsAsync());
    }

    [Fact]
    public async Task ConfiguredPinsWinAndNothingIsRead()
    {
        var p = new string('A', 43);
        var q = new string('Q', 43);
        _a.Reader.Leaf = TestCertificates.SelfSigned().RawData;
        _a.Settings = _a.Settings with { Pins = new[] { "not a pin", p, p, q } };
        Assert.Equal(new[] { p, q }, await _a.Service.OwnPinsAsync());
        Assert.Equal(0, _a.Reader.Reads);
    }

    [Fact]
    public async Task OnlyInvalidConfiguredPinsMeanReading()
    {
        using var x = TestCertificates.SelfSigned();
        _a.Reader.Leaf = x.RawData;
        _a.Settings = _a.Settings with { Pins = new[] { "nope" } };
        Assert.Equal(new[] { Pins.Of(x) }, await _a.Service.OwnPinsAsync());
    }
}
