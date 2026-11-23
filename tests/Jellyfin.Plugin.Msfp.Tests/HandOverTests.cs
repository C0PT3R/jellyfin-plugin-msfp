using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Msfp.Viewing;
using MediaBrowser.Model.Dlna;
using Msfp;
using Xunit;
using Copy = Msfp.Protocol.Copy;
using CopyAudio = Msfp.Protocol.CopyAudio;
using CopyChapter = Msfp.Protocol.CopyChapter;
using CopySegment = Msfp.Protocol.CopySegment;
using CopySubtitle = Msfp.Protocol.CopySubtitle;
using Limits = Msfp.Limits;
using VideoStream = Msfp.Protocol.VideoStream;
using VideoTitle = Msfp.VideoTitle;

namespace Jellyfin.Plugin.Msfp.Tests;

/// <summary>
/// The hand-over at PlaybackInfo (migration plan 4.2): the holder and the
/// way chosen, refusals remembered, the per-play token (lapse, binding to a
/// user and an item, the secret), a title that is a span of its copy.
/// </summary>
public class HandOverTests
{
    // A copy of 60 s: its size sets its bitrate (8 Mbit/s by default).
    private static Copy C(string id, int height = 1080, long size = 60_000_000, int? parts = null, long? start = null, long? end = null, string codec = "h264") =>
        new(id, 60_000, "video/matroska", size, "e" + id, [new CopyAudio("a1", "eac3") { Default = true }, new CopyAudio("a2", "aac")])
        {
            Video = new VideoStream(codec, height * 16 / 9, height),
            Parts = parts,
            StartMs = start,
            EndMs = end,
        };

    private static Playable Title(params (string Peer, Copy[] Copies)[] holders)
    {
        var p = new Playable { Path = "msfp://paired/movie/tmdb/603" };
        foreach (var (peer, copies) in holders)
        {
            p.Add(peer, new VideoTitle { Key = "movie/tmdb/603", Copies = copies.ToList() });
        }

        return p;
    }

    private static Func<string, HandOver.PeerState> States(params (string Peer, HandOver.PeerState State)[] s) =>
        peer => s.FirstOrDefault(x => x.Peer == peer).State ?? new HandOver.PeerState(true, false, 0);

    private static HandOver.PeerState Free => new(true, false, 0);

    private static HandOver.PeerState Busy => new(false, false, 0);

    [Fact]
    public void TheStandInsHolderItsBestCopyAsAFile()
    {
        var title = Title(("a", [C("x", 720)]), ("b", [C("hd", 2160), C("sd", 480)]));
        var c = HandOver.Choose(title, null, States());
        Assert.Equal(HandOver.Failure.None, c.Failure);
        Assert.Equal("b", c.Holder.Peer);
        Assert.Equal("hd", c.Copy.Id);
        Assert.Equal(Way.File, c.Way);
        Assert.Null(c.Asked);
    }

    [Fact]
    public void TheCopyPickedOnTheStandInsHolder()
    {
        var title = Title(("b", [C("hd", 2160), C("sd", 480)]));
        Assert.Equal("sd", HandOver.Choose(title, "sd", States()).Copy.Id);

        // An ID it doesn't hold: its best.
        Assert.Equal("hd", HandOver.Choose(title, "nope", States()).Copy.Id);
    }

    [Fact]
    public void TheNextHolderWhenTheFirstIsBusyItsBestCopy()
    {
        var title = Title(("a", [C("x", 720)]), ("b", [C("hd", 2160), C("sd", 480)]));
        var c = HandOver.Choose(title, "sd", States(("b", Busy)));
        Assert.Equal("a", c.Holder.Peer);
        Assert.Equal("x", c.Copy.Id);
    }

    [Fact]
    public void AllBusyIsBusyNoneHeldIsNotHeld()
    {
        var title = Title(("a", [C("x")]), ("b", [C("y")]));
        Assert.Equal(HandOver.Failure.Busy, HandOver.Choose(title, null, States(("a", Busy), ("b", Busy))).Failure);
        Assert.Equal(HandOver.Failure.NotHeld, HandOver.Choose(new Playable(), null, States()).Failure);
    }

