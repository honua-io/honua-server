// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.Geocoding.Abstractions;
using Honua.Postgres.Features.Geocoding.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Postgres.Features.Geocoding;

/// <summary>
/// Service collection extensions for registering the Esri geocoding provider
/// </summary>
public static class EsriGeocodingServiceCollectionExtensions
{
    /// <summary>
    /// Adds Esri geocoding provider to the service collection
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configuration">Configuration</param>
    /// <param name="sectionName">Configuration section name (default: "Geocoding:Esri")</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddEsriGeocoding(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "Geocoding:Esri")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Register configuration
        services.AddOptions<EsriGeocodingOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<EsriGeocodingOptions>, EsriGeocodingOptionsValidator>();

        // Register HTTP client for Esri services
        services.AddHttpClient<EsriGeocodeProvider>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<EsriGeocodingOptions>>().Value;
            var logger = serviceProvider.GetRequiredService<ILogger<EsriGeocodeProvider>>();

            // Validate base URL
            if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException($"Invalid Esri BaseUrl: {options.BaseUrl}");
            }

            if (baseUri.Scheme != Uri.UriSchemeHttps)
            {
                logger.LogWarning("Esri BaseUrl is not using HTTPS: {BaseUrl}", options.BaseUrl);
            }

            // Configure HTTP client
            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

            // Set user agent
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);

            // Enable compression if configured
            if (options.UseCompression)
            {
                client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
            }

            logger.LogInformation("Configured Esri geocoding client with base URL: {BaseUrl}", baseUri);
        })
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler();

            // Enable compression
            if (handler.SupportsAutomaticDecompression)
            {
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }

            return handler;
        });

        // Register the provider
        services.AddScoped<IGeocodeProvider, EsriGeocodeProvider>();

        return services;
    }

    /// <summary>
    /// Adds Esri geocoding provider with custom configuration
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureOptions">Configuration action</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddEsriGeocoding(
        this IServiceCollection services,
        Action<EsriGeocodingOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.Configure(configureOptions);
        services.AddSingleton<IValidateOptions<EsriGeocodingOptions>, EsriGeocodingOptionsValidator>();

        // Register HTTP client
        services.AddHttpClient<EsriGeocodeProvider>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<EsriGeocodingOptions>>().Value;
            var logger = serviceProvider.GetRequiredService<ILogger<EsriGeocodeProvider>>();

            if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException($"Invalid Esri BaseUrl: {options.BaseUrl}");
            }

            client.BaseAddress = baseUri;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);

            if (options.UseCompression)
            {
                client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
            }

            logger.LogInformation("Configured Esri geocoding client with base URL: {BaseUrl}", baseUri);
        });

        services.AddScoped<IGeocodeProvider, EsriGeocodeProvider>();

        return services;
    }
}