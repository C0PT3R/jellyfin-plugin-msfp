using Jellyfin.Plugin.Msfp.Catalog;
using Jellyfin.Plugin.Msfp.Federation;
using Jellyfin.Plugin.Msfp.Identification;
using Jellyfin.Plugin.Msfp.Playback;
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
        serviceCollection.AddSingleton<ICatalogSource>(sp =>
        {
            var identifier = sp.GetRequiredService<Identifier>();
            return new JellyfinCatalogSource(sp.GetRequiredService<ILibraryManager>(), () => identifier.Identities);
        });
        serviceCollection.AddSingleton<HostCatalog>();
        serviceCollection.AddSingleton<JellyfinPlayback>();
        serviceCollection.AddSingleton<EngineHost>();
        serviceCollection.AddSingleton<WantsKeeper>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<EngineHost>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<WantsKeeper>());
        serviceCollection.AddHostedService<CatalogWatcher>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Identifier>());
    }
}
