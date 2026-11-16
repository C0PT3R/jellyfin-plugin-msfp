using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Msfp.Protocol;

namespace Jellyfin.Plugin.Msfp.Federation;

/// <summary>
/// How the plugin connects to peers (MSFP section 3.1): pair and hello
/// accept any certificate; every other call, and media, a trusted one or a
/// leaf the peer named.
/// </summary>
public interface IPeerTransport
{
    /// <summary>For pair and hello: any certificate.</summary>
    HttpMessageHandler Any { get; }

    /// <summary>
    /// For every other call and media to one peer: trusted, or named by one
    /// of these pins (the peer's latest).
    /// </summary>
    HttpMessageHandler TrustedOrNamed(string peerKey, IReadOnlyList<string> pins);
}

/// <summary>
/// The plugin's transport: real HTTP, redirects never followed. One
/// handler per peer for checked calls, made again when the peer's pins
/// change, so no connection opened under old pins is used after them.
/// </summary>
public sealed class DefaultTransport : IPeerTransport, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Pins, SocketsHttpHandler Handler)> _checked = new(StringComparer.Ordinal);

    public DefaultTransport()
    {
        Any = Make(static (_, _, _, _) => true);
    }

    /// <inheritdoc />
    public HttpMessageHandler Any { get; }

    /// <inheritdoc />
    public HttpMessageHandler TrustedOrNamed(string peerKey, IReadOnlyList<string> pins)
    {
        var snapshot = pins.ToArray();
        var id = string.Join(',', snapshot);
        lock (_lock)
        {
            if (_checked.TryGetValue(peerKey, out var known) && known.Pins == id)
            {
                return known.Handler;
            }

            var handler = Make((_, leaf, _, errors) => Pins.TrustedOrNamed(leaf, errors, snapshot));
            if (known.Handler != null)
            {
                known.Handler.Dispose();
            }

            _checked[peerKey] = (id, handler);
            return handler;
        }
    }

    public void Dispose()
    {
        Any.Dispose();
        lock (_lock)
        {
            foreach (var (_, h) in _checked.Values)
            {
                h.Dispose();
            }

            _checked.Clear();
        }
    }

    private static SocketsHttpHandler Make(RemoteCertificateValidationCallback check) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = check },
    };
}

/// <summary>Reads the leaf certificate served at an https address.</summary>
public interface ICertificateReader
{
    /// <summary>The DER leaf served at the address, or null when none could be read.</summary>
    Task<byte[]?> ReadLeafAsync(string address, CancellationToken ct);
}

/// <summary>
/// Connects to an address as a peer would and reads the leaf certificate
/// served there, whatever it is (MSFP section 3.1, "knowing one's own
/// certificate").
/// </summary>
public sealed class TlsCertificateReader : ICertificateReader
{
    /// <summary>How long one reading may take.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task<byte[]?> ReadLeafAsync(string address, CancellationToken ct)
    {
        if (!address.StartsWith("https://", StringComparison.Ordinal) || !Protocol.Address.TryGetHost(address, out var authority))
        {
            return null;
        }

        var (host, port) = Split(authority);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            byte[]? leaf = null;
            using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
            {
                leaf = cert?.GetRawCertData();
                return true;
            });
            var bare = host.Trim('[', ']');
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                // SNI carries names only.
                TargetHost = System.Net.IPAddress.TryParse(bare, out _) ? string.Empty : host,
            }, cts.Token).ConfigureAwait(false);
            return leaf;
        }
        catch (Exception e) when (e is SocketException or System.IO.IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException)
        {
            return null;
        }
    }

    private static (string Host, int Port) Split(string authority)
    {
        var close = authority.LastIndexOf(']');
        var colon = authority.LastIndexOf(':');
        if (colon > close)
        {
            return (authority[..colon].Trim('[', ']'), int.Parse(authority[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        }

        return (authority.Trim('[', ']'), 443);
    }
}
