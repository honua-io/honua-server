// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

public sealed record ForwardGeocodeRequest(
    string Query,
    int MaxResults = 10,
    int SpatialReferenceWkid = 4326,
    string? CountryCodes = null,
    GeocodeInputType InputType = GeocodeInputType.SingleLine,
    GeocodeBounds? SearchBounds = null)
{
    public StructuredAddress? StructuredAddress { get; init; }

    public string? CategoryFilter { get; init; }

    public string? MagicKey { get; init; }

    public GeocodePoint? BiasLocation { get; init; }

    public double? BiasDistanceMeters { get; init; }
}

public sealed record ReverseGeocodeRequest(
    double X,
    double Y,
    int SpatialReferenceWkid = 4326,
    double? DistanceMeters = null)
{
    public string[]? FeatureTypes { get; init; }

    public string? LanguageCode { get; init; }
}

public sealed record SuggestGeocodeRequest(
    string Text,
    int MaxResults = 10,
    string? CountryCodes = null,
    string? CategoryFilter = null)
{
    public GeocodePoint? BiasLocation { get; init; }

    public double? BiasDistanceMeters { get; init; }

    public GeocodeBounds? SearchBounds { get; init; }
}

public sealed record BatchGeocodeRequest(
    IReadOnlyList<string> Queries,
    int SpatialReferenceWkid = 4326,
    string? CountryCodes = null)
{
    public int MaxResultsPerQuery { get; init; } = 1;
}

public sealed record GeocodeCandidate(
    string Address,
    double X,
    double Y,
    double Score,
    IReadOnlyDictionary<string, string?> Attributes,
    string? ProviderId = null,
    double? DistanceMeters = null)
{
    public const string ResultIdAttribute = "ResultID";

    public string? MatchLevel { get; init; }

    public string? AddressType { get; init; }

    public int SpatialReferenceWkid { get; init; } = 4326;

    public StructuredAddress? StructuredAddress { get; init; }

    public bool IsMatch { get; init; } = true;

    public GeocodeCandidate WithResultId(int resultId)
    {
        var attributes = new Dictionary<string, string?>(Attributes, StringComparer.Ordinal)
        {
            [ResultIdAttribute] = resultId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        return this with { Attributes = attributes };
    }

    public static GeocodeCandidate CreateNoMatch(int resultId, int spatialReferenceWkid = 4326)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ResultIdAttribute] = resultId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        return new GeocodeCandidate(
            Address: string.Empty,
            X: double.NaN,
            Y: double.NaN,
            Score: 0.0,
            Attributes: attributes)
        {
            IsMatch = false,
            SpatialReferenceWkid = spatialReferenceWkid
        };
    }
}

public sealed record ReverseGeocodeMatch(
    string Address,
    double X,
    double Y,
    IReadOnlyDictionary<string, string?> Attributes,
    string? ProviderId = null,
    double? DistanceMeters = null)
{
    public string? AddressType { get; init; }

    public int SpatialReferenceWkid { get; init; } = 4326;

    public StructuredAddress? StructuredAddress { get; init; }
}

public sealed record GeocodeSuggestion(
    string Text,
    string MagicKey,
    bool IsCollection = false)
{
    public string? Category { get; init; }

    public GeocodeBounds? Bounds { get; init; }
}

public sealed record StructuredAddress
{
    public string? AddressNumber { get; init; }

    public string? StreetName { get; init; }

    public string? City { get; init; }

    public string? Region { get; init; }

    public string? PostalCode { get; init; }

    public string? Country { get; init; }

    public string? CountryCode { get; init; }

    public string? Subaddress { get; init; }

    public string? Neighborhood { get; init; }
}

public sealed record GeocodePoint(double X, double Y, int SpatialReferenceWkid = 4326);

public sealed record GeocodeBounds(double XMin, double YMin, double XMax, double YMax, int SpatialReferenceWkid = 4326);

public enum GeocodeInputType
{
    SingleLine,

    Structured,

    PointOfInterest
}
