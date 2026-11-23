using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Federation;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Msfp.Catalog;

/// <summary>
/// Tells the engine when this server's titles change: 30 seconds after
/// Jellyfin's library events settle, and every 10 minutes whatever happens
/// (a change Jellyfin raised no event for). The engine reads the catalog
/// again then, and keeps what changed in its log; and whenever the copy
/// keeper's pass changed some title's copies. A library gone from
/// Jellyfin is told apart: peers' playbacks from it end.
/// </summary>
public sealed class CatalogWatcher : IHostedService, IDisposable
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _library;
    private readonly HostCatalog _catalog;
    private readonly EngineHost _host;
    private readonly Identification.Identifier _identifier;
    private readonly CopyKeeper _copies;
    private readonly ILogger<CatalogWatcher> _log;
    private readonly object _lock = new();
    private HashSet<string>? _libraries;
    private Timer? _settle;
    private Timer? _every;
    private int _running;

    public CatalogWatcher(ILibraryManager library, HostCatalog catalog, EngineHost host, Identification.Identifier identifier, CopyKeeper copies, ILogger<CatalogWatcher> log)
    {
        _copies = copies;
        _library = library;
        _catalog = catalog;
        _host = host;
        _identifier = identifier;
        _log = log;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += Changed;
        _library.ItemUpdated += Changed;
        _library.ItemRemoved += Changed;
        // An identity changed (a match, the owner's choice): the titles may have.
        _identifier.Identities.Changed += IdentitiesChanged;
        // The copies changed (a pass, a file read): read again now.
        _copies.Changed += CopiesChanged;
        _every = new Timer(_ => Run(), null, TimeSpan.FromSeconds(15), Every);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= Changed;
        _library.ItemUpdated -= Changed;
        _library.ItemRemoved -= Changed;
        _identifier.Identities.Changed -= IdentitiesChanged;
        _copies.Changed -= CopiesChanged;
        _every?.Change(Timeout.Infinite, Timeout.Infinite);
        _settle?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _every?.Dispose();
        _settle?.Dispose();
    }

    private void Changed(object? sender, ItemChangeEventArgs e)
    {
        // The paired servers' titles aren't this server's.
        if (Viewing.PairedSources.IsStandIn(e.Item))
        {
            return;
        }

        // Files changed: identified again where needed, described again, then read.
        _identifier.Poke();
        _copies.Wake();
        Soon();
    }

    private void CopiesChanged(object? sender, EventArgs e) => Task.Run(Run);

    private void IdentitiesChanged(object? sender, EventArgs e) => Soon();

    private void Soon()
    {
        lock (_lock)
        {
            // Each event pushes the reading back: a scan is read once, after it.
            _settle ??= new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
            _settle.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Run()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            _catalog.Changed();
            var libraries = _catalog.Read().Libraries.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            var gone = _libraries?.Except(libraries).ToList() ?? [];
            _libraries = libraries;
            if (_host.Engine is not { } engine)
            {
                return;
            }

            foreach (var id in gone)
            {
                engine.LibraryRemovedAsync(id).GetAwaiter().GetResult();
            }

            engine.CatalogChanged();
        }
        catch (Exception e)
        {
            _log.LogError(e, "MSFP: reading the catalog failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
