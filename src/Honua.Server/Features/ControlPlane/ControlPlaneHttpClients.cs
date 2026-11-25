// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

internal static class ControlPlaneHttpClients
{
    public const string Telemetry = "control-plane-telemetry";

    // A probe observes every response once, so this client does not retry, and a failing candidate must not open a circuit breaker shared with the telemetry gate.
    public const string Probe = "control-plane-probe";

    private static readonly PinnedDnsConnector TelemetryConnector = new(
        Dns.GetHostAddressesAsync,
        new PinnedDnsFailureMessages(
            "Telemetry destination is not allowed.",
            "Could not connect to the telemetry destination."));

    public static IServiceCollection AddControlPlaneHttpClients(this IServiceCollection services)
    {
        services.AddResilientHttpClient(Telemetry, Telemetry, HttpResiliencePolicies.FastApiDefaults)
            .ConfigurePrimaryHttpMessageHandler(provider => CreateTelemetryHttpHandler(
                () => provider.GetService<IOptionsMonitor<ControlPlaneOptions>>()?.CurrentValue));
        services.AddHttpClient(Probe)
            .ConfigurePrimaryHttpMessageHandler(provider => ControlPlaneProbeHttpHandler.Create(
                () => provider.GetService<IOptionsMonitor<ControlPlaneOptions>>()?.CurrentValue.AllowPrivateNetworkProbes == true));
        return services;
    }

    internal static SocketsHttpHandler CreateTelemetryHttpHandler(
        Func<ControlPlaneOptions?> getOptions,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        var connector = new TelemetryConnectionCallback(getOptions, resolver);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = connector.ConnectAsync
        };
    }

    private sealed class TelemetryConnectionCallback(
        Func<ControlPlaneOptions?> getOptions,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver)
    {
        public async ValueTask<Stream> ConnectAsync(
            SocketsHttpConnectionContext context,
            CancellationToken cancellationToken)
        {
            var requestUri = context.InitialRequestMessage.RequestUri
                ?? throw new HttpRequestException("Telemetry destination URI is missing.");
            var addresses = await ResolveValidatedAddressesAsync(context, requestUri, cancellationToken)
                .ConfigureAwait(false);

            return await TelemetryConnector.ConnectToFirstReachableAsync(addresses, context.DnsEndPoint.Port, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<IPAddress[]> ResolveValidatedAddressesAsync(
            SocketsHttpConnectionContext context,
            Uri requestUri,
            CancellationToken cancellationToken)
        {
            var allowPrivateNetworks = IsPrivateNetworkAllowedForRequest(getOptions(), requestUri);
            var resolveHost = resolver ?? Dns.GetHostAddressesAsync;
            IPAddress[]? addresses = null;
            async Task<IPAddress[]> ResolveOnceAsync(string host, CancellationToken token)
                => addresses ??= await resolveHost(host, token).ConfigureAwait(false);

            var validation = await OutboundHttpUrlValidator.ValidateAsync(
                    requestUri.AbsoluteUri,
                    ResolveOnceAsync,
                    allowPrivateNetworks,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!validation.IsValid)
            {
                throw new HttpRequestException(validation.ErrorMessage ?? "Telemetry destination is not allowed.");
            }

            addresses ??= IPAddress.TryParse(context.DnsEndPoint.Host, out var literalAddress)
                ? [literalAddress]
                : await resolveHost(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);

            if (addresses.Length == 0)
            {
                throw new HttpRequestException("Could not resolve telemetry destination.");
            }

            return addresses;
        }
    }

    private static bool IsPrivateNetworkAllowedForRequest(ControlPlaneOptions? options, Uri requestUri)
    {
        if (options?.TelemetryConnections is not { Count: > 0 } connections)
        {
            return false;
        }

        for (var i = 0; i < connections.Count; i++)
        {
            var connection = connections[i];
            if (!connection.AllowPrivateNetworks ||
                string.IsNullOrWhiteSpace(connection.BaseUrl) ||
                !Uri.TryCreate(connection.BaseUrl.Trim(), UriKind.Absolute, out var configuredUri))
            {
                continue;
            }

            if (string.Equals(configuredUri.Scheme, requestUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(configuredUri.Host, requestUri.Host, StringComparison.OrdinalIgnoreCase) &&
                configuredUri.Port == requestUri.Port)
            {
                return true;
            }
        }

        return false;
    }
}
