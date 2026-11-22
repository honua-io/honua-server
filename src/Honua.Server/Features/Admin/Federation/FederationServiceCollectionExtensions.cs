// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Federation;
using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Infrastructure.Validation;
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

        // The executor applies per-source timeouts and resilience; the transport vets each connection.
        services.AddHttpClient(HttpGeoJsonFederatedSourceConnector.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(static () => new PinnedDnsConnector(
                static (host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken),
                new("The federation destination is not a public address.",
                    "Unable to connect to the federation destination."))
                .CreateHandler(maxConnectionsPerServer: 10));

        // Transport connectors are additive: the executor maps each registered connector by its
        // FederatedSourceKind, so a deployment without a kind simply cannot federate to it.
        services.AddSingleton<IFederatedSourceConnector, GeoServicesRestFederatedSourceConnector>();
        services.AddSingleton<IFederatedSourceConnector, OgcFeaturesFederatedSourceConnector>();

        return services;
    }
}
