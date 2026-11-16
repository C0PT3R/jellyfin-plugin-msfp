using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Viewing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Msfp;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp =>
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("the MSFP plugin isn't loaded");
            Directory.CreateDirectory(plugin.DataFolderPath);
            return new OwnCatalog(new JellyfinCatalogSource(sp.GetRequiredService<ILibraryManager>()), TimeProvider.System, Path.Combine(plugin.DataFolderPath, "catalog.json"));
        });
        serviceCollection.AddSingleton(sp =>
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("the MSFP plugin isn't loaded");
            return new CatalogReader(sp.GetRequiredService<FederationService>(), new PeerCatalogStore(Path.Combine(plugin.DataFolderPath, "peer-catalogs")), TimeProvider.System);
        });
        serviceCollection.AddSingleton(sp =>
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("the MSFP plugin isn't loaded");
            Directory.CreateDirectory(plugin.DataFolderPath);
            var key = KeyFile.LoadOrCreate(Path.Combine(plugin.DataFolderPath, "server-key.seed"));
            var store = new PeerStore(Path.Combine(plugin.DataFolderPath, "peers.json"));
            return new FederationService(
                key,
                store,
                TimeProvider.System,
                () => new ServerSettings(
                    plugin.Configuration.Address ?? string.Empty,
                    plugin.Configuration.ServerName ?? string.Empty,
                    (plugin.Configuration.CertificatePins ?? string.Empty).Split((char[])['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries)),
                new DefaultTransport(),
                new TlsCertificateReader(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Jellyfin.Plugin.Msfp"),
                sp.GetRequiredService<OwnCatalog>());
        });
        serviceCollection.AddHostedService<FederationStartup>();
        serviceCollection.AddHostedService<CatalogUpdater>();
        serviceCollection.AddHostedService<CatalogSync>();
    }
}

/// <summary>
/// Makes the federation service when Jellyfin starts, so its nonce memory
/// begins then, and not at the first request.
/// </summary>
public sealed class FederationStartup : IHostedService
{
    private readonly IServiceProvider _services;

    public FederationStartup(IServiceProvider services)
    {
        _services = services;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _services.GetRequiredService<FederationService>();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Reads paired peers' catalogs when they are due: checked every minute.</summary>
public sealed class CatalogSync : BackgroundService
{
    private readonly CatalogReader _reader;
    private readonly ILogger<CatalogSync> _log;

    public CatalogSync(CatalogReader reader, ILogger<CatalogSync> log)
    {
        _reader = reader;
        _log = log;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await _reader.ReadDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError(e, "MSFP: reading peers' catalogs failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
