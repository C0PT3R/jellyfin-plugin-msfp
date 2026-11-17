using System.Linq;
using Xunit;

namespace Msfp.Tests;

public class NamesTests
{
    [Theory]
    [InlineData("Server A", "Server A")]
    [InlineData("  a  b  ", "a b")]
    [InlineData("a  b", "a b")]
    [InlineData("a​b", "ab")]
    [InlineData("a\u0000b", "ab")]
    [InlineData("a\tb\r\nc", "a b c")]
    [InlineData("", "host")]
    [InlineData("​\u0007", "host")]
    [InlineData(null, "host")]
    public void Cleans(string? name, string expected)
    {
        Assert.Equal(expected, Names.Clean(name, "host"));
    }

    [Fact]
    public void CutsTo80CodePoints()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 100));
        var clean = Names.Clean(emoji, "h");
        Assert.Equal(80, clean.EnumerateRunes().Count());

        var spaced = new string('a', 79) + " b";
        Assert.Equal(new string('a', 79), Names.Clean(spaced, "h"));
    }
}
