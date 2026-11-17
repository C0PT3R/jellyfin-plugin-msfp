using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Linq;

namespace Msfp;

/// <summary>
/// A server's address, http(s)://host[:port] with no path (MSFP section
/// 5.2), and its host as request messages name it (section 6.2).
/// </summary>
public static class Address
{
    /// <summary>The longest address, in bytes.</summary>
    public const int MaxLength = 512;

    /// <summary>
    /// Whether an address is one section 5.2 accepts, and no other form:
    /// http(s) in lowercase, a host that is a name in lowercase, four
    /// decimal numbers, or a bracketed IPv6 address in RFC 5952's form; a
    /// port from 1 to 65535 without leading zeros that isn't the scheme's
    /// default; no path, not even "/".
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
        string authority;
        if (address.StartsWith("https://", StringComparison.Ordinal))
        {
            defaultPort = "443";
            authority = address["https://".Length..];
        }
        else if (address.StartsWith("http://", StringComparison.Ordinal))
        {
            defaultPort = "80";
            authority = address["http://".Length..];
        }
        else
        {
            return false;
        }

        // No path, query, fragment or user info, not even an empty one.
        if (authority.Length == 0 || authority.IndexOfAny(['/', '?', '#', '@', '\\']) >= 0)
        {
            return false;
        }

        // Split off the port: after the last ':' outside brackets.
        var close = authority.LastIndexOf(']');
        var colon = authority.LastIndexOf(':');
        var name = authority;
        if (colon > close)
        {
            var port = authority[(colon + 1)..];
            name = authority[..colon];
            if (port.Length == 0 || port.Length > 5 || port == defaultPort || port[0] == '0'
                || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p > 65535)
            {
                return false;
            }
        }

        if (!IsHost(name))
        {
            return false;
        }

        host = authority;
        return true;
    }

    /// <summary>A host: a name, an IPv4 address or a bracketed IPv6 one.</summary>
    private static bool IsHost(string name)
    {
        if (name.StartsWith('['))
        {
            if (!name.EndsWith(']') || name.Length < 3)
            {
                return false;
            }

            var inner = name[1..^1];

            // RFC 5952's text form: what .NET writes back is that form
            // (lowercase, the longest run of zeros compressed), and a zone
            // identifier is refused.
            return !inner.Contains('%', StringComparison.Ordinal)
                && IPAddress.TryParse(inner, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
                && ip.ToString() == inner;
        }

        if (name.Length == 0 || name.Length > 253)
        {
            return false;
        }

        var labels = name.Split('.');

        // A host whose last label is all digits is an IPv4 address, and
        // must be one.
        if (labels[^1].Length > 0 && labels[^1].All(char.IsAsciiDigit))
        {
            if (labels.Length != 4)
            {
                return false;
            }

            foreach (var label in labels)
            {
                if (label.Length is 0 or > 3 || !label.All(char.IsAsciiDigit) || (label.Length > 1 && label[0] == '0')
                    || int.Parse(label, CultureInfo.InvariantCulture) > 255)
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var c in label)
            {
                if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// An address as a server writes its own, or keeps one it stored before
    /// these rules (section 5.2, "Stored pairings"): scheme and host in
    /// lowercase, a trailing "/" and an empty query removed. The result may
    /// still be invalid.
    /// </summary>
    public static string Normalize(string address)
    {
        var a = address.Trim();
        if (a.EndsWith('?'))
        {
            a = a[..^1];
        }

        if (a.EndsWith('/'))
        {
            a = a[..^1];
        }

        var sep = a.IndexOf("://", StringComparison.Ordinal);
        if (sep > 0)
        {
            var end = a.IndexOf('/', sep + 3);
            var head = end < 0 ? a : a[..end];
            a = head.ToLowerInvariant() + (end < 0 ? string.Empty : a[end..]);
        }

        return a;
    }
}
