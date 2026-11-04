// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Linq;
using System.Net;
using System.Net.Sockets;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Internal;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.GeoServicesRest.Features.FeatureStore.Services;

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
        if (string.IsNullOrWhiteSpace(host) || OutboundHttpUrlValidator.IsLocalhostHostName(host))
        {
            throw new ArgumentException(DisallowedNetworkAddressMessage);
        }

        if (IPAddress.TryParse(host, out var literalAddress))
        {
            if (OutboundHttpUrlValidator.IsPrivateOrReservedAddress(literalAddress))
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

        if (addresses.Any(OutboundHttpUrlValidator.IsPrivateOrReservedAddress))
        {
            throw new ArgumentException(DisallowedNetworkAddressMessage);
        }

        return addresses;
    }

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
            using var socketOwner = new SocketConnectionOwner(
                new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp));

            try
            {
                await socketOwner.Socket
                    .ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken)
                    .ConfigureAwait(false);
                return socketOwner.TransferToNetworkStream();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                lastException = ex;
            }
        }

        throw new HttpRequestException(
            "Unable to establish a secure connection to the ArcGIS service host.", lastException);
    }

}
