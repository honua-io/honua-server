// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response model for geocoding provider status.
/// </summary>
public sealed class GeocodingProvidersResponse
{
    /// <summary>
    /// Name of the default geocoding provider.
    /// </summary>
    public string? DefaultProvider { get; init; }

    /// <summary>
    /// Whether provider failover is enabled.
    /// </summary>
    public required bool FailoverEnabled { get; init; }

    /// <summary>
    /// List of configured geocoding providers with health status.
    /// </summary>
    public required GeocodingProviderDetail[] Providers { get; init; }
}

/// <summary>
/// Detailed status of a single geocoding provider.
/// </summary>
public sealed class GeocodingProviderDetail
{
    /// <summary>
    /// Provider name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether the provider is healthy.
    /// </summary>
    public required bool IsHealthy { get; init; }

    /// <summary>
    /// Error message if the provider is unhealthy.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// When the health was last checked.
    /// </summary>
    public DateTime? LastChecked { get; init; }

    /// <summary>
    /// Response time in milliseconds for the last health check.
    /// </summary>
    public double? ResponseTimeMs { get; init; }

    /// <summary>
    /// Provider capabilities.
    /// </summary>
    public required GeocodingProviderCapabilitiesDto Capabilities { get; init; }
}

/// <summary>
/// DTO for geocoding provider capabilities (AOT-safe, no Dictionary{string, object}).
/// </summary>
public sealed class GeocodingProviderCapabilitiesDto
{
    /// <summary>
    /// Whether forward geocoding is supported.
    /// </summary>
    public required bool SupportsForwardGeocode { get; init; }

    /// <summary>
    /// Whether reverse geocoding is supported.
    /// </summary>
    public required bool SupportsReverseGeocode { get; init; }

    /// <summary>
    /// Whether suggestion/autocomplete is supported.
    /// </summary>
    public required bool SupportsSuggest { get; init; }

    /// <summary>
    /// Whether batch geocoding is supported.
    /// </summary>
    public required bool SupportsBatch { get; init; }

    /// <summary>
    /// Maximum results per request.
    /// </summary>
    public required int MaxResultsPerRequest { get; init; }

    /// <summary>
    /// Rate limit in requests per minute, null if unlimited.
    /// </summary>
    public int? RateLimitPerMinute { get; init; }

    /// <summary>
    /// Whether the provider requires authentication.
    /// </summary>
    public required bool RequiresAuthentication { get; init; }
}
