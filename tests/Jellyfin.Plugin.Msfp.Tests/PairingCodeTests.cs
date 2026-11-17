using System;
using System.Text;
using Jellyfin.Plugin.Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class PairingCodeTests
{
    private const string Example = "overfin-pair-1.A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbhAQUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVpbXF1eXwAAAABrSyOAaHR0cHM6Ly9hLmV4YW1wbGUub3Jn";

    [Fact]
    public void ParsesTheSpecsExample()
    {
        Assert.True(PairingCode.TryParse(Example, out var code));
        Assert.Equal("A6EHv_POEL4dcN0Y50vAmWfk1jCbpQ1fHdyGZBJVMbg", Encodings.Base64Url(code.PublicKey));
        Assert.Equal("404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f", Encodings.Hex(code.Secret));
        Assert.Equal(1800086400UL, code.Expires);
        Assert.Equal("https://a.example.org", code.Address);
        Assert.Equal(Example, code.Format());
    }

    [Fact]
    public void RemovesWhiteSpaceAnywhere()
    {
        var wrapped = "  overfin-pair-1.\r\n" + Example["overfin-pair-1.".Length..].Insert(40, "\n\t ").Insert(10, " ") + "\n";
        Assert.True(PairingCode.TryParse(wrapped, out var code));
        Assert.Equal(Example, code.Format());
        Assert.True(PairingCode.TryParse("overfin- pair-1." + Example["overfin-pair-1.".Length..], out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("overfin-pair-1.")]
    [InlineData("Overfin-pair-1.A6EH")]
    [InlineData("overfin-pair-2.A6EH")]
    [InlineData("overfin-pair-1A6EH")]
    public void RefusesWithoutThePrefix(string text)
    {
        Assert.False(PairingCode.TryParse(text, out _));
    }

    [Fact]
    public void RefusesBadBase64()
    {
        Assert.False(PairingCode.TryParse(Example + "=", out _));
        Assert.False(PairingCode.TryParse(Example.Replace('_', '/'), out _));
    }

    [Fact]
    public void RefusesAnEmptyAddress()
    {
        var bytes = new byte[72];
        Assert.False(PairingCode.TryParse(PairingCode.Prefix + Encodings.Base64Url(bytes), out _));
    }

    [Theory]
    [InlineData("https://a.example.org/x")]
    [InlineData("https://a.example.org:443")]
    [InlineData("a.example.org")]
    public void RefusesABadAddress(string address)
    {
        var text = new PairingCode(new byte[32], new byte[32], 1, address).Format();
        Assert.False(PairingCode.TryParse(text, out _));
    }

    [Fact]
    public void RefusesANonAsciiAddress()
    {
        var addr = Encoding.UTF8.GetBytes("https://é.example.org");
        var bytes = new byte[72 + addr.Length];
        addr.CopyTo(bytes, 72);
        Assert.False(PairingCode.TryParse(PairingCode.Prefix + Encodings.Base64Url(bytes), out _));
    }

    [Fact]
    public void RefusesARemainderOver779Characters()
    {
        Assert.Equal(779, PairingCode.MaxRemainder);
        var text = PairingCode.Prefix + new string('A', 780);
        Assert.False(PairingCode.TryParse(text, out _));
    }

    [Fact]
    public void CreateExpiresIn24HoursCutToTheSecond()
    {
        var key = ServerKey.Generate();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_999);
        var code = PairingCode.Create(key.PublicKey, "http://192.0.2.10:8096", now);
        Assert.Equal(1_800_086_400UL, code.Expires);
        Assert.Equal(32, code.Secret.Length);
        Assert.True(PairingCode.TryParse(code.Format(), out var back));
        Assert.Equal(code.Secret, back.Secret);
        Assert.Equal(key.PublicKey, back.PublicKey);
        Assert.Equal(code.Address, back.Address);
        Assert.False(code.IsExpired(now));
        Assert.True(code.IsExpired(DateTimeOffset.FromUnixTimeSeconds(1_800_086_400)));
    }

    [Fact]
    public void CreateRefusesABadAddress()
    {
        Assert.Throws<ArgumentException>(() => PairingCode.Create(new byte[32], "https://a.example.org/x", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void EachCodeHasItsOwnSecret()
    {
        var a = PairingCode.Create(new byte[32], "https://a.example.org", DateTimeOffset.UnixEpoch);
        var b = PairingCode.Create(new byte[32], "https://a.example.org", DateTimeOffset.UnixEpoch);
        Assert.NotEqual(a.Secret, b.Secret);
    }

    [Fact]
    public void ProofIsBoundToKeyAndAddress()
    {
        var secret = new byte[32];
        var key = new byte[32];
        var p = PairingProof.Compute(secret, key, Encoding.ASCII.GetBytes("https://b.example.org"));
        Assert.True(PairingProof.Check(secret, key, Encoding.ASCII.GetBytes("https://b.example.org"), p));
        Assert.False(PairingProof.Check(secret, key, Encoding.ASCII.GetBytes("https://c.example.org"), p));
        key[0] = 1;
        Assert.False(PairingProof.Check(secret, key, Encoding.ASCII.GetBytes("https://b.example.org"), p));
        Assert.False(PairingProof.Check(secret, new byte[32], Encoding.ASCII.GetBytes("https://b.example.org"), p.AsSpan(0, 31)));
    }
}
