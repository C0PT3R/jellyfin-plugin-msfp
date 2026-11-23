using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Playback;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// The copying out of an embedded PGS track: bounded as the probe is, one
// run per track at a time. A shell script stands in for ffmpeg.
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class PgsExtractionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pgs-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Script(string body)
    {
        var path = Path.Combine(_dir, "ff" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void TheArgumentsAllowTheFileProtocolOnly()
    {
        var a = PgsExtraction.Arguments("/proc/1/fd/7", 3).ToList();
        var w = a.IndexOf("-protocol_whitelist");
        Assert.True(w >= 0 && a[w + 1] == "file" && w < a.IndexOf("-i"));
        Assert.Equal("file:/proc/1/fd/7", a[a.IndexOf("-i") + 1]);
        Assert.Equal("0:3", a[a.IndexOf("-map") + 1]);
        Assert.Contains("-nostdin", a);
        Assert.Equal("pipe:1", a[^1]);
        // The bounds chosen.
        Assert.Equal(TimeSpan.FromMinutes(10), PgsExtraction.Timeout);
        Assert.Equal(128 << 20, PgsExtraction.MaxOutput);
        Assert.Equal(19, PgsExtraction.Nice);
    }

    [Fact]
    public async Task ARunWithinTheBoundsGivesItsOutputAtTheLowestPriority()
    {
        // Its niceness, read once the parent has lowered it; no environment.
        var (data, failed) = await PgsExtraction.RunAsync(Script("/usr/bin/sleep 0.5; /usr/bin/nice; echo \"[$HOME]\""), [], TimeSpan.FromSeconds(20), 1 << 20, 19);
        Assert.Equal(PgsExtraction.Failure.None, failed);
        Assert.Equal("19\n[]\n", System.Text.Encoding.ASCII.GetString(data!));
    }

    [Fact]
    public async Task ARunPastTheTimeLimitIsKilled()
    {
        var pid = Path.Combine(_dir, "pid");
        var watch = Stopwatch.StartNew();
        var (data, failed) = await PgsExtraction.RunAsync(Script("echo $$ > " + pid + "; exec /usr/bin/sleep 60"), [], TimeSpan.FromMilliseconds(500), 1 << 20, 19);
        Assert.Null(data);
        Assert.Equal(PgsExtraction.Failure.Time, failed);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists("/proc/" + File.ReadAllText(pid).Trim()));
    }

    [Fact]
    public async Task ARunPastTheOutputCapIsKilled()
    {
        var pid = Path.Combine(_dir, "pid");
        var (data, failed) = await PgsExtraction.RunAsync(Script("echo $$ > " + pid + "; exec /usr/bin/cat /dev/zero"), [], TimeSpan.FromSeconds(20), 1 << 20, 19);
        Assert.Null(data);
        Assert.Equal(PgsExtraction.Failure.Size, failed);
        Assert.False(Directory.Exists("/proc/" + File.ReadAllText(pid).Trim()));
    }

    [Fact]
    public async Task AFailedOrMissingProgramGivesNothing()
    {
        Assert.Equal(PgsExtraction.Failure.Exit, (await PgsExtraction.RunAsync(Script("echo x; exit 1"), [], TimeSpan.FromSeconds(20), 1 << 20, 19)).Failed);
        Assert.Equal(PgsExtraction.Failure.Start, (await PgsExtraction.RunAsync(Path.Combine(_dir, "none"), [], TimeSpan.FromSeconds(20), 1 << 20, 19)).Failed);
    }

    [Fact]
    public async Task AsksWhileARunRunsWaitForThatOne()
    {
        var runs = new SingleRuns<byte[]?>();
        var started = 0;
        var gate = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<byte[]?> Start()
        {
            Interlocked.Increment(ref started);
            return gate.Task;
        }

        var asks = Enumerable.Range(0, 8).Select(_ => runs.Run("item/3", Start)).ToList();
        var other = runs.Run("item/4", () => Task.FromResult<byte[]?>([4]));
        Assert.Equal([4], await other);
        gate.SetResult([3]);
        foreach (var a in asks)
        {
            Assert.Equal([3], await a);
        }

        Assert.Equal(1, started);
        // Once done, a new ask runs again.
        Assert.Equal([5], await runs.Run("item/3", () => Task.FromResult<byte[]?>([5])));
    }
}
