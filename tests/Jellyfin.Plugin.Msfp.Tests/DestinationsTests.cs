using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
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

    public static IEnumerable<object[]> V1Destinations() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "vectors", "v1-strict.json"))).RootElement
            .GetProperty("destinations").EnumerateArray().Select(c => new object[] { c.GetProperty("text").GetString()!, c.GetProperty("valid").GetBoolean() });

    /// <summary>
    /// The destinations of vectors/v1/strict.json (msfp-spec fdc4d3b), NAT64
    /// and 6to4 included: section 3.2 of version 0 says the same since
    /// 2026-11-17. A text that isn't an IP address (a zone) can't be one.
    /// </summary>
    [Theory]
    [MemberData(nameof(V1Destinations))]
    public void TheV1DestinationVectors(string text, bool valid)
    {
        var allowed = IPAddress.TryParse(text, out var ip) && !text.Contains('%', StringComparison.Ordinal) && Destinations.IsAllowed(ip, allowLoopback: false);
        Assert.Equal(valid, allowed);
    }

    [Theory]
    [InlineData("64:ff9b:1::a9fe:a9fe", false)]
    [InlineData("64:ff9b:1::c000:20a", true)]
    public void TheLocalUseNat64PrefixByItsLast32Bits(string ip, bool allowed)
    {
        Assert.Equal(allowed, Destinations.IsAllowed(IPAddress.Parse(ip), allowLoopback: false));
    }

    [Fact]
    public void TheClientAsksForNoContentCoding()
    {
        using var t = new Jellyfin.Plugin.Msfp.Federation.DefaultTransport();
        Assert.Equal(DecompressionMethods.None, ((System.Net.Http.SocketsHttpHandler)t.Any).AutomaticDecompression);
    }
}
