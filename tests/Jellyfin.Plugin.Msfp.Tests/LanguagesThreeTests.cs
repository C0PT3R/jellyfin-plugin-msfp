using Jellyfin.Plugin.Msfp.Identification;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class LanguagesThreeTests
{
    [Theory]
    [InlineData("fr", "fre")]
    [InlineData("fr-CA", "fre")]
    [InlineData("en", "eng")]
    [InlineData("de", "ger")]
    [InlineData("fra", "fre")]
    [InlineData("xx", "")]
    public void ThreeIsTheCodeFilesWrite(string tag, string three) => Assert.Equal(three, Languages.Three(tag));
}
