using System;
using System.Linq;
using Jellyfin.Plugin.Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class EncodingsTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("66", "Zg")]
    [InlineData("666f", "Zm8")]
    [InlineData("666f6f", "Zm9v")]
    [InlineData("fbff", "-_8")]
    public void Base64UrlRoundTrips(string hex, string text)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Equal(text, Encodings.Base64Url(bytes));
        Assert.True(Encodings.TryDecodeBase64Url(text, out var back));
        Assert.Equal(bytes, back);
    }

    [Theory]
    [InlineData("Zg==")] // padding
    [InlineData("Zm9v\n")] // white space
    [InlineData(" Zm9v")]
    [InlineData("Zm+v")] // standard alphabet
    [InlineData("Zm/v")]
    [InlineData("Zh")] // unused bits not zero
    [InlineData("Zm9")] // unused bits not zero
    [InlineData("Z")] // impossible length
    [InlineData("Zm9vY")]
    public void Base64UrlDecodingIsStrict(string text)
    {
        Assert.False(Encodings.TryDecodeBase64Url(text, out _));
    }

    [Fact]
    public void Base64UrlRefusesNull()
    {
        Assert.False(Encodings.TryDecodeBase64Url(null, out _));
    }

    [Theory]
    // RFC 4648 section 10, without padding.
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32MatchesRfc4648(string input, string expected)
    {
        Assert.Equal(expected, Encodings.Base32(System.Text.Encoding.ASCII.GetBytes(input)));
    }

    [Fact]
    public void Sha256HexOfNothingIsTheSpecsEmptyBodyHash()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Encodings.Sha256Hex(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void HexIsLowercase()
    {
        Assert.Equal("00abff", Encodings.Hex(new byte[] { 0, 0xab, 0xff }));
    }
}
