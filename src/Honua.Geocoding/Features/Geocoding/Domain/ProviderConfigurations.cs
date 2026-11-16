// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

public sealed record NominatimProviderConfiguration : GeocodeProviderConfiguration
{
    public NominatimProviderConfiguration()
    {
        Enabled = true;
    }

    public string BaseUrl { get; set; } = "https://nominatim.openstreetmap.org";

    public string UserAgent { get; set; } = "Honua/1.0 (+https://honua.io)";

    public string? Email { get; set; }

    public bool EnableSuggestFromSearch { get; set; } = true;

    public int MaxSuggestions { get; set; } = 5;

    public int MaxBatchSize { get; set; } = 100;
}

public sealed record AmazonLocationProviderConfiguration : GeocodeProviderConfiguration
{
    public string Region { get; set; } = "us-east-1";

    public string PlaceIndexName { get; set; } = string.Empty;

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    public bool UseIamRole { get; set; } = true;
}

public sealed record AzureMapsProviderConfiguration : GeocodeProviderConfiguration
{
    public string SubscriptionKey { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "1.0";

    public string BaseUrl { get; set; } = "https://atlas.microsoft.com";

    public string? View { get; set; }

    public string? Language { get; set; }
}

public sealed record LocalGeocoderProviderConfiguration : GeocodeProviderConfiguration
{
    public string ConnectionString { get; set; } = string.Empty;

    public string Schema { get; set; } = "public";

    public string Table { get; set; } = "honua_geocode_reference";

    public int MaxCandidates { get; set; } = 50;

    public int MaxBatchSize { get; set; } = 1000;

    public double DefaultReverseRadiusMeters { get; set; } = 1000.0;
}
