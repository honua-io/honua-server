// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ArcGisRest.Features.FeatureStore;
using Honua.ArcGisRest.Features.FeatureStore.Services;
using Honua.Core.Features.FeatureStore.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.ArcGisRest;

/// <summary>
/// Public DI entry point for the federated ArcGIS REST feature provider (#1251).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ArcGIS REST FeatureServer/MapServer read-through provider as
    /// an additional <see cref="IFeatureDataProvider"/> in the runtime. Metadata v2
    /// publications whose backing connection resolves to provider <c>arcgis-rest</c>
    /// (canonical name; aliases such as <c>arcgis</c>, <c>esri</c>, and
    /// <c>esri-featureserver</c> are accepted) are routed through this provider via
    /// the shared <c>FeatureProviderQueryRouter</c>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration. Reserved for future
    /// options binding (default timeout, retry policy); no settings are read in the
    /// MVP slice.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddArcGisRestFeatureProvider(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Typed-HttpClient registration: lets host applications attach resilience
        // / retry / telemetry handlers via the same well-known name without
        // taking a hard dependency on this provider's internals.
        //
        // The primary handler pins DNS and disables automatic redirects so a
        // configured (or redirected) service URL cannot be used to reach private,
        // loopback, link-local, or cloud-metadata addresses (SSRF), and so the
        // bearer token / authorization header is never replayed to a 3xx target.
        services
            .AddHttpClient<IArcGisRestFeatureClient, ArcGisRestFeatureClient>(ArcGisRestServiceClientName.Default)
            .ConfigurePrimaryHttpMessageHandler(
                static () => ArcGisRestOutboundGuard.CreatePinnedDnsHttpMessageHandler());

        services.AddScoped<ArcGisRestFeatureStore>();
        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<ArcGisRestFeatureStore>());

        return services;
    }
}
