// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Postgres.Features.Geocoding.Providers;

/// <summary>
/// Response model for Esri findAddressCandidates API
/// </summary>
internal sealed class EsriFindCandidatesResponse
{
    [JsonPropertyName("spatialReference")]
    public EsriSpatialReference? SpatialReference { get; init; }

    [JsonPropertyName("candidates")]
    public EsriCandidate[] Candidates { get; init; } = [];

    [JsonPropertyName("error")]
    public EsriError? Error { get; init; }
}

/// <summary>
/// Response model for Esri reverseGeocode API
/// </summary>
internal sealed class EsriReverseGeocodeResponse
{
    [JsonPropertyName("address")]
    public EsriAddress? Address { get; init; }

    [JsonPropertyName("location")]
    public EsriLocation? Location { get; init; }

    [JsonPropertyName("error")]
    public EsriError? Error { get; init; }
}

/// <summary>
/// Response model for Esri suggest API
/// </summary>
internal sealed class EsriSuggestResponse
{
    [JsonPropertyName("suggestions")]
    public EsriSuggestion[] Suggestions { get; init; } = [];

    [JsonPropertyName("error")]
    public EsriError? Error { get; init; }
}

/// <summary>
/// Response model for Esri geocodeAddresses (batch) API
/// </summary>
internal sealed class EsriBatchGeocodeResponse
{
    [JsonPropertyName("locations")]
    public EsriBatchLocation[] Locations { get; init; } = [];

    [JsonPropertyName("error")]
    public EsriError? Error { get; init; }
}

/// <summary>
/// Esri geocoding candidate
/// </summary>
internal sealed class EsriCandidate
{
    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("location")]
    public EsriLocation? Location { get; init; }

    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, object?>? Attributes { get; init; }

    [JsonPropertyName("extent")]
    public EsriExtent? Extent { get; init; }
}

/// <summary>
/// Esri batch geocoding location result
/// </summary>
internal sealed class EsriBatchLocation
{
    [JsonPropertyName("address")]
    public string? Address { get; init; }

    [JsonPropertyName("location")]
    public EsriLocation? Location { get; init; }

    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, object?>? Attributes { get; init; }

    [JsonPropertyName("resultId")]
    public string? ResultId { get; init; }
}

/// <summary>
/// Esri address components
/// </summary>
internal sealed class EsriAddress
{
    [JsonPropertyName("Address")]
    public string? AddressLine { get; init; }

    [JsonPropertyName("City")]
    public string? City { get; init; }

    [JsonPropertyName("Region")]
    public string? Region { get; init; }

    [JsonPropertyName("RegionAbbr")]
    public string? RegionAbbr { get; init; }

    [JsonPropertyName("Postal")]
    public string? PostalCode { get; init; }

    [JsonPropertyName("PostalExt")]
    public string? PostalExt { get; init; }

    [JsonPropertyName("CountryCode")]
    public string? CountryCode { get; init; }

    [JsonPropertyName("AddNum")]
    public string? AddressNumber { get; init; }

    [JsonPropertyName("AddNumFrom")]
    public string? AddressNumberFrom { get; init; }

    [JsonPropertyName("AddNumTo")]
    public string? AddressNumberTo { get; init; }

    [JsonPropertyName("Side")]
    public string? Side { get; init; }

    [JsonPropertyName("StPreDir")]
    public string? StreetPreDirection { get; init; }

    [JsonPropertyName("StPreType")]
    public string? StreetPreType { get; init; }

    [JsonPropertyName("StName")]
    public string? StreetName { get; init; }

    [JsonPropertyName("StType")]
    public string? StreetType { get; init; }

    [JsonPropertyName("StDir")]
    public string? StreetDirection { get; init; }

    [JsonPropertyName("Subaddress")]
    public string? Subaddress { get; init; }

    [JsonPropertyName("SubAddrType")]
    public string? SubaddressType { get; init; }

    [JsonPropertyName("SubAddrUnit")]
    public string? SubaddressUnit { get; init; }

    [JsonPropertyName("PlaceName")]
    public string? PlaceName { get; init; }

    [JsonPropertyName("Neighborhood")]
    public string? Neighborhood { get; init; }

    [JsonPropertyName("District")]
    public string? District { get; init; }

    [JsonPropertyName("MetroArea")]
    public string? MetroArea { get; init; }

    [JsonPropertyName("LongLabel")]
    public string? LongLabel { get; init; }

    [JsonPropertyName("ShortLabel")]
    public string? ShortLabel { get; init; }

    [JsonPropertyName("Addr_type")]
    public string? AddressType { get; init; }

    [JsonPropertyName("Type")]
    public string? Type { get; init; }

    [JsonPropertyName("Match_addr")]
    public string? MatchAddress { get; init; }
}

/// <summary>
/// Esri location (point geometry)
/// </summary>
internal sealed class EsriLocation
{
    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("spatialReference")]
    public EsriSpatialReference? SpatialReference { get; init; }
}

/// <summary>
/// Esri spatial reference system
/// </summary>
internal sealed class EsriSpatialReference
{
    [JsonPropertyName("wkid")]
    public int Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }
}

/// <summary>
/// Esri extent (bounding box)
/// </summary>
internal sealed class EsriExtent
{
    [JsonPropertyName("xmin")]
    public double XMin { get; init; }

    [JsonPropertyName("ymin")]
    public double YMin { get; init; }

    [JsonPropertyName("xmax")]
    public double XMax { get; init; }

    [JsonPropertyName("ymax")]
    public double YMax { get; init; }

    [JsonPropertyName("spatialReference")]
    public EsriSpatialReference? SpatialReference { get; init; }
}

/// <summary>
/// Esri geocoding suggestion
/// </summary>
internal sealed class EsriSuggestion
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("magicKey")]
    public string? MagicKey { get; init; }

    [JsonPropertyName("isCollection")]
    public bool IsCollection { get; init; }
}

/// <summary>
/// Esri API error response
/// </summary>
internal sealed class EsriError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("details")]
    public string[]? Details { get; init; }
}

/// <summary>
/// JSON context for Esri response serialization
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(EsriFindCandidatesResponse))]
[JsonSerializable(typeof(EsriReverseGeocodeResponse))]
[JsonSerializable(typeof(EsriSuggestResponse))]
[JsonSerializable(typeof(EsriBatchGeocodeResponse))]
internal sealed partial class EsriJsonContext : JsonSerializerContext
{
}