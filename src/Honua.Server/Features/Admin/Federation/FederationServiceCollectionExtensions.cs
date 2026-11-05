// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Federation;
using Honua.Core.Features.Federation.Abstractions;
using Honua.Server.Features.Admin.Federation.Connectors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Admin.Federation;

public static class FederationServiceCollectionExtensions
{
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
        services.AddSingleton<IFederatedSourceConnector, GeoServicesRestFederatedSourceConnector>();
        services.AddSingleton<IFederatedSourceConnector, OgcFeaturesFederatedSourceConnector>();

        return services;
    }
}
