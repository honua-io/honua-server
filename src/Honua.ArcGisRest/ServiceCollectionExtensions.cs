// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ArcGisRest.Features.FeatureStore;
using Honua.ArcGisRest.Features.FeatureStore.Services;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
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
        // The primary handler MUST be the SSRF-hardened pinned-DNS handler:
        // it disables automatic redirects (so a remote 3xx cannot bounce a
        // validated public request to an internal address) and re-validates the
        // resolved IP at connect time against private/loopback/link-local/CGNAT/
        // ULA/reserved ranges (incl. 169.254.169.254 cloud metadata). Without
        // this, the federated client would follow redirects and apply no IP
        // filtering at runtime — a full SSRF + DNS-rebinding exposure.
        //
        // A per-request timeout and the shared retry/circuit-breaker policy (reused from
        // Honua.Core.Features.Infrastructure.Resilience rather than a bespoke policy here)
        // guard against a slow/unresponsive federated ArcGIS endpoint hanging a request or
        // hammering a downed one (#2404 PA-123).
        services
            .AddHttpClient<IArcGisRestFeatureClient, ArcGisRestFeatureClient>(ArcGisRestServiceClientName.Default)
            .ConfigurePrimaryHttpMessageHandler(
                static () => ArcGisRestOutboundGuard.CreatePinnedDnsHttpMessageHandler())
            .ConfigureHttpClient(static client => client.Timeout = TimeSpan.FromSeconds(30))
            .AddHttpResiliencePolicy("arcgis-rest");

        services.AddScoped<ArcGisRestFeatureStore>();
        services.AddScoped<IFeatureDataProvider>(sp => sp.GetRequiredService<ArcGisRestFeatureStore>());

        return services;
    }
}
