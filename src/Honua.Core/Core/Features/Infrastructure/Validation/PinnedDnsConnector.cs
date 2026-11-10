// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using Honua.Core.Features.Infrastructure.Internal;

namespace Honua.Core.Features.Infrastructure.Validation;

public readonly record struct PinnedDnsFailureMessages(string DisallowedAddress, string ConnectionFailure);

// The host is resolved again at connect time so DNS cannot change between validation and the socket.
public sealed class PinnedDnsConnector(
    Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
    PinnedDnsFailureMessages messages)
{
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

    // Redirects are never followed, so a 3xx cannot send the request or its credentials to another host.
    public SocketsHttpHandler CreateHandler(int maxConnectionsPerServer)
        => new()
        {
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = maxConnectionsPerServer,
            ConnectCallback = ConnectAsync,
        };

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var addresses = await TryResolveAllowedAddressesAsync(context.DnsEndPoint.Host, hostAddressResolver, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new HttpRequestException(messages.DisallowedAddress);

        return await ConnectToFirstReachableAsync(addresses, context.DnsEndPoint.Port, cancellationToken)
            .ConfigureAwait(false);
    }

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
