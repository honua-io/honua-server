// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Geocoding.Features.Geocoding;
using Honua.Geocoding.Features.Geocoding.Integration;
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

        // Nominatim stays the default when the flag is absent. An explicit false removes
        // it from the host so a deployment can refuse public geocoding (#6601).
        if (configuration.GetValue<bool?>("Geocoding:Providers:Nominatim:Enabled") != false)
        {
            services.AddNominatimGeocodeProvider(configuration);
        }

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

        if (geocodingConfig.GetValue<bool>("Providers:Local:Enabled"))
        {
            services.AddLocalGeocodeProvider(configuration);
        }

#if !HONUA_EXCLUDE_GEOSERVICES
        // Register the handler for GeoServices-compatible geocoding endpoints
        services.AddScoped<GeocodingHandler>();
#endif

        return services;
    }
}
