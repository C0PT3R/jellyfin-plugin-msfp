using System;
using System.Linq;
using Xunit;

namespace Msfp.Tests;

/// <summary>Section 8.3: limits on the answering side.</summary>
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
}
