using System;
using Jellyfin.Plugin.Msfp.Viewing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using Msfp;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;
using Copy = Msfp.Protocol.Copy;
using CopyAudio = Msfp.Protocol.CopyAudio;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>
/// Stand-ins out of the plugin's old channel (Jellyfin gives a channel's
/// films and episodes no metadata): library items like local ones, what
/// plays a shortcut with no target; those an earlier version made in the
/// channel moved as they are; the HLS way's source given by the provider.
/// </summary>
public class OutOfChannelTests
{
    private static readonly Guid OldChannel = Guid.Parse("fe98357981b38eb51439129ec5bb8269");

    private static T Made<T>(string path, bool inChannel = true)
        where T : BaseItem, new() =>
        new() { Id = Guid.NewGuid(), Path = path, ChannelId = inChannel ? OldChannel : Guid.Empty };

    [Fact]
    public void AFilmOrAnEpisodeOfTheOldChannelLeavesItAsAShortcutWithNoTarget()
    {
        foreach (var item in new BaseItem[]
        {
            Made<Movie>("msfp://paired/movie/tmdb/782"),
            Made<Episode>("msfp://paired/series/tmdb/95396/s1/e1"),
        })
        {
            var id = item.Id;
            Assert.True(StandIns.Unread(item));
            Assert.Equal(Guid.Empty, item.ChannelId);
            Assert.True(item.IsShortcut);
            Assert.Null(item.ShortcutPath);

            // Kept as it was: its ID (watched state, previews, extras by owner), its path.
            Assert.Equal(id, item.Id);
            Assert.StartsWith("msfp://paired/", item.Path, StringComparison.Ordinal);

            // Once only.
            Assert.False(StandIns.Unread(item));
        }
    }

    [Fact]
    public void ExtrasLeaveTooTheThemeSongAsAnAudioShortcut()
    {
        var trailer = Made<Video>("msfp://paired/movie/tmdb/782/x/p/TrA1");
        var song = Made<Audio>("msfp://paired/movie/tmdb/27205/x/p/Th1");
        Assert.True(StandIns.Unread(trailer));
        Assert.True(StandIns.Unread(song));
        Assert.True(trailer.IsShortcut && song.IsShortcut);
        Assert.Equal(Guid.Empty, trailer.ChannelId);
        Assert.Equal(Guid.Empty, song.ChannelId);
    }

    [Fact]
    public void ASeriesOrASeasonLeavesTheChannelButIsNoShortcut()
    {
        var series = Made<Series>("msfp://paired/series/tmdb/95396");
        var season = Made<Season>("msfp://paired/series/tmdb/95396/s1");
        Assert.True(StandIns.Unread(series));
        Assert.True(StandIns.Unread(season));
        Assert.False(series.IsShortcut || season.IsShortcut);

        // Moved by an earlier version already: nothing to do.
        Assert.False(StandIns.Unread(Made<Series>("msfp://paired/series/tmdb/1399", inChannel: false)));
    }

    [Fact]
    public void AStandInMadeOutOfTheChannelBeforeShortcutsBecomesOne()
    {
        var film = Made<Movie>("msfp://paired/movie/tmdb/949", inChannel: false);
        Assert.True(StandIns.Unread(film));
        Assert.True(film.IsShortcut);

        // A shortcut pointing somewhere is made one with no target.
        film.ShortcutPath = "http://example.invalid/f.mkv";
        Assert.True(StandIns.Unread(film));
        Assert.Null(film.ShortcutPath);
    }

    [Theory]
    [InlineData("", "msfp://paired/movie/tmdb/782", true)]
    [InlineData("782", "msfp://paired/movie/tmdb/782", true)]
    [InlineData("e1", "msfp://paired/series/tmdb/95396/s1/e1", true)]
    [InlineData("Gattaca", "msfp://paired/movie/tmdb/782", false)]
    [InlineData("Good News About Hell", "msfp://paired/series/tmdb/95396/s1/e1", false)]
    public void ItsMetadataIsAskedAgainWhileItIsNamedByItsPath(string name, string path, bool unnamed) =>
        Assert.Equal(unnamed, StandIns.Unnamed(new Movie { Name = name, Path = path }));

    [Fact]
    public void TheHlsWaysSourceIsOpenableUnderThePlaysHandleForItsRequestOnly()
    {
        var film = new Movie { Id = Guid.NewGuid(), Path = "msfp://paired/movie/tmdb/1" };
        var item = film.Id;
        var source = RemotePlays.Openable(new MediaSourceInfo { Id = item.ToString("N"), SupportsDirectPlay = true, SupportsProbing = true }, film, "0f0e0d0c0b0a09080706050403020100");
        Assert.Equal("0f0e0d0c0b0a09080706050403020100", source.Id);
        Assert.True(source.RequiresOpening && source.RequiresClosing);
        Assert.False(source.SupportsDirectPlay || source.SupportsProbing);
        Assert.Equal(PairedSources.TokenPrefix + item.ToString("N") + "-0f0e0d0c0b0a09080706050403020100", source.OpenToken);

        var copy = new Copy("v1", 60_000, "video/mp4", 1000, "e1", [new CopyAudio("a1", "aac")]);
        var tokens = new PlayTokens();
        var hls = tokens.Mint(Guid.NewGuid(), item, "p", "movie/tmdb/1", copy, Way.Hls);
        hls.Source = source;
        var given = PairedSources.HlsOf(hls, item);
        Assert.NotNull(given);
        Assert.NotSame(source, given);
        Assert.Equal(source.OpenToken, given!.OpenToken);

        // Another stand-in, a file's play, no play in the request: nothing.
        Assert.Null(PairedSources.HlsOf(hls, Guid.NewGuid()));
        var file = tokens.Mint(Guid.NewGuid(), item, "p", "movie/tmdb/1", copy, Way.File);
        file.Source = source;
        Assert.Null(PairedSources.HlsOf(file, item));
        Assert.Null(PairedSources.HlsOf(null, item));
    }
}
