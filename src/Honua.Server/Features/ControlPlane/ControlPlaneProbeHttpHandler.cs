// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.ControlPlane;

internal static class ControlPlaneProbeHttpHandler
{
    private static readonly PinnedDnsConnector Connector = new(
        Dns.GetHostAddressesAsync,
        new PinnedDnsFailureMessages("Probe destination is not allowed.", "Could not connect to the probe destination."));

    internal static SocketsHttpHandler Create(
        Func<bool> allowPrivateNetworks,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = ConnectAsync
        };

        async ValueTask<Stream> ConnectAsync(
            SocketsHttpConnectionContext context,
            CancellationToken cancellationToken)
        {
            IPAddress[]? addresses = null;
            async Task<IPAddress[]> ResolveAsync(string host, CancellationToken token)
                => addresses ??= await (resolver ?? Dns.GetHostAddressesAsync)(host, token).ConfigureAwait(false);

            var validation = await DeployProbeUrlPolicy.ValidateAsync(
                context.InitialRequestMessage.RequestUri!.AbsoluteUri, allowPrivateNetworks(), ResolveAsync, cancellationToken)
                .ConfigureAwait(false);
            if (!validation.IsValid)
            {
                throw new HttpRequestException(validation.ErrorMessage);
            }

            // Use the addresses the policy checked rather than resolving the host again when connecting.
            addresses ??= [IPAddress.Parse(context.DnsEndPoint.Host)];
            return await Connector.ConnectToFirstReachableAsync(addresses, context.DnsEndPoint.Port, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
