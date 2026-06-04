// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

/// <summary>
/// Main configuration for the geocoding system
/// </summary>
public sealed class GeocodingConfiguration
{
    /// <summary>
    /// Configuration section name
    /// </summary>
    public const string SectionName = "Geocoding";

    /// <summary>
    /// Whether geocoding is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default provider to use when none is specified
    /// </summary>
    public string DefaultProvider { get; set; } = "nominatim";

    /// <summary>
    /// Default spatial reference system (WKID)
    /// </summary>
    public int DefaultSpatialReferenceWkid { get; set; } = 4326;

    /// <summary>
    /// Default maximum results per request
    /// </summary>
    public int DefaultMaxResults { get; set; } = 10;

    /// <summary>
    /// Default timeout in seconds for provider requests
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to enable failover to backup providers
    /// </summary>
    public bool EnableFailover { get; set; } = true;

    /// <summary>
    /// Maximum number of providers to attempt during failover
    /// </summary>
    public int MaxFailoverAttempts { get; set; } = 3;

    /// <summary>
    /// Whether to cache results
    /// </summary>
    public bool EnableCaching { get; set; } = true;

    /// <summary>
    /// Cache expiration time in minutes
    /// </summary>
    public int CacheExpirationMinutes { get; set; } = 60;

    /// <summary>
    /// Whether to log geocoding requests and responses
    /// </summary>
    public bool EnableLogging { get; set; } = true;

    /// <summary>
    /// Locator name for ArcGIS compatibility
    /// </summary>
    public string LocatorName { get; set; } = "World";

    /// <summary>
    /// Provider configurations
    /// </summary>
    public ProvidersConfiguration Providers { get; set; } = new();
}

/// <summary>
/// Configuration for all geocoding providers
/// </summary>
public sealed class ProvidersConfiguration
{
    /// <summary>
    /// Nominatim provider configuration
    /// </summary>
    public NominatimProviderConfiguration Nominatim { get; set; } = new();

    /// <summary>
    /// Amazon Location Service provider configuration
    /// </summary>
    public AmazonLocationProviderConfiguration AmazonLocation { get; set; } = new();

    /// <summary>
    /// Azure Maps provider configuration
    /// </summary>
    public AzureMapsProviderConfiguration AzureMaps { get; set; } = new();

}

/// <summary>
/// Constants for provider names
/// </summary>
public static class GeocodeProviderNames
{
    /// <summary>
    /// Nominatim provider name
    /// </summary>
    public const string Nominatim = "nominatim";

    /// <summary>
    /// Amazon Location Service provider name
    /// </summary>
    public const string AmazonLocation = "amazon-location";

    /// <summary>
    /// Azure Maps provider name
    /// </summary>
    public const string AzureMaps = "azure-maps";

    /// <summary>
    /// Get all available provider names
    /// </summary>
    /// <returns>Array of provider names</returns>
    public static string[] GetAll() => [Nominatim, AmazonLocation, AzureMaps];
}

/// <summary>
/// Common spatial reference systems used in geocoding
/// </summary>
public static class GeocodeSpatialReferenceSystems
{
    /// <summary>
    /// WGS 84 (World Geodetic System 1984)
    /// </summary>
    public const int Wgs84 = 4326;

    /// <summary>
    /// Web Mercator (used by most web mapping services)
    /// </summary>
    public const int WebMercator = 3857;

    /// <summary>
    /// NAD 83 (North American Datum 1983)
    /// </summary>
    public const int Nad83 = 4269;

    /// <summary>
    /// GCJ-02 (Chinese coordinate system)
    /// </summary>
    public const int Gcj02 = 4490;

    /// <summary>
    /// Get commonly supported spatial reference systems
    /// </summary>
    /// <returns>Array of WKID values</returns>
    public static int[] GetCommon() => [Wgs84, WebMercator, Nad83];
}
