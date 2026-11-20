using Jellyfin.Plugin.Msfp.Viewing;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class LiveStreamIdTests
{
    [Theory]
    // As a session reports it: Jellyfin's provider prefix, then ours.
    [InlineData("c976990fb88b2e8dcf2f7b4181898d90_msfp-2437e421a5ab448ea36790f0a5024447", "msfp-2437e421a5ab448ea36790f0a5024447", true)]
    [InlineData("msfp-2437e421a5ab448ea36790f0a5024447", "msfp-2437e421a5ab448ea36790f0a5024447", true)]
    [InlineData("c976990fb88b2e8dcf2f7b4181898d90_msfp-aaaa", "msfp-2437e421a5ab448ea36790f0a5024447", false)]
    [InlineData("xmsfp-2437e421a5ab448ea36790f0a5024447", "msfp-2437e421a5ab448ea36790f0a5024447", false)]
    [InlineData(null, "msfp-2437e421a5ab448ea36790f0a5024447", false)]
    [InlineData("", "", false)]
    public void SameStream(string? jellyfin, string ours, bool same) =>
        Assert.Equal(same, RemotePlays.SameStream(jellyfin, ours));
}
