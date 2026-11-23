using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Msfp.Viewing;
using MediaBrowser.Model.Entities;
using Msfp;
using Xunit;
using Copy = Msfp.Protocol.Copy;
using CopyAudio = Msfp.Protocol.CopyAudio;
using PreviewLayout = Msfp.Protocol.PreviewLayout;
using VideoStream = Msfp.Protocol.VideoStream;
using VideoTitle = Msfp.VideoTitle;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>
/// Migration phase 7 (plan 4.3): paired servers' extras under their titles'
/// stand-ins, their names and types, themes only when let in; previews
/// registered from a copy's layout and fetched at its playback; a play's
/// source shown to its client as the stand-in's.
/// </summary>
public class ExtrasPreviewsTests
{
    private static Copy C(string id, int height = 1080, PreviewLayout? layout = null, long? start = null, long? end = null) =>
        new(id, 60_000, "video/matroska", 1000, "e" + id, [new CopyAudio("a1", "aac")])
        {
            Video = new VideoStream("h264", height * 16 / 9, height),
            PreviewLayout = layout,
            StartMs = start,
            EndMs = end,
        };

    private static VideoTitle T(string key, string? extraType = null, Copy[]? copies = null, string? name = null, Place? place = null)
    {
        var playable = key.StartsWith("movie", StringComparison.Ordinal) || key.Contains("/e", StringComparison.Ordinal) || key.Contains("/x/", StringComparison.Ordinal);
        return new VideoTitle { Key = key, ExtraType = extraType, Name = name, Place = place, Copies = playable ? (copies ?? [C("v1")]).ToList() : null };
    }

    private static Wanted Of(bool themes, params (string Peer, VideoTitle[] Titles)[] peers) =>
        Wanted.Of(peers.Select(p => (p.Peer, p.Titles.Cast<Title>())), new Held(), null, themes);

    // ---- Extras ----

    [Fact]
    public void AFilmsExtrasAreItsChosenHoldersUnderItsStandIn()
    {
        var w = Of(false,
            ("a", [T("movie/tmdb/348", copies: [C("v1", 720)]), T("movie/tmdb/348/x/TrA1", "trailer")]),
            ("b", [T("movie/tmdb/348", copies: [C("v1", 2160)]), T("movie/tmdb/348/x/TrB1", "trailer"), T("movie/tmdb/348/x/FeB1", "featurette")]));
        Assert.Equal("b", w.Films.Values.Single().Copy.Chosen!.Peer);
        Assert.Equal(["msfp://paired/movie/tmdb/348/x/b/FeB1", "msfp://paired/movie/tmdb/348/x/b/TrB1"], w.Extras.Keys);
        var trailer = w.Extras["msfp://paired/movie/tmdb/348/x/b/TrB1"];
        Assert.Equal("msfp://paired/movie/tmdb/348", trailer.Owner);
        Assert.Equal("trailer", trailer.Type);
        Assert.Equal(0, trailer.Number);
        Assert.Equal("movie/tmdb/348/x/TrB1", trailer.Copy.Chosen!.Key);

        // Playable like any copy: found by its path.
        Assert.Same(trailer.Copy, w.ByPath[trailer.Path]);
    }

    [Fact]
    public void NoExtrasWithoutTheirTitlesStandIn()
    {
        var held = new Held();
        held.Films.Add(348);
        var w = Wanted.Of([("a", new Title[] { T("movie/tmdb/348"), T("movie/tmdb/348/x/Tr1", "trailer"), T("movie/tmdb/1/x/Tr2", "trailer") })], held);
        Assert.Empty(w.Extras);
        Assert.Empty(w.ByPath);
    }

    [Fact]
    public void ThemesOnlyWhenLetIn()
    {
        var titles = new[] { T("movie/tmdb/1"), T("movie/tmdb/1/x/Th1", "themevideo"), T("movie/tmdb/1/x/Th2", "themesong"), T("movie/tmdb/1/x/Tr1", "trailer") };
        Assert.Equal(["trailer"], Of(false, ("a", titles)).Extras.Values.Select(x => x.Type));
        Assert.Equal(["themevideo", "themesong", "trailer"], Of(true, ("a", titles)).Extras.Values.Select(x => x.Type));
        Assert.True(Wanted.IsTheme("themesong"));
        Assert.False(Wanted.IsTheme("trailer"));
    }

    [Fact]
    public void SeveralOfATypeAreNumberedByKey()
    {
        var w = Of(false, ("a", [T("movie/tmdb/1"), T("movie/tmdb/1/x/Tb", "trailer"), T("movie/tmdb/1/x/Ta", "trailer"), T("movie/tmdb/1/x/F1", "featurette")]));
        Assert.Equal(1, w.Extras["msfp://paired/movie/tmdb/1/x/a/Ta"].Number);
        Assert.Equal(2, w.Extras["msfp://paired/movie/tmdb/1/x/a/Tb"].Number);
        Assert.Equal(0, w.Extras["msfp://paired/movie/tmdb/1/x/a/F1"].Number);
    }

    [Fact]
    public void ASeriesExtrasAreThoseOfTheServerHoldingMostOfItsEpisodes()
    {
        // b shows e1 (its copy better) and e2, a e3 only.
        var w = Of(false,
            ("a", [T("series/tmdb/95396/s1/e1"), T("series/tmdb/95396/s1/e3"), T("series/tmdb/95396/x/Ta", "trailer")]),
            ("b", [T("series/tmdb/95396/s1/e1", copies: [C("v1", 2160)]), T("series/tmdb/95396/s1/e2"), T("series/tmdb/95396/x/Tb", "trailer"), T("series/tmdb/95396/s1/x/Sb", "featurette"), T("series/tmdb/95396/s2/x/S2", "featurette")]));
        Assert.Equal("msfp://paired/series/tmdb/95396", w.Extras["msfp://paired/series/tmdb/95396/x/b/Tb"].Owner);

        // A season's, under its season; none for a season without episodes here.
        Assert.Equal("msfp://paired/series/tmdb/95396/s1", w.Extras["msfp://paired/series/tmdb/95396/s1/x/b/Sb"].Owner);
        Assert.Equal(2, w.Extras.Count);
    }

