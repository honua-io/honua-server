// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response listing geocoding provider health and capability status.
/// </summary>
internal sealed class GeocodingOperationsProvidersResponse
{
    /// <summary>
    /// Per-provider health and capability details.
    /// </summary>
    public GeocodingOperationsProviderStatusResponse[] Providers { get; init; } = [];

    /// <summary>
    /// Name of the default geocoding provider.
    /// </summary>
    public string? DefaultProvider { get; init; }

    /// <summary>
    /// Whether failover to backup providers is enabled.
    /// </summary>
    public bool FailoverEnabled { get; init; }

    /// <summary>
    /// Timestamp when this response was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// Combined health and capability status for a single geocoding provider.
/// </summary>
internal sealed class GeocodingOperationsProviderStatusResponse
{
    /// <summary>
    /// Name of the geocoding provider.
    /// </summary>
    public string? ProviderName { get; init; }

    /// <summary>
    /// Whether the provider is currently healthy.
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Error message if the provider is unhealthy.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Timestamp of the last health check.
    /// </summary>
    public DateTime? LastChecked { get; init; }

    /// <summary>
    /// Response time in milliseconds for the last health check.
    /// </summary>
    public double? ResponseTimeMs { get; init; }

    /// <summary>
    /// Capability matrix for this provider.
    /// </summary>
    public GeocodingOperationsCapabilitiesResponse? Capabilities { get; init; }
}

/// <summary>
/// Capability flags for a geocoding provider.
/// </summary>
internal sealed class GeocodingOperationsCapabilitiesResponse
{
    /// <summary>
    /// Whether forward geocoding (address to coordinates) is supported.
    /// </summary>
    public bool SupportsForwardGeocode { get; init; }

    /// <summary>
    /// Whether reverse geocoding (coordinates to address) is supported.
    /// </summary>
    public bool SupportsReverseGeocode { get; init; }

    /// <summary>
    /// Whether autocomplete suggestions are supported.
    /// </summary>
    public bool SupportsSuggest { get; init; }

    /// <summary>
    /// Whether batch geocoding is supported.
    /// </summary>
    public bool SupportsBatch { get; init; }

    /// <summary>
    /// Whether structured address input is supported.
    /// </summary>
    public bool SupportsStructuredInput { get; init; }

    /// <summary>
    /// Maximum results per request.
    /// </summary>
    public int MaxResultsPerRequest { get; init; }

    /// <summary>
    /// Maximum batch size for batch requests.
    /// </summary>
    public int MaxBatchSize { get; init; }

    /// <summary>
    /// Rate limit in requests per minute, if applicable.
    /// </summary>
    public int? RateLimitPerMinute { get; init; }

    /// <summary>
    /// Whether the provider requires authentication.
    /// </summary>
    public bool RequiresAuthentication { get; init; }
}

/// <summary>
/// Read-only snapshot of geocoding configuration.
/// </summary>
internal sealed class GeocodingConfigurationResponse
{
    /// <summary>
    /// Whether geocoding is enabled.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Default geocoding provider name.
    /// </summary>
    public string? DefaultProvider { get; init; }

    /// <summary>
    /// Whether failover to backup providers is enabled.
    /// </summary>
    public bool EnableFailover { get; init; }

    /// <summary>
    /// Maximum number of failover attempts.
    /// </summary>
    public int MaxFailoverAttempts { get; init; }

    /// <summary>
    /// Whether result caching is enabled.
    /// </summary>
    public bool EnableCaching { get; init; }

    /// <summary>
    /// Cache expiration time in minutes.
    /// </summary>
    public int CacheExpirationMinutes { get; init; }

    /// <summary>
    /// Default request timeout in seconds.
    /// </summary>
    public int DefaultTimeoutSeconds { get; init; }
}
