using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Protocol;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>Section 3.2: where the plugin connects for a peer.</summary>
public class DestinationsTests
{
    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("192.0.2.10", true)]
    [InlineData("fd00::1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.255.0.9", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("fe80::1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("0.1.2.3", false)]
    [InlineData("::", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("239.255.255.250", false)]
    [InlineData("ff02::1", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("64:ff9b::a9fe:a9fe", false)]
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("64:ff9b::c000:20a", true)]
    public void AddressesByDefault(string ip, bool allowed)
    {
        Assert.Equal(allowed, Destinations.IsAllowed(IPAddress.Parse(ip), allowLoopback: false));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("169.254.169.254", false)]
    [InlineData("fe80::1", false)]
    [InlineData("0.0.0.0", false)]
    public void LoopbackOnlyWhenAllowed(string ip, bool allowed)
    {
        Assert.Equal(allowed, Destinations.IsAllowed(IPAddress.Parse(ip), allowLoopback: true));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("169.254.169.254")]
    [InlineData("localhost")]
    public async Task ARefusedAddressIsNeverConnectedTo(string host)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => Destinations.ConnectAsync(host, 9, allowLoopback: false, CancellationToken.None));
    }
}
