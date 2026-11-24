using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using Microsoft.Extensions.Logging.Abstractions;
using Msfp;
using Msfp.Stores;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// After an update from 0.10.x, each paired server's change number is
// forgotten once (its wants kept), so its next reading is whole; never
// again after that.
public class ReadWholeOnceTests
{
    [Fact]
    public async Task ForgetsChangeNumbersOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "msfp-readwhole-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var store = JsonFileStore.Open(Path.Combine(dir, "msfp"));
            var ct = CancellationToken.None;
            await store.UpdateAsync(async tx =>
            {
                await tx.PutPeerAsync(new PeerRecord { Id = "pread", Name = "read", CatalogRead = new CatalogRead { Seq = 135382, Wants = ["films", "series"] } });
                await tx.PutPeerAsync(new PeerRecord { Id = "pnever", Name = "never read" });
            }, ct);

            Assert.Equal(1, await EngineHost.ReadWholeOnceAsync(dir, store, NullLogger.Instance, ct));
            var p = await store.ReadAsync(tx => tx.PeerAsync("pread"), ct);
            Assert.NotNull(p);
            Assert.Equal(0, p!.CatalogRead.Seq);
            Assert.Equal(["films", "series"], p.CatalogRead.Wants);

            // Done once: a later change number is kept.
            await store.UpdateAsync(async tx =>
            {
                var q = await tx.PeerAsync("pread");
                q!.CatalogRead = new CatalogRead { Seq = 7, Wants = q.CatalogRead.Wants };
                await tx.PutPeerAsync(q);
            }, ct);
            Assert.Equal(0, await EngineHost.ReadWholeOnceAsync(dir, store, NullLogger.Instance, ct));
            Assert.Equal(7, (await store.ReadAsync(tx => tx.PeerAsync("pread"), ct))!.CatalogRead.Seq);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