    [Fact]
    public void HlsForSeveralPartsARefusalOfFileOrABitrateAboveTheCap()
    {
        var free = Free;
        Assert.Equal(Way.File, HandOver.WayOf(C("x"), free));
        Assert.Equal(Way.Hls, HandOver.WayOf(C("x", parts: 2), free));
        Assert.Equal(Way.Hls, HandOver.WayOf(C("x"), new HandOver.PeerState(true, true, 0)));

        // 8 Mbit/s: within a cap of 8,000 kbit/s, above one of 7,999.
        Assert.Equal(Way.File, HandOver.WayOf(C("x"), new HandOver.PeerState(true, false, 8000)));
        Assert.Equal(Way.Hls, HandOver.WayOf(C("x"), new HandOver.PeerState(true, false, 7999)));
    }

    [Fact]
    public void RefusedFileWithinTheCapIsARemuxOfTheTrackAsked()
    {
        var title = Title(("a", [C("x")]));
        var c = HandOver.Choose(title, null, States(("a", new HandOver.PeerState(true, true, 0))), "a2");
        Assert.Equal(Way.Hls, c.Way);
        Assert.Equal("remux", c.Asked!.Delivery);
        Assert.Equal("video/mp2t", c.Asked.MediaType);
        Assert.Equal("a2", c.Asked.AudioTrack);
        Assert.Equal("x", c.Asked.VersionId);
        Assert.Equal("movie/tmdb/603", c.Asked.Title);

        // VP9 doesn't go in MPEG-TS: fMP4.
        var vp9 = Title(("a", [C("x", codec: "vp9")]));
        Assert.Equal("video/mp4", HandOver.Choose(vp9, null, States(("a", new HandOver.PeerState(true, true, 0)))).Asked!.MediaType);
    }

    [Fact]
    public void AboveTheCapThePeerConvertsWithinIt()
    {
        var title = Title(("a", [C("x")]));
        var c = HandOver.Choose(title, null, States(("a", new HandOver.PeerState(true, false, 4000))), audioPlayed: codec => codec == "eac3");
        Assert.Equal("transcode", c.Asked!.Delivery);
        Assert.Equal("h264", c.Asked.Transcode!.VideoCodec);
        Assert.Equal(4000, c.Asked.Transcode.MaxBitrateKbps);

        // The default track (a1, E-AC-3) copied: the client plays it.
        Assert.Equal("a1", c.Asked.AudioTrack);
        Assert.Equal("copy", c.Asked.Transcode.AudioCodec);

        // A client that doesn't: AAC in two channels.
        var aac = HandOver.Choose(title, null, States(("a", new HandOver.PeerState(true, false, 4000))));
        Assert.Equal("aac", aac.Asked!.Transcode!.AudioCodec);
        Assert.Equal(2, aac.Asked.Transcode.AudioChannels);
    }

    [Fact]
    public void ARefusedHlsDeliveryStepsDownToTheBaseline()
    {
        var remux = new PlayRequest { Title = "movie/tmdb/1", Delivery = "remux", MediaType = "video/mp2t" };
        var transcode = HandOver.Lower(remux)!;
        Assert.Equal("transcode", transcode.Delivery);
        Assert.Equal(string.Empty, transcode.MediaType);
        var baseline = HandOver.Lower(transcode with { Transcode = transcode.Transcode! with { MaxBitrateKbps = 3000 } })!;
        Assert.Equal(string.Empty, baseline.Delivery);
        Assert.Null(baseline.Transcode);
        Assert.Equal(3000, baseline.MaxBitrateKbps);
        Assert.Null(HandOver.Lower(baseline));
    }

    [Fact]
    public void AClientsAudioCodecsFromItsDirectPlayProfiles()
    {
        var profile = new DeviceProfile
        {
            DirectPlayProfiles =
            [
                new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mp4", AudioCodec = "aac, mp3,ac3" },
                new DirectPlayProfile { Type = DlnaProfileType.Audio, Container = "flac", AudioCodec = "flac" },
            ],
        };
        var plays = HandOver.AudioPlayedBy(profile);
        Assert.True(plays("aac"));
        Assert.True(plays("ac-3"));
        Assert.False(plays("flac"));
        Assert.False(HandOver.AudioPlayedBy(null)("aac"));
    }

    [Fact]
    public void APeersStateFromItsLimitsOurPlaysAndWhatWasRemembered()
    {
        // Limits not known: free.
        Assert.True(RemotePlays.State("a", null, 5, false, false).Free);
        var one = new Limits { Streams = 1, Transcodes = 0, BitrateKbps = 6000 };
        Assert.True(RemotePlays.State("a", one, 0, false, false).Free);
        Assert.False(RemotePlays.State("a", one, 1, false, false).Free);
        Assert.False(RemotePlays.State("a", null, 0, true, false).Free);
        Assert.Equal(6000, RemotePlays.State("a", one, 0, false, false).CapKbps);
        Assert.True(RemotePlays.State("a", one, 0, false, true).RefusedFile);
    }

