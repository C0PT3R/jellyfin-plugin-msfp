using System;
using System.IO;
using Jellyfin.Plugin.Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>Every value of vectors/v0.json, recomputed (MSFP section 15).</summary>
public class VectorTests
{
    [Fact]
    public void CopyMatchesTheSpecRepositoryFile()
    {
        // The copy the tests read is the one in the source tree.
        Assert.Equal(File.ReadAllBytes(Vectors.SourceCopyPath), File.ReadAllBytes(Vectors.CopyPath));

        // And it is the specification's own, byte for byte, when that file
        // is at hand (MSFP_SPEC_DIR, or msfp-spec beside this repository).
        if (File.Exists(Vectors.SpecPath))
        {
            Assert.Equal(File.ReadAllBytes(Vectors.SpecPath), File.ReadAllBytes(Vectors.CopyPath));
        }
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public void KeysAndFingerprints(string name)
    {
        var v = Vectors.Key(name);
        var key = ServerKey.FromSeed(v.Hex("seed"));
        Assert.Equal(v.Str("public"), key.PublicKeyText);
        Assert.Equal(v.Str("fingerprint"), key.Fingerprint);
        Assert.True(Encodings.TryDecodeBase64Url(v.Str("public"), out var raw));
        Assert.Equal(v.Str("fingerprint"), Keys.Fingerprint(raw));
    }
}

public class PairingVectorTests
{
    [Fact]
    public void CodeParsesAndFormats()
    {
        var v = Vectors.Root.GetProperty("code");
        var maker = ServerKey.FromSeed(Vectors.Key(v.Str("key")).Hex("seed"));
        var code = new PairingCode(maker.PublicKey, v.Hex("secret"), v.GetProperty("expires").GetUInt64(), v.Str("address"));
        Assert.Equal(v.Str("text"), code.Format());

        Assert.True(PairingCode.TryParse(v.Str("text"), out var parsed));
        Assert.Equal(maker.PublicKey, parsed.PublicKey);
        Assert.Equal(v.Hex("secret"), parsed.Secret);
        Assert.Equal(v.GetProperty("expires").GetUInt64(), parsed.Expires);
        Assert.Equal(v.Str("address"), parsed.Address);
    }

    [Fact]
    public void SecretHash()
    {
        var v = Vectors.Root.GetProperty("code");
        Assert.Equal(v.Str("secretHash"), Encodings.Base64Url(PairingProof.SecretHash(v.Hex("secret"))));
    }

    [Fact]
    public void Proof()
    {
        var code = Vectors.Root.GetProperty("code");
        var v = Vectors.Root.GetProperty("proof");
        var redeemer = ServerKey.FromSeed(Vectors.Key(v.Str("redeemer")).Hex("seed"));
        var address = System.Text.Encoding.ASCII.GetBytes(v.Str("address"));
        var proof = PairingProof.Compute(code.Hex("secret"), redeemer.PublicKey, address);
        Assert.Equal(v.Str("value"), Encodings.Base64Url(proof));
        Assert.True(Encodings.TryDecodeBase64Url(v.Str("value"), out var raw));
        Assert.True(PairingProof.Check(code.Hex("secret"), redeemer.PublicKey, address, raw));
    }
}

public class ExchangeVectorTests
{
    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var e in Vectors.Root.GetProperty("exchanges").EnumerateArray())
        {
            data.Add(e.Str("name"));
        }

