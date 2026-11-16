// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

public sealed class GeocodingConfiguration
{
    public const string SectionName = "Geocoding";

    public bool Enabled { get; set; } = true;

    public string DefaultProvider { get; set; } = "nominatim";

    public int DefaultSpatialReferenceWkid { get; set; } = 4326;

    public int DefaultMaxResults { get; set; } = 10;

    public int DefaultTimeoutSeconds { get; set; } = 30;

    public bool EnableFailover { get; set; } = true;

    public int MaxFailoverAttempts { get; set; } = 3;

    public bool EnableCaching { get; set; } = true;

    public int CacheExpirationMinutes { get; set; } = 60;

    public bool EnableLogging { get; set; } = true;

    public string LocatorName { get; set; } = "World";

    public bool EnforceRateLimits { get; set; } = true;

    public int? MaxBatchSizeLimit { get; set; }

    public ProvidersConfiguration Providers { get; set; } = new();
}

public sealed class ProvidersConfiguration
{
    public NominatimProviderConfiguration Nominatim { get; set; } = new();

    public AmazonLocationProviderConfiguration AmazonLocation { get; set; } = new();

    public AzureMapsProviderConfiguration AzureMaps { get; set; } = new();

    public LocalGeocoderProviderConfiguration Local { get; set; } = new();
}

public static class GeocodeProviderNames
{
    public const string Nominatim = "nominatim";

    public const string AmazonLocation = "amazon-location";

    public const string AzureMaps = "azure-maps";

    public const string Local = "local";

    public static string[] GetAll() => [Nominatim, AmazonLocation, AzureMaps, Local];
}
