using Xunit;

namespace Msfp.Tests;

public class AddressTests
{
    [Theory]
    [InlineData("https://a.example.org", "a.example.org")]
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
        // Labels of 63 characters, joined by dots: names stop at 253
        // characters, so only a long name with a port gets near 512; the
        // whole address is also capped at 512 bytes.
        var label = new string('a', 63);
        var name = string.Join('.', label, label, label, new string('a', 61));
        Assert.Equal(253, name.Length);
        Assert.True(Address.IsValid("https://" + name));
        Assert.False(Address.IsValid("https://" + name + "a"));
        Assert.False(Address.IsValid("https://" + new string('a', 504)));
    }
}
