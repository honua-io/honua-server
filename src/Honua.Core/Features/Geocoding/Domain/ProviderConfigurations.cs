// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geocoding.Domain;

/// <summary>
/// Configuration for Nominatim geocoding provider
/// </summary>
public sealed record NominatimProviderConfiguration : GeocodeProviderConfiguration
{
    /// <summary>
    /// Base URL for the Nominatim service
    /// </summary>
    public string BaseUrl { get; init; } = "https://nominatim.openstreetmap.org";

    /// <summary>
    /// User agent for API requests
    /// </summary>
    public string UserAgent { get; init; } = "Honua/1.0 (+https://honua.io)";

    /// <summary>
    /// Contact email for the Nominatim service
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Enable suggest functionality using search endpoint
    /// </summary>
    public bool EnableSuggestFromSearch { get; init; }

    /// <summary>
    /// Default maximum suggestions for autocomplete
    /// </summary>
    public int MaxSuggestions { get; init; } = 5;
}

/// <summary>
/// Configuration for Amazon Location Services geocoding provider
/// </summary>
public sealed record AmazonLocationProviderConfiguration : GeocodeProviderConfiguration
{
    /// <summary>
    /// AWS region for the Location Service
    /// </summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>
    /// Name of the place index in Amazon Location Service
    /// </summary>
    public string PlaceIndexName { get; init; } = string.Empty;

    /// <summary>
    /// AWS access key ID
    /// </summary>
    public string? AccessKeyId { get; init; }

    /// <summary>
    /// AWS secret access key
    /// </summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>
    /// Use AWS IAM role for authentication instead of access keys
    /// </summary>
    public bool UseIamRole { get; init; } = true;
}

/// <summary>
/// Configuration for Azure Maps geocoding provider
/// </summary>
public sealed record AzureMapsProviderConfiguration : GeocodeProviderConfiguration
{
    /// <summary>
    /// Azure Maps subscription key
    /// </summary>
    public string SubscriptionKey { get; init; } = string.Empty;

    /// <summary>
    /// API version for Azure Maps services
    /// </summary>
    public string ApiVersion { get; init; } = "1.0";

    /// <summary>
    /// Base URL for Azure Maps API
    /// </summary>
    public string BaseUrl { get; init; } = "https://atlas.microsoft.com";

    /// <summary>
    /// View parameter for localized results
    /// </summary>
    public string? View { get; init; }

    /// <summary>
    /// Language for localized results
    /// </summary>
    public string? Language { get; init; }
}

/// <summary>
/// Configuration for Esri ArcGIS geocoding provider
/// </summary>
public sealed record EsriProviderConfiguration : GeocodeProviderConfiguration
{
    /// <summary>
    /// Base URL for the ArcGIS geocoding service
    /// </summary>
    public string BaseUrl { get; init; } = "https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer";

    /// <summary>
    /// ArcGIS Online token for authenticated requests
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    /// Client ID for OAuth authentication
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Client secret for OAuth authentication
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Use ArcGIS Online service (true) or custom service (false)
    /// </summary>
    public bool UseArcGISOnline { get; init; } = true;

    /// <summary>
    /// Preferred language for results
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Storage parameter for ArcGIS Online usage tracking
    /// </summary>
    public bool ForStorage { get; init; }
}

/// <summary>
/// Configuration for Google Maps geocoding provider
/// </summary>
public sealed record GoogleMapsProviderConfiguration : GeocodeProviderConfiguration
{
    /// <summary>
    /// Google Maps API key
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Base URL for Google Maps Geocoding API
    /// </summary>
    public string BaseUrl { get; init; } = "https://maps.googleapis.com/maps/api/geocode";

    /// <summary>
    /// Base URL for Google Places API (for suggestions)
    /// </summary>
    public string PlacesBaseUrl { get; init; } = "https://maps.googleapis.com/maps/api/place";

    /// <summary>
    /// Language for localized results
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Region biasing for results
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Component restrictions (e.g., country:US)
    /// </summary>
    public string[]? Components { get; init; }
}

/// <summary>
/// Configuration for MapBox geocoding provider
/// </summary>
public sealed record MapboxProviderConfiguration : GeocodeProviderConfiguration
{
    /// <summary>
    /// MapBox access token
    /// </summary>
    public string AccessToken { get; init; } = string.Empty;

    /// <summary>
    /// Base URL for MapBox Geocoding API
    /// </summary>
    public string BaseUrl { get; init; } = "https://api.mapbox.com/geocoding/v5/mapbox.places";

    /// <summary>
    /// Language for localized results
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Feature types to include in results
    /// </summary>
    public string[]? Types { get; init; }

    /// <summary>
    /// Use autocomplete endpoint for suggestions
    /// </summary>
    public bool UseAutocomplete { get; init; } = true;
}
