// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

internal sealed class DeployProbeTransport(
    Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
    IOptionsMonitor<ControlPlaneOptions>? controlPlaneOptions = null)
{
    private const string DisallowedDestinationMessage =
        "The probe destination is outside the configured connection policy.";
    private readonly PinnedDnsConnector _connector = new(hostAddressResolver, new PinnedDnsFailureMessages(
        DisallowedDestinationMessage, "The probe destination could not be contacted."));

    private bool AllowPrivateNetworkProbes => controlPlaneOptions?.CurrentValue.AllowPrivateNetworkProbes == true;

    public HttpMessageHandler CreateHandler()
        => new RequestPolicyHandler(hostAddressResolver, () => AllowPrivateNetworkProbes)
        {
            InnerHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectCallback = ConnectAsync,
            },
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
            uri.AbsoluteUri, AllowPrivateNetworkProbes, (_, _) => Task.FromResult(addresses), cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new HttpRequestException(DisallowedDestinationMessage);
        }

        return await _connector.ConnectToFirstReachableAsync(addresses, context.DnsEndPoint.Port, cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed class RequestPolicyHandler(
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        Func<bool> allowPrivateNetworkProbes) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new HttpRequestException(DisallowedDestinationMessage);
            var validation = await DeployProbeUrlPolicy.ValidateAsync(
                uri.AbsoluteUri, allowPrivateNetworkProbes(), hostAddressResolver, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                throw new HttpRequestException(DisallowedDestinationMessage);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
