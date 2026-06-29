// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Federation;
using Honua.Core.Features.Federation.Abstractions;
using Honua.Server.Features.Admin.Federation.Connectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Admin.Federation;

/// <summary>
/// Service registration for the federation admin surface and query planner (issue #341).
/// </summary>
public static class FederationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the federation query planner, the remote-execution layer, the configuration-backed
    /// source registry, the HTTP transport connectors (Esri REST and OGC API - Features / WFS),
    /// and binds <see cref="FederationSourceOptions"/> from the <c>Federation</c> section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFederationServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<FederationSourceOptions>(configuration.GetSection(FederationSourceOptions.SectionName));
        services.AddFederationCore();
        services.TryAddSingleton<IFederationSourceRegistry, ConfiguredFederationSourceRegistry>();

        // Named HTTP client shared by every HTTP federation connector. The executor applies the
        // per-source timeout and circuit breaker, so the client itself stays unopinionated.
        services.AddHttpClient(HttpGeoJsonFederatedSourceConnector.HttpClientName);

        // Transport connectors are additive: the executor maps each registered connector by its
        // FederatedSourceKind, so a deployment without a kind simply cannot federate to it.
        services.AddSingleton<IFederatedSourceConnector, EsriRestFederatedSourceConnector>();
        services.AddSingleton<IFederatedSourceConnector, OgcFeaturesFederatedSourceConnector>();

        return services;
    }
}