        return data;
    }

    private static System.Text.Json.JsonElement Exchange(string name)
    {
        foreach (var e in Vectors.Root.GetProperty("exchanges").EnumerateArray())
        {
            if (e.Str("name") == name)
            {
                return e;
            }
        }

        throw new ArgumentException(name);
    }

    private static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ThereAreThreeExchanges()
    {
        Assert.Equal(3, Names().Count);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void RequestMessageAndSignature(string name)
    {
        var e = Exchange(name);
        var signer = ServerKey.FromSeed(Vectors.Key(e.Str("signer")).Hex("seed"));
        var nonce = e.Hex("nonce");
        Assert.Equal(e.Str("nonceText"), Encodings.Base64Url(nonce));

        var date = DateTimeOffset.FromUnixTimeSeconds(long.Parse(e.Str("date"), System.Globalization.CultureInfo.InvariantCulture));
        var headers = Signing.SignRequest(signer, e.Str("method"), e.Str("host"), e.Str("target"), Utf8(e.Str("body")), date, nonce);
        Assert.Equal(signer.PublicKeyText, headers.Key);
        Assert.Equal(e.Str("date"), headers.Date);
        Assert.Equal(e.Str("nonceText"), headers.Nonce);
        Assert.Equal(e.Str("signature"), headers.Signature);

        var message = Signing.RequestMessage(e.Str("method"), e.Str("host"), e.Str("target"), e.Str("date"), e.Str("nonceText"), Utf8(e.Str("body")));
        Assert.Equal(Utf8(e.Str("message")), message);
        Assert.True(Encodings.TryDecodeBase64Url(e.Str("signature"), out var sig));
        Assert.True(Keys.Verify(signer.PublicKey, message, sig));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void AnswerMessageAndSignature(string name)
    {
        var e = Exchange(name);
        var answerer = ServerKey.FromSeed(Vectors.Key(e.Str("answerer")).Hex("seed"));
        var status = e.GetProperty("status").GetInt32();
        var body = Utf8(e.Str("answerBody"));

        Assert.Equal(Utf8(e.Str("answerMessage")), Signing.AnswerMessage(e.Str("nonceText"), status, body));
        Assert.Equal(e.Str("answerSignature"), Signing.SignAnswer(answerer, e.Str("nonceText"), status, body));
        Assert.True(Signing.CheckAnswer(answerer.PublicKey, e.Str("nonceText"), status, body, e.Str("answerSignature")));

        // Bound to the nonce, the status and the body, and to the answerer.
        var signer = ServerKey.FromSeed(Vectors.Key(e.Str("signer")).Hex("seed"));
        Assert.False(Signing.CheckAnswer(signer.PublicKey, e.Str("nonceText"), status, body, e.Str("answerSignature")));
        Assert.False(Signing.CheckAnswer(answerer.PublicKey, "AAAAAAAAAAAAAAAAAAAAAA", status, body, e.Str("answerSignature")));
        Assert.False(Signing.CheckAnswer(answerer.PublicKey, e.Str("nonceText"), status == 200 ? 404 : 200, body, e.Str("answerSignature")));
        Assert.False(Signing.CheckAnswer(answerer.PublicKey, e.Str("nonceText"), status, Utf8(e.Str("answerBody") + " "), e.Str("answerSignature")));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReceiverAcceptsTheRequestOnce(string name)
    {
        var e = Exchange(name);
        var date = long.Parse(e.Str("date"), System.Globalization.CultureInfo.InvariantCulture);
        var clock = ManualClock.AtSeconds(date - 3600);
        var check = new RequestCheck(clock, new NonceMemory(clock));
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(date);

        var signer = ServerKey.FromSeed(Vectors.Key(e.Str("signer")).Hex("seed"));
        var headers = new SignatureHeaders(signer.PublicKeyText, e.Str("date"), e.Str("nonceText"), e.Str("signature"));
        var body = Utf8(e.Str("body"));

        var result = check.Check(e.Str("host"), e.Str("method"), e.Str("target"), headers, body, _ => Hearing.Peer);
        Assert.Equal(CheckOutcome.Accepted, result.Outcome);
        Assert.Equal(signer.PublicKey, result.Key);
        Assert.Equal(e.Str("nonceText"), result.Nonce);

        // The same request again is a replay.
        Assert.Equal(CheckOutcome.Refused, check.Check(e.Str("host"), e.Str("method"), e.Str("target"), headers, body, _ => Hearing.Peer).Outcome);
    }
}
