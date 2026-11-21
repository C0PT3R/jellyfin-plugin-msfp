using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// The configuration page's wait for federation to change.
public class ChangesTests
{
    [Fact]
    public async Task AnswersAtOncePastTheCountSeen()
    {
        var c = new Changes();
        c.Bump();
        Assert.Equal(1, await c.WaitAsync(0, TimeSpan.FromSeconds(10), CancellationToken.None));
    }

    [Fact]
    public async Task WaitsForAChange()
    {
        var c = new Changes();
        var wait = c.WaitAsync(0, TimeSpan.FromSeconds(10), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        c.Bump();
        Assert.Equal(1, await wait);
    }

    [Fact]
    public async Task GivesUpAfterTheWait()
    {
        var c = new Changes();
        Assert.Equal(0, await c.WaitAsync(0, TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }
}
