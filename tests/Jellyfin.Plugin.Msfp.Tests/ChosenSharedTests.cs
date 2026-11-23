using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Identification;
using Msfp;
using Msfp.Stores;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

// A title waiting for the owner ("to check") is shared as soon as the owner
// chooses its TMDB title, not after the libraries settle or a restart.
public class ChosenSharedTests
{
    // The films as the plugin lists them: those an identity takes.
    private sealed class Source(Identities identities) : ICatalogSource
    {
        public IReadOnlyList<Library> Libraries() => [new() { Id = "aaaa", Name = "Films" }];

        public IEnumerable<CatalogEntry> Entries() =>
            identities.Snapshot().Values.Where(t => t.Taken is not null)
                .Select(t => new CatalogEntry("aaaa", "movie/tmdb/" + t.Taken!.Value.ToString(CultureInfo.InvariantCulture), "movie"));

        public ValueTask<byte[]?> ImageAsync(Guid item, CancellationToken ct) => ValueTask.FromResult<byte[]?>(null);
    }

    // A series Jellyfin places on TheTVDB only, keyed as the plugin keys it.
    private sealed class TvdbSeries(Identities identities, Guid id) : ICatalogSource
    {
        private readonly MediaBrowser.Controller.Entities.TV.Series _series = new() { Id = id, Name = "S", ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "81189" } };

        public IReadOnlyList<Library> Libraries() => [new() { Id = "aaaa", Name = "Series" }];

        public IEnumerable<CatalogEntry> Entries() =>
            JellyfinCatalogSource.KeyOf(_series, identities.Of(id), _ => null) is { } k ? [new CatalogEntry("aaaa", k.Key.ToString(), "series")] : [];

        public ValueTask<byte[]?> ImageAsync(Guid item, CancellationToken ct) => ValueTask.FromResult<byte[]?>(null);
    }

    private static async Task<List<string>> Keys(HostCatalog catalog) =>
        (await catalog.TitlesAsync(["aaaa"], "", 100, default)).Titles.Select(t => t.Key).ToList();

    [Fact]
    public async Task TheOwnersChoiceIsInTheNextReadingAtOnce()
    {
        var identities = new Identities(null);
        var catalog = new HostCatalog(new Source(identities));
        using var engine = await MsfpEngine.OpenAsync(new MsfpConfig
        {
            Address = "https://a.example.org",
            Store = new MemoryStore(),
            Catalog = catalog,
        });
        var now = new List<bool>();
        identities.Changed += (_, e) => now.Add(CatalogWatcher.ShareNow(e, catalog, engine));

        var sure = Guid.NewGuid();
        var doubt = Guid.NewGuid();
        identities.Put(sure, new TitleIdentity { Kind = "movie", Tmdb = "603", Sure = true });
        identities.Put(doubt, new TitleIdentity { Kind = "movie", Tmdb = "78", Sure = false });
        // A pass's matches wait for the libraries to settle: not shared now.
        Assert.Equal([false, false], now);
        catalog.Changed();
        Assert.Equal(["movie/tmdb/603"], await Keys(catalog));

        // The owner confirms the match on the page.
        var chosen = identities.Of(doubt)!.Copy();
        chosen.Chosen = "78";
        identities.Put(doubt, chosen, chosen: true);
        Assert.Equal([false, false, true], now);
        Assert.Equal(["movie/tmdb/603", "movie/tmdb/78"], await Keys(catalog));
    }

    [Fact]
    public async Task ATvdbSeriesWaitingIsUnsharedThenSharedUnderTmdbAtOnce()
    {
        var identities = new Identities(null);
        var id = Guid.NewGuid();
        var catalog = new HostCatalog(new TvdbSeries(identities, id));
        using var engine = await MsfpEngine.OpenAsync(new MsfpConfig
        {
            Address = "https://a.example.org",
            Store = new MemoryStore(),
            Catalog = catalog,
        });
        var now = new List<bool>();
        identities.Changed += (_, e) => now.Add(CatalogWatcher.ShareNow(e, catalog, engine));

        // Not looked at yet, then a match waiting for the owner: not shared, not even under tvdb.
        Assert.Empty(await Keys(catalog));
        identities.Put(id, new TitleIdentity { Kind = "series", Tmdb = "1396" });
        catalog.Changed();
        Assert.Empty(await Keys(catalog));

        // The owner confirms it: shared at once, under its TMDB key.
        var chosen = identities.Of(id)!.Copy();
        chosen.Chosen = "1396";
        identities.Put(id, chosen, chosen: true);
        Assert.Equal([false, true], now);
        Assert.Equal(["series/tmdb/1396"], await Keys(catalog));
    }

    [Fact]
    public async Task ATvdbSeriesTmdbDoesNotKnowStaysUnderTvdb()
    {
        var identities = new Identities(null);
        var id = Guid.NewGuid();
        var catalog = new HostCatalog(new TvdbSeries(identities, id));
        identities.Put(id, new TitleIdentity { Kind = "series" });
        catalog.Changed();
        Assert.Equal(["series/tvdb/81189"], await Keys(catalog));
    }

    [Fact]
    public async Task AMatchWaitsForTheLibrariesToSettle()
    {
        var identities = new Identities(null);
        var catalog = new HostCatalog(new Source(identities));
        Assert.Empty(await Keys(catalog));
        identities.Changed += (_, e) => Assert.False(CatalogWatcher.ShareNow(e, catalog, null));
        identities.Put(Guid.NewGuid(), new TitleIdentity { Kind = "movie", Tmdb = "603", Sure = true });
        // The catalog as read until the watcher's settled reading.
        Assert.Empty(await Keys(catalog));
    }
}
