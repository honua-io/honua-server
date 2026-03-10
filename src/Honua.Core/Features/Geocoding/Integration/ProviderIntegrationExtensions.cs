// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Core.Features.Geocoding.Domain;
using Honua.Core.Features.Geocoding.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Geocoding.Integration;

/// <summary>
/// Integration extensions for registering specific geocoding providers
/// </summary>
public static class ProviderIntegrationExtensions
{
    /// <summary>
    /// Add the mock geocoding provider for testing
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Provider configuration (optional)</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddMockGeocodeProvider(
        this IServiceCollection services,
        MockGeocodeProviderConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        configuration ??= new MockGeocodeProviderConfiguration();

        services.AddGeocodeProvider(
            MockGeocodeProvider.ProviderName,
            _ => new MockGeocodeProvider(configuration));

        return services;
    }

    /// <summary>
    /// Add Nominatim geocoding provider
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddNominatimGeocodeProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<NominatimProviderConfiguration>()
            .Bind(configuration.GetSection($"{GeocodingConfiguration.SectionName}:Providers:Nominatim"));

        services.AddHttpClient<NominatimGeocodeProvider>();

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

    /// <summary>
    /// Add Amazon Location Services geocoding provider
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
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

    /// <summary>
    /// Add Azure Maps geocoding provider
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddAzureMapsGeocodeProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AzureMapsProviderConfiguration>()
            .Bind(configuration.GetSection($"{GeocodingConfiguration.SectionName}:Providers:AzureMaps"));

        services.AddHttpClient<AzureMapsGeocodeProvider>();

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

    /// <summary>
    /// Add Esri geocoding provider
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddEsriGeocodeProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EsriProviderConfiguration>()
            .Bind(configuration.GetSection($"{GeocodingConfiguration.SectionName}:Providers:Esri"));

        // TODO: Implement Esri provider
        // This would target the ArcGIS REST Geocoding API

        return services;
    }

    /// <summary>
    /// Add all available geocoding providers based on configuration
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddAllGeocodeProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var geocodingConfig = configuration.GetSection(GeocodingConfiguration.SectionName);

        // Add providers based on enabled status in configuration
        if (geocodingConfig.GetValue<bool>("Providers:Nominatim:Enabled"))
        {
            services.AddNominatimGeocodeProvider(configuration);
        }

        if (geocodingConfig.GetValue<bool>("Providers:AmazonLocation:Enabled"))
        {
            services.AddAmazonLocationGeocodeProvider(configuration);
        }

        if (geocodingConfig.GetValue<bool>("Providers:AzureMaps:Enabled"))
        {
            services.AddAzureMapsGeocodeProvider(configuration);
        }

        if (geocodingConfig.GetValue<bool>("Providers:Esri:Enabled"))
        {
            services.AddEsriGeocodeProvider(configuration);
        }

        // Always add mock provider for testing/fallback if enabled
        var mockEnabled = geocodingConfig.GetValue<bool>("Providers:Mock:Enabled");
        if (mockEnabled)
        {
            var mockConfig = new MockGeocodeProviderConfiguration
            {
                Enabled = true,
                Priority = geocodingConfig.GetValue<int>("Providers:Mock:Priority", 0),
                TimeoutSeconds = geocodingConfig.GetValue<int>("Providers:Mock:TimeoutSeconds", 30),
                MaxResults = geocodingConfig.GetValue<int>("Providers:Mock:MaxResults", 10),
                EnableSuggest = geocodingConfig.GetValue<bool>("Providers:Mock:EnableSuggest", true),
                EnableBatch = geocodingConfig.GetValue<bool>("Providers:Mock:EnableBatch", true)
            };

            services.AddMockGeocodeProvider(mockConfig);
        }

        return services;
    }
}
