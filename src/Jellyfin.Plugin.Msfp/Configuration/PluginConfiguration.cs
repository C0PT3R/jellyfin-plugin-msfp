using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Msfp.Configuration;

/// <summary>What the operator sets in the plugin's page.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// This server's own address as peers reach it, https://host[:port]
    /// (MSFP core section 5.2). Empty: federation is off. Another address
    /// than before is a move, which the engine announces to the peers.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The name peers show for this server. Empty: the address's host.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>
    /// The pins (MSFP core section 3.2) of the certificates this server's
    /// address serves, one per line. Empty: the engine reads the certificate
    /// served at this server's own address and names that one's pin.
    /// </summary>
    public string CertificatePins { get; set; } = string.Empty;

    /// <summary>
    /// Whether the plugin may connect, for a peer, to this machine itself
    /// (loopback): only to pair with a server on the same machine. Link-local
    /// (a cloud's metadata service), unspecified, multicast and reserved
    /// addresses stay refused whatever this says (MSFP core section 3.3).
    /// </summary>
    public bool AllowLoopback { get; set; }

    /// <summary>
    /// Serves the discovery document at /.well-known/msfp (MSFP core section
    /// 11). Off by default: a server that serves it tells anyone it
    /// federates.
    /// </summary>
    public bool Discovery { get; set; }
}
