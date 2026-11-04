// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using Honua.Core.Features.Infrastructure.Internal;

namespace Honua.Core.Features.Infrastructure.Validation;

/// <summary>
/// Messages a caller's outbound guard reports when a pinned connection is refused.
/// </summary>
/// <param name="DisallowedAddress">The host is a localhost name, unresolvable, or resolves to a disallowed address.</param>
/// <param name="ConnectionFailure">Every allowed address refused the TCP connection.</param>
public readonly record struct PinnedDnsFailureMessages(string DisallowedAddress, string ConnectionFailure);

/// <summary>
/// Connects outbound HTTP only to addresses <see cref="OutboundHttpUrlValidator.IsPrivateOrReservedAddress"/>
/// allows. The host is resolved again at connect time and the socket goes to an address from that
/// resolution, so DNS cannot be rebound between validation and connection.
/// </summary>
/// <param name="hostAddressResolver">Resolves a host name to its addresses.</param>
/// <param name="messages">The caller's refusal messages.</param>
public sealed class PinnedDnsConnector(
    Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
    PinnedDnsFailureMessages messages)
{
    /// <summary>
    /// Resolves <paramref name="host"/> and returns its addresses, or <see langword="null"/> when the
    /// host is empty, a localhost name, unresolvable, or resolves to any disallowed address.
    /// </summary>
    /// <param name="host">A host name or IP literal.</param>
    /// <param name="hostAddressResolver">Resolves a host name to its addresses.</param>
    /// <param name="cancellationToken">Cancels resolution.</param>
    /// <returns>The allowed addresses, or <see langword="null"/>.</returns>
    public static async Task<IPAddress[]?> TryResolveAllowedAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host) || OutboundHttpUrlValidator.IsLocalhostHostName(host))
        {
            return null;
        }

        if (IPAddress.TryParse(host, out var literalAddress))
        {
            return OutboundHttpUrlValidator.IsPrivateOrReservedAddress(literalAddress) ? null : [literalAddress];
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return addresses.Length == 0 || addresses.Any(OutboundHttpUrlValidator.IsPrivateOrReservedAddress)
            ? null
            : addresses;
    }

    /// <summary>
    /// Builds an outbound handler that never follows redirects, so a 3xx cannot send a vetted request
    /// or its credentials to another host, and that connects through <see cref="ConnectAsync"/>.
    /// </summary>
    /// <param name="maxConnectionsPerServer">The per-destination connection cap.</param>
    /// <returns>The handler.</returns>
    public SocketsHttpHandler CreateHandler(int maxConnectionsPerServer)
        => new()
        {
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = maxConnectionsPerServer,
            ConnectCallback = ConnectAsync,
        };

    /// <summary>
    /// A <see cref="SocketsHttpHandler.ConnectCallback"/>: resolves the connection's host to allowed
    /// addresses and connects to the first that accepts.
    /// </summary>
    /// <param name="context">The connection the handler is opening.</param>
    /// <param name="cancellationToken">Cancels resolution and connection.</param>
    /// <returns>The connected network stream.</returns>
    /// <exception cref="HttpRequestException">The host is disallowed, or no allowed address accepted.</exception>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var addresses = await TryResolveAllowedAddressesAsync(context.DnsEndPoint.Host, hostAddressResolver, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new HttpRequestException(messages.DisallowedAddress);

        return await ConnectToFirstReachableAsync(addresses, context.DnsEndPoint.Port, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to the first of <paramref name="addresses"/> that accepts on <paramref name="port"/>.
    /// </summary>
    /// <param name="addresses">Addresses that have already passed the outbound address check.</param>
    /// <param name="port">The destination port.</param>
    /// <param name="cancellationToken">Cancels the connection attempts.</param>
    /// <returns>The connected network stream.</returns>
    /// <exception cref="HttpRequestException">No address accepted the connection.</exception>
    public async ValueTask<Stream> ConnectToFirstReachableAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            using var socketOwner = new SocketConnectionOwner(
                new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true });
            try
            {
                await socketOwner.Socket.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                return socketOwner.TransferToNetworkStream();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                lastFailure = ex;
            }
        }

        throw new HttpRequestException(messages.ConnectionFailure, lastFailure);
    }
}
