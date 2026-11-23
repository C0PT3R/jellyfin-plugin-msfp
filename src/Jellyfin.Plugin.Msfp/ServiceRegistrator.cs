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
        // The engine's opaque key parts (local titles, extras) likewise: the
        // engine needs the catalog.
        serviceCollection.AddSingleton<CopyKeeper>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<CopyKeeper>());
        serviceCollection.AddSingleton<ICatalogSource>(sp =>
            new JellyfinCatalogSource(sp.GetRequiredService<ILibraryManager>(), () => sp.GetRequiredService<Identifier>().Identities,
                sp.GetRequiredService<CopyKeeper>(), id => sp.GetRequiredService<EngineHost>().Engine?.OpaqueKeyPart(id)));
        serviceCollection.AddSingleton<HostCatalog>();
        // Peers' remuxes and transcodes, by Jellyfin's own transcoder: its
        // watchdog runs as a service.
        serviceCollection.AddSingleton<Deliveries>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<Deliveries>());
        serviceCollection.AddSingleton(sp => new JellyfinPlayback(
            sp.GetRequiredService<ILibraryManager>(), sp.GetRequiredService<MediaBrowser.Controller.MediaEncoding.ISubtitleEncoder>(),
            sp.GetRequiredService<CopyKeeper>(), sp.GetRequiredService<Deliveries>(), sp.GetRequiredService<MediaBrowser.Controller.Session.ISessionManager>(),
            sp.GetRequiredService<MediaBrowser.Controller.Trickplay.ITrickplayManager>(), sp.GetRequiredService<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JellyfinPlayback>>()));
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
        serviceCollection.AddSingleton<PeerPreviews>();
        serviceCollection.AddSingleton<StandIns>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<StandIns>());
        // Jellyfin takes its segment providers from here: the stand-ins' skip markers.
        serviceCollection.AddSingleton<MediaBrowser.Controller.MediaSegments.IMediaSegmentProvider, PairedSegments>();
        serviceCollection.AddSingleton<RemotePlays>();
        serviceCollection.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Mvc.MvcOptions>, BusyFilterSetup>();
        serviceCollection.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Mvc.MvcOptions>, HandOverFilterSetup>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<RemotePlays>());
    }
}
