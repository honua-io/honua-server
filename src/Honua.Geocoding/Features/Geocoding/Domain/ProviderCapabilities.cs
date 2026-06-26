// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

/// <summary>
/// Capabilities of a geocoding provider
/// </summary>
public sealed record GeocodeProviderCapabilities(
    bool SupportsForwardGeocode = true,
    bool SupportsReverseGeocode = true,
    bool SupportsSuggest = false,
    bool SupportsBatch = false,
    bool SupportsStructuredInput = false,
    bool SupportsBiasing = false)
{
    /// <summary>
    /// Supported spatial reference systems (WKID codes)
    /// </summary>
    public int[] SupportedSpatialReferences { get; init; } = [4326];

    /// <summary>
    /// Maximum number of results per request
    /// </summary>
    public int MaxResultsPerRequest { get; init; } = 50;

    /// <summary>
    /// Maximum number of queries in a batch request
    /// </summary>
    public int MaxBatchSize { get; init; } = 100;

    /// <summary>
    /// Rate limiting information (requests per minute)
    /// </summary>
    public int? RateLimitPerMinute { get; init; }

    /// <summary>
    /// Structured-address fields the provider can consume natively (mapped to its request shape).
    /// Used to advertise structured-input fidelity in capability metadata so callers know which
    /// components (e.g. <c>Address</c>, <c>City</c>, <c>Region</c>, <c>Postal</c>, <c>CountryCode</c>)
    /// are honored rather than flattened. Empty when <see cref="SupportsStructuredInput"/> is false.
    /// </summary>
    public string[] SupportedStructuredFields { get; init; } = [];

    /// <summary>
    /// Supported feature types for reverse geocoding
    /// </summary>
    public string[] SupportedFeatureTypes { get; init; } = [];

    /// <summary>
    /// Supported countries (ISO 3166-1 alpha-2 codes)
    /// </summary>
    public string[] SupportedCountries { get; init; } = [];

    /// <summary>
    /// Supported languages (ISO 639-1 codes)
    /// </summary>
    public string[] SupportedLanguages { get; init; } = [];

    /// <summary>
    /// Whether the provider requires authentication
    /// </summary>
    public bool RequiresAuthentication { get; init; }

    /// <summary>
    /// Whether the provider supports HTTPS
    /// </summary>
    public bool SupportsHttps { get; init; } = true;

    /// <summary>
    /// Default timeout in seconds
    /// </summary>
    public int DefaultTimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// Canonical structured-address field tokens advertised in <see cref="GeocodeProviderCapabilities.SupportedStructuredFields"/>.
/// These mirror the Esri GeocodeServer structured-input fields and map onto
/// <see cref="StructuredAddress"/> components.
/// </summary>
public static class GeocodeStructuredFields
{
    /// <summary>Street address line (number + street name).</summary>
    public const string Address = "Address";

    /// <summary>Neighborhood or district.</summary>
    public const string Neighborhood = "Neighborhood";

    /// <summary>City or locality.</summary>
    public const string City = "City";

    /// <summary>Sub-region or county.</summary>
    public const string Subregion = "Subregion";

    /// <summary>State, province, or region.</summary>
    public const string Region = "Region";

    /// <summary>Postal or ZIP code.</summary>
    public const string Postal = "Postal";

    /// <summary>ISO country code or country name.</summary>
    public const string CountryCode = "CountryCode";

    /// <summary>
    /// The full set of structured fields supported by providers that consume structured input
    /// (Nominatim, Azure Maps, Amazon Location, and the local PostGIS backend) so structured-input
    /// fidelity is advertised consistently across providers.
    /// </summary>
    public static string[] All() => [Address, Neighborhood, City, Subregion, Region, Postal, CountryCode];
}

/// <summary>
/// Configuration for a geocoding provider
/// </summary>
public abstract record GeocodeProviderConfiguration
{
    /// <summary>
    /// Whether the provider is enabled
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Provider priority (higher numbers = higher priority)
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Timeout in seconds for requests
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Maximum results per request
    /// </summary>
    public int MaxResults { get; init; } = 10;

    /// <summary>
    /// Default country codes for biasing results
    /// </summary>
    public string? DefaultCountryCodes { get; init; }
}

/// <summary>
/// Health status of a geocoding provider
/// </summary>
public sealed record GeocodeProviderHealth(
    string ProviderName,
    bool IsHealthy,
    string? ErrorMessage = null,
    DateTime? LastChecked = null)
{
    /// <summary>
    /// Response time in milliseconds for the last health check
    /// </summary>
    public double? ResponseTimeMs { get; init; }

    /// <summary>
    /// Additional health metrics
    /// </summary>
    public IReadOnlyDictionary<string, object>? Metrics { get; init; }
}
