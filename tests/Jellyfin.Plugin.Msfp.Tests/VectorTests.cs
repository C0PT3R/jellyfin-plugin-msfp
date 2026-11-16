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
