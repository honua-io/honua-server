// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geocoding.Domain;

namespace Honua.Postgres.Features.Geocoding;

/// <summary>
/// Configuration options for Esri geocoding provider
/// </summary>
public sealed record EsriGeocodingOptions : GeocodeProviderConfiguration
{
    /// <summary>
    /// Base URL for the Esri geocoding service
    /// </summary>
    public string BaseUrl { get; init; } = "https://geocode-api.arcgis.com/arcgis/rest/services/World/GeocodeServer";

    /// <summary>
    /// API key for authentication
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Client ID for OAuth authentication (alternative to API key)
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Client secret for OAuth authentication
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// OAuth token endpoint for generating access tokens
    /// </summary>
    public string TokenEndpoint { get; init; } = "https://www.arcgis.com/sharing/rest/oauth2/token";

    /// <summary>
    /// Default spatial reference system WKID
    /// </summary>
    public int DefaultSpatialReference { get; init; } = 4326;

    /// <summary>
    /// Default output fields to return
    /// </summary>
    public string[] DefaultOutFields { get; init; } = ["Addr_type", "Country", "PlaceName", "Region", "Subregion"];

    /// <summary>
    /// Default locator to use for geocoding
    /// </summary>
    public string? DefaultLocator { get; init; }

    /// <summary>
    /// Default categories for POI searches
    /// </summary>
    public string[]? DefaultCategories { get; init; }

    /// <summary>
    /// Default country codes to bias results
    /// </summary>
    public string[]? DefaultCountries { get; init; }

    /// <summary>
    /// Whether to enable batch geocoding
    /// </summary>
    public bool EnableBatchGeocoding { get; init; } = true;

    /// <summary>
    /// Whether to enable suggestions/autocomplete
    /// </summary>
    public bool EnableSuggestions { get; init; } = true;

    /// <summary>
    /// Maximum batch size for batch requests
    /// </summary>
    public int MaxBatchSize { get; init; } = 1000;

    /// <summary>
    /// Token cache duration in minutes
    /// </summary>
    public int TokenCacheDurationMinutes { get; init; } = 55; // Tokens typically last 1 hour

    /// <summary>
    /// Whether to use HTTP compression
    /// </summary>
    public bool UseCompression { get; init; } = true;

    /// <summary>
    /// User agent string for requests
    /// </summary>
    public string UserAgent { get; init; } = "Honua/1.0 (+https://honua.io) Esri-Provider";

    /// <summary>
    /// Rate limit configuration (requests per second)
    /// </summary>
    public double? RateLimitRequestsPerSecond { get; init; }

    /// <summary>
    /// Custom locator URLs for different operations
    /// </summary>
    public Dictionary<string, string> CustomLocators { get; init; } = new();
}
