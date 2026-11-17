using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Msfp.Protocol;

/// <summary>
/// Where the plugin may connect for a peer (MSFP section 3.2). Every address
/// it connects to comes from a peer (a code, a pair request), and a hostile
/// peer could name one inside this machine or its network.
/// </summary>
public static class Destinations
{
    /// <summary>
    /// Whether an IP address may be connected to: loopback only when the
    /// operator allows it; never link-local (a cloud's metadata service),
    /// unspecified, multicast, reserved or broadcast. Private ranges pass.
    /// An IPv4-mapped IPv6 address is judged as the IPv4 address it carries.
    /// </summary>
    public static bool IsAllowed(IPAddress ip, bool allowLoopback)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                0 => false, // 0.0.0.0/8, unspecified
                127 => allowLoopback,
                169 when b[1] == 254 => false, // link-local
                >= 224 => false, // multicast 224/4, reserved 240/4, broadcast
                _ => true,
            };
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
            {
                return false;
            }

            if (IPAddress.IsLoopback(ip))
            {
                return allowLoopback;
            }

            var b = ip.GetAddressBytes();

            // Hardening beyond section 3.2's list: the NAT64 prefix
            // 64:ff9b::/96 reaches the IPv4 address it carries, so it is
            // judged as that address (169.254.169.254 stays out of reach).
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
            {
                return IsAllowed(new IPAddress(b.AsSpan(12, 4)), allowLoopback);
            }

            if (b[0] == 0xff || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80))
            {
                return false; // multicast ff00::/8, link-local fe80::/10
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Connects to a host for a peer: the name looked up once, every address
    /// of the answer checked, and the connection made to an address that
    /// was checked, never to a second lookup's. A refused address fails like
    /// an unreachable peer.
    /// </summary>
    public static async Task<Socket> ConnectAsync(string host, int port, bool allowLoopback, CancellationToken ct)
    {
        var bare = host.Trim('[', ']');
        var addresses = IPAddress.TryParse(bare, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(bare, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(a => !IsAllowed(a, allowLoopback)))
        {
            throw new HttpRequestException("MSFP: the peer's address points where federation doesn't connect (section 3.2)");
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
                return socket;
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
            catch
            {
                // Cancelled or timed out: the socket goes too.
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("MSFP: the peer couldn't be reached", last);
    }

    /// <summary>A ConnectCallback for SocketsHttpHandler that checks as above.</summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Callback(Func<bool> allowLoopback) =>
        async (context, ct) =>
        {
            var socket = await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, allowLoopback(), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        };
}
