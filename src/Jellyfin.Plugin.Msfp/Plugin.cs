using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Msfp.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Msfp;

/// <summary>The plugin as Jellyfin sees it: name, ID and configuration page.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin's ID. It never changes.</summary>
    public const string PluginId = "505184bf-b92c-487f-a406-bf6b3570fb7f";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>The plugin as Jellyfin loaded it.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "MSFP Federation";

    /// <inheritdoc />
    public override string Description => "Federates this server with other servers over MSFP, the Media Server Federation Protocol.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginId);

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            },
        ];
    }
}
