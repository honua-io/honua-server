// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geocoding.Features.Geocoding.Domain;

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
    /// Attribute key carrying the zero-based index of the batch input record this candidate
    /// corresponds to. Batch geocoding returns one candidate slot per input in request order;
    /// this value lets callers correlate each result back to its submitted record, matching the
    /// Esri <c>geocodeAddresses</c> contract where every location reports a <c>ResultID</c>.
    /// </summary>
    public const string ResultIdAttribute = "ResultID";

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

    /// <summary>
    /// Indicates whether this candidate represents a successful match. A <see langword="false"/>
    /// value denotes a "no match" slot emitted to keep batch results positionally aligned with
    /// their inputs (see <see cref="CreateNoMatch"/>).
    /// </summary>
    public bool IsMatch { get; init; } = true;

    /// <summary>
    /// Returns a copy of this candidate whose <see cref="Attributes"/> include a stable
    /// <see cref="ResultIdAttribute"/> entry equal to <paramref name="resultId"/>. Existing
    /// attributes are preserved; any prior <c>ResultID</c> entry is overwritten.
    /// </summary>
    /// <param name="resultId">Zero-based index of the originating batch input record.</param>
    public GeocodeCandidate WithResultId(int resultId)
    {
        var attributes = new Dictionary<string, string?>(Attributes, StringComparer.Ordinal)
        {
            [ResultIdAttribute] = resultId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        return this with { Attributes = attributes };
    }

    /// <summary>
    /// Creates an explicit "no match" candidate for a batch input that was blank or that the
    /// provider could not geocode. The slot preserves positional alignment with the input list
    /// and carries the originating <see cref="ResultIdAttribute"/> so callers can still correlate
    /// it back to the submitted record. Coordinates are <see cref="double.NaN"/> and
    /// <see cref="Score"/> is zero, matching the Esri convention for unmatched records.
    /// </summary>
    /// <param name="resultId">Zero-based index of the originating batch input record.</param>
    /// <param name="spatialReferenceWkid">Spatial reference requested for the batch.</param>
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
