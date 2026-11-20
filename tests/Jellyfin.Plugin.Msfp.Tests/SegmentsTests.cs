using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Msfp.Viewing;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class SegmentsTests
{
    [Theory]
    [InlineData("intro", MediaSegmentType.Intro)]
    [InlineData("outro", MediaSegmentType.Outro)]
    [InlineData("recap", MediaSegmentType.Recap)]
    [InlineData("preview", MediaSegmentType.Preview)]
    [InlineData("commercial", MediaSegmentType.Commercial)]
    public void KindsMapBothWays(string kind, MediaSegmentType type)
    {
        Assert.Equal(type, PairedSegments.TypeOf(kind));
        Assert.Equal(kind, PairedSegments.KindOf(type));
    }

    [Fact]
    public void OthersAreLeftOut()
    {
        Assert.Null(PairedSegments.TypeOf("credits"));
        Assert.Null(PairedSegments.KindOf(MediaSegmentType.Unknown));
    }

    [Fact]
    public void TheProviderIdIsJellyfins()
    {
        // Jellyfin's: the MD5 of the name in lower case (UTF-16), as a GUID.
        Assert.Equal("6d839e0b569931cb4a7e84f5b8cfaafa", PairedSegments.ProviderId);
    }
}
