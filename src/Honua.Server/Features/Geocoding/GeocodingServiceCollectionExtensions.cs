// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geocoding;
using Honua.Core.Features.Geocoding.Integration;
using Honua.Protocols.GeoServices.Catalog;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Geocoding;

internal static class GeocodingServiceCollectionExtensions
{
    public static IServiceCollection AddGeocoding(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Register server-specific options
        services.AddOptions<GeocodingOptions>()
            .Bind(configuration.GetSection(GeocodingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GeocodingOptions>, GeocodingOptionsValidator>();

        // Add core geocoding services
        services.AddGeocodingCore(configuration);

        // Always register Nominatim by default (maintains backwards compatibility with trunk)
        services.AddNominatimGeocodeProvider(configuration);

        // Add other providers based on configuration
        var geocodingConfig = configuration.GetSection("Geocoding");

        if (geocodingConfig.GetValue<bool>("Providers:AmazonLocation:Enabled"))
        {
            services.AddAmazonLocationGeocodeProvider(configuration);
        }

        if (geocodingConfig.GetValue<bool>("Providers:AzureMaps:Enabled"))
        {
            services.AddAzureMapsGeocodeProvider(configuration);
        }

        // Register the handler for GeoServices-compatible geocoding endpoints
        services.AddScoped<GeocodingHandler>();

        // Advertise GeocodeServer in the /rest/services catalog so Esri-style clients
        // can discover the locator alongside FeatureServer/MapServer/ImageServer.
        services.AddSingleton<IGeoServicesCatalogContributor, GeocodeServerCatalogContributor>();

        return services;
    }
}
