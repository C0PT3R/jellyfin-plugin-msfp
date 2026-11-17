using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using Msfp.Tests;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>Section 3, corrected 2026-11-17: no content codings.</summary>
public class ContentCodingTests
{
    private readonly ManualClock _clock = ManualClock.AtSeconds(1_800_000_000);

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task AnAnswerWithAContentCodingCountsAsBadlySigned(string coding)
    {
        var net = new Network();
        var a = new TestServer(net, _clock, "https://a.example.org", "Server A");
        var b = new TestServer(net, _clock, "http://192.0.2.10:8096", "Server B");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await b.Service.RedeemAsync(a.Service.MakeCode().Format())).Ok);
        await a.Service.AcceptAsync(b.KeyText);
        b.Service.RetryDelay = TimeSpan.Zero;

        // A's answer, signed over its bytes, but labelled as coded: B
        // refuses it without decoding it, whatever the signature.
        net.Tamper = r =>
        {
            r.Content.Headers.ContentEncoding.Add(coding);
            return r;
        };
        var result = await b.Service.CallCheckedAsync(a.KeyText, FederationService.LibrariesPath, Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(CallOutcome.Unreachable, result.Outcome);

        net.Tamper = r =>
        {
            r.Content.Headers.ContentEncoding.Add("identity");
            return r;
        };
        Assert.Equal(CallOutcome.Ok, (await b.Service.CallCheckedAsync(a.KeyText, FederationService.LibrariesPath, Encoding.UTF8.GetBytes("{}"))).Outcome);
    }
}