    // ---- The token ----

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    }

    private static (PlayTokens Tokens, Clock Clock) Tokens()
    {
        var clock = new Clock();
        return (new PlayTokens(() => clock.Now), clock);
    }

    [Fact]
    public void ATokenIs256RandomBitsItsHandleAGuid()
    {
        var (tokens, _) = Tokens();
        var a = tokens.Mint(Guid.NewGuid(), Guid.NewGuid(), "p", "movie/tmdb/1", C("x"), Way.File);
        var b = tokens.Mint(Guid.NewGuid(), Guid.NewGuid(), "p", "movie/tmdb/1", C("x"), Way.File);
        Assert.Equal(43, a.Secret.Length);
        Assert.Equal(32, Convert.FromBase64String(a.Secret.Replace('-', '+').Replace('_', '/') + "=").Length);
        Assert.NotEqual(a.Secret, b.Secret);
        Assert.True(Guid.TryParseExact(a.Handle, "N", out _));
        Assert.Same(a, tokens.Find(a.Secret));
    }

    [Fact]
    public void OnlyTheExactSecretOpensItNeverTheItemOrHandle()
    {
        var (tokens, _) = Tokens();
        var item = Guid.NewGuid();
        var t = tokens.Mint(Guid.NewGuid(), item, "p", "k", C("x"), Way.File);
        Assert.Null(tokens.Find(item.ToString("N")));
        Assert.Null(tokens.Find(t.Handle));
        Assert.Null(tokens.Find(t.Secret[..42] + (t.Secret[42] == 'A' ? 'B' : 'A')));
        Assert.Null(tokens.Find(t.Secret + "A"));
        Assert.Null(tokens.Find(null));
    }

    [Fact]
    public void ATokenUnreadLapsesOnceReadItStays()
    {
        var (tokens, clock) = Tokens();
        var unread = tokens.Mint(Guid.NewGuid(), Guid.NewGuid(), "p", "k", C("x"), Way.File);
        var read = tokens.Mint(Guid.NewGuid(), Guid.NewGuid(), "p", "k", C("y"), Way.File);
        read.FirstRead = clock.Now;
        clock.Now += PlayTokens.Lapse - TimeSpan.FromSeconds(1);
        Assert.NotNull(tokens.Find(unread.Secret));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Null(tokens.Find(unread.Secret));
        Assert.Null(tokens.OfHandle(unread.Handle));
        Assert.Same(read, tokens.Find(read.Secret));
        Assert.Single(tokens.All);
    }

    [Fact]
    public void AHandleIsTheUsersAndTheItemsOnly()
    {
        var (tokens, _) = Tokens();
        var user = Guid.NewGuid();
        var item = Guid.NewGuid();
        var t = tokens.Mint(user, item, "p", "k", C("x"), Way.File);
        Assert.Same(t, tokens.OfHandle(t.Handle, user, item));
        Assert.Same(t, tokens.OfHandle(t.Handle.ToUpperInvariant(), user, item));
        Assert.Null(tokens.OfHandle(t.Handle, Guid.NewGuid(), item));
        Assert.Null(tokens.OfHandle(t.Handle, user, Guid.NewGuid()));
        Assert.Null(tokens.OfHandle(item.ToString("N"), user, item));
    }

    [Fact]
    public void ADroppedTokenOpensNothing()
    {
        var (tokens, _) = Tokens();
        var t = tokens.Mint(Guid.NewGuid(), Guid.NewGuid(), "p", "k", C("x"), Way.File);
        tokens.Drop(t);
        Assert.Null(tokens.Find(t.Secret));
        Assert.Null(tokens.OfHandle(t.Handle));
    }

    [Fact]
    public void PlaysHoldAStreamWhileWaitingForTheirFirstReadAndWhileReading()
    {
        var (tokens, clock) = Tokens();
        var waiting = tokens.Mint(Guid.NewGuid(), Guid.NewGuid(), "p", "k", C("x"), Way.File);
        Assert.Equal(1, tokens.Holding("p"));
        Assert.Equal(0, tokens.Holding("q"));

        // Read, then its playback ended (a stop): no stream held.
        waiting.FirstRead = clock.Now;
        Assert.Equal(0, tokens.Holding("p"));
        waiting.Playback = new RemotePlayback { Id = "r1", Peer = "p" };
        Assert.Equal(1, tokens.Holding("p"));

        // Lapsed: none.
        var lapsed = tokens.Mint(Guid.NewGuid(), Guid.NewGuid(), "p", "k", C("x"), Way.File);
        clock.Now += PlayTokens.Lapse + TimeSpan.FromSeconds(1);
        _ = lapsed;
        Assert.Equal(1, tokens.Holding("p"));
    }

    // ---- A title that is a span of its copy ----

    [Fact]
    public void AnEpisodesSpanInItsCopy()
    {
        Assert.Equal((0L, 60_000L), StandInStreams.Span(C("x")));
        Assert.False(StandInStreams.Spanned(C("x")));
        Assert.Equal((20_000L, 45_000L), StandInStreams.Span(C("x", start: 20_000, end: 45_000)));
        Assert.Equal(25_000, StandInStreams.DurationMs(C("x", start: 20_000, end: 45_000)));

        // The last episode: to the copy's end.
        Assert.Equal((30_000L, 60_000L), StandInStreams.Span(C("x", start: 30_000)));
        Assert.Equal((0L, 30_000L), StandInStreams.Span(C("x", end: 30_000)));
        Assert.True(StandInStreams.Spanned(C("x", end: 30_000)));
    }

    [Fact]
    public void ASpansChaptersAndMarkersFromItsStart()
    {
        var c = C("x", start: 20_000, end: 45_000) with
        {
            Chapters = [new CopyChapter(0, "One"), new CopyChapter(15_000, "Two"), new CopyChapter(30_000, "Three"), new CopyChapter(50_000, "Four")],
            Segments = [new CopySegment("intro", 18_000, 25_000), new CopySegment("outro", 40_000, 50_000), new CopySegment("recap", 0, 5_000)],
        };
        var chapters = StandInStreams.Chapters(c, n => "Chapter " + n);
        Assert.Equal([0L, 10_000L * TimeSpan.TicksPerMillisecond], chapters.Select(x => x.StartPositionTicks));
        Assert.Equal(["Two", "Three"], chapters.Select(x => x.Name));
        var markers = StandInStreams.Segments(c);
        Assert.Equal([("intro", 0L, 5_000L), ("outro", 20_000L, 25_000L)], markers.Select(m => (m.Kind, m.StartMs, m.EndMs)));

        // The whole copy: as it is.
        Assert.Equal(4, StandInStreams.Chapters(c with { StartMs = null, EndMs = null }, n => "Chapter " + n).Count);
    }

    [Fact]
    public void ASpansScriptForJellyfinsFfmpegAndPositionsInTheCopy()
    {
        Assert.Equal("ffconcat version 1.0\nfile 'f'\ninpoint 20.000\noutpoint 45.500\n", RemotePlays.SpanScript(C("x", start: 20_000, end: 45_500)));
        Assert.Equal("ffconcat version 1.0\nfile 'f'\ninpoint 30.000\n", RemotePlays.SpanScript(C("x", start: 30_000)));

        var (tokens, _) = Tokens();
        var spanned = tokens.Mint(Guid.NewGuid(), Guid.NewGuid(), "p", "k", C("x", start: 20_000, end: 45_000), Way.File);
        Assert.Equal(20_000, spanned.StartMs);
        Assert.Equal(27_000, RemotePlays.InCopy(spanned, 7_000));
        Assert.EndsWith("/" + RemotePlays.SpanName, RemotePlays.FilePath(spanned), StringComparison.Ordinal);
        var whole = tokens.Mint(Guid.NewGuid(), Guid.NewGuid(), "p", "k", C("x"), Way.File);
        Assert.Equal(7_000, RemotePlays.InCopy(whole, 7_000));
        Assert.Equal("/Msfp/File/" + whole.Secret, RemotePlays.FilePath(whole));

        // A span's play starts at the episode's start.
        Assert.Equal(20_000, HandOver.HlsAsk("k", C("x", start: 20_000, end: 45_000), 0, true, string.Empty, _ => false).StartMs);
    }

    [Fact]
    public void SubtitlesServedApartAtTheirTokensRoute()
    {
        var (tokens, _) = Tokens();
        var t = tokens.Mint(Guid.NewGuid(), Guid.NewGuid(), "p", "k", C("x"), Way.File);
        Assert.Equal("/Msfp/Subtitle/" + t.Secret + "/s3.vtt", RemotePlays.SubtitlePath(t, new CopySubtitle("s3", "subrip") { External = true }));
        Assert.Equal("/Msfp/Subtitle/" + t.Secret + "/s4.sup", RemotePlays.SubtitlePath(t, new CopySubtitle("s4", "pgs") { External = true }));
    }
}
