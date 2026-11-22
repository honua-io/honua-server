// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.ControlPlane;

internal sealed class DeployProbeTransport(
    Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver)
{
    private const string DisallowedDestinationMessage =
        "The probe destination is outside the configured connection policy.";
    private readonly PinnedDnsConnector _connector = new(hostAddressResolver, new PinnedDnsFailureMessages(
        DisallowedDestinationMessage, "The probe destination could not be contacted."));

    public SocketsHttpHandler CreateHandler()
        => new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = ConnectAsync,
        };

    private async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var uri = context.InitialRequestMessage.RequestUri
            ?? throw new HttpRequestException(DisallowedDestinationMessage);
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literalAddress)
            ? [literalAddress]
            : await hostAddressResolver(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var validation = await DeployProbeUrlPolicy.ValidateAsync(
            uri.AbsoluteUri, false, (_, _) => Task.FromResult(addresses), cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new HttpRequestException(DisallowedDestinationMessage);
        }

        return await _connector.ConnectToFirstReachableAsync(addresses, context.DnsEndPoint.Port, cancellationToken)
            .ConfigureAwait(false);
    }
}
