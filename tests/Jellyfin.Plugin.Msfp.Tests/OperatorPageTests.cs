using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Jellyfin.Plugin.Msfp.Api;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Configuration;
using Jellyfin.Plugin.Msfp.Federation;
using Msfp;
using Msfp.Protocol;
using Xunit;
using PlaybackRules = Msfp.Protocol.Playback;
using VideoTitle = Msfp.VideoTitle;

namespace Jellyfin.Plugin.Msfp.Tests;

// What the operator's page sets and shows: the settings and their defaults,
// a pairing's or a pool's limits as the page sends them, the libraries
// described by their titles, the last reading of a peer's titles.
public class OperatorPageTests
{
    [Fact]
    public void SettingsDefaultToNothingMoreShared()
    {
        var c = new PluginConfiguration();
        Assert.False(c.BurnSubtitles);
        Assert.Equal(2, c.TranscodesAtOnce);
        Assert.False(c.Discovery);
        Assert.False(c.AllowLoopback);
    }

    [Fact]
    public void SettingsSavedBeforeTheSwitchKeepItOff()
    {
        // Jellyfin keeps the settings as XML: a file written by 0.10 has no
        // BurnSubtitles, and reads as off; a saved switch reads back.
        var xml = new XmlSerializer(typeof(PluginConfiguration));
        const string Old = "<?xml version=\"1.0\"?><PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" +
            "<Address>https://media.example.org</Address><TranscodesAtOnce>3</TranscodesAtOnce></PluginConfiguration>";
        var old = (PluginConfiguration)xml.Deserialize(new StringReader(Old))!;
        Assert.False(old.BurnSubtitles);
        Assert.Equal(3, old.TranscodesAtOnce);

        var on = new StringWriter();
        xml.Serialize(on, new PluginConfiguration { BurnSubtitles = true });
        Assert.True(((PluginConfiguration)xml.Deserialize(new StringReader(on.ToString()))!).BurnSubtitles);
    }

    [Fact]
    public void LimitsFromThePageReachTheEngine()
    {
        // Downloads and scans as the page sends them (a 32 MiB scan cap).
        var r = new LimitsRequest { Streams = 3, Transcodes = 1, BitrateKbps = 8000, Downloads = 2, DownloadsPerDay = 5, DownloadKbps = 20_000, ScansPerDay = 100, ScanBytes = 32 << 20 };
        var l = r.ToLimits();
        Assert.Equal((3, 1, 8000, 2, 5, 20_000, 100, 32 << 20), (l.Streams, l.Transcodes, l.BitrateKbps, l.Downloads, l.DownloadsPerDay, l.DownloadKbps, l.ScansPerDay, l.ScanBytes));
        l.Check();

        // Nothing typed for downloads and scans: none of either (MSFP playback section 5).
        var none = new LimitsRequest { Streams = 2, Transcodes = 1 }.ToLimits();
        Assert.Equal((0, 0, 0, 0, 0), (none.Downloads, none.DownloadsPerDay, none.DownloadKbps, none.ScansPerDay, none.ScanBytes));
        none.Check();

        // The page's bounds are the engine's: 4 MiB to 1 GiB for a scan, 0 for the floor.
        new LimitsRequest { Streams = 1, ScanBytes = PlaybackRules.ScanBytesFloor }.ToLimits().Check();
        new LimitsRequest { Streams = 1, ScanBytes = PlaybackRules.ScanBytesTop, Downloads = PlaybackRules.MaxDownloads, DownloadsPerDay = PlaybackRules.MaxDownloadsPerDay, ScansPerDay = PlaybackRules.MaxScansPerDay, DownloadKbps = PlaybackRules.MaxLimitKbps }.ToLimits().Check();
        foreach (var bad in new[]
        {
            new LimitsRequest { Streams = 1, ScanBytes = (4 << 20) - 1 },
            new LimitsRequest { Streams = 1, ScanBytes = (1 << 30) + 1 },
            new LimitsRequest { Streams = 1, Downloads = 1001 },
            new LimitsRequest { Streams = 1, DownloadsPerDay = 10_001 },
            new LimitsRequest { Streams = 1, DownloadKbps = -1 },
            new LimitsRequest { Streams = 1, ScansPerDay = 1_000_001 },
        })
        {
            Assert.Equal(MsfpError.InvalidLimits, Assert.Throws<MsfpException>(() => bad.ToLimits().Check()).Code);
        }
    }

    [Fact]
    public void LibrariesDescribedByTheirTitles()
    {
        VideoTitle T(string key, string kind, params string[] libraries) => new() { Key = key, Kind = kind, Libraries = libraries.ToList() };
        var counts = HostCatalog.Count(
        [
            T("movie/tmdb/603", "movie", "films", "4k"),
            T("movie/local/q3Zp0Lw8q3Zp0Lw8q3Zp0L", "movie", "films"),
            T("series/tmdb/1399", "series", "series"),
            T("series/tvdb/81189", "series", "series"),
            T("series/local/AAAAAAAAAAAAAAAAAAAAAA", "series", "series"),
            T("series/tmdb/1399/s1/e2", "episode", "series"),
            T("movie/tmdb/603/x/Tr7aQ1Tr7aQ1Tr7aQ1Tr7a", "extra", "films"),
        ]);
        Assert.Equal(new LibraryCounts(2, 0, 1), counts["films"]);
        Assert.Equal(new LibraryCounts(1, 0, 0), counts["4k"]);
        Assert.Equal(new LibraryCounts(0, 3, 1), counts["series"]);
        Assert.Equal(3, counts.Count);
    }

    [Fact]
    public void LastReadingsKeepCodesOnly()
    {
        var reads = new LastReads();
        Assert.Null(reads.Of("p1"));
        reads.Note(new CatalogReadEnded("p1", "Peer", 12, false, null));
        Assert.Equal((12, ""), (reads.Of("p1")!.Titles, reads.Of("p1")!.Error));

        // A failure: its code, never its message (which may hold an address).
        reads.Note(new CatalogReadEnded("p1", "Peer", 0, true, new MsfpException(MsfpError.InvalidLimits, "at https://peer.example.org/x")));
        Assert.Equal("InvalidLimits", reads.Of("p1")!.Error);
        reads.Note(new CatalogReadEnded("p2", "Other", 0, false, new System.Net.Http.HttpRequestException("no route to 192.0.2.7")));
        Assert.Equal("Unreachable", reads.Of("p2")!.Error);
        Assert.Equal("Failed", LastReads.CodeOf(new InvalidOperationException("x")));
        Assert.Equal("Timeout", LastReads.CodeOf(new TaskCanceledExceptionStandIn()));

        // A pairing's end forgets it.
        reads.Note(new PeerEnded("p1", "Peer", true));
        Assert.Null(reads.Of("p1"));
        Assert.NotNull(reads.Of("p2"));
    }

    private sealed class TaskCanceledExceptionStandIn : OperationCanceledException;
}
