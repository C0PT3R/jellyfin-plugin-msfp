using Jellyfin.Plugin.Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class AddressTests
{
    [Theory]
    [InlineData("https://a.example.org", "a.example.org")]
    [InlineData("https://a.example.org/", "a.example.org")]
    [InlineData("http://192.0.2.10:8096", "192.0.2.10:8096")]
    [InlineData("http://192.0.2.10:443", "192.0.2.10:443")]
    [InlineData("https://a.example.org:80", "a.example.org:80")]
    [InlineData("https://[2001:db8::1]", "[2001:db8::1]")]
    [InlineData("https://[2001:db8::1]:8920", "[2001:db8::1]:8920")]
    [InlineData("http://localhost:65535", "localhost:65535")]
    [InlineData("https://x-1.example.org", "x-1.example.org")]
    public void AcceptsAndNamesTheHost(string address, string host)
    {
        Assert.True(Address.TryGetHost(address, out var h));
        Assert.Equal(host, h);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.example.org")]
    [InlineData("ftp://a.example.org")]
    [InlineData("HTTPS://a.example.org")] // scheme in capitals
    [InlineData("https://A.example.org")] // host in capitals
    [InlineData("https://")]
    [InlineData("https://a.example.org/media")]
    [InlineData("https://a.example.org//")]
    [InlineData("https://a.example.org?x=1")]
    [InlineData("https://a.example.org?")]
    [InlineData("https://a.example.org#top")]
    [InlineData("https://user@a.example.org")]
    [InlineData("https://a.example.org:443")]
    [InlineData("http://a.example.org:80")]
    [InlineData("https://a.example.org:")]
    [InlineData("https://a.example.org:0")]
    [InlineData("https://a.example.org:08096")]
    [InlineData("https://a.example.org:65536")]
    [InlineData("https://a.example.org:123456")]
    [InlineData("https://-a.example.org")]
    [InlineData("https://a-.example.org")]
    [InlineData("https://a..example.org")]
    [InlineData("https://a.example.org.")]
    [InlineData("https://a_b.example.org")]
    [InlineData("https://[2001:DB8::1]")]
    [InlineData("https://[...]")]
    [InlineData("https://[192.0.2.1]")]
    [InlineData("https://2001:db8::1")]
    [InlineData("https://a.example.org /")]
    [InlineData("https://é.example.org")]
    public void Refuses(string address)
    {
        Assert.False(Address.IsValid(address));
    }

    [Fact]
    public void RefusesNull()
    {
        Assert.False(Address.IsValid(null));
    }

    [Fact]
    public void AtMost512Bytes()
    {
        // The host pattern sets no length on labels; only the 512 bytes count.
        var address = "https://" + new string('a', 504);
        Assert.Equal(512, address.Length);
        Assert.True(Address.IsValid(address));
        Assert.False(Address.IsValid(address + "b"));
    }
}
