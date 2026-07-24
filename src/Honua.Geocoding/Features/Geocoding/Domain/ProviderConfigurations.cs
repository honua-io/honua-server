// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

/// <summary>
/// Configuration for Nominatim geocoding provider
/// </summary>
public sealed record NominatimProviderConfiguration : GeocodeProviderConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NominatimProviderConfiguration"/> record,
    /// enabling the provider by default.
    /// </summary>
    public NominatimProviderConfiguration()
    {
        Enabled = true;
    }

    /// <summary>
    /// Base URL for the Nominatim service
    /// </summary>
    public string BaseUrl { get; set; } = "https://nominatim.openstreetmap.org";

    /// <summary>
    /// User agent for API requests
    /// </summary>
    public string UserAgent { get; set; } = "Honua/1.0 (+https://honua.io)";

    /// <summary>
    /// Contact email for the Nominatim service
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Enable suggest functionality using search endpoint. Enabled by default so the
    /// GeocodeServer <c>suggest</c> operation works out of the box (Esri SDK certification
    /// expects suggest support); set to <c>false</c> to disable for a deployment.
    /// </summary>
    public bool EnableSuggestFromSearch { get; set; } = true;

    /// <summary>
    /// Default maximum suggestions for autocomplete
    /// </summary>
    public int MaxSuggestions { get; set; } = 5;

    /// <summary>
    /// Maximum number of addresses accepted in a single batch (geocodeAddresses) request.
    /// Nominatim has no native batch endpoint, so batches are fanned out to sequential
    /// forward-geocode calls; this is kept modest to respect the public service usage policy.
    /// </summary>
    public int MaxBatchSize { get; set; } = 100;
}

/// <summary>
/// Configuration for Amazon Location Services geocoding provider
/// </summary>
public sealed record AmazonLocationProviderConfiguration : GeocodeProviderConfiguration
{
    /// <summary>
    /// AWS region for the Location Service
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Name of the place index in Amazon Location Service
    /// </summary>
    public string PlaceIndexName { get; set; } = string.Empty;

    /// <summary>
    /// AWS access key ID
    /// </summary>
    public string? AccessKeyId { get; set; }

    /// <summary>
    /// AWS secret access key
    /// </summary>
    public string? SecretAccessKey { get; set; }

    /// <summary>
    /// Use AWS IAM role for authentication instead of access keys
    /// </summary>
    public bool UseIamRole { get; set; } = true;
}

/// <summary>
/// Configuration for Azure Maps geocoding provider
/// </summary>
public sealed record AzureMapsProviderConfiguration : GeocodeProviderConfiguration
{
    /// <summary>
    /// Azure Maps subscription key
    /// </summary>
    public string SubscriptionKey { get; set; } = string.Empty;

    /// <summary>
    /// API version for Azure Maps services
    /// </summary>
    public string ApiVersion { get; set; } = "1.0";

    /// <summary>
    /// Base URL for Azure Maps API
    /// </summary>
    public string BaseUrl { get; set; } = "https://atlas.microsoft.com";

    /// <summary>
    /// View parameter for localized results
    /// </summary>
    public string? View { get; set; }

    /// <summary>
    /// Language for localized results
    /// </summary>
    public string? Language { get; set; }
}

/// <summary>
/// Configuration for the local, self-hosted PostGIS-backed geocoder. The provider runs entirely
/// offline against a locally-loaded reference dataset and makes no external service calls.
/// </summary>
public sealed record LocalGeocoderProviderConfiguration : GeocodeProviderConfiguration
{
    /// <summary>
    /// PostgreSQL/PostGIS connection string for the reference dataset. When empty, the provider
    /// falls back to the application's default connection (resolved at registration time).
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Schema containing the reference table. Defaults to <c>public</c>.
    /// </summary>
    public string Schema { get; set; } = "public";

    /// <summary>
    /// Name of the reference table holding geocodable records (see the documented schema:
    /// <c>docs/reference/geocoding/local-postgis-geocoder.md</c>). Defaults to
    /// <c>honua_geocode_reference</c>.
    /// </summary>
    public string Table { get; set; } = "honua_geocode_reference";

    /// <summary>
    /// Maximum candidate rows considered per forward/suggest query. Defaults to 50.
    /// </summary>
    public int MaxCandidates { get; set; } = 50;

    /// <summary>
    /// Maximum number of addresses accepted in a single batch (geocodeAddresses) request. The
    /// local backend has no upstream usage policy, so this defaults higher than the public
    /// hosted providers.
    /// </summary>
    public int MaxBatchSize { get; set; } = 1000;

    /// <summary>
    /// Default reverse-geocode search radius in meters when a request supplies none. Defaults to
    /// 1000 m.
    /// </summary>
    public double DefaultReverseRadiusMeters { get; set; } = 1000.0;
}
