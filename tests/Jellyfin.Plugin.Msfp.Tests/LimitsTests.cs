using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using Msfp;
using Msfp.Tests;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>Section 8.3: limits as the federation service applies them, and what a caller does.</summary>
public class LimitsTests
{
    private readonly ManualClock _clock = ManualClock.AtSeconds(1_800_000_000);

    [Fact]
    public async Task APeerCallingTooMuchGetsASigned429WithRetryAfter_AndTheCallerWaits()
    {
        var net = new Network();
        var a = new TestServer(net, _clock, "https://a.example.org", "Server A");
        var b = new TestServer(net, _clock, "http://192.0.2.10:8096", "Server B");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await b.Service.RedeemAsync(a.Service.MakeCode().Format())).Ok);
        await a.Service.AcceptAsync(b.KeyText);

        // B calls A past A's limit for B: A answers 429, signed, with
        // retryAfter in the body.
        CallResult last = CallResult.Unreachable;
        for (var i = 0; i < CallLimits.PeerBurst + 5 && last.Status != 429; i++)
        {
            last = await b.Service.CallCheckedAsync(a.KeyText, FederationService.LibrariesPath, Encoding.UTF8.GetBytes("{}"));
        }

        Assert.Equal(CallOutcome.Refused, last.Outcome);
        Assert.Equal(429, last.Status);
        Assert.Equal(1, last.RetryAfter);

        // B now holds its calls to A until retryAfter has passed: nothing
        // is sent.
        var calls = net.Calls;
        Assert.Equal(CallOutcome.Held, (await b.Service.CallCheckedAsync(a.KeyText, FederationService.LibrariesPath, Encoding.UTF8.GetBytes("{}"))).Outcome);
        Assert.Equal(calls, net.Calls);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CallOutcome.Ok, (await b.Service.CallCheckedAsync(a.KeyText, FederationService.LibrariesPath, Encoding.UTF8.GetBytes("{}"))).Outcome);
    }

    [Fact]
    public async Task WithoutRetryAfterTheCallerBacksOffDoubling()
    {
        var net = new Network();
        var a = new TestServer(net, _clock, "https://a.example.org", "Server A");
        var b = new TestServer(net, _clock, "http://192.0.2.10:8096", "Server B");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await b.Service.RedeemAsync(a.Service.MakeCode().Format())).Ok);
        await a.Service.AcceptAsync(b.KeyText);
        net.Fakes["a.example.org"] = (a.Service.Key, (_, _) => (503, "{\"type\":\"server-busy\",\"title\":\"Busy\",\"status\":503}\n"));

        var body = Encoding.UTF8.GetBytes("{}");
        Assert.Equal(503, (await b.Service.CallCheckedAsync(a.KeyText, FederationService.LibrariesPath, body)).Status);
        Assert.Equal(CallOutcome.Held, (await b.Service.CallCheckedAsync(a.KeyText, FederationService.LibrariesPath, body)).Outcome);
        _clock.Advance(TimeSpan.FromSeconds(7)); // past 5 s, jitter included
        Assert.Equal(503, (await b.Service.CallCheckedAsync(a.KeyText, FederationService.LibrariesPath, body)).Status);
        _clock.Advance(TimeSpan.FromSeconds(7)); // the wait is now about 10 s
        Assert.Equal(CallOutcome.Held, (await b.Service.CallCheckedAsync(a.KeyText, FederationService.LibrariesPath, body)).Outcome);
        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(503, (await b.Service.CallCheckedAsync(a.KeyText, FederationService.LibrariesPath, body)).Status);
    }

    [Fact]
    public async Task AnUnsigned404IsTriedOnceMoreWithANewNonce()
    {
        var net = new Network();
        var a = new TestServer(net, _clock, "https://a.example.org", "Server A");
        var b = new TestServer(net, _clock, "http://192.0.2.10:8096", "Server B");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await b.Service.RedeemAsync(a.Service.MakeCode().Format())).Ok);
        await a.Service.AcceptAsync(b.KeyText);
        b.Service.RetryDelay = TimeSpan.Zero;

        // The first answer is unsigned (as from a peer that just
        // restarted), the second goes through.
        var first = true;
        net.Tamper = r =>
        {
            if (!first)
            {
                return r;
            }

            first = false;
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new System.Net.Http.ByteArrayContent(Array.Empty<byte>()) };
        };
        var before = net.Bodies.Count;
        var result = await b.Service.CallCheckedAsync(a.KeyText, FederationService.LibrariesPath, Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(CallOutcome.Ok, result.Outcome);
        Assert.Equal(before + 2, net.Bodies.Count);
    }
}
