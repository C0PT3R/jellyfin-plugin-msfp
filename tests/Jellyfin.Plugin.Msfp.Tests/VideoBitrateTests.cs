using System.Collections.Generic;
using Jellyfin.Plugin.Msfp.Viewing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// A paired server's stream never gets Jellyfin's guessed bitrate for live
// streams: its video track gets the file's share left after its audio.
public class VideoBitrateTests
{
    private static MediaSourceInfo Source(int? total, int? video, params int?[] audio)
    {
        var streams = new List<MediaStream> { new() { Type = MediaStreamType.Video, Width = 1280, BitRate = video } };
        foreach (var a in audio)
        {
            streams.Add(new MediaStream { Type = MediaStreamType.Audio, BitRate = a });
        }

        return new MediaSourceInfo { Bitrate = total, MediaStreams = streams };
    }

    [Fact]
    public void TheShareLeftAfterTheAudio()
    {
        var s = Source(5_000_000, null, 192_000, 128_000);
        Assert.True(RemotePlays.VideoBitrate(s));
        Assert.Equal(4_680_000, s.MediaStreams[0].BitRate);
    }

    [Fact]
    public void AKnownBitrateStays()
    {
        var s = Source(5_000_000, 3_000_000, 192_000);
        Assert.False(RemotePlays.VideoBitrate(s));
        Assert.Equal(3_000_000, s.MediaStreams[0].BitRate);
    }

    [Fact]
    public void NoTotalNoGuess()
    {
        var s = Source(null, null, 192_000);
        Assert.False(RemotePlays.VideoBitrate(s));
        Assert.Null(s.MediaStreams[0].BitRate);
    }

    [Fact]
    public void AudioAboveTheTotalLeavesTheTotal()
    {
        var s = Source(100_000, null, 192_000);
        RemotePlays.VideoBitrate(s);
        Assert.Equal(100_000, s.MediaStreams[0].BitRate);
    }
}

// A paired server's subtitles start as a local file's own tracks would.
public class EmbeddedLikeTests
{
    private static MediaStream Sub(int index, string lang, bool isDefault = false, bool forced = false) =>
        new() { Index = index, Type = MediaStreamType.Subtitle, Language = lang, IsDefault = isDefault, IsForced = forced, IsExternal = true };

    [Fact]
    public void NoneMarkedStaysOff() =>
        Assert.Equal(-1, AudioSwitchFilter.EmbeddedLike([Sub(3, "eng"), Sub(4, "fre")], "fre"));

    [Fact]
    public void TheDefaultOne() =>
        Assert.Equal(4, AudioSwitchFilter.EmbeddedLike([Sub(3, "eng"), Sub(4, "fre", isDefault: true)], "eng"));

    [Fact]
    public void ForcedInTheUsersLanguageFirst() =>
        Assert.Equal(5, AudioSwitchFilter.EmbeddedLike([Sub(3, "eng"), Sub(4, "ger", forced: true), Sub(5, "fre", forced: true)], "fre"));
}
