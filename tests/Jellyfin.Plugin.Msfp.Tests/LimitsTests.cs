using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>Section 8.3: limits on the answering side, and what a caller does.</summary>
public class LimitsTests
{
    private readonly ManualClock _clock = ManualClock.AtSeconds(1_800_000_000);

    [Fact]
    public void ABucketSpendsItsBurstThenRefillsAtItsRate()
    {
        var b = new TokenBucket(20, 200, _clock.Now);
        Assert.All(Enumerable.Range(0, 200), _ => Assert.True(b.TryTake(_clock.Now, out _)));
        Assert.False(b.TryTake(_clock.Now, out var wait));
        Assert.Equal(1, wait);
        _clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(b.TryTake(_clock.Now, out _));
        Assert.True(b.TryTake(_clock.Now, out _));
        Assert.False(b.TryTake(_clock.Now, out _));
    }

    [Fact]
    public void AnAddressIsLimitedBeforeAnySignature()
    {
        var limits = new CallLimits(_clock);
        Assert.Equal(400, Enumerable.Range(0, 450).Count(_ => limits.AllowAddress("192.0.2.7")));
        Assert.True(limits.AllowAddress("192.0.2.8"));
        Assert.True(limits.AllowAddress(null));

        // IPv6 by its /64: another address of the same network shares it.
        Assert.Equal(400, Enumerable.Range(0, 450).Count(i => limits.AllowAddress("2001:db8:1:2::" + (i + 1).ToString("x", System.Globalization.CultureInfo.InvariantCulture))));
        Assert.True(limits.AllowAddress("2001:db8:1:3::1"));
    }

    [Fact]
    public void AllAddressesTogetherAreLimitedToo()
    {
        var limits = new CallLimits(_clock);
        var passed = Enumerable.Range(0, 5000).Count(i => limits.AllowAddress("10.0." + (i / 200).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + (i % 200).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal((int)CallLimits.BeforeSignatureBurst, passed);
    }

    [Fact]
    public void APeerOverItsLimitIsTheOnlyOneRefused()
    {
        var limits = new CallLimits(_clock);
        for (var i = 0; i < CallLimits.PeerBurst; i++)
        {
            Assert.Equal(LimitVerdict.Pass, limits.Enter("p1", out _, out var slot));
            slot!.Dispose();
        }

        Assert.Equal(LimitVerdict.PeerLimited, limits.Enter("p1", out var retryAfter, out _));
        Assert.Equal(1, retryAfter);
        Assert.Equal(LimitVerdict.Pass, limits.Enter("p2", out _, out var other));
        other!.Dispose();
    }

    [Fact]
    public void RequestsAtOnceAreCapped()
    {
        var limits = new CallLimits(_clock);
        var held = Enumerable.Range(0, CallLimits.PeerAtOnce).Select(_ => { limits.Enter("p1", out _, out var s); return s!; }).ToList();
        Assert.Equal(LimitVerdict.PeerLimited, limits.Enter("p1", out _, out _));
        held[0].Dispose();
        held[0].Dispose(); // twice changes nothing
        Assert.Equal(LimitVerdict.Pass, limits.Enter("p1", out _, out var again));
        Assert.Equal(LimitVerdict.PeerLimited, limits.Enter("p1", out _, out _));
        again!.Dispose();
        held.ForEach(s => s.Dispose());

        var many = Enumerable.Range(0, CallLimits.AllAtOnce).Select(i => { limits.Enter("q" + (i % 16), out _, out var s); return s!; }).ToList();
        Assert.Equal(LimitVerdict.Busy, limits.Enter("someone", out _, out _));
        many.ForEach(s => s.Dispose());
    }

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
