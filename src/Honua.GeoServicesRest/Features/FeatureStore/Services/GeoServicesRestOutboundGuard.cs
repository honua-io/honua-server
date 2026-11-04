// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Infrastructure.Validation;

namespace Honua.GeoServicesRest.Features.FeatureStore.Services;

// Redirects are never followed, and connections go only to addresses the pinned DNS connector allows.
internal static class GeoServicesRestOutboundGuard
{
    internal const string DisallowedNetworkAddressMessage =
        "ArcGIS service URL resolves to a disallowed network address.";

    // A slow ArcGIS endpoint must not exhaust the local socket pool.
    private const int MaxConnectionsPerArcGisHost = 32;

    private static readonly PinnedDnsFailureMessages PinnedConnectionFailures = new(
        DisallowedNetworkAddressMessage,
        "Unable to establish a secure connection to the ArcGIS service host.");

    public static HttpMessageHandler CreatePinnedDnsHttpMessageHandler(
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        var resolver = hostAddressResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));

        return new PinnedDnsConnector(resolver, PinnedConnectionFailures).CreateHandler(MaxConnectionsPerArcGisHost);
    }

    public static async Task<IPAddress[]> ResolveAllowedAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
        => await PinnedDnsConnector.TryResolveAllowedAddressesAsync(host, hostAddressResolver, cancellationToken).ConfigureAwait(false)
           ?? throw new ArgumentException(DisallowedNetworkAddressMessage);
}
