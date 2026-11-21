using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Identification;
using Msfp;
using Msfp.Stores;
using Xunit;

namespace Jellyfin.Plugin.Msfp.Tests;

public class OfferedTests
{
    private sealed class Source(IReadOnlyList<Library> libraries) : ICatalogSource
    {
        public IReadOnlyList<Library> Libraries() => libraries;

        public IEnumerable<CatalogEntry> Entries() => [];
    }

    // A library offered only to a group gets identified: without it, the
    // group's members read none of its titles.
    [Fact]
    public async Task LibrariesOfferedToAGroupCount()
    {
        var films = Guid.NewGuid();
        var kids = Guid.NewGuid();
        var catalog = new HostCatalog(new Source(
        [
            new() { Id = films.ToString("N"), Name = "Films", Kind = "movies" },
            new() { Id = kids.ToString("N"), Name = "Kids", Kind = "movies" },
        ]));
        using var engine = await MsfpEngine.OpenAsync(new MsfpConfig
        {
            Address = "https://a.example.org",
            Store = new MemoryStore(),
            Catalog = catalog,
        });
        Assert.Empty(await Identifier.OfferedAsync(engine, default));

        var g = await engine.CreateGroupAsync("Friends");
        await engine.SetGroupOffersAsync(g.Id, [films.ToString("N")]);
        Assert.Equal([films], await Identifier.OfferedAsync(engine, default));
    }
}
