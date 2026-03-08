// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Server.Features.Geocoding.Providers;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Geocoding;

internal static class GeocodingServiceCollectionExtensions
{
    public static IServiceCollection AddGeocoding(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<GeocodingOptions>()
            .Bind(configuration.GetSection(GeocodingOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<GeocodingOptions>, GeocodingOptionsValidator>();

        services.AddHttpClient<NominatimGeocodeProvider>((serviceProvider, client) =>
        {
            var geocodingOptions = serviceProvider.GetRequiredService<IOptions<GeocodingOptions>>().Value;
            var nominatim = geocodingOptions.Nominatim;
            var baseUrlValidation = OutboundHttpUrlValidator.ValidateConfiguration(nominatim.BaseUrl);
            if (!baseUrlValidation.IsValid || baseUrlValidation.Uri is null)
            {
                throw new InvalidOperationException(
                    $"Geocoding:Nominatim:BaseUrl {baseUrlValidation.ErrorMessage ?? "must be a valid HTTPS URL."}");
            }

            var baseAddress = baseUrlValidation.Uri.AbsoluteUri.TrimEnd('/') + "/";
            client.BaseAddress = new Uri(baseAddress, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(nominatim.TimeoutSeconds);

            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(nominatim.UserAgent);

            if (!string.IsNullOrWhiteSpace(nominatim.Email))
            {
                client.DefaultRequestHeaders.Remove("From");
                client.DefaultRequestHeaders.Add("From", nominatim.Email);
            }
        });

        services.AddScoped<IGeocodeProvider, NominatimGeocodeProvider>();
        services.AddScoped<IGeocodeProviderResolver, GeocodeProviderResolver>();
        services.AddScoped<GeocodingHandler>();

        return services;
    }
}
