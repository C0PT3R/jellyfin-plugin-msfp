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
