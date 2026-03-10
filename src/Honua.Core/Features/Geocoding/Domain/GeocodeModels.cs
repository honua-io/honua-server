// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geocoding.Domain;

/// <summary>
/// Request for forward geocoding (address to coordinates)
/// </summary>
public sealed record ForwardGeocodeRequest(
    string Query,
    int MaxResults = 10,
    int SpatialReferenceWkid = 4326,
    string? CountryCodes = null,
    GeocodeInputType InputType = GeocodeInputType.SingleLine,
    GeocodeBounds? SearchBounds = null)
{
    /// <summary>
    /// Structured address components for providers that support it
    /// </summary>
    public StructuredAddress? StructuredAddress { get; init; }
}

/// <summary>
/// Request for reverse geocoding (coordinates to address)
/// </summary>
public sealed record ReverseGeocodeRequest(
    double X,
    double Y,
    int SpatialReferenceWkid = 4326,
    double? DistanceMeters = null)
{
    /// <summary>
    /// Feature types to return (e.g., "PointAddress", "Subaddress", "StreetAddress")
    /// </summary>
    public string[]? FeatureTypes { get; init; }

    /// <summary>
    /// Language code for localized results
    /// </summary>
    public string? LanguageCode { get; init; }
}

/// <summary>
/// Request for geocoding suggestions/autocomplete
/// </summary>
public sealed record SuggestGeocodeRequest(
    string Text,
    int MaxResults = 10,
    string? CountryCodes = null,
    string? CategoryFilter = null)
{
    /// <summary>
    /// Bias results towards this location
    /// </summary>
    public GeocodePoint? BiasLocation { get; init; }

    /// <summary>
    /// Search within this bounding box
    /// </summary>
    public GeocodeBounds? SearchBounds { get; init; }
}

/// <summary>
/// Request for batch geocoding multiple addresses
/// </summary>
public sealed record BatchGeocodeRequest(
    IReadOnlyList<string> Queries,
    int SpatialReferenceWkid = 4326,
    string? CountryCodes = null)
{
    /// <summary>
    /// Maximum number of results per query
    /// </summary>
    public int MaxResultsPerQuery { get; init; } = 1;
}

/// <summary>
/// Geocoding result candidate
/// </summary>
public sealed record GeocodeCandidate(
    string Address,
    double X,
    double Y,
    double Score,
    IReadOnlyDictionary<string, string?> Attributes,
    string? ProviderId = null,
    double? DistanceMeters = null)
{
    /// <summary>
    /// Geocode match level (e.g., "exact", "interpolated", "approximate")
    /// </summary>
    public string? MatchLevel { get; init; }

    /// <summary>
    /// Address type (e.g., "PointAddress", "StreetAddress", "Locality")
    /// </summary>
    public string? AddressType { get; init; }

    /// <summary>
    /// Spatial reference system of the coordinates
    /// </summary>
    public int SpatialReferenceWkid { get; init; } = 4326;

    /// <summary>
    /// Structured address components
    /// </summary>
    public StructuredAddress? StructuredAddress { get; init; }
}

/// <summary>
/// Reverse geocoding match result
/// </summary>
public sealed record ReverseGeocodeMatch(
    string Address,
    double X,
    double Y,
    IReadOnlyDictionary<string, string?> Attributes,
    string? ProviderId = null,
    double? DistanceMeters = null)
{
    /// <summary>
    /// Address type (e.g., "PointAddress", "StreetAddress", "Locality")
    /// </summary>
    public string? AddressType { get; init; }

    /// <summary>
    /// Spatial reference system of the coordinates
    /// </summary>
    public int SpatialReferenceWkid { get; init; } = 4326;

    /// <summary>
    /// Structured address components
    /// </summary>
    public StructuredAddress? StructuredAddress { get; init; }
}

/// <summary>
/// Geocoding suggestion for autocomplete
/// </summary>
public sealed record GeocodeSuggestion(
    string Text,
    string MagicKey,
    bool IsCollection = false)
{
    /// <summary>
    /// Suggestion type/category
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Bounding box for the suggestion
    /// </summary>
    public GeocodeBounds? Bounds { get; init; }
}

/// <summary>
/// Structured address components
/// </summary>
public sealed record StructuredAddress
{
    /// <summary>
    /// Street address number
    /// </summary>
    public string? AddressNumber { get; init; }

    /// <summary>
    /// Street name
    /// </summary>
    public string? StreetName { get; init; }

    /// <summary>
    /// City or locality
    /// </summary>
    public string? City { get; init; }

    /// <summary>
    /// State, province, or region
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Postal/ZIP code
    /// </summary>
    public string? PostalCode { get; init; }

    /// <summary>
    /// Country name or code
    /// </summary>
    public string? Country { get; init; }

    /// <summary>
    /// Subaddress (e.g., unit, suite, apartment)
    /// </summary>
    public string? Subaddress { get; init; }

    /// <summary>
    /// Neighborhood or district
    /// </summary>
    public string? Neighborhood { get; init; }
}

/// <summary>
/// Geographic point
/// </summary>
public sealed record GeocodePoint(double X, double Y, int SpatialReferenceWkid = 4326);

/// <summary>
/// Geographic bounding box
/// </summary>
public sealed record GeocodeBounds(double XMin, double YMin, double XMax, double YMax, int SpatialReferenceWkid = 4326);

/// <summary>
/// Type of geocoding input
/// </summary>
public enum GeocodeInputType
{
    /// <summary>
    /// Single line address string
    /// </summary>
    SingleLine,

    /// <summary>
    /// Structured address components
    /// </summary>
    Structured,

    /// <summary>
    /// POI (Point of Interest) search
    /// </summary>
    PointOfInterest
}
