using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Msfp.Protocol;

/// <summary>
/// A server's address, http(s)://host[:port] with no path (MSFP section
/// 5.2), and its host as request messages name it (section 6.2).
/// </summary>
public static partial class Address
{
    /// <summary>The longest address, in bytes.</summary>
    public const int MaxLength = 512;

    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*|\[[0-9a-f:.]+\])(:[0-9]{1,5})?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    /// <summary>
    /// Whether an address is one section 5.2 accepts. Stricter than the
    /// section in three ways, each in SPEC-GAPS.md: the scheme in lowercase
    /// only, a port from 1 to 65535 without leading zeros, and a bracketed
    /// host that is a real IPv6 address.
    /// </summary>
    public static bool IsValid(string? address)
    {
        return TryGetHost(address, out _);
    }

    /// <summary>
    /// The host of a valid address, with its port when it has one: the
    /// &lt;host&gt; line of a request message. False for an invalid address.
    /// </summary>
    public static bool TryGetHost(string? address, out string host)
    {
        host = string.Empty;
        if (address == null || address.Length > MaxLength)
        {
            return false;
        }

        // Every valid address is ASCII, so its length in bytes is its
        // length in characters.
        foreach (var c in address)
        {
            if (c > 0x7e || c < 0x21)
            {
                return false;
            }
        }

        string defaultPort;
        string rest;
        if (address.StartsWith("https://", StringComparison.Ordinal))
        {
            defaultPort = "443";
            rest = address["https://".Length..];
        }
        else if (address.StartsWith("http://", StringComparison.Ordinal))
        {
            defaultPort = "80";
            rest = address["http://".Length..];
        }
        else
        {
            return false;
        }

        // No query or fragment, not even an empty one; no user info.
        if (rest.Contains('?', StringComparison.Ordinal) || rest.Contains('#', StringComparison.Ordinal) || rest.Contains('@', StringComparison.Ordinal))
        {
            return false;
        }

        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? string.Empty : rest[slash..];
        if (path.Length != 0 && path != "/")
        {
            return false;
        }

        if (!HostPattern().IsMatch(authority))
        {
            return false;
        }

        // Split off the port: after the last ':' outside brackets.
        var close = authority.LastIndexOf(']');
        var colon = authority.LastIndexOf(':');
        string name = authority;
        if (colon > close)
        {
            var port = authority[(colon + 1)..];
            name = authority[..colon];
            if (port == defaultPort || port[0] == '0'
                || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p > 65535)
            {
                return false;
            }
        }

        if (name.StartsWith('['))
        {
            if (!IPAddress.TryParse(name[1..^1], out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }
        }

        host = authority;
        return true;
    }
}
