using System;
using System.IO;
using Jellyfin.Plugin.Msfp.Viewing;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>"Recently added" (migration plan 4.1, as Overfin): the day this server first saw a title, a first reading's titles old, kept across passes, seeded on an update.</summary>
public sealed class FirstSeenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msfp-firstseen-" + Guid.NewGuid().ToString("N"));

    private string File => FirstSeen.PathIn(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void AFirstReadingsTitlesAreOldTheNextOnesNew()
    {
        var day1 = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);
        var seen = FirstSeen.Load(File);
        Assert.False(seen.Existed);
        Assert.Equal(FirstSeen.Old, seen.Of("msfp://paired/movie/tmdb/1", ["a"], day1));
        seen.Read(["a"]);
        seen.Save(File);

        var day2 = day1.AddDays(1);
        seen = FirstSeen.Load(File);
        Assert.True(seen.Existed);
        Assert.Equal(FirstSeen.Old, seen.Of("msfp://paired/movie/tmdb/1", ["a"], day2));   // kept
        Assert.Equal(day2, seen.Of("msfp://paired/movie/tmdb/2", ["a"], day2));            // new from a server read before
        Assert.Equal(FirstSeen.Old, seen.Of("msfp://paired/movie/tmdb/3", ["b"], day2));   // b's first reading
        Assert.Equal(day2, seen.Of("msfp://paired/movie/tmdb/4", ["b", "a"], day2));       // a read before holds it too
        seen.Save(File);

        // Made again later (moved, gone and back): its day stays.
        seen = FirstSeen.Load(File);
        Assert.Equal(day2, seen.Of("msfp://paired/movie/tmdb/2", ["c"], day2.AddDays(30)));
    }

    [Fact]
    public void AnUpdateTakesTheStandInsDays()
    {
        var made = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);
        var seen = FirstSeen.Load(File);
        seen.Seed("msfp://paired/movie/tmdb/1", made);
        seen.Read(["a"]);
        Assert.Equal(made, seen.Of("msfp://paired/movie/tmdb/1", ["a"], now));
        Assert.Equal(now, seen.Of("msfp://paired/movie/tmdb/2", ["a"], now));
    }

    [Fact]
    public void TitlesUnseenLongAreForgotten()
    {
        var day1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var seen = FirstSeen.Load(File);
        seen.Read(["a"]);
        Assert.Equal(day1, seen.Of("msfp://paired/movie/tmdb/1", ["a"], day1));
        seen.Prune(day1.AddDays(399));
        Assert.Equal(day1, seen.Of("msfp://paired/movie/tmdb/1", ["a"], day1.AddDays(399)));
        seen.Prune(day1.AddDays(399 + 401));
        var later = day1.AddDays(900);
        Assert.Equal(later, seen.Of("msfp://paired/movie/tmdb/1", ["a"], later));
    }
}
