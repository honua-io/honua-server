// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geocoding.Domain;

/// <summary>
/// Configuration for Nominatim geocoding provider
/// </summary>
public sealed record NominatimProviderConfiguration : GeocodeProviderConfiguration
{
    public NominatimProviderConfiguration()
    {
        Enabled = true;
    }

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
