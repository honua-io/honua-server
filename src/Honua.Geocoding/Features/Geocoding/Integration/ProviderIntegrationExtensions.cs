// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geocoding.Features.Geocoding.Providers;
using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Geocoding.Features.Geocoding.Integration;

public static class ProviderIntegrationExtensions
{
    public static IServiceCollection AddNominatimGeocodeProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<NominatimProviderConfiguration>()
            .Bind(configuration.GetSection($"{GeocodingConfiguration.SectionName}:Providers:Nominatim"));

        services.AddResilientHttpClient<NominatimGeocodeProvider>(
            "nominatim-geocoding",
            HttpResiliencePolicies.FastApiDefaults);

        services.AddGeocodeProvider(
            NominatimGeocodeProvider.ProviderName,
            serviceProvider =>
            {
                var config = serviceProvider.GetRequiredService<IOptionsMonitor<NominatimProviderConfiguration>>().CurrentValue;
                var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient(typeof(NominatimGeocodeProvider).FullName ?? nameof(NominatimGeocodeProvider));

                return new NominatimGeocodeProvider(config, httpClient);
            });

        return services;
    }

    public static IServiceCollection AddAmazonLocationGeocodeProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AmazonLocationProviderConfiguration>()
            .Bind(configuration.GetSection($"{GeocodingConfiguration.SectionName}:Providers:AmazonLocation"));

        services.AddGeocodeProvider(
            AmazonLocationGeocodeProvider.ProviderName,
            serviceProvider =>
            {
                var config = serviceProvider.GetRequiredService<IOptionsMonitor<AmazonLocationProviderConfiguration>>().CurrentValue;
                // Optional: Allow injection of pre-configured IAmazonLocationService
                var locationClient = serviceProvider.GetService<Amazon.LocationService.IAmazonLocationService>();

                return new AmazonLocationGeocodeProvider(config, locationClient);
            });

        return services;
    }

    public static IServiceCollection AddAzureMapsGeocodeProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AzureMapsProviderConfiguration>()
            .Bind(configuration.GetSection($"{GeocodingConfiguration.SectionName}:Providers:AzureMaps"));

        services.AddResilientHttpClient<AzureMapsGeocodeProvider>(
            "azure-maps-geocoding",
            HttpResiliencePolicies.FastApiDefaults);

        services.AddGeocodeProvider(
            AzureMapsGeocodeProvider.ProviderName,
            serviceProvider =>
            {
                var config = serviceProvider.GetRequiredService<IOptionsMonitor<AzureMapsProviderConfiguration>>().CurrentValue;
                var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient(typeof(AzureMapsGeocodeProvider).FullName ?? nameof(AzureMapsGeocodeProvider));

                return new AzureMapsGeocodeProvider(config, httpClient);
            });

        return services;
    }

    public static IServiceCollection AddLocalGeocodeProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<LocalGeocoderProviderConfiguration>()
            .Bind(configuration.GetSection($"{GeocodingConfiguration.SectionName}:Providers:Local"));

        // A single pooled data source backs the provider. Registered as a singleton so the pool is
        // shared across requests; the provider owns and disposes it on shutdown.
        services.AddGeocodeProvider(
            LocalPostgisGeocodeProvider.ProviderName,
            serviceProvider =>
            {
                var config = serviceProvider.GetRequiredService<IOptionsMonitor<LocalGeocoderProviderConfiguration>>().CurrentValue;
                var connectionString = !string.IsNullOrWhiteSpace(config.ConnectionString)
                    ? config.ConnectionString
                    : configuration.GetConnectionString("DefaultConnection");

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new GeocodeProviderException(
                        "The local PostGIS geocoder requires a connection string (Geocoding:Providers:Local:ConnectionString or ConnectionStrings:DefaultConnection).")
                    {
                        ProviderName = LocalPostgisGeocodeProvider.ProviderName,
                        ErrorCode = GeocodeErrorCodes.InvalidConfiguration
                    };
                }

                var dataSource = NpgsqlDataSource.Create(connectionString);
                return new LocalPostgisGeocodeProvider(dataSource, config, ownsDataSource: true);
            },
            ServiceLifetime.Singleton);

        return services;
    }

}
