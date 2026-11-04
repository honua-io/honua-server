// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

public sealed record GeocodeProviderCapabilities(
    bool SupportsForwardGeocode = true,
    bool SupportsReverseGeocode = true,
    bool SupportsSuggest = false,
    bool SupportsBatch = false,
    bool SupportsStructuredInput = false,
    bool SupportsBiasing = false)
{
    public int[] SupportedSpatialReferences { get; init; } = [4326];

    public int MaxResultsPerRequest { get; init; } = 50;

    public int MaxBatchSize { get; init; } = 100;

    public int? RateLimitPerMinute { get; init; }

    public string[] SupportedStructuredFields { get; init; } = [];

    public string[] SupportedFeatureTypes { get; init; } = [];

    public string[] SupportedCountries { get; init; } = [];

    public string[] SupportedLanguages { get; init; } = [];

    public bool RequiresAuthentication { get; init; }

    public bool SupportsHttps { get; init; } = true;

    public int DefaultTimeoutSeconds { get; init; } = 30;
}

public static class GeocodeStructuredFields
{
    public const string Address = "Address";

    public const string Neighborhood = "Neighborhood";

    public const string City = "City";

    public const string Subregion = "Subregion";

    public const string Region = "Region";

    public const string Postal = "Postal";

    public const string CountryCode = "CountryCode";

    public static string[] All() => [Address, Neighborhood, City, Subregion, Region, Postal, CountryCode];
}

public abstract record GeocodeProviderConfiguration
{
    public bool Enabled { get; set; }

    public int Priority { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxResults { get; set; } = 10;

    public string? DefaultCountryCodes { get; set; }
}

public sealed record GeocodeProviderHealth(
    string ProviderName,
    bool IsHealthy,
    string? ErrorMessage = null,
    DateTime? LastChecked = null)
{
    public double? ResponseTimeMs { get; init; }

    public IReadOnlyDictionary<string, object>? Metrics { get; init; }
}
