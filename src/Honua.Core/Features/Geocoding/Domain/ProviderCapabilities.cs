// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geocoding.Domain;

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
/// Configuration for a geocoding provider
/// </summary>
public abstract record GeocodeProviderConfiguration
{
    /// <summary>
    /// Whether the provider is enabled
    /// </summary>
    public bool Enabled { get; init; } = true;

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