    [Fact]
    public void ALocalFilmsExtrasUnderItsOwnStandIn()
    {
        var w = Of(false, ("a", [T("movie/local/q3Zp0Lw8", name: "Holidays"), T("movie/local/q3Zp0Lw8/x/Tr7aQ1", "clip")]));
        var x = Assert.Single(w.Extras.Values);
        Assert.Equal("msfp://paired/local/a/movie/local/q3Zp0Lw8", x.Owner);
        Assert.Equal("msfp://paired/local/a/movie/local/q3Zp0Lw8/x/a/Tr7aQ1", x.Path);
    }

    [Fact]
    public void ExtraTypesAndNames()
    {
        Assert.Equal(ExtraType.Trailer, ExtraNames.TypeOf("trailer"));
        Assert.Equal(ExtraType.BehindTheScenes, ExtraNames.TypeOf("behindthescenes"));
        Assert.Equal(ExtraType.ThemeSong, ExtraNames.TypeOf("themesong"));
        Assert.Equal(ExtraType.ThemeVideo, ExtraNames.TypeOf("themevideo"));
        Assert.Equal(ExtraType.Unknown, ExtraNames.TypeOf("making-of-2"));
        Assert.Equal("Trailer", ExtraNames.Of("trailer", 0, "en-US"));
        Assert.Equal("Featurette 2", ExtraNames.Of("featurette", 2, "en"));
        Assert.Equal("Bande-annonce 1", ExtraNames.Of("trailer", 1, "fr-FR"));
        Assert.Equal("Extra", ExtraNames.Of("something", 0, "pt"));
    }

    // ---- Previews ----

    [Fact]
    public void ALayoutAsJellyfinsTrickplay()
    {
        var item = Guid.NewGuid();
        var l = new PreviewLayout(320, 180, 10, 10, 10_000, 540);
        Assert.Equal(6, l.Sheets);
        var t = PeerPreviews.InfoOf(item, l);
        Assert.Equal(item, t.ItemId);
        Assert.Equal(320, t.Width);
        Assert.Equal(180, t.Height);

        // Jellyfin's TileWidth and TileHeight are the grid.
        Assert.Equal(10, t.TileWidth);
        Assert.Equal(10, t.TileHeight);
        Assert.Equal(540, t.ThumbnailCount);
        Assert.Equal(10_000, t.Interval);

        // Before the sheets: one bit a pixel over the 1,000 s a sheet covers;
        // after, the biggest sheet's bits.
        Assert.Equal(5760, t.Bandwidth);
        Assert.Equal(8000, PeerPreviews.InfoOf(item, l, 1_000_000).Bandwidth);
    }

    [Fact]
    public void PreviewsOnlyForAWholeCopyWithALayoutKeptByEtagAndLayout()
    {
        var l = new PreviewLayout(320, 180, 10, 10, 10_000, 5);
        Assert.True(PeerPreviews.Usable(C("x", layout: l)));
        Assert.False(PeerPreviews.Usable(C("x")));

        // An episode that is only part of its copy: the previews are the file's.
        Assert.False(PeerPreviews.Usable(C("x", layout: l, start: 0, end: 30_000)));
        Assert.NotEqual(PeerPreviews.Mark(C("x", layout: l)), PeerPreviews.Mark(C("x", layout: l with { IntervalMs = 5000 })));
        Assert.NotEqual(PeerPreviews.Mark(C("x", layout: l)), PeerPreviews.Mark(C("y", layout: l)));
        Assert.True(PeerPreviews.Again(MsfpError.RateLimited));
        Assert.False(PeerPreviews.Again(MsfpError.NotFound));
    }

    // ---- The source a client is shown ----

    [Fact]
    public void APlayByTheSourceItsClientWasShownForItsUserItemAndDeviceOnly()
    {
        var tokens = new PlayTokens();
        var (user, item) = (Guid.NewGuid(), Guid.NewGuid());
        var a = tokens.Mint(user, item, "p", "k", C("x"), Way.File);
        a.Shown = item.ToString("N");
        a.Device = "tv";
        Assert.Same(a, tokens.OfShown(item.ToString("N").ToUpperInvariant(), user, item, "tv"));
        Assert.Null(tokens.OfShown(item.ToString("N"), Guid.NewGuid(), item, "tv"));
        Assert.Null(tokens.OfShown(item.ToString("N"), user, Guid.NewGuid(), "tv"));
        Assert.Null(tokens.OfShown(null, user, item, "tv"));

        // The newest of the device's; a request naming no device, the user's newest.
        var b = tokens.Mint(user, item, "p", "k", C("x"), Way.File);
        b.Shown = a.Shown;
        b.Device = "phone";
        Assert.Same(a, tokens.OfShown(a.Shown, user, item, "tv"));
        Assert.Same(b, tokens.OfShown(a.Shown, user, item, "phone"));
        Assert.Same(b, tokens.OfShown(a.Shown, user, item, string.Empty));
        Assert.Same(b, tokens.OfShown(a.Shown, user, item, null));
        Assert.Null(tokens.OfShown(a.Shown, Guid.NewGuid(), item, null));
        tokens.Drop(a);
        Assert.Null(tokens.OfShown(a.Shown, user, item, "tv"));
    }
}
