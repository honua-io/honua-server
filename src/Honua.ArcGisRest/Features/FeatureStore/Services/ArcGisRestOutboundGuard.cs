// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;

namespace Honua.ArcGisRest.Features.FeatureStore.Services;

/// <summary>
/// SSRF defenses for the federated ArcGIS REST provider. Resolves outbound hosts
/// and rejects any address in a private, loopback, link-local, CGNAT, ULA, or
/// reserved range so a configured (or redirected) service URL cannot be used to
/// reach internal infrastructure or cloud-metadata endpoints
/// (e.g. <c>169.254.169.254</c>).
/// </summary>
/// <remarks>
/// This mirrors the pinned-DNS / address-allow-list logic the import-side
/// <c>ArcGisRestClient</c> applies, but is duplicated here intentionally: the
/// import-side helper is <c>internal</c> to <c>Honua.Core</c> and not visible to
/// this assembly, so the federated provider carries its own copy of the guard.
/// </remarks>
internal static class ArcGisRestOutboundGuard
{
    internal const string DisallowedNetworkAddressMessage =
        "ArcGIS service URL resolves to a disallowed network address.";

    /// <summary>
    /// Builds the primary <see cref="HttpMessageHandler"/> used by the federated
    /// outbound client. Disables automatic redirects (so a remote 3xx cannot
    /// bounce a validated public request to an internal address — and so the
    /// authorization header/token is never replayed to a redirect target) and
    /// re-validates the resolved IP at connect time to close the
    /// DNS-rebinding window between validation and connection.
    /// </summary>
    public static HttpMessageHandler CreatePinnedDnsHttpMessageHandler(
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        var resolver = hostAddressResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Bound per-host connections so a slow/pathological ArcGIS endpoint
            // cannot exhaust the local socket pool.
            MaxConnectionsPerServer = 32,
            ConnectCallback = (context, cancellationToken) =>
                ConnectWithPinnedDnsAsync(context, resolver, cancellationToken)
        };
    }

    /// <summary>
    /// Resolves <paramref name="host"/> to its allowed IP addresses, throwing when
    /// the host is a localhost name, an IP literal in a disallowed range, or
    /// resolves to any disallowed address. Used both at validation time and at
    /// connect time (pinned-DNS) so the two cannot diverge.
    /// </summary>
    public static async Task<IPAddress[]> ResolveAllowedAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || IsLocalhostHostName(host))
        {
            throw new ArgumentException(DisallowedNetworkAddressMessage);
        }

        if (IPAddress.TryParse(host, out var literalAddress))
        {
            if (IsPrivateOrReservedAddress(literalAddress))
            {
                throw new ArgumentException(DisallowedNetworkAddressMessage);
            }

            return [literalAddress];
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArgumentException(DisallowedNetworkAddressMessage, ex);
        }

        if (addresses.Length == 0)
        {
            throw new ArgumentException(DisallowedNetworkAddressMessage);
        }

        foreach (var address in addresses)
        {
            if (IsPrivateOrReservedAddress(address))
            {
                throw new ArgumentException(DisallowedNetworkAddressMessage);
            }
        }

        return addresses;
    }

    public static bool IsLocalhostHostName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<Stream> ConnectWithPinnedDnsAsync(
        SocketsHttpConnectionContext context,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAllowedAddressesAsync(
                context.DnsEndPoint.Host,
                hostAddressResolver,
                cancellationToken)
            .ConfigureAwait(false);

        Exception? lastException = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var connected = false;

            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                connected = true;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                lastException = ex;
            }
            finally
            {
                if (!connected)
                {
                    socket.Dispose();
                }
            }
        }

        throw new HttpRequestException(
            "Unable to establish a secure connection to the ArcGIS service host.", lastException);
    }

    private static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 0.0.0.0/8 — "this network"
            if (bytes[0] == 0)
            {
                return true;
            }

            // 10.0.0.0/8 — private
            if (bytes[0] == 10)
            {
                return true;
            }

            // 100.64.0.0/10 — CGNAT
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            {
                return true;
            }

            // 127.0.0.0/8 — loopback
            if (bytes[0] == 127)
            {
                return true;
            }

            // 169.254.0.0/16 — link-local (cloud metadata 169.254.169.254)
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }

            // 172.16.0.0/12 — private
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            // 192.0.0.0/24 — IETF protocol assignments
            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
            {
                return true;
            }

            // 192.0.2.0/24 — TEST-NET-1 documentation
            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
            {
                return true;
            }

            // 192.168.0.0/16 — private
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            // 198.18.0.0/15 — benchmarking
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            {
                return true;
            }

            // 198.51.100.0/24 — TEST-NET-2 documentation
            if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
            {
                return true;
            }

            // 203.0.113.0/24 — TEST-NET-3 documentation
            if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
            {
                return true;
            }

            // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
            if (bytes[0] >= 224)
            {
                return true;
            }
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();

            if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Loopback))
            {
                return true;
            }

            // fe80::/10 — link-local
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            {
                return true;
            }

            // fec0::/10 — deprecated site-local
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0)
            {
                return true;
            }

            // fc00::/7 — unique local addresses
            if ((bytes[0] & 0xfe) == 0xfc)
            {
                return true;
            }

            // 2001:db8::/32 — documentation
            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            {
                return true;
            }

            // ff00::/8 — multicast
            if (bytes[0] == 0xff)
            {
                return true;
            }
        }

        return false;
    }
}
