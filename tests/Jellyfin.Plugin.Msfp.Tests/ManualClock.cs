using System;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>A clock the tests set by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    public ManualClock(DateTimeOffset now)
    {
        Now = now;
    }

    public DateTimeOffset Now { get; set; }

    public static ManualClock AtSeconds(double unixSeconds) =>
        new(DateTimeOffset.UnixEpoch.AddTicks((long)(unixSeconds * TimeSpan.TicksPerSecond)));

    public void Advance(TimeSpan by) => Now += by;

    public override DateTimeOffset GetUtcNow() => Now;
}
