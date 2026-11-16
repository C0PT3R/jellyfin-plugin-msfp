using System;
using System.Text;
using Jellyfin.Plugin.Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class KeysTests
{
    [Fact]
    public void Rfc8032FirstTestVector()
    {
        // RFC 8032 section 7.1, TEST 1: the empty message.
        var key = ServerKey.FromSeed(Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"));
        Assert.Equal("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", Encodings.Hex(key.PublicKey));
        var sig = key.Sign(ReadOnlySpan<byte>.Empty);
        Assert.Equal("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b", Encodings.Hex(sig));
        Assert.True(Keys.Verify(key.PublicKey, ReadOnlySpan<byte>.Empty, sig));
    }

    [Fact]
    public void GeneratedKeysSignAndVerify()
    {
        var key = ServerKey.Generate();
        var other = ServerKey.Generate();
        var msg = Encoding.ASCII.GetBytes("hello");
        var sig = key.Sign(msg);
        Assert.Equal(43, key.PublicKeyText.Length);
        Assert.Equal(86, Encodings.Base64Url(sig).Length);
        Assert.True(Keys.Verify(key.PublicKey, msg, sig));
        Assert.False(Keys.Verify(other.PublicKey, msg, sig));
        Assert.False(Keys.Verify(key.PublicKey, Encoding.ASCII.GetBytes("hellO"), sig));
        sig[10] ^= 1;
        Assert.False(Keys.Verify(key.PublicKey, msg, sig));
    }

    [Fact]
    public void SeedRoundTrips()
    {
        var key = ServerKey.Generate();
        var again = ServerKey.FromSeed(key.ExportSeed());
        Assert.Equal(key.PublicKey, again.PublicKey);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(64)]
    public void SeedMustBe32Bytes(int size)
    {
        Assert.Throws<ArgumentException>(() => ServerKey.FromSeed(new byte[size]));
    }

    [Fact]
    public void VerifyRefusesWrongSizes()
    {
        var key = ServerKey.Generate();
        var sig = key.Sign(new byte[] { 1 });
        Assert.False(Keys.Verify(key.PublicKey.AsSpan(0, 31), new byte[] { 1 }, sig));
        Assert.False(Keys.Verify(key.PublicKey, new byte[] { 1 }, sig.AsSpan(0, 63)));
    }

    [Fact]
    public void FingerprintHasSixGroupsOfFour()
    {
        var fp = ServerKey.Generate().Fingerprint;
        Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){5}$", fp);
    }
}

/// <summary>
/// How the Ed25519 verifier treats the edge cases SPEC-GAPS.md 4.1 lists:
/// these tests pin the behaviour down, so a library update can't change it
/// unnoticed.
/// </summary>
public class Ed25519EdgeCaseTests
{
    // The group order L, little-endian.
    private static readonly byte[] L = Convert.FromHexString("edd3f55c1a631258d69cf7a2def9de1400000000000000000000000000000010");

    [Fact]
    public void RefusesSPlusL()
    {
        var key = ServerKey.Generate();
        var msg = new byte[] { 1, 2, 3 };
        var sig = key.Sign(msg);

        // S + L is the same scalar, in a non-canonical form.
        var carry = 0;
        for (var i = 0; i < 32; i++)
        {
            var sum = sig[32 + i] + L[i] + carry;
            sig[32 + i] = (byte)sum;
            carry = sum >> 8;
        }

        Assert.Equal(0, carry);
        Assert.False(Keys.Verify(key.PublicKey, msg, sig));
    }

    [Fact]
    public void RefusesTheIdentityAsPublicKey()
    {
        // A = identity (small order), R = identity, S = 0 verifies under the
        // plain cofactorless equation for every message.
        var identity = new byte[32];
        identity[0] = 1;
        var sig = new byte[64];
        sig[0] = 1;
        Assert.False(Keys.Verify(identity, new byte[] { 1 }, sig));
    }
}
