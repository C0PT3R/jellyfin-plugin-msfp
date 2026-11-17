using System;
using System.Text;
using Xunit;

namespace Msfp.Tests;

public class RequestCheckTests
{
    private const string Host = "a.example.org";
    private const string Target = "/api/v1/federation/hello";
    private const long Date = 1_800_000_000;
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"state\":\"paired\"}");

    private readonly ServerKey _sender = ServerKey.Generate();

    private static (RequestCheck Check, ManualClock Clock, NonceMemory Memory) Receiver(double startedAt = Date - 3600, int peers = 100_000, int strangers = 1_000)
    {
        var clock = ManualClock.AtSeconds(startedAt);
        var memory = new NonceMemory(clock, peers, strangers);
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(Date);
        return (new RequestCheck(clock, memory), clock, memory);
    }

    private SignatureHeaders Sign(long date = Date, byte[]? body = null, string host = Host, string target = Target, ServerKey? key = null)
    {
        return Signing.SignRequest(key ?? _sender, "POST", host, target, body ?? Body, DateTimeOffset.FromUnixTimeSeconds(date));
    }

    private static CheckOutcome Run(RequestCheck check, SignatureHeaders headers, byte[]? body = null, Hearing hearing = Hearing.Peer, string target = Target)
    {
        return check.Check(Host, "POST", target, headers, body ?? Body, _ => hearing).Outcome;
    }

    [Theory]
    [InlineData(Target + "?x=1")]
    [InlineData(Target + "?")]
    public void ARequestWithAQueryIsRefusedEvenWhenSignedSo(string target)
    {
        // Found live: both the plugin and Overfin accepted a hello signed
        // over /api/v1/federation/hello?x=1 (sections 6.2 and 6.4, step 1).
        var (check, _, _) = Receiver();
        Assert.Equal(CheckOutcome.Refused, Run(check, Sign(target: target), target: target));
    }

    [Fact]
    public void AcceptsAGoodRequest()
    {
        var (check, _, _) = Receiver();
        Assert.Equal(CheckOutcome.Accepted, Run(check, Sign()));
    }

    [Fact]
    public void RefusesARequestSignedForAnotherHost()
    {
        var (check, _, _) = Receiver();
        Assert.Equal(CheckOutcome.Refused, Run(check, Sign(host: "b.example.org")));
        Assert.Equal(CheckOutcome.Refused, Run(check, Sign(host: "a.example.org:8096")));
    }

    [Fact]
    public void RefusesAnotherTargetOrBody()
    {
        var (check, _, _) = Receiver();
        Assert.Equal(CheckOutcome.Refused, Run(check, Sign(), target: "/api/v1/federation/pair"));
        Assert.Equal(CheckOutcome.Refused, Run(check, Sign(), body: Encoding.UTF8.GetBytes("{\"state\":\"ended\"}")));
    }

    [Theory]
    // Offsets of the receiver's clock from the date, in ticks (100 ns).
    [InlineData(-3_000_000_000L, true)]
    [InlineData(3_000_000_000L, true)]
    [InlineData(3_000_000_001L, false)]
    [InlineData(-3_000_000_001L, false)]
    [InlineData(3_010_000_000L, false)]
    [InlineData(-3_010_000_000L, false)]
    [InlineData(9_999_999L, true)]
    public void TimeWindowIsFiveMinutesUnrounded(long clockOffsetTicks, bool accepted)
    {
        var (check, clock, _) = Receiver();
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(Date).AddTicks(clockOffsetTicks);
        Assert.Equal(accepted ? CheckOutcome.Accepted : CheckOutcome.Refused, Run(check, Sign()));
    }

    [Fact]
    public void RefusesRequestsSignedBeforeTheMemoryBegan()
    {
        var (check, clock, _) = Receiver(startedAt: Date + 10);
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(Date + 20);
        Assert.Equal(CheckOutcome.Refused, Run(check, Sign(Date + 9)));
        Assert.Equal(CheckOutcome.Accepted, Run(check, Sign(Date + 10)));
    }

    [Fact]
    public void RefusesTheStartSecondUnlessStartedExactlyOnIt()
    {
        var (late, clock, _) = Receiver(startedAt: Date + 0.25);
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(Date + 5);
        Assert.Equal(CheckOutcome.Refused, Run(late, Sign(Date)));
        Assert.Equal(CheckOutcome.Accepted, Run(late, Sign(Date + 1)));

        var (exact, clock2, _) = Receiver(startedAt: Date);
        clock2.Now = DateTimeOffset.FromUnixTimeSeconds(Date + 5);
        Assert.Equal(CheckOutcome.Accepted, Run(exact, Sign(Date)));
    }

    [Fact]
    public void RefusesAReplay()
    {
        var (check, _, _) = Receiver();
        var headers = Sign();
        Assert.Equal(CheckOutcome.Accepted, Run(check, headers));
        Assert.Equal(CheckOutcome.Refused, Run(check, headers));
    }

    [Fact]
    public void RefusesAReplayLongAfterwardThroughTheWindow()
    {
        var (check, clock, _) = Receiver();
        var headers = Sign();
        Assert.Equal(CheckOutcome.Accepted, Run(check, headers));
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(CheckOutcome.Refused, Run(check, headers));
    }

    [Fact]
    public void ANonceIsPerKey()
    {
        var (check, _, _) = Receiver();
        var nonce = new byte[16];
        var a = Signing.SignRequest(_sender, "POST", Host, Target, Body, DateTimeOffset.FromUnixTimeSeconds(Date), nonce);
        var b = Signing.SignRequest(ServerKey.Generate(), "POST", Host, Target, Body, DateTimeOffset.FromUnixTimeSeconds(Date), nonce);
        Assert.Equal(CheckOutcome.Accepted, Run(check, a));
        Assert.Equal(CheckOutcome.Accepted, Run(check, b));
    }

    [Fact]
    public void UnheardKeysAreRefusedBeforeTheirNonceIsStored()
    {
        var (check, _, memory) = Receiver();
        var headers = Sign();
        Assert.Equal(CheckOutcome.Refused, Run(check, headers, hearing: Hearing.NotHeard));
        Assert.Equal(0, memory.Count);
        Assert.Equal(CheckOutcome.Accepted, Run(check, headers));
    }

    [Fact]
    public void ForgottenKeysGetASignedRefusalAndNoNonceStored()
    {
        var (check, _, memory) = Receiver();
        var result = check.Check(Host, "POST", Target, Sign(), Body, _ => Hearing.Forgotten);
        Assert.Equal(CheckOutcome.RefusedSigned, result.Outcome);
        Assert.Equal(_sender.PublicKey, result.Key);
        Assert.NotNull(result.Nonce);
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void ABadSignatureFromAForgottenKeyIsPlainlyRefused()
    {
        var (check, _, _) = Receiver();
        var headers = Sign() with { Signature = Encodings.Base64Url(new byte[64]) };
        Assert.Equal(CheckOutcome.Refused, Run(check, headers, hearing: Hearing.Forgotten));
    }

    [Fact]
    public void ANonceInEitherMemoryIsRefused()
    {
        var (check, _, _) = Receiver();
        var headers = Sign();
        Assert.Equal(CheckOutcome.Accepted, Run(check, headers, hearing: Hearing.Stranger));
        Assert.Equal(CheckOutcome.Refused, Run(check, headers, hearing: Hearing.Peer));
    }

    [Fact]
    public void AFullMemoryRefusesUntilRoomComesBack()
    {
        var (check, clock, _) = Receiver(strangers: 2);
        Assert.Equal(CheckOutcome.Accepted, Run(check, Sign(), hearing: Hearing.Stranger));
        Assert.Equal(CheckOutcome.Accepted, Run(check, Sign(), hearing: Hearing.Stranger));
        Assert.Equal(CheckOutcome.Refused, Run(check, Sign(), hearing: Hearing.Stranger));

        // The peers' memory is apart.
        Assert.Equal(CheckOutcome.Accepted, Run(check, Sign(), hearing: Hearing.Peer));

        // Ten minutes on, the old entries go.
        clock.Advance(NonceMemory.KeepFor);
        Assert.Equal(CheckOutcome.Accepted, Run(check, Sign(Date + 600), hearing: Hearing.Stranger));
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("short", null, null, null)]
    [InlineData(null, "+1800000000", null, null)]
    [InlineData(null, "01800000000", null, null)]
    [InlineData(null, "1800000000.0", null, null)]
    [InlineData(null, " 1800000000", null, null)]
    [InlineData(null, "", null, null)]
    [InlineData(null, null, "AAAAAAAAAAAAAAAAAAAA", null)]
    [InlineData(null, null, "AAAAAAAAAAAAAAAAAAAAAAAA", null)]
    [InlineData(null, null, "AAAAAAAAAAAAAAAAAAAAAA==", null)]
    [InlineData(null, null, null, "not base64!")]
    [InlineData(null, null, null, "")]
    public void RefusesMalformedHeaders(string? key, string? date, string? nonce, string? signature)
    {
        var (check, _, _) = Receiver();
        var good = Sign();
        var headers = new SignatureHeaders(
            key ?? good.Key,
            date ?? good.Date,
            nonce ?? good.Nonce,
            signature ?? good.Signature);
        if (key == null && date == null && nonce == null && signature == null)
        {
            headers = new SignatureHeaders(null, null, null, null);
        }

        Assert.Equal(CheckOutcome.Refused, Run(check, headers));
    }

    [Theory]
    [InlineData("0", false, 0L)]
    [InlineData("1800000000", true, 1800000000L)]
    [InlineData("9007199254740991", true, 9007199254740991L)]
    [InlineData("9007199254740992", false, 0L)]
    [InlineData("9999999999999999999", false, 0L)]
    [InlineData("-1", false, 0L)]
    [InlineData("+1", false, 0L)]
    [InlineData("01", false, 0L)]
    [InlineData("1e9", false, 0L)]
    [InlineData("１", false, 0L)]
    public void DateParsing(string text, bool ok, long value)
    {
        Assert.Equal(ok, RequestCheck.TryParseDate(text, out var d));
        Assert.Equal(value, d);
    }

    [Fact]
    public void SignedLinesMustBeAscii()
    {
        Assert.Throws<ArgumentException>(() => Signing.RequestMessage("POST", "é.example.org", Target, "1", "n", "h"));
        Assert.Throws<ArgumentException>(() => Signing.RequestMessage("POST", Host, "/a\nb", "1", "n", "h"));
    }

    [Fact]
    public void ARequestWithAnEmptyBodyHashesTheEmptyString()
    {
        var m = Encoding.ASCII.GetString(Signing.RequestMessage("POST", Host, Target, "1", "n", ReadOnlySpan<byte>.Empty));
        Assert.EndsWith("\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", m);
        Assert.Equal(6, m.Split('\n').Length - 1);
    }
}
