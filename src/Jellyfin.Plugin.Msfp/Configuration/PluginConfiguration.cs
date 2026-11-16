using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Msfp.Configuration;

/// <summary>What the operator sets in the plugin's page.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// This server's own address as peers reach it, for example
    /// https://media.example.org (MSFP section 5.2). Empty: federation is off.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The name peers show for this server. Empty: the address's host.</summary>
    public string ServerName { get; set; } = string.Empty;
}
