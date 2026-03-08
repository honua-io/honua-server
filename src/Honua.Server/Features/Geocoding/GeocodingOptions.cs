// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Geocoding;

internal sealed class GeocodingOptions
{
    public const string SectionName = "Geocoding";

    public bool Enabled { get; set; } = true;

    public string DefaultProvider { get; set; } = Providers.NominatimGeocodeProvider.ProviderName;

    public string LocatorName { get; set; } = "World";

    public int DefaultSpatialReferenceWkid { get; set; } = 4326;

    public NominatimGeocodingOptions Nominatim { get; set; } = new();
}

internal sealed class NominatimGeocodingOptions
{
    public string BaseUrl { get; set; } = "https://nominatim.openstreetmap.org";

    public string UserAgent { get; set; } = "Honua/1.0 (+https://honua.io)";

    public string? Email { get; set; }

    public int TimeoutSeconds { get; set; } = 10;

    public int DefaultMaxResults { get; set; } = 10;

    public int DefaultMaxSuggestions { get; set; } = 5;

    public bool EnableSuggestFromSearch { get; set; }

    public string? CountryCodes { get; set; }
}

internal sealed class GeocodingOptionsValidator : OptionsValidator<GeocodingOptions>
{
    protected override void ValidateOptions(GeocodingOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.LocatorName))
        {
            failures.Add("Geocoding:LocatorName is required.");
        }

        if (string.IsNullOrWhiteSpace(options.DefaultProvider))
        {
            failures.Add("Geocoding:DefaultProvider is required.");
        }

        if (options.DefaultSpatialReferenceWkid <= 0)
        {
            failures.Add("Geocoding:DefaultSpatialReferenceWkid must be greater than 0.");
        }

        ValidateOutboundHttpUrl(options.Nominatim.BaseUrl, "Geocoding:Nominatim:BaseUrl", failures);

        if (string.IsNullOrWhiteSpace(options.Nominatim.UserAgent))
        {
            failures.Add("Geocoding:Nominatim:UserAgent is required.");
        }

        if (options.Nominatim.TimeoutSeconds <= 0)
        {
            failures.Add("Geocoding:Nominatim:TimeoutSeconds must be greater than 0.");
        }

        if (options.Nominatim.DefaultMaxResults <= 0)
        {
            failures.Add("Geocoding:Nominatim:DefaultMaxResults must be greater than 0.");
        }

        if (options.Nominatim.DefaultMaxSuggestions <= 0)
        {
            failures.Add("Geocoding:Nominatim:DefaultMaxSuggestions must be greater than 0.");
        }
    }
}
