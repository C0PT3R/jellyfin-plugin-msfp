using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Identification;
using Jellyfin.Plugin.Msfp.Playback;
using Jellyfin.Plugin.Msfp.Viewing;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Msfp;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<Identifier>();
        // The catalog asks the identifier for its identities when it reads,
        // not when it's made: the identifier needs the engine, which needs
        // the catalog.
        serviceCollection.AddSingleton<ICatalogSource>(sp =>
            new JellyfinCatalogSource(sp.GetRequiredService<ILibraryManager>(), () => sp.GetRequiredService<Identifier>().Identities));
        serviceCollection.AddSingleton<HostCatalog>();
        serviceCollection.AddSingleton<JellyfinPlayback>();
        serviceCollection.AddSingleton<EngineHost>();
        serviceCollection.AddSingleton<WantsKeeper>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<EngineHost>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<WantsKeeper>());
        serviceCollection.AddHostedService<CatalogWatcher>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Identifier>());
        // The paired servers' titles: their channel (registered here, as
        // Jellyfin finds a plugin's channels among its services), and the
        // stand-ins' keeper.
        serviceCollection.AddSingleton<MediaBrowser.Controller.Channels.IChannel, PairedChannel>();
        serviceCollection.AddSingleton<StandIns>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<StandIns>());
        serviceCollection.AddSingleton<RemotePlays>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<RemotePlays>());
    }
}
